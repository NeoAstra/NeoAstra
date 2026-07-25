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
    private NeoWindow? _mainWindow;
    private NeoApplicationShutdownMode _shutdownMode;
    private ExceptionDispatchInfo? _startupException;
    private int _disposed;

    private NeoApplication(SafeAppHandle handle, NeoApplicationOptions options)
    {
        _handle = handle;
        _shutdownMode = options.ShutdownMode;
        Dispatcher = new NeoDispatcher(this, Environment.CurrentManagedThreadId);
        RegisterEventCallback();
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
    /// <exception cref="NotSupportedException">Custom schemes were supplied, but the native ABI does not define their descriptor layout.</exception>
    public unsafe ValueTask<NeoEnvironment> CreateEnvironmentAsync(NeoEnvironmentOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new NeoEnvironmentOptions();
        options.Validate();
        if (options.CustomSchemes.Count != 0)
        {
            throw new NotSupportedException("The current native ABI does not define the custom-scheme descriptor layout.");
        }

        using var userDataRoot = new Utf8String(options.UserDataRoot);
        using var runtimePath = new Utf8String(options.BrowserRuntimePath);
        using var arguments = new Utf8String(options.BrowserArguments);
        using var languages = new Utf8String(string.Join(',', options.PreferredLanguages));
        var rawOptions = new NativeMethods.neo_webview_environment_options
        {
            size = (uint)sizeof(NativeMethods.neo_webview_environment_options),
            version = 1,
            user_data_root = userDataRoot.View,
            browser_runtime_path = runtimePath.View,
            browser_arguments = arguments.View,
            preferred_languages = languages.View,
            private_mode = options.IsPrivate ? 1u : 0u,
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
    public void Shutdown(int exitCode = 0)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        NativeMethods.neo_webview_app_quit(NativeHandle, exitCode);
    }

    /// <summary>Unregisters callbacks and releases this application's native reference.</summary>
    /// <returns>A completed value task.</returns>
    public unsafe ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        Dispatcher.MarkShutdown();
        try
        {
            NativeMethods.neo_webview_app_set_event_callback(new(_handle.DangerousGetHandle()), default, null);
        }
        catch
        {
            // The native application may already have completed shutdown.
        }

        if (_eventRoot.IsAllocated)
        {
            _eventRoot.Free();
        }

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

        _handle.Dispose();
        return ValueTask.CompletedTask;
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
        var raw = new NativeMethods.neo_webview_app_options
        {
            size = (uint)sizeof(NativeMethods.neo_webview_app_options),
            version = 1,
            application_name = name.View,
            // Managed code implements the mutable shutdown modes from application events.
            shutdown_mode = NativeMethods.neo_webview_app_shutdown_mode.NEO_WEBVIEW_APP_SHUTDOWN_EXPLICIT,
            maximum_pending_dispatches = options.MaximumPendingDispatches,
        };
        var nativeOptions = new NativeMethods.neo_webview_app_options_t(raw);
        NativeMethods.neo_webview_app_t app = default;
        NativeMethods.neo_webview_error_t error = default;
        var result = embedded
            ? NativeMethods.neo_webview_app_attach(&nativeOptions, &app, &error)
            : NativeMethods.neo_webview_app_create(&nativeOptions, &app, &error);
        NativeError.ThrowIfFailed(result, error, embedded ? "attach application" : "create application");
        if (app.Handle == 0)
        {
            throw new NeoWebViewException(NeoErrorCode.NativeFailure, "The native backend returned a null application.");
        }

        return new NeoApplication(new SafeAppHandle(app.Handle), options);
    }

    private unsafe void RegisterEventCallback()
    {
        _eventRoot = GCHandle.Alloc(this);
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
