// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using NeoWebView.Interop;
using NeoWebView.Interop.Generated;

namespace NeoWebView;

/// <summary>Owns the native UI dispatcher, windows, and standalone or embedded application lifetime.</summary>
public sealed class NeoApplication : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SafeAppHandle _handle;
    private readonly Dictionary<ulong, NeoWindow> _windows = [];
    private GCHandle _eventRoot;
    private GCHandle _logRoot;
    private NeoWindow? _mainWindow;
    private NeoApplicationShutdownMode _shutdownMode;
    private ExceptionDispatchInfo? _startupException;
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private NeoApplication(SafeAppHandle handle, NeoApplicationOptions options, GCHandle logRoot)
    {
        _handle = handle;
        _logRoot = logRoot;
        _shutdownMode = options.ShutdownMode;
        Dispatcher = new NeoDispatcher(this, Environment.CurrentManagedThreadId);
        RegisterEventCallback();
    }

    /// <summary>Releases the managed callback root before safe-handle finalization requests native teardown.</summary>
    ~NeoApplication()
    {
        UnregisterEventCallback();
        ReleaseLogCallback(canFree: false);
    }

    /// <summary>Runs a standalone application event loop on the current thread.</summary>
    /// <param name="options">Application options.</param>
    /// <param name="startup">Initialization invoked after the event loop becomes dispatchable.</param>
    /// <returns>The application exit code.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="NeoWebViewNativeLibraryException">The native library cannot be loaded or has an incompatible ABI.</exception>
    public static int Run(NeoApplicationOptions options, Func<NeoApplication, ValueTask> startup)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(startup);

        var application = Create(options, embedded: false);
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new NeoDispatcherSynchronizationContext(application.Dispatcher));
        var addedRef = false;
        try
        {
            application.Dispatcher.Post(() => application.BeginStartup(startup));
            application._handle.DangerousAddRef(ref addedRef);
            var exitCode = NativeMethods.neo_webview_app_run(application.NativeHandle);
            application.Dispatcher.MarkShutdown();
            application._startupException?.Throw();
            return exitCode;
        }
        finally
        {
            if (addedRef)
            {
                application._handle.DangerousRelease();
            }

            SynchronizationContext.SetSynchronizationContext(previousContext);
            application.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Attaches NeoWebView to the host event loop on the current UI thread.</summary>
    /// <param name="options">Application options.</param>
    /// <returns>The attached application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="NeoWebViewNativeLibraryException">The native library cannot be loaded or has an incompatible ABI.</exception>
    /// <remarks>The host must await <see cref="DisposeAsync"/> while continuing to pump this thread's UI loop.</remarks>
    public static NeoApplication AttachToCurrentThread(NeoApplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Create(options, embedded: true);
    }

    /// <summary>Gets the UI-thread dispatcher.</summary>
    public NeoDispatcher Dispatcher { get; }

    /// <summary>Gets a snapshot of application-owned open windows.</summary>
    public IReadOnlyCollection<NeoWindow> Windows
    {
        get
        {
            lock (_sync)
            {
                return Array.AsReadOnly(_windows.Values.ToArray());
            }
        }
    }

    /// <summary>Gets or sets the window used by <see cref="NeoApplicationShutdownMode.OnMainWindowClosed"/>.</summary>
    /// <exception cref="ArgumentException">The assigned window belongs to another application.</exception>
    public NeoWindow? MainWindow
    {
        get
        {
            lock (_sync) { return _mainWindow; }
        }
        set
        {
            if (value is not null && !ReferenceEquals(value.Application, this))
            {
                throw new ArgumentException("The main window must belong to this application.", nameof(value));
            }

            lock (_sync) { _mainWindow = value; }
        }
    }

    /// <summary>Gets or sets the current managed shutdown policy.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public NeoApplicationShutdownMode ShutdownMode
    {
        get
        {
            lock (_sync) { return _shutdownMode; }
        }
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            lock (_sync) { _shutdownMode = value; }
        }
    }

    /// <summary>Creates a browser environment asynchronously.</summary>
    /// <param name="options">Environment options, or <see langword="null"/> for defaults.</param>
    /// <param name="cancellationToken">Cancels the managed wait and requests native cancellation.</param>
    /// <returns>The created environment.</returns>
    /// <exception cref="PlatformNotSupportedException">Custom schemes were supplied on a backend that does not implement them.</exception>
    public unsafe ValueTask<NeoEnvironment> CreateEnvironmentAsync(NeoEnvironmentOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new NeoEnvironmentOptions();
        options.Validate();
        using var userDataRoot = new Utf8String(options.UserDataRoot);
        using var runtimePath = new Utf8String(options.BrowserRuntimePath);
        using var arguments = new Utf8String(options.BrowserArguments);
        using var languages = new Utf8String(string.Join(',', options.PreferredLanguages));
        using var customSchemes = new CustomSchemeMarshaller(options.CustomSchemes);
        var rawOptions = new NativeMethods.neo_webview_environment_options
        {
            size = (uint)sizeof(NativeMethods.neo_webview_environment_options),
            version = 1,
            user_data_root = userDataRoot.View,
            browser_runtime_path = runtimePath.View,
            browser_arguments = arguments.View,
            preferred_languages = languages.View,
            private_mode = options.IsPrivate ? 1u : 0u,
            custom_scheme_count = customSchemes.Count,
            custom_schemes = customSchemes.Schemes,
            custom_scheme_stride = customSchemes.Stride,
        };
        var nativeOptions = new NativeMethods.neo_webview_environment_options_t(rawOptions);
        var operation = new NativeOperation<NeoEnvironment>(cancellationToken, this);
        NativeMethods.neo_webview_operation_t nativeOperation = default;
        NativeMethods.neo_webview_error_t error = default;
        NativeMethods.neo_webview_result_t result;
        try
        {
            result = NativeMethods.neo_webview_environment_create_async(
                NativeHandle,
                &nativeOptions,
                (delegate* unmanaged[Cdecl]<void*, NativeMethods.neo_webview_result_t, NativeMethods.neo_webview_environment_t, NativeMethods.neo_webview_error_t, void>)&EnvironmentCreated,
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
            operation.FailStart(CreateOwnedError(result, error, "create environment", cancellationToken));
            return operation.ValueTask;
        }

        _ = customSchemes.TakeRegistrations();
        operation.AttachOperation(nativeOperation.Handle);
        return operation.ValueTask;
    }

    /// <summary>Creates and registers a top-level native window.</summary>
    /// <param name="options">Window options, or <see langword="null"/> for defaults.</param>
    /// <returns>The created window.</returns>
    public unsafe NeoWindow CreateWindow(NeoWindowOptions? options = null)
    {
        ThrowIfDisposed();
        options ??= new NeoWindowOptions();
        options.Validate(this);

        using var title = new Utf8String(options.Title);
        var flags = (options.IsResizable ? 1u : 0u) |
                    (options.HasDecorations ? 2u : 0u) |
                    (options.IsVisible ? 4u : 0u) |
                    (options.IsAlwaysOnTop ? 8u : 0u) |
                    (options.ShowInTaskbar ? 16u : 0u);
        var raw = new NativeMethods.neo_webview_window_options
        {
            size = (uint)sizeof(NativeMethods.neo_webview_window_options),
            version = 1,
            title = title.View,
            bounds = new NativeMethods.neo_webview_rect
            {
                x = options.X,
                y = options.Y,
                width = options.Width,
                height = options.Height,
            },
            minimum_size = new NativeMethods.neo_webview_size { width = options.MinimumClientSize.Width, height = options.MinimumClientSize.Height },
            maximum_size = new NativeMethods.neo_webview_size { width = options.MaximumClientSize.Width, height = options.MaximumClientSize.Height },
            owner = options.Owner is null ? default : options.Owner.NativeHandle,
            state = (NativeMethods.neo_webview_window_state)options.State,
            flags = flags,
            background_color = new NativeMethods.neo_webview_color
            {
                red = options.BackgroundColor.Red,
                green = options.BackgroundColor.Green,
                blue = options.BackgroundColor.Blue,
                alpha = options.BackgroundColor.Alpha,
            },
        };
        var nativeOptions = new NativeMethods.neo_webview_window_options_t(raw);
        NativeMethods.neo_webview_window_t nativeWindow = default;
        NativeMethods.neo_webview_error_t error = default;
        var result = NativeMethods.neo_webview_app_create_window(NativeHandle, &nativeOptions, &nativeWindow, &error);
        NativeError.ThrowIfFailed(result, error, "create window");
        if (nativeWindow.Handle == 0)
        {
            throw new NeoWebViewException(NeoErrorCode.NativeFailure, "The native backend returned a null window.", "create window");
        }

        var window = new NeoWindow(this, new SafeWindowHandle(nativeWindow.Handle), options);
        lock (_sync)
        {
            _windows.Add(window.Id, window);
            _mainWindow ??= window;
        }

        return window;
    }

    /// <summary>Requests application shutdown. This method may be called from any thread.</summary>
    /// <param name="exitCode">The process-style exit code returned by <see cref="Run"/>.</param>
    /// <remarks>Safe to call concurrently with <see cref="DisposeAsync"/>; it is a no-op after disposal starts.</remarks>
    public void Shutdown(int exitCode = 0)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var addedRef = false;
        try
        {
            try
            {
                _handle.DangerousAddRef(ref addedRef);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Dispatcher.MarkShutdown();
            NativeMethods.neo_webview_app_quit(new NativeMethods.neo_webview_app_t(_handle.DangerousGetHandle()), exitCode);
        }
        finally
        {
            if (addedRef)
            {
                _handle.DangerousRelease();
            }
        }
    }

    /// <summary>Detaches the native application on its owning UI thread and releases its native reference.</summary>
    /// <returns>A task completed after native UI and platform teardown is acknowledged.</returns>
    /// <exception cref="NeoWebViewException">Native application detach fails.</exception>
    /// <remarks>
    /// For an application created by <see cref="AttachToCurrentThread"/>, the host must continue pumping its UI loop
    /// until this task completes. Finalization can only request teardown and cannot complete it after pumping stops.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return new ValueTask(_disposeCompletion.Task);
        }

        if (Dispatcher.CheckAccess())
        {
            CompleteDisposeOnCurrentThread();
        }
        else
        {
            _ = DisposeOnDispatcherAsync();
        }

        return new ValueTask(_disposeCompletion.Task);
    }

    internal NativeMethods.neo_webview_app_t DangerousNativeHandle => new(_handle.DangerousGetHandle());

    private async Task DisposeOnDispatcherAsync()
    {
        try
        {
            await Dispatcher.InvokeShutdownAsync(DisposeCore);
            _disposeCompletion.TrySetResult();
        }
        catch (Exception ex)
        {
            ReleaseWithoutDetach();
            _disposeCompletion.TrySetException(ex);
        }
    }

    private void CompleteDisposeOnCurrentThread()
    {
        try
        {
            DisposeCore();
            _disposeCompletion.TrySetResult();
        }
        catch (Exception ex)
        {
            ReleaseWithoutDetach();
            _disposeCompletion.TrySetException(ex);
        }
    }

    private unsafe void DisposeCore()
    {
        Dispatcher.MarkShutdown();
        UnregisterEventCallback();
        DisposeManagedWindows();

        NativeMethods.neo_webview_error_t error = default;
        var result = NativeMethods.neo_webview_app_detach(DangerousNativeHandle, &error);
        NativeError.ThrowIfFailed(result, error, "detach application");
        ReleaseLogCallback(canFree: true);
        _handle.Dispose();
        GC.SuppressFinalize(this);
    }

    private void DisposeManagedWindows()
    {
        NeoWindow[] windows;
        lock (_sync)
        {
            windows = _windows.Values.ToArray();
            _windows.Clear();
            _mainWindow = null;
        }

        foreach (var window in windows)
        {
            window.DisposeFromApplication();
        }
    }

    private void ReleaseWithoutDetach()
    {
        Dispatcher.MarkShutdown();
        UnregisterEventCallback();
        ReleaseLogCallback(canFree: false);
        DisposeManagedWindows();
        _handle.Dispose();
        GC.SuppressFinalize(this);
    }

    private unsafe void UnregisterEventCallback()
    {
        try
        {
            NativeMethods.neo_webview_app_set_event_callback(DangerousNativeHandle, default, null);
        }
        catch
        {
            // SafeHandle finalization may run after native loading or shutdown failed.
        }

        if (_eventRoot.IsAllocated)
        {
            _eventRoot.Free();
        }
    }

    private void ReleaseLogCallback(bool canFree)
    {
        if (!_logRoot.IsAllocated)
        {
            return;
        }

        (_logRoot.Target as LogCallbackRegistration)?.Clear();
        if (canFree)
        {
            _logRoot.Free();
        }

        // When detach cannot be acknowledged, retain the small registration root so a
        // late native callback can safely observe the cleared registration.
        _logRoot = default;
    }

    internal NativeMethods.neo_webview_app_t NativeHandle
    {
        get
        {
            ThrowIfDisposed();
            return new(_handle.DangerousGetHandle());
        }
    }

    internal void OnManagedWindowDisposed(NeoWindow window) => OnWindowClosed(window);

    private static unsafe NeoApplication Create(NeoApplicationOptions options, bool embedded)
    {
        options.Validate();
        NativeLibraryLoader.EnsureLoaded();
        using var name = new Utf8String(options.ApplicationName);
        var logRoot = options.LogCallback is null
            ? default
            : GCHandle.Alloc(new LogCallbackRegistration(options.LogCallback));
        var raw = new NativeMethods.neo_webview_app_options
        {
            size = (uint)sizeof(NativeMethods.neo_webview_app_options),
            version = 1,
            application_name = name.View,
            // Managed code implements the mutable shutdown modes from application events.
            shutdown_mode = NativeMethods.neo_webview_app_shutdown_mode.NEO_WEBVIEW_APP_SHUTDOWN_EXPLICIT,
            maximum_pending_dispatches = options.MaximumPendingDispatches,
            log_callback = options.LogCallback is null
                ? default
                : (delegate* unmanaged[Cdecl]<void*, NativeMethods.neo_webview_log_level_t, NativeMethods.neo_webview_string_view_t, NativeMethods.neo_webview_string_view_t, ulong, ulong, long, ulong, void>)&NativeLog,
            log_context = logRoot.IsAllocated ? (void*)GCHandle.ToIntPtr(logRoot) : null,
        };
        var nativeOptions = new NativeMethods.neo_webview_app_options_t(raw);
        NativeMethods.neo_webview_app_t app = default;
        NativeMethods.neo_webview_error_t error = default;
        try
        {
            var result = embedded
                ? NativeMethods.neo_webview_app_attach(&nativeOptions, &app, &error)
                : NativeMethods.neo_webview_app_create(&nativeOptions, &app, &error);
            NativeError.ThrowIfFailed(result, error, embedded ? "attach application" : "create application");
            if (app.Handle == 0)
            {
                throw new NeoWebViewException(NeoErrorCode.NativeFailure, "The native backend returned a null application.");
            }

            return new NeoApplication(new SafeAppHandle(app.Handle), options, logRoot);
        }
        catch
        {
            if (logRoot.IsAllocated)
            {
                (logRoot.Target as LogCallbackRegistration)?.Clear();
                if (app.Handle == 0)
                {
                    logRoot.Free();
                }

                // Once native creation succeeds, the partially constructed application or
                // native final-release path may still observe this context. Keep it rooted
                // when teardown was not acknowledged rather than risking a late callback.
            }

            throw;
        }
    }

    private unsafe void RegisterEventCallback()
    {
        _eventRoot = GCHandle.Alloc(this, GCHandleType.Weak);
        var result = NativeMethods.neo_webview_app_set_event_callback(
            new(_handle.DangerousGetHandle()),
            (delegate* unmanaged[Cdecl]<void*, NativeMethods.neo_webview_event_t*, void>)&ApplicationEvent,
            (void*)GCHandle.ToIntPtr(_eventRoot));
        if (NativeError.Code(result) != NeoErrorCode.Success)
        {
            _eventRoot.Free();
            _handle.Dispose();
            NativeError.ThrowIfFailed(result, default, "register application events");
        }
    }

    private void BeginStartup(Func<NeoApplication, ValueTask> startup)
    {
        _ = RunStartupAsync(startup);
    }

    private async Task RunStartupAsync(Func<NeoApplication, ValueTask> startup)
    {
        try
        {
            await startup(this);
        }
        catch (Exception ex)
        {
            _startupException = ExceptionDispatchInfo.Capture(ex);
            Shutdown(-1);
        }
    }

    private void DispatchEvent(NativeMethods.neo_webview_event value)
    {
        NeoWindow? window;
        lock (_sync)
        {
            _windows.TryGetValue(value.object_id, out window);
        }

        if (window is null)
        {
            return;
        }

        var type = value.header.Value.type.Value;
        switch (type)
        {
            case NativeMethods.neo_webview_event_type.NEO_WEBVIEW_EVENT_WINDOW_CLOSE_REQUESTED:
                window.OnClosing();
                break;
            case NativeMethods.neo_webview_event_type.NEO_WEBVIEW_EVENT_WINDOW_CLOSED:
                OnWindowClosed(window);
                break;
            case NativeMethods.neo_webview_event_type.NEO_WEBVIEW_EVENT_WINDOW_MOVED:
            case NativeMethods.neo_webview_event_type.NEO_WEBVIEW_EVENT_WINDOW_RESIZED:
                window.OnBoundsChanged();
                break;
            case NativeMethods.neo_webview_event_type.NEO_WEBVIEW_EVENT_WINDOW_FOCUS_CHANGED:
                window.OnFocusChanged(value.value != 0);
                break;
            case NativeMethods.neo_webview_event_type.NEO_WEBVIEW_EVENT_WINDOW_SCALE_FACTOR_CHANGED:
                window.OnScaleFactorChanged(value.value == 0 ? 1d : value.value / 1000d);
                break;
            case NativeMethods.neo_webview_event_type.NEO_WEBVIEW_EVENT_WINDOW_STATE_CHANGED:
                window.OnStateChanged((NeoWindowState)value.value);
                break;
        }
    }

    private void OnWindowClosed(NeoWindow window)
    {
        if (!window.OnClosed())
        {
            return;
        }

        NeoApplicationShutdownMode mode;
        NeoWindow? main;
        var noWindows = false;
        lock (_sync)
        {
            _windows.Remove(window.Id);
            mode = _shutdownMode;
            main = _mainWindow;
            noWindows = _windows.Count == 0;
        }

        if (mode == NeoApplicationShutdownMode.OnLastWindowClosed && noWindows ||
            mode == NeoApplicationShutdownMode.OnMainWindowClosed && ReferenceEquals(main, window))
        {
            Shutdown();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private static Exception CreateOwnedError(NativeMethods.neo_webview_result_t result, NativeMethods.neo_webview_error_t error, string operation, CancellationToken cancellationToken)
    {
        NativeErrorInfo info;
        try
        {
            info = NativeError.Read(NativeError.Code(result), error.Handle);
        }
        finally
        {
            if (error.Handle != 0)
            {
                new SafeErrorHandle(error.Handle).Dispose();
            }
        }

        return NativeError.CreateException(info, operation, cancellationToken);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void ApplicationEvent(void* context, NativeMethods.neo_webview_event_t* nativeEvent)
    {
        try
        {
            if (nativeEvent is null)
            {
                return;
            }

            var root = GCHandle.FromIntPtr((nint)context);
            (root.Target as NeoApplication)?.DispatchEvent(nativeEvent->Value);
        }
        catch
        {
            // User handlers and malformed native events are contained at the ABI boundary.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void NativeLog(
        void* context,
        NativeMethods.neo_webview_log_level_t level,
        NativeMethods.neo_webview_string_view_t category,
        NativeMethods.neo_webview_string_view_t message,
        ulong threadId,
        ulong timestampNanoseconds,
        long nativeCode,
        ulong objectId)
    {
        try
        {
            var root = GCHandle.FromIntPtr((nint)context);
            (root.Target as LogCallbackRegistration)?.Invoke(new NeoLogMessage(
                (NeoLogLevel)level.Value,
                Utf8String.Decode(category),
                Utf8String.Decode(message),
                threadId,
                timestampNanoseconds,
                nativeCode,
                objectId));
        }
        catch
        {
            // User logging and malformed native strings are contained at the ABI boundary.
        }
    }

    private sealed class LogCallbackRegistration(Action<NeoLogMessage> callback)
    {
        private Action<NeoLogMessage>? _callback = callback;

        internal void Invoke(NeoLogMessage message) => Volatile.Read(ref _callback)?.Invoke(message);

        internal void Clear() => Interlocked.Exchange(ref _callback, null);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void EnvironmentCreated(
        void* context,
        NativeMethods.neo_webview_result_t result,
        NativeMethods.neo_webview_environment_t environment,
        NativeMethods.neo_webview_error_t error)
    {
        try
        {
            var operation = NativeOperation.Get<NeoEnvironment>(context);
            if (operation is null)
            {
                if (environment.Handle != 0) new SafeEnvironmentHandle(environment.Handle).Dispose();
                return;
            }

            if (NativeError.Code(result) == NeoErrorCode.Success && environment.Handle != 0)
            {
                operation.Complete(new NeoEnvironment((NeoApplication)operation.Owner!, new SafeEnvironmentHandle(environment.Handle)));
            }
            else
            {
                if (environment.Handle != 0) new SafeEnvironmentHandle(environment.Handle).Dispose();
                operation.Fail(NativeError.CreateException(NativeError.Read(NativeError.Code(result), error.Handle), "create environment"));
            }
        }
        catch (Exception ex)
        {
            NativeOperation.Get<NeoEnvironment>(context)?.Fail(ex);
        }
    }
}
