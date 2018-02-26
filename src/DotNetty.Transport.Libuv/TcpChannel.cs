﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// ReSharper disable ConvertToAutoPropertyWithPrivateSetter
namespace DotNetty.Transport.Libuv
{
    using System;
    using System.Net;
    using DotNetty.Transport.Channels;
    using DotNetty.Transport.Channels.Sockets;
    using DotNetty.Transport.Libuv.Native;

    public class TcpChannel<TChannel> : NativeChannel<TChannel, TcpChannel<TChannel>.TcpChannelUnsafe>, ISocketChannel
        where TChannel : TcpChannel<TChannel>
    {
        static readonly ChannelMetadata TcpMetadata = new ChannelMetadata(false);

        static readonly Action<object> FlushAction = c => ((INativeChannel)c).Flush();

        readonly TcpChannelConfig config;
        Tcp tcp;
        bool isBound;

        public TcpChannel() : this(null, null)
        {
        }

        internal TcpChannel(IChannel parent, Tcp tcp) : base(parent)
        {
            this.config = new TcpChannelConfig(this);
            this.SetState(StateFlags.Open);
            this.tcp = tcp;
        }

        public override IChannelConfiguration Configuration => this.config;

        public override ChannelMetadata Metadata => TcpMetadata;

        protected override EndPoint LocalAddressInternal => this.tcp?.GetLocalEndPoint();

        protected override EndPoint RemoteAddressInternal => this.tcp?.GetPeerEndPoint();

        //protected override IChannelUnsafe NewUnsafe() => new TcpChannelUnsafe(this); ## 苦竹 屏蔽 ##

        protected override void DoRegister()
        {
            if (this.tcp == null)
            {
                var loopExecutor = (LoopExecutor)this.EventLoop;
                this.tcp = new Tcp(loopExecutor.UnsafeLoop);
            }
            else
            {
                this.OnConnected();
            }
        }

        internal override NativeHandle GetHandle()
        {
            if (this.tcp == null)
            {
                throw new InvalidOperationException("Tcp handle not intialized");
            }
            return this.tcp;
        }

        protected override void DoBind(EndPoint localAddress)
        {
            this.tcp.Bind((IPEndPoint)localAddress);
            this.config.Apply();
            this.isBound = true;
            this.CacheLocalAddress();
        }

        internal override bool IsBound => this.isBound;

        protected override void OnConnected()
        {
            if (!this.isBound)
            {
                // Either channel is created by tcp server channel
                // or connect to remote without bind first
                this.config.Apply();
                this.isBound = true;
            }

            base.OnConnected();
        }

        protected override void DoDisconnect() => this.DoClose();

        protected override void DoClose()
        {
            try
            {
                if (this.TryResetState(StateFlags.Open | StateFlags.Active))
                {
                    if (this.tcp != null)
                    {
                        this.tcp.ReadStop();
                        this.tcp.CloseHandle();
                    }
                    this.tcp = null;
                }
            }
            finally
            {
                base.DoClose();
            }
        }

        protected override void DoBeginRead()
        {
            if (!this.Open)
            {
                return;
            }

            this.ReadPending = true;
            if (!this.IsInState(StateFlags.ReadScheduled))
            {
                this.SetState(StateFlags.ReadScheduled);
                this.tcp.ReadStart((TcpChannelUnsafe)this.Unsafe);
            }
        }

        protected override void DoStopRead()
        {
            if (!this.Open)
            {
                return;
            }

            if (this.IsInState(StateFlags.ReadScheduled))
            {
                this.ResetState(StateFlags.ReadScheduled);
                this.tcp.ReadStop();
            }
        }

        protected override void DoWrite(ChannelOutboundBuffer input)
        {
            int writeSpinCount = this.config.WriteSpinCount;
            var loopExecutor = (LoopExecutor)this.EventLoop;

            long writtenBytes = 0;
            int inputCount = input.Size;
            do
            {
                if (inputCount == 0)
                {
                    break;
                }

                WriteRequest request = loopExecutor.WriteRequestPool.Take();
                int bytes = request.Prepare((TcpChannelUnsafe)this.Unsafe, input);
                int flushed = request.DoWrite();

                writtenBytes += bytes;
                inputCount -= flushed;
                writeSpinCount--;
            }
            while (writeSpinCount > 0);
            input.RemoveBytes(writtenBytes, false);

            if (inputCount > 0)
            {
                loopExecutor.Execute(FlushAction, this);
            }
        }

        public sealed class TcpChannelUnsafe : NativeChannelUnsafe
        {
            public TcpChannelUnsafe() : base() //TcpChannel channel) : base(channel)
            {
            }

            public override IntPtr UnsafeHandle => this.channel.tcp.Handle;
        }
    }
}
