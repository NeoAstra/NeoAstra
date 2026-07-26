// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NeoAstra.Interop;
using NeoAstra.Interop.Generated;

namespace NeoAstra;

/// <summary>Represents browser-process configuration and runtime scope.</summary>
public sealed unsafe class NeoEnvironment : IAsyncDisposable
{
    private readonly SafeEnvironmentHandle _handle;
    private int _disposed;

    internal NeoEnvironment(NeoApplication application, SafeEnvironmentHandle handle)
    {
        Application = application;
        _handle = handle;
        RuntimeInfo = ReadRuntimeInfo();
    }

    /// <summary>Gets information about the active native browser backend.</summary>
    public NeoRuntimeInfo RuntimeInfo { get; }

    /// <summary>Creates a browser profile asynchronously.</summary>
    /// <param name="options">Profile options, or <see langword="null"/> for defaults.</param>
    /// <param name="cancellationToken">Cancels the managed wait and requests native cancellation.</param>
    /// <returns>The created profile.</returns>
    public ValueTask<NeoProfile> CreateProfileAsync(NeoProfileOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new NeoProfileOptions();
        options.Validate();

        using var name = new Utf8String(options.Name);
        var raw = new NativeMethods.neoastra_profile_options
        {
            size = (uint)sizeof(NativeMethods.neoastra_profile_options),
            version = 1,
            name = name.View,
            ephemeral = options.IsEphemeral ? 1u : 0u,
        };
        var nativeOptions = new NativeMethods.neoastra_profile_options_t(raw);
        var operation = new NativeOperation<NeoProfile>(cancellationToken, new ProfileCreation(this, options.IsEphemeral));
        NativeMethods.neoastra_operation_t nativeOperation = default;
        NativeMethods.neoastra_error_t error = default;
        NativeMethods.neoastra_result_t result;
        try
        {
            result = NativeMethods.neoastra_environment_create_profile_async(
                NativeHandle,
                &nativeOptions,
                (delegate* unmanaged[Cdecl]<void*, NativeMethods.neoastra_result_t, NativeMethods.neoastra_profile_t, NativeMethods.neoastra_error_t, void>)&ProfileCreated,
                (void*)operation.Context,
                &nativeOperation,
                &error);
        }
        catch (Exception ex)
        {
            operation.FailStart(ex);
            return operation.ValueTask;
        }

        if (NativeError.Code(result) != NeoErrorCode.Success)
        {
            operation.FailStart(CreateOwnedError(result, error, "create profile", cancellationToken));
            return operation.ValueTask;
        }

        operation.AttachOperation(nativeOperation.Handle);
        return operation.ValueTask;
    }

    /// <summary>Creates a browser view asynchronously.</summary>
    /// <param name="host">The owned-window or borrowed-native host.</param>
    /// <param name="options">View options, or <see langword="null"/> for defaults.</param>
    /// <param name="cancellationToken">Cancels the managed wait and requests native cancellation.</param>
    /// <returns>The created browser view.</returns>
    /// <exception cref="PlatformNotSupportedException">Explicit bridge origins were supplied on Linux, which does not implement them.</exception>
    public ValueTask<NeoAstra> CreateWebViewAsync(NeoAstraHost host, NeoAstraOptions? options = null, CancellationToken cancellationToken = default)
        => CreateWebViewCoreAsync(host, options, 0, cancellationToken);

    internal ValueTask<NeoAstra> CreatePopupWebViewAsync(NeoAstraHost host, NeoAstraOptions? options, nint popupRequest, CancellationToken cancellationToken)
        => CreateWebViewCoreAsync(host, options, popupRequest, cancellationToken);

    private ValueTask<NeoAstra> CreateWebViewCoreAsync(NeoAstraHost host, NeoAstraOptions? options, nint popupRequest, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(host);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new NeoAstraOptions();
        options.Validate(this);
        if (host.Window is not null && !ReferenceEquals(host.Window.Application, Application))
        {
            throw new ArgumentException("The host window must belong to this environment's application.", nameof(host));
        }

        if (options.BridgeOrigins.Count != 0 && !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Explicit bridge origins are not supported by the Linux backend.");
        }

        using var bridgeOrigins = new Utf8StringArray(options.BridgeOrigins);
        var parent = new NativeMethods.neoastra_native_parent
        {
            size = (uint)sizeof(NativeMethods.neoastra_native_parent),
            version = 1,
        };
        if (host.Parent is { } nativeParent)
        {
            parent.kind = nativeParent.Kind switch
            {
                NeoNativeHandleKind.Win32Hwnd => NativeMethods.neoastra_native_parent_kind.NEOASTRA_NATIVE_PARENT_WIN32_HWND,
                NeoNativeHandleKind.CocoaNSView => NativeMethods.neoastra_native_parent_kind.NEOASTRA_NATIVE_PARENT_COCOA_NSVIEW,
                NeoNativeHandleKind.GtkWidget => NativeMethods.neoastra_native_parent_kind.NEOASTRA_NATIVE_PARENT_GTK_WIDGET,
                _ => NativeMethods.neoastra_native_parent_kind.NEOASTRA_NATIVE_PARENT_NONE,
            };
            parent.handle = (void*)nativeParent.Value;
        }

        var raw = new NativeMethods.neoastra_view_options
        {
            size = (uint)sizeof(NativeMethods.neoastra_view_options),
            version = 1,
            profile = options.Profile is null ? default : options.Profile.NativeHandle,
            parent = parent,
            window = host.Window is null ? default : host.Window.NativeHandle,
            bounds = new NativeMethods.neoastra_rect
            {
                x = options.Bounds.X,
                y = options.Bounds.Y,
                width = options.Bounds.Width,
                height = options.Bounds.Height,
            },
            fill_parent = host.Window is not null || options.FillParent ? 1u : 0u,
            maximum_message_size = options.MaximumMessageSize,
            decision_timeout_ms = checked((ulong)options.DecisionTimeout.TotalMilliseconds),
            popup_request = new NativeMethods.neoastra_decision_t(popupRequest),
            bridge_origin_count = bridgeOrigins.Count,
            bridge_policy = (NativeMethods.neoastra_bridge_policy)options.BridgePolicy,
            bridge_origins = bridgeOrigins.Views,
        };
        var nativeOptions = new NativeMethods.neoastra_view_options_t(raw);
        var operation = new NativeOperation<NeoAstra>(cancellationToken, new ViewCreation(this, host, options));
        NativeMethods.neoastra_operation_t nativeOperation = default;
        NativeMethods.neoastra_error_t error = default;
        NativeMethods.neoastra_result_t result;
        try
        {
            result = NativeMethods.neoastra_environment_create_view_async(
                NativeHandle,
                &nativeOptions,
                (delegate* unmanaged[Cdecl]<void*, NativeMethods.neoastra_result_t, NativeMethods.neoastra_view_t, NativeMethods.neoastra_error_t, void>)&ViewCreated,
                (void*)operation.Context,
                &nativeOperation,
                &error);
        }
        catch (Exception ex)
        {
            operation.FailStart(ex);
            return operation.ValueTask;
        }

        if (NativeError.Code(result) != NeoErrorCode.Success)
        {
            operation.FailStart(CreateOwnedError(result, error, "create web view", cancellationToken));
            return operation.ValueTask;
        }

        operation.AttachOperation(nativeOperation.Handle);
        return operation.ValueTask;
    }

    /// <summary>Queries support for a portable capability.</summary>
    /// <param name="capability">The capability to query.</param>
    /// <returns>Support information from the active backend.</returns>
    public NeoCapabilityInfo GetCapability(NeoCapability capability)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(capability))
        {
            throw new ArgumentOutOfRangeException(nameof(capability));
        }

        var raw = new NativeMethods.neoastra_capability_info
        {
            size = (uint)sizeof(NativeMethods.neoastra_capability_info),
            version = 1,
        };
        var native = new NativeMethods.neoastra_capability_info_t(raw);
        var result = NativeMethods.neoastra_environment_get_capability(NativeHandle, (NativeMethods.neoastra_capability)capability, &native);
        NativeError.ThrowIfFailed(result, default, "query capability");
        raw = native.Value;
        var details = Utf8String.Decode(raw.details);
        return new NeoCapabilityInfo((NeoSupportLevel)raw.support.Value, raw.capability_version, raw.flags, string.IsNullOrEmpty(details) ? null : details);
    }

    /// <summary>Releases the native environment reference.</summary>
    /// <returns>A completed value task.</returns>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _handle.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    internal NeoApplication Application { get; }

    internal NativeMethods.neoastra_environment_t NativeHandle
    {
        get
        {
            ThrowIfDisposed();
            return new(_handle.DangerousGetHandle());
        }
    }

    private static NeoRuntimeInfo ReadRuntimeInfo()
    {
        var raw = new NativeMethods.neoastra_runtime_info
        {
            size = (uint)sizeof(NativeMethods.neoastra_runtime_info),
            version = 1,
        };
        var native = new NativeMethods.neoastra_runtime_info_t(raw);
        NativeMethods.neoastra_error_t error = default;
        var result = NativeMethods.neoastra_get_runtime_info(&native, &error);
        NativeError.ThrowIfFailed(result, error, "get runtime information");
        raw = native.Value;
        return new NeoRuntimeInfo(
            Utf8String.Decode(raw.backend_name),
            Utf8String.Decode(raw.backend_version),
            Utf8String.Decode(raw.browser_version),
            Utf8String.Decode(raw.operating_system),
            Utf8String.Decode(raw.architecture),
            raw.build_features,
            raw.debug_build != 0);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static Exception CreateOwnedError(NativeMethods.neoastra_result_t result, NativeMethods.neoastra_error_t error, string operation, CancellationToken cancellationToken)
    {
        var info = NativeError.Read(NativeError.Code(result), error.Handle);
        if (error.Handle != 0) new SafeErrorHandle(error.Handle).Dispose();
        return NativeError.CreateException(info, operation, cancellationToken);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ProfileCreated(void* context, NativeMethods.neoastra_result_t result, NativeMethods.neoastra_profile_t profile, NativeMethods.neoastra_error_t error)
    {
        try
        {
            var operation = NativeOperation.Get<NeoProfile>(context);
            if (operation is null)
            {
                if (profile.Handle != 0) new SafeProfileHandle(profile.Handle).Dispose();
                return;
            }

            if (NativeError.Code(result) == NeoErrorCode.Success && profile.Handle != 0)
            {
                var creation = (ProfileCreation)operation.Owner!;
                operation.Complete(new NeoProfile(creation.Environment, new SafeProfileHandle(profile.Handle), creation.IsEphemeral));
            }
            else
            {
                if (profile.Handle != 0) new SafeProfileHandle(profile.Handle).Dispose();
                operation.Fail(NativeError.CreateException(NativeError.Read(NativeError.Code(result), error.Handle), "create profile"));
            }
        }
        catch (Exception ex)
        {
            NativeOperation.Get<NeoProfile>(context)?.Fail(ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ViewCreated(void* context, NativeMethods.neoastra_result_t result, NativeMethods.neoastra_view_t view, NativeMethods.neoastra_error_t error)
    {
        try
        {
            var operation = NativeOperation.Get<NeoAstra>(context);
            if (operation is null)
            {
                if (view.Handle != 0) new SafeViewHandle(view.Handle).Dispose();
                return;
            }

            if (NativeError.Code(result) == NeoErrorCode.Success && view.Handle != 0)
            {
                var creation = (ViewCreation)operation.Owner!;
                operation.Complete(new NeoAstra(creation.Environment, new SafeViewHandle(view.Handle), creation.Host, creation.Options));
            }
            else
            {
                if (view.Handle != 0) new SafeViewHandle(view.Handle).Dispose();
                operation.Fail(NativeError.CreateException(NativeError.Read(NativeError.Code(result), error.Handle), "create web view"));
            }
        }
        catch (Exception ex)
        {
            NativeOperation.Get<NeoAstra>(context)?.Fail(ex);
        }
    }

    private sealed record ProfileCreation(NeoEnvironment Environment, bool IsEphemeral);
    private sealed record ViewCreation(NeoEnvironment Environment, NeoAstraHost Host, NeoAstraOptions Options);
}
