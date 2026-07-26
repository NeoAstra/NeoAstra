// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using Microsoft.Win32.SafeHandles;
using NeoAstra.Interop.Generated;

namespace NeoAstra.Interop;

internal abstract class NeoSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    protected NeoSafeHandle()
        : base(true)
    {
    }

    protected NeoSafeHandle(nint value)
        : base(true)
    {
        SetHandle(value);
    }
}

internal sealed class SafeAppHandle(nint value) : NeoSafeHandle(value)
{
    protected override bool ReleaseHandle()
    {
        try { NativeMethods.neoastra_app_release(new(handle)); } catch { }
        return true;
    }
}

internal sealed class SafeEnvironmentHandle(nint value) : NeoSafeHandle(value)
{
    protected override bool ReleaseHandle()
    {
        try { NativeMethods.neoastra_environment_release(new(handle)); } catch { }
        return true;
    }
}

internal sealed class SafeProfileHandle(nint value) : NeoSafeHandle(value)
{
    protected override bool ReleaseHandle()
    {
        try { NativeMethods.neoastra_profile_release(new(handle)); } catch { }
        return true;
    }
}

internal sealed class SafeWindowHandle(nint value) : NeoSafeHandle(value)
{
    protected override bool ReleaseHandle()
    {
        try { NativeMethods.neoastra_window_release(new(handle)); } catch { }
        return true;
    }
}

internal sealed class SafeViewHandle(nint value) : NeoSafeHandle(value)
{
    protected override bool ReleaseHandle()
    {
        try { NativeMethods.neoastra_view_release(new(handle)); } catch { }
        return true;
    }
}

internal sealed class SafeOperationHandle(nint value) : NeoSafeHandle(value)
{
    internal void Cancel()
    {
        if (!IsInvalid && !IsClosed)
        {
            NativeMethods.neoastra_operation_cancel(new(handle));
        }
    }

    protected override bool ReleaseHandle()
    {
        try { NativeMethods.neoastra_operation_release(new(handle)); } catch { }
        return true;
    }
}

internal sealed class SafeDecisionHandle(nint value) : NeoSafeHandle(value)
{
    protected override bool ReleaseHandle()
    {
        try { NativeMethods.neoastra_decision_release(new(handle)); } catch { }
        return true;
    }
}

internal sealed class SafeDownloadHandle(nint value) : NeoSafeHandle(value)
{
    protected override bool ReleaseHandle()
    {
        try { NativeMethods.neoastra_download_release(new(handle)); } catch { }
        return true;
    }
}

internal sealed class SafeErrorHandle(nint value) : NeoSafeHandle(value)
{
    protected override bool ReleaseHandle()
    {
        try { NativeMethods.neoastra_error_release(new(handle)); } catch { }
        return true;
    }
}

internal sealed class SafeBufferHandle(nint value) : NeoSafeHandle(value)
{
    protected override bool ReleaseHandle()
    {
        try { NativeMethods.neoastra_buffer_release(new(handle)); } catch { }
        return true;
    }
}
