// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using Microsoft.Win32.SafeHandles;
using NeoWebView.Interop.Generated;

namespace NeoWebView.Interop;

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
        try { NativeMethods.neo_webview_app_release(new(handle)); } catch { }
        return true;
    }
}

internal sealed class SafeEnvironmentHandle(nint value) : NeoSafeHandle(value)
{
    protected override bool ReleaseHandle()
    {
        try { NativeMethods.neo_webview_environment_release(new(handle)); } catch { }
        return true;
    }
}

internal sealed class SafeProfileHandle(nint value) : NeoSafeHandle(value)
{
    protected override bool ReleaseHandle()
    {
        try { NativeMethods.neo_webview_profile_release(new(handle)); } catch { }
        return true;
    }
}

internal sealed class SafeWindowHandle(nint value) : NeoSafeHandle(value)
{
    protected override bool ReleaseHandle()
    {
        try { NativeMethods.neo_webview_window_release(new(handle)); } catch { }
        return true;
    }
}

internal sealed class SafeViewHandle(nint value) : NeoSafeHandle(value)
{
    protected override bool ReleaseHandle()
    {
        try { NativeMethods.neo_webview_view_release(new(handle)); } catch { }
        return true;
    }
}

internal sealed class SafeOperationHandle(nint value) : NeoSafeHandle(value)
{
    internal void Cancel()
    {
        if (!IsInvalid && !IsClosed)
        {
            NativeMethods.neo_webview_operation_cancel(new(handle));
        }
    }

    protected override bool ReleaseHandle()
    {
        try { NativeMethods.neo_webview_operation_release(new(handle)); } catch { }
        return true;
    }
}

internal sealed class SafeDecisionHandle(nint value) : NeoSafeHandle(value)
{
    protected override bool ReleaseHandle()
    {
        try { NativeMethods.neo_webview_decision_release(new(handle)); } catch { }
        return true;
    }
}

internal sealed class SafeErrorHandle(nint value) : NeoSafeHandle(value)
{
    protected override bool ReleaseHandle()
    {
        try { NativeMethods.neo_webview_error_release(new(handle)); } catch { }
        return true;
    }
}

internal sealed class SafeBufferHandle(nint value) : NeoSafeHandle(value)
{
    protected override bool ReleaseHandle()
    {
        try { NativeMethods.neo_webview_buffer_release(new(handle)); } catch { }
        return true;
    }
}
