﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Transport.Channels.Sockets
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.Contracts;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading.Tasks;
    using DotNetty.Common.Concurrency;
    using DotNetty.Common.Internal.Logging;
    using DotNetty.Common.Utilities;

    public abstract class AbstractSocketChannel<TChannel, TUnsafe> : AbstractChannel<TChannel, TUnsafe>
      where TChannel : AbstractSocketChannel<TChannel, TUnsafe>
      where TUnsafe : AbstractSocketChannel<TChannel, TUnsafe>.AbstractSocketUnsafe, new()
    {
        //static readonly IInternalLogger Logger = InternalLoggerFactory.GetInstance<AbstractSocketChannel>(); ## 苦竹 屏蔽 ##

        [Flags]
        protected enum StateFlags
        {
            Open = 1,
            ReadScheduled = 1 << 1,
            WriteScheduled = 1 << 2,
            Active = 1 << 3
            // todo: add input shutdown and read pending here as well?
        }

        internal static readonly EventHandler<SocketAsyncEventArgs> IoCompletedCallback = OnIoCompleted;
        static readonly Action<object, object> ConnectCallbackAction = (u, e) => ((ISocketChannelUnsafe)u).FinishConnect((SocketChannelAsyncOperation<TChannel, TUnsafe>)e);
        static readonly Action<object, object> ReadCallbackAction = (u, e) => ((ISocketChannelUnsafe)u).FinishRead((SocketChannelAsyncOperation<TChannel, TUnsafe>)e);
        static readonly Action<object, object> WriteCallbackAction = (u, e) => ((ISocketChannelUnsafe)u).FinishWrite((SocketChannelAsyncOperation<TChannel, TUnsafe>)e);

        protected readonly Socket Socket;
        SocketChannelAsyncOperation<TChannel, TUnsafe> readOperation;
        SocketChannelAsyncOperation<TChannel, TUnsafe> writeOperation;
        volatile bool inputShutdown;
        internal bool ReadPending;
        volatile StateFlags state;

        TaskCompletionSource connectPromise;
        IScheduledTask connectCancellationTask;

        protected AbstractSocketChannel(IChannel parent, Socket socket)
            : base(parent)
        {
            this.Socket = socket;
            this.state = StateFlags.Open;

            try
            {
                this.Socket.Blocking = false;
            }
            catch (SocketException ex)
            {
                try
                {
                    socket.Dispose();
                }
                catch (SocketException ex2)
                {
                    if (Logger.WarnEnabled)
                    {
                        Logger.Warn("Failed to close a partially initialized socket.", ex2);
                    }
                }

                throw new ChannelException("Failed to enter non-blocking mode.", ex);
            }
        }

        public override bool Open => this.IsInState(StateFlags.Open);

        public override bool Active => this.IsInState(StateFlags.Active);

        /// <summary>
        ///     Set read pending to <c>false</c>.
        /// </summary>
        protected internal void ClearReadPending()
        {
            if (this.Registered)
            {
                IEventLoop eventLoop = this.EventLoop;
                if (eventLoop.InEventLoop)
                {
                    this.ClearReadPending0();
                }
                else
                {
                    eventLoop.Execute(channel => ((TChannel)channel).ClearReadPending0(), this);
                }
            }
            else
            {
                // Best effort if we are not registered yet clear ReadPending. This happens during channel initialization.
                // NB: We only set the boolean field instead of calling ClearReadPending0(), because the SelectionKey is
                // not set yet so it would produce an assertion failure.
                this.ReadPending = false;
            }
        }

        void ClearReadPending0() => this.ReadPending = false;

        protected bool InputShutdown => this.inputShutdown;

        protected void ShutdownInput() => this.inputShutdown = true;

        protected void SetState(StateFlags stateToSet) => this.state |= stateToSet;

        /// <returns>state before modification</returns>
        protected StateFlags ResetState(StateFlags stateToReset)
        {
            StateFlags oldState = this.state;
            if ((oldState & stateToReset) != 0)
            {
                this.state = oldState & ~stateToReset;
            }
            return oldState;
        }

        protected bool TryResetState(StateFlags stateToReset)
        {
            StateFlags oldState = this.state;
            if ((oldState & stateToReset) != 0)
            {
                this.state = oldState & ~stateToReset;
                return true;
            }
            return false;
        }

        protected bool IsInState(StateFlags stateToCheck) => (this.state & stateToCheck) == stateToCheck;

        protected SocketChannelAsyncOperation<TChannel, TUnsafe> ReadOperation => this.readOperation ?? (this.readOperation = new SocketChannelAsyncOperation<TChannel, TUnsafe>((TChannel)this, true));

        SocketChannelAsyncOperation<TChannel, TUnsafe> WriteOperation => this.writeOperation ?? (this.writeOperation = new SocketChannelAsyncOperation<TChannel, TUnsafe>((TChannel)this, false));

        protected SocketChannelAsyncOperation<TChannel, TUnsafe> PrepareWriteOperation(ArraySegment<byte> buffer)
        {
            var operation = this.WriteOperation;
            operation.SetBuffer(buffer.Array, buffer.Offset, buffer.Count);
            return operation;
        }

        protected SocketChannelAsyncOperation<TChannel, TUnsafe> PrepareWriteOperation(IList<ArraySegment<byte>> buffers)
        {
            var operation = this.WriteOperation;
            operation.BufferList = buffers;
            return operation;
        }

        protected void ResetWriteOperation()
        {
            var operation = this.writeOperation;

            Contract.Assert(operation != null);

            if (operation.BufferList == null)
            {
                operation.SetBuffer(null, 0, 0);
            }
            else
            {
                operation.BufferList = null;
            }
        }

        /// <remarks>PORT NOTE: matches behavior of NioEventLoop.processSelectedKey</remarks>
        static void OnIoCompleted(object sender, SocketAsyncEventArgs args)
        {
            var operation = (SocketChannelAsyncOperation<TChannel, TUnsafe>)args;
            var channel = operation.Channel;
            var @unsafe = channel.Unsafe;
            IEventLoop eventLoop = channel.EventLoop;
            switch (args.LastOperation)
            {
                case SocketAsyncOperation.Accept:
                    if (eventLoop.InEventLoop)
                    {
                        @unsafe.FinishRead(operation);
                    }
                    else
                    {
                        eventLoop.Execute(ReadCallbackAction, @unsafe, operation);
                    }
                    break;
                case SocketAsyncOperation.Connect:
                    if (eventLoop.InEventLoop)
                    {
                        @unsafe.FinishConnect(operation);
                    }
                    else
                    {
                        eventLoop.Execute(ConnectCallbackAction, @unsafe, operation);
                    }
                    break;
                case SocketAsyncOperation.Receive:
                case SocketAsyncOperation.ReceiveFrom:
                    if (eventLoop.InEventLoop)
                    {
                        @unsafe.FinishRead(operation);
                    }
                    else
                    {
                        eventLoop.Execute(ReadCallbackAction, @unsafe, operation);
                    }
                    break;
                case SocketAsyncOperation.Send:
                case SocketAsyncOperation.SendTo:
                    if (eventLoop.InEventLoop)
                    {
                        @unsafe.FinishWrite(operation);
                    }
                    else
                    {
                        eventLoop.Execute(WriteCallbackAction, @unsafe, operation);
                    }
                    break;
                default:
                    // todo: think of a better way to comm exception
                    throw new ArgumentException("The last operation completed on the socket was not expected");
            }
        }

        internal interface ISocketChannelUnsafe : IChannelUnsafe
        {
            /// <summary>
            ///     Finish connect
            /// </summary>
            void FinishConnect(SocketChannelAsyncOperation<TChannel, TUnsafe> operation);

            /// <summary>
            ///     Read from underlying {@link SelectableChannel}
            /// </summary>
            void FinishRead(SocketChannelAsyncOperation<TChannel, TUnsafe> operation);

            void FinishWrite(SocketChannelAsyncOperation<TChannel, TUnsafe> operation);
        }

        public abstract class AbstractSocketUnsafe : AbstractUnsafe, ISocketChannelUnsafe
        {
            protected AbstractSocketUnsafe() //(AbstractSocketChannel channel)
                : base() //(channel)
            {
            }

            //public AbstractSocketChannel Channel => (AbstractSocketChannel)this.channel;
            //public TChannel Channel => this.channel;

            public sealed override Task ConnectAsync(EndPoint remoteAddress, EndPoint localAddress)
            {
                // todo: handle cancellation
                var ch = this.Channel;
                if (!ch.Open)
                {
                    return this.CreateClosedChannelExceptionTask();
                }

                try
                {
                    if (ch.connectPromise != null)
                    {
                        throw new InvalidOperationException("connection attempt already made");
                    }

                    bool wasActive = this.channel.Active;
                    if (ch.DoConnect(remoteAddress, localAddress))
                    {
                        this.FulfillConnectPromise(wasActive);
                        return TaskUtil.Completed;
                    }
                    else
                    {
                        ch.connectPromise = new TaskCompletionSource(remoteAddress);

                        // Schedule connect timeout.
                        TimeSpan connectTimeout = ch.Configuration.ConnectTimeout;
                        if (connectTimeout > TimeSpan.Zero)
                        {
                            ch.connectCancellationTask = ch.EventLoop.Schedule(
                                (c, a) =>
                                {
                                    // todo: make static / cache delegate?..
                                    var self = (TChannel)c;
                                    // todo: call Socket.CancelConnectAsync(...)
                                    TaskCompletionSource promise = self.connectPromise;
                                    var cause = new ConnectTimeoutException("connection timed out: " + a.ToString());
                                    if (promise != null && promise.TrySetException(cause))
                                    {
                                        self.CloseSafe();
                                    }
                                },
                                this.channel,
                                remoteAddress,
                                connectTimeout);
                        }

#if NET40
                        Action<Task> continuationAction = completed =>
                        {
                            var c = ch;
                            c.connectCancellationTask?.Cancel();
                            c.connectPromise = null;
                            c.CloseSafe();
                        };
                        ch.connectPromise.Task.ContinueWith(
                            continuationAction,
                            TaskContinuationOptions.OnlyOnCanceled | TaskContinuationOptions.ExecuteSynchronously);
#else
                        ch.connectPromise.Task.ContinueWith(
                            (t, s) =>
                            {
                                var c = (TChannel)s;
                                c.connectCancellationTask?.Cancel();
                                c.connectPromise = null;
                                c.CloseSafe();
                            },
                            ch,
                            TaskContinuationOptions.OnlyOnCanceled | TaskContinuationOptions.ExecuteSynchronously);
#endif

                        return ch.connectPromise.Task;
                    }
                }
                catch (Exception ex)
                {
                    this.CloseIfClosed();
                    return TaskUtil.FromException(this.AnnotateConnectException(ex, remoteAddress));
                }
            }

            void FulfillConnectPromise(bool wasActive)
            {
                TaskCompletionSource promise = this.Channel.connectPromise;
                if (promise == null)
                {
                    // Closed via cancellation and the promise has been notified already.
                    return;
                }

                // trySuccess() will return false if a user cancelled the connection attempt.
                bool promiseSet = promise.TryComplete();

                // Regardless if the connection attempt was cancelled, channelActive() event should be triggered,
                // because what happened is what happened.
                if (!wasActive && this.channel.Active)
                {
                    this.channel.Pipeline.FireChannelActive();
                }

                // If a user cancelled the connection attempt, close the channel, which is followed by channelInactive().
                if (!promiseSet)
                {
                    this.CloseSafe();
                }
            }

            void FulfillConnectPromise(Exception cause)
            {
                TaskCompletionSource promise = this.Channel.connectPromise;
                if (promise == null)
                {
                    // Closed via cancellation and the promise has been notified already.
                    return;
                }

                // Use tryFailure() instead of setFailure() to avoid the race against cancel().
                promise.TrySetException(cause);
                this.CloseIfClosed();
            }

            public void FinishConnect(SocketChannelAsyncOperation<TChannel, TUnsafe> operation)
            {
                Contract.Assert(this.channel.EventLoop.InEventLoop);

                var ch = this.Channel;
                try
                {
                    bool wasActive = ch.Active;
                    ch.DoFinishConnect(operation);
                    this.FulfillConnectPromise(wasActive);
                }
                catch (Exception ex)
                {
                    TaskCompletionSource promise = ch.connectPromise;
                    var remoteAddress = (EndPoint)promise?.Task.AsyncState;
                    this.FulfillConnectPromise(this.AnnotateConnectException(ex, remoteAddress));
                }
                finally
                {
                    // Check for null as the connectTimeoutFuture is only created if a connectTimeoutMillis > 0 is used
                    // See https://github.com/netty/netty/issues/1770
                    ch.connectCancellationTask?.Cancel();
                    ch.connectPromise = null;
                }
            }

            public abstract void FinishRead(SocketChannelAsyncOperation<TChannel, TUnsafe> operation);

            protected sealed override void Flush0()
            {
                // Flush immediately only when there's no pending flush.
                // If there's a pending flush operation, event loop will call FinishWrite() later,
                // and thus there's no need to call it now.
                if (this.IsFlushPending())
                {
                    return;
                }
                base.Flush0();
            }

            public void FinishWrite(SocketChannelAsyncOperation<TChannel, TUnsafe> operation)
            {
                bool resetWritePending = this.Channel.TryResetState(StateFlags.WriteScheduled);

                Contract.Assert(resetWritePending);

                ChannelOutboundBuffer input = this.OutboundBuffer;
                try
                {
                    operation.Validate();
                    int sent = operation.BytesTransferred;
                    this.Channel.ResetWriteOperation();
                    if (sent > 0)
                    {
                        input.RemoveBytes(sent);
                    }
                }
                catch (Exception ex)
                {
                    Util.CompleteChannelCloseTaskSafely(this.channel, this.CloseAsync(new ClosedChannelException("Failed to write", ex), false));
                }

                // Double check if there's no pending flush
                // See https://github.com/Azure/DotNetty/issues/218
                this.Flush0(); // todo: does it make sense now that we've actually written out everything that was flushed previously? concurrent flush handling?
            }

            bool IsFlushPending() => this.Channel.IsInState(StateFlags.WriteScheduled);
        }

        protected override bool IsCompatible(IEventLoop eventLoop) => true;

        protected override void DoBeginRead()
        {
            if (this.inputShutdown)
            {
                return;
            }

            if (!this.Open)
            {
                return;
            }

            this.ReadPending = true;

            if (!this.IsInState(StateFlags.ReadScheduled))
            {
                this.state |= StateFlags.ReadScheduled;
                this.ScheduleSocketRead();
            }
        }

        protected abstract void ScheduleSocketRead();

        /// <summary>
        ///     Connect to the remote peer
        /// </summary>
        protected abstract bool DoConnect(EndPoint remoteAddress, EndPoint localAddress);

        /// <summary>
        ///     Finish the connect
        /// </summary>
        protected abstract void DoFinishConnect(SocketChannelAsyncOperation<TChannel, TUnsafe> operation);

        protected override void DoClose()
        {
            TaskCompletionSource promise = this.connectPromise;
            if (promise != null)
            {
                // Use TrySetException() instead of SetException() to avoid the race against cancellation due to timeout.
                promise.TrySetException(new ClosedChannelException());
                this.connectPromise = null;
            }

            IScheduledTask cancellationTask = this.connectCancellationTask;
            if (cancellationTask != null)
            {
                cancellationTask.Cancel();
                this.connectCancellationTask = null;
            }

            var readOp = this.readOperation;
            if (readOp != null)
            {
                readOp.Dispose();
                this.readOperation = null;
            }

            var writeOp = this.writeOperation;
            if (writeOp != null)
            {
                writeOp.Dispose();
                this.writeOperation = null;
            }
        }
    }
}