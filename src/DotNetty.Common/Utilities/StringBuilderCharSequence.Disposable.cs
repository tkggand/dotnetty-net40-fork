﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Common.Utilities
{
    partial class StringBuilderCharSequence
    {
        public virtual void Dispose() { this.builder = null; }
    }
}