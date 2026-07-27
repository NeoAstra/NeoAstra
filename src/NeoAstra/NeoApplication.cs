// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using NeoAstra.Interop;
using NeoAstra.Interop.Generated;

namespace NeoAstra;

/// <summary>Owns the native UI dispatcher, windows, and standalone or embedded application lifetime.</summary>
public sealed class NeoApplication : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SafeAppHandle _handle;
    private readonly Dictionary<ulong, NeoWindow> _windows = [];
    private readonly Dictionary<string, NeoWindow> _windowsByLabel = new(StringComparer.Ordinal);
    private readonly HashSet<NeoAstra> _views = [];
    private readonly HashSet<string> _viewLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NeoAstra> _viewsByLabel = new(StringComparer.Ordinal);
    private readonly HashSet<NeoWindow> _closingTrees = [];
    private readonly Queue<NeoLaunchEvent> _launchEvents = new();
    private readonly int _maximumPendingLaunchEvents;
    private readonly Action<NeoLogMessage>? _logCallback;
    private GCHandle _eventRoot;
    private GCHandle _logRoot;
    private NeoWindow? _mainWindow;
    private NeoApplicationShutdownMode _shutdownMode;
    private ExceptionDispatchInfo? _startupException;
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<NeoQuitResult>? _activeQuit;
    private NeoApplicationState _state = NeoApplicationState.Created;
    private ulong _nextLaunchOrder;
    private bool _launchDispatchActive;
    private bool _startupCompleted;
    private int _sessionEndPhase;
    private CancellationTokenSource? _sessionQueryCancellation;
    private int _disposed;

    private NeoApplication(SafeAppHandle handle, NeoApplicationOptions options, GCHandle logRoot)
    {
        _handle = handle;
        _logRoot = logRoot;
        _shutdownMode = options.ShutdownMode;
        _maximumPendingLaunchEvents = options.MaximumPendingLaunchEvents;
        _logCallback = options.LogCallback;
        Dispatcher = new NeoDispatcher(this, Environment.CurrentManagedThreadId);
        RegisterEventCallback();
        TransitionTo(NeoApplicationState.Starting);
        if (options.QueueInitialLaunchEvent)
        {
            QueueLaunchEvent(new NeoLaunchEvent(NeoLaunchReason.Initial, Environment.GetCommandLineArgs().Skip(1).ToArray(), Environment.CurrentDirectory));
        }
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
    /// <exception cref="NeoAstraNativeLibraryException">The native library cannot be loaded or has an incompatible ABI.</exception>
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
            var exitCode = NativeMethods.neoastra_app_run(application.NativeHandle);
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

    /// <summary>Attaches NeoAstra to the host event loop on the current UI thread.</summary>
    /// <param name="options">Application options.</param>
    /// <returns>The attached application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="NeoAstraNativeLibraryException">The native library cannot be loaded or has an incompatible ABI.</exception>
    /// <remarks>The host must await <see cref="DisposeAsync"/> while continuing to pump this thread's UI loop.</remarks>
    public static NeoApplication AttachToCurrentThread(NeoApplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Create(options, embedded: true);
    }

    /// <summary>Gets the UI-thread dispatcher.</summary>
    public NeoDispatcher Dispatcher { get; }

    /// <summary>Gets the current deterministic application lifecycle state.</summary>
    public NeoApplicationState State { get { lock (_sync) return _state; } }

    /// <summary>Occurs after an application lifecycle state transition.</summary>
    public event EventHandler<NeoApplicationStateChangedEventArgs>? StateChanged;

    /// <summary>Registers ordered asynchronous application-level quit handlers.</summary>
    public event Func<NeoQuitRequest, ValueTask>? BeforeQuit;

    /// <summary>Occurs once when stopping begins, before views and capabilities are torn down.</summary>
    public event EventHandler? Stopping;

    internal event Func<CancellationToken, ValueTask>? StoppingAsync;

    /// <summary>Occurs once when application teardown has completed.</summary>
    public event EventHandler? Stopped;

    /// <summary>Registers serial ordered launch-event handlers.</summary>
    public event Func<NeoLaunchEvent, ValueTask>? LaunchReceived;

    /// <summary>Occurs when the bounded early launch queue rejects an event.</summary>
    public event EventHandler<NeoLaunchEvent>? LaunchQueueOverflow;

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

    /// <summary>Finds an open window by its immutable application-local label.</summary>
    /// <param name="label">The exact label.</param>
    /// <param name="window">Receives the window when found.</param>
    /// <returns><see langword="true"/> when found.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="label"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="label"/> is empty.</exception>
    public bool TryGetWindow(string label, out NeoWindow? window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        lock (_sync) return _windowsByLabel.TryGetValue(label, out window);
    }

    /// <summary>Finds an open view by its immutable application-local label.</summary>
    /// <param name="label">The exact label.</param>
    /// <param name="view">Receives the view when found.</param>
    /// <returns><see langword="true"/> when found.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="label"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="label"/> is empty.</exception>
    public bool TryGetView(string label, out NeoAstra? view)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        lock (_sync) return _viewsByLabel.TryGetValue(label, out view);
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
    /// <exception cref="InvalidOperationException">Quit negotiation or stopping has begun.</exception>
    /// <exception cref="ObjectDisposedException">The application is disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public unsafe ValueTask<NeoEnvironment> CreateEnvironmentAsync(NeoEnvironmentOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureTopLevelWorkAccepted();
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new NeoEnvironmentOptions();
        options.Validate();
        using var userDataRoot = new Utf8String(options.UserDataRoot);
        using var runtimePath = new Utf8String(options.BrowserRuntimePath);
        using var arguments = new Utf8String(options.BrowserArguments);
        using var languages = new Utf8String(string.Join(',', options.PreferredLanguages));
        using var customSchemes = new CustomSchemeMarshaller(options.CustomSchemes);
        var rawOptions = new NativeMethods.neoastra_environment_options
        {
            size = (uint)sizeof(NativeMethods.neoastra_environment_options),
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
        var nativeOptions = new NativeMethods.neoastra_environment_options_t(rawOptions);
        var operation = new NativeOperation<NeoEnvironment>(cancellationToken, this);
        NativeMethods.neoastra_operation_t nativeOperation = default;
        NativeMethods.neoastra_error_t error = default;
        NativeMethods.neoastra_result_t result;
        try
        {
            result = NativeMethods.neoastra_environment_create_async(
                NativeHandle,
                &nativeOptions,
                (delegate* unmanaged[Cdecl]<void*, NativeMethods.neoastra_result_t, NativeMethods.neoastra_environment_t, NativeMethods.neoastra_error_t, void>)&EnvironmentCreated,
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
    /// <exception cref="ArgumentException">The label is invalid or already in use, or the owner belongs to another application.</exception>
    /// <exception cref="InvalidOperationException">Quit negotiation or stopping has begun.</exception>
    /// <exception cref="ObjectDisposedException">The application is disposed.</exception>
    public unsafe NeoWindow CreateWindow(NeoWindowOptions? options = null)
    {
        ThrowIfDisposed();
        options ??= new NeoWindowOptions();
        options.Validate(this);
        EnsureTopLevelWorkAccepted();
        lock (_sync)
        {
            if (options.Owner is not null && _closingTrees.Any(root => ReferenceEquals(options.Owner, root) || IsOwnedBy(options.Owner, root)))
                throw new InvalidOperationException("A window cannot be added to an owner tree while that tree is closing.");
            if (options.Label is not null && _windowsByLabel.ContainsKey(options.Label))
                throw new ArgumentException($"The window label '{options.Label}' is already in use by this application.", nameof(options));
        }

        using var title = new Utf8String(options.Title);
        var flags = (options.IsResizable ? 1u : 0u) |
                    (options.HasDecorations ? 2u : 0u) |
                    (options.IsVisible ? 4u : 0u) |
                    (options.IsAlwaysOnTop ? 8u : 0u) |
                    (options.ShowInTaskbar ? 16u : 0u);
        var raw = new NativeMethods.neoastra_window_options
        {
            size = (uint)sizeof(NativeMethods.neoastra_window_options),
            version = 1,
            title = title.View,
            bounds = new NativeMethods.neoastra_rect
            {
                x = options.X,
                y = options.Y,
                width = options.Width,
                height = options.Height,
            },
            minimum_size = new NativeMethods.neoastra_size { width = options.MinimumClientSize.Width, height = options.MinimumClientSize.Height },
            maximum_size = new NativeMethods.neoastra_size { width = options.MaximumClientSize.Width, height = options.MaximumClientSize.Height },
            owner = options.Owner is null ? default : options.Owner.NativeHandle,
            state = (NativeMethods.neoastra_window_state)options.State,
            flags = flags,
            background_color = new NativeMethods.neoastra_color
            {
                red = options.BackgroundColor.Red,
                green = options.BackgroundColor.Green,
                blue = options.BackgroundColor.Blue,
                alpha = options.BackgroundColor.Alpha,
            },
        };
        var nativeOptions = new NativeMethods.neoastra_window_options_t(raw);
        NativeMethods.neoastra_window_t nativeWindow = default;
        NativeMethods.neoastra_error_t error = default;
        var result = NativeMethods.neoastra_app_create_window(NativeHandle, &nativeOptions, &nativeWindow, &error);
        NativeError.ThrowIfFailed(result, error, "create window");
        if (nativeWindow.Handle == 0)
        {
            throw new NeoAstraException(NeoErrorCode.NativeFailure, "The native backend returned a null window.", "create window");
        }

        var window = new NeoWindow(this, new SafeWindowHandle(nativeWindow.Handle), options);
        lock (_sync)
        {
            _windows.Add(window.Id, window);
            if (window.Label is not null) _windowsByLabel.Add(window.Label, window);
            _mainWindow ??= window;
        }

        return window;
    }

    /// <summary>Urgently bypasses cancelable quit and requests application shutdown. This method may be called from any thread.</summary>
    /// <param name="exitCode">The process-style exit code returned by <see cref="Run"/>.</param>
    /// <remarks>Safe to call concurrently with <see cref="DisposeAsync"/>; it is a no-op after disposal starts.</remarks>
    public void Shutdown(int exitCode = 0)
        => ForceShutdown(exitCode);

    /// <summary>Urgently bypasses close negotiation and requests backend shutdown.</summary>
    /// <param name="exitCode">The process-style exit code.</param>
    /// <remarks>This backend-only escape hatch is not exposed through renderer transport or RPC.</remarks>
    public void ForceShutdown(int exitCode = 0)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        CompleteActiveQuitAsForced();
        if (!Dispatcher.CheckAccess())
        {
            try { Dispatcher.Post(() => ForceShutdown(exitCode)); }
            catch { RequestNativeQuit(exitCode); }
            return;
        }

        BeginStopping();
        Dispatcher.MarkShutdown();
        RequestNativeQuit(exitCode);
    }

    private void RequestNativeQuit(int exitCode)
    {
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

            NativeMethods.neoastra_app_quit(new NativeMethods.neoastra_app_t(_handle.DangerousGetHandle()), exitCode);
        }
        finally
        {
            if (addedRef)
            {
                _handle.DangerousRelease();
            }
        }
    }

    /// <summary>Transitions successful startup to ready and begins serial launch delivery.</summary>
    /// <exception cref="InvalidOperationException">The application is not starting.</exception>
    public void NotifyReady()
    {
        ThrowIfDisposed();
        if (!Dispatcher.CheckAccess()) throw new InvalidOperationException("Ready must be signaled on the application dispatcher.");
        var transition = false;
        lock (_sync)
        {
            _startupCompleted = true;
            if (_state == NeoApplicationState.Ready) return;
            if (_state == NeoApplicationState.Starting) transition = true;
            else if (_activeQuit is not null || _state is NeoApplicationState.QuitRequested or NeoApplicationState.ClosingWindows) return;
            else throw new InvalidOperationException($"Cannot become ready from {_state}.");
        }
        if (transition)
        {
            TransitionTo(NeoApplicationState.Ready);
            StartLaunchDispatch();
        }
    }

    /// <summary>Queues validated launch data for ordered delivery, including before ready.</summary>
    /// <param name="launchEvent">Immutable launch data.</param>
    /// <returns><see langword="false"/> if the bounded queue is full or stopping has begun.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="launchEvent"/> is <see langword="null"/>.</exception>
    public bool QueueLaunchEvent(NeoLaunchEvent launchEvent)
    {
        ArgumentNullException.ThrowIfNull(launchEvent);
        var accepted = false;
        var schedule = false;
        NeoLaunchEvent? queued = null;
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) == 0 && _state is not (NeoApplicationState.Stopping or NeoApplicationState.Stopped) && _launchEvents.Count < _maximumPendingLaunchEvents)
            {
                queued = launchEvent with { Order = ++_nextLaunchOrder };
                _launchEvents.Enqueue(queued);
                accepted = true;
                schedule = _state == NeoApplicationState.Ready && !_launchDispatchActive;
            }
        }
        if (schedule)
        {
            try { Dispatcher.Post(StartLaunchDispatch); }
            catch
            {
                lock (_sync)
                {
                    var retained = _launchEvents.Where(value => value.Order != queued!.Order).ToArray();
                    _launchEvents.Clear();
                    foreach (var value in retained) _launchEvents.Enqueue(value);
                }
                accepted = false;
            }
        }
        if (!accepted)
        {
            try
            {
                Dispatcher.Post(() =>
                {
                    try { LaunchQueueOverflow?.Invoke(this, launchEvent); }
                    catch (Exception exception) { ReportLifecycleFailure("application.launch-overflow", exception, 0); }
                });
            }
            catch { /* Dispatcher shutdown is already the authoritative rejection signal. */ }
            return false;
        }
        return true;
    }

    /// <summary>Requests bounded normal quit. Concurrent and reentrant callers join one negotiation.</summary>
    /// <param name="reason">The portable reason.</param>
    /// <param name="exitCode">The requested process exit code.</param>
    /// <param name="options">Quit policy, or <see langword="null"/> for safe defaults.</param>
    /// <param name="cancellationToken">Cancels this negotiation and therefore all joined callers before stopping begins.</param>
    /// <returns>The shared quit result.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> or an option is invalid.</exception>
    /// <exception cref="ObjectDisposedException">The application is disposed.</exception>
    public Task<NeoQuitResult> RequestQuitAsync(NeoQuitReason reason = NeoQuitReason.Programmatic, int exitCode = 0,
        NeoQuitOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        options ??= new NeoQuitOptions();
        options.Validate();
        var timeout = options.Timeout;
        var preflightWindows = options.PreflightWindows;
        return RequestQuitCore(reason, exitCode, timeout, preflightWindows,
            reason is not (NeoQuitReason.Forced or NeoQuitReason.SessionEnd), cancellationToken, null);
    }

    private Task<NeoQuitResult> RequestQuitCore(NeoQuitReason reason, int exitCode, TimeSpan timeout, bool preflightWindows,
        bool canCancel, CancellationToken cancellationToken, SafeDecisionHandle? platformDecision)
    {
        TaskCompletionSource<NeoQuitResult> completion;
        Task<NeoQuitResult>? joined = null;
        lock (_sync)
        {
            if (_activeQuit is not null) joined = _activeQuit.Task;
            if (_state is NeoApplicationState.Stopping or NeoApplicationState.Stopped)
            {
                CompletePlatformQuitDecision(platformDecision, true);
                return Task.FromResult(NeoQuitResult.Forced);
            }
            if (joined is not null)
            {
                if (platformDecision is not null) _ = CompleteJoinedPlatformDecisionAsync(joined, platformDecision);
                return joined;
            }
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeQuit = completion;
        }
        try { Dispatcher.Post(() => _ = RunQuitAsync(reason, exitCode, timeout, preflightWindows, canCancel, cancellationToken, completion, platformDecision)); }
        catch (Exception exception)
        {
            lock (_sync) if (ReferenceEquals(_activeQuit, completion)) _activeQuit = null;
            CompletePlatformQuitDecision(platformDecision, false);
            completion.TrySetException(exception);
        }
        return completion.Task;
    }

    private async Task CompleteJoinedPlatformDecisionAsync(Task<NeoQuitResult> quit, SafeDecisionHandle decision)
    {
        try
        {
            var result = await quit.ConfigureAwait(true);
            await Dispatcher.InvokeAsync(() => CompletePlatformQuitDecision(decision, result != NeoQuitResult.Canceled)).ConfigureAwait(false);
        }
        catch
        {
            try { await Dispatcher.InvokeAsync(() => CompletePlatformQuitDecision(decision, false)).ConfigureAwait(false); }
            catch { decision.Dispose(); }
        }
    }

    private async Task RunQuitAsync(NeoQuitReason reason, int exitCode, TimeSpan timeout, bool preflightWindows, bool canCancel,
        CancellationToken cancellationToken, TaskCompletionSource<NeoQuitResult> completion, SafeDecisionHandle? platformDecision)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var forced = !canCancel;
        try
        {
            TransitionTo(NeoApplicationState.QuitRequested);
            var request = new NeoQuitRequest(reason, exitCode, canCancel, DateTimeOffset.UtcNow + timeout, deadline.Token);
            var appApproved = await InvokeOrderedAsync(BeforeQuit, request, deadline.Token).ConfigureAwait(true);
            if (!forced && (!appApproved || request.IsCanceled))
            {
                CancelQuit(completion);
                CompletePlatformQuitDecision(platformDecision, false);
                return;
            }

            var windows = GetWindowsInCloseOrder();
            if (preflightWindows)
            {
                foreach (var window in windows)
                {
                    if (!await window.EvaluateCloseAsync(forced ? NeoWindowCloseReason.SessionEnd : NeoWindowCloseReason.ApplicationQuit, !forced, deadline.Token).ConfigureAwait(true))
                    {
                        CancelQuit(completion);
                        CompletePlatformQuitDecision(platformDecision, false);
                        return;
                    }
                }
                TransitionTo(NeoApplicationState.ClosingWindows);
                foreach (var window in windows) window.ForceCloseFromApplication();
            }
            else
            {
                TransitionTo(NeoApplicationState.ClosingWindows);
                foreach (var window in windows)
                {
                    if (!await window.EvaluateCloseAsync(forced ? NeoWindowCloseReason.SessionEnd : NeoWindowCloseReason.ApplicationQuit, !forced, deadline.Token).ConfigureAwait(true))
                    {
                        CancelQuit(completion);
                        CompletePlatformQuitDecision(platformDecision, false);
                        return;
                    }
                    window.ForceCloseFromApplication();
                }
            }

            BeginStopping();
            await InvokeStoppingHandlersAsync(deadline.Token).ConfigureAwait(true);
            if (platformDecision is null) NativeMethods.neoastra_app_quit(DangerousNativeHandle, exitCode);
            else CompletePlatformQuitDecision(platformDecision, true);
            completion.TrySetResult(forced ? NeoQuitResult.Forced : NeoQuitResult.Completed);
        }
        catch (OperationCanceledException)
        {
            if (forced)
            {
                CompletePlatformQuitDecision(platformDecision, true);
                ForceShutdown(exitCode);
                completion.TrySetResult(NeoQuitResult.Forced);
            }
            else
            {
                CancelQuit(completion);
                CompletePlatformQuitDecision(platformDecision, false);
            }
        }
        catch (Exception exception)
        {
            ReportLifecycleFailure("application.quit", exception, 0);
            if (forced)
            {
                CompletePlatformQuitDecision(platformDecision, true);
                ForceShutdown(exitCode);
                completion.TrySetResult(NeoQuitResult.Forced);
            }
            else
            {
                // Handler failures use the safe default for cancelable requests: preserve windows.
                CancelQuit(completion);
                CompletePlatformQuitDecision(platformDecision, false);
            }
        }
    }

    private void CancelQuit(TaskCompletionSource<NeoQuitResult> completion)
    {
        bool stopping;
        lock (_sync)
        {
            if (ReferenceEquals(_activeQuit, completion)) _activeQuit = null;
            stopping = _state is NeoApplicationState.Stopping or NeoApplicationState.Stopped;
        }
        if (!stopping)
        {
            bool startupCompleted;
            lock (_sync) startupCompleted = _startupCompleted;
            TransitionTo(startupCompleted ? NeoApplicationState.Ready : NeoApplicationState.Starting);
            if (startupCompleted) StartLaunchDispatch();
        }
        completion.TrySetResult(stopping ? NeoQuitResult.Forced : NeoQuitResult.Canceled);
    }

    /// <summary>Detaches the native application on its owning UI thread and releases its native reference.</summary>
    /// <returns>A task completed after native UI and platform teardown is acknowledged.</returns>
    /// <exception cref="NeoAstraException">Native application detach fails.</exception>
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

    internal NativeMethods.neoastra_app_t DangerousNativeHandle => new(_handle.DangerousGetHandle());

    internal void ReserveViewLabel(string? label)
    {
        if (label is null) return;
        lock (_sync)
        {
            if (!_viewLabels.Add(label)) throw new ArgumentException($"The view label '{label}' is already in use by this application.", nameof(label));
        }
    }

    internal void EnsureTopLevelWorkAccepted()
    {
        lock (_sync)
            if (_activeQuit is not null || _state is NeoApplicationState.QuitRequested or NeoApplicationState.ClosingWindows or NeoApplicationState.Stopping or NeoApplicationState.Stopped)
                throw new InvalidOperationException("New top-level work is not accepted after quit negotiation begins.");
    }

    internal void ReleaseViewLabel(string? label)
    {
        if (label is null) return;
        lock (_sync) _viewLabels.Remove(label);
    }

    internal void RegisterView(NeoAstra view)
    {
        lock (_sync)
        {
            _views.Add(view);
            if (view.ViewLabel is not null) _viewsByLabel.Add(view.ViewLabel, view);
        }
    }

    internal void UnregisterView(NeoAstra view)
    {
        lock (_sync)
        {
            _views.Remove(view);
            if (view.ViewLabel is not null) _viewsByLabel.Remove(view.ViewLabel);
        }
        ReleaseViewLabel(view.ViewLabel);
    }

    private void StartLaunchDispatch()
    {
        lock (_sync)
        {
            if (_launchDispatchActive || _state != NeoApplicationState.Ready || _launchEvents.Count == 0) return;
            _launchDispatchActive = true;
        }
        _ = DispatchLaunchEventsAsync();
    }

    private async Task DispatchLaunchEventsAsync()
    {
        for (;;)
        {
            NeoLaunchEvent launchEvent;
            lock (_sync)
            {
                if (_state != NeoApplicationState.Ready || _launchEvents.Count == 0)
                {
                    _launchDispatchActive = false;
                    return;
                }
                launchEvent = _launchEvents.Dequeue();
            }
            var handlers = LaunchReceived;
            if (handlers is null) continue;
            foreach (var handler in handlers.GetInvocationList().Cast<Func<NeoLaunchEvent, ValueTask>>())
            {
                try { await handler(launchEvent); }
                catch (Exception exception) { ReportLifecycleFailure("application.launch", exception, 0); }
            }
        }
    }

    private async ValueTask<bool> InvokeOrderedAsync(Func<NeoQuitRequest, ValueTask>? handlers,
        NeoQuitRequest request, CancellationToken cancellationToken)
    {
        if (handlers is null) return true;
        foreach (var handler in handlers.GetInvocationList().Cast<Func<NeoQuitRequest, ValueTask>>())
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await handler(request).AsTask().WaitAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                ReportLifecycleFailure("application.quit", exception, 0);
                request.Cancel();
                return false;
            }
            if (request.IsCanceled) return false;
        }
        return true;
    }

    private async ValueTask InvokeStoppingHandlersAsync(CancellationToken cancellationToken)
    {
        var handlers = StoppingAsync;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Func<CancellationToken, ValueTask>>())
        {
            try
            {
                await handler(cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                ReportLifecycleFailure("application.stopping", exception, 0);
            }
        }
    }

    private NeoWindow[] GetWindowsInCloseOrder()
    {
        lock (_sync)
        {
            return _windows.Values.OrderByDescending(static window => window.OwnerDepth).ThenBy(static window => window.Id).ToArray();
        }
    }

    private async ValueTask<bool> EvaluateCloseTreeAsync(NeoWindow root, NeoWindowCloseReason reason, bool canCancel,
        CancellationToken cancellationToken)
    {
        NeoWindow[] order;
        lock (_sync)
        {
            if (_closingTrees.Any(existing => ReferenceEquals(root, existing) || IsOwnedBy(root, existing) || IsOwnedBy(existing, root))) return false;
            _closingTrees.Add(root);
            order = _windows.Values.Where(window => ReferenceEquals(window, root) || IsOwnedBy(window, root))
                .OrderByDescending(static window => window.OwnerDepth).ThenBy(static window => window.Id).ToArray();
        }
        try
        {
            foreach (var window in order)
            {
                var windowReason = ReferenceEquals(window, root) ? reason : NeoWindowCloseReason.Owner;
                if (!await window.EvaluateCloseAsync(windowReason, canCancel, cancellationToken).ConfigureAwait(true)) return false;
            }
            foreach (var window in order)
                if (!ReferenceEquals(window, root)) window.ForceCloseFromApplication();
            return true;
        }
        finally
        {
            lock (_sync) _closingTrees.Remove(root);
        }
    }

    private static bool IsOwnedBy(NeoWindow window, NeoWindow owner)
    {
        for (var current = window.Owner; current is not null; current = current.Owner)
            if (ReferenceEquals(current, owner)) return true;
        return false;
    }

    private void BeginStopping()
    {
        NeoApplicationState state;
        lock (_sync) state = _state;
        if (state is NeoApplicationState.Stopping or NeoApplicationState.Stopped) return;
        lock (_sync) _sessionQueryCancellation?.Cancel();
        TransitionTo(NeoApplicationState.Stopping);
        InvokeLifecycleEvent(Stopping, "application.stopping");
        NotifyViewsOfShutdown();
    }

    private void CompleteActiveQuitAsForced()
    {
        TaskCompletionSource<NeoQuitResult>? active;
        lock (_sync) { active = _activeQuit; _activeQuit = null; }
        active?.TrySetResult(NeoQuitResult.Forced);
    }

    internal void ReportLifecycleFailure(string category, Exception exception, ulong objectId)
    {
        try
        {
            _logCallback?.Invoke(new NeoLogMessage(NeoLogLevel.Error, category, exception.Message, 0,
                (ulong)(Stopwatch.GetTimestamp() * (1_000_000_000d / Stopwatch.Frequency)), 0, objectId));
        }
        catch { }
    }

    private unsafe void CompletePlatformQuitDecision(SafeDecisionHandle? decision, bool allow)
    {
        if (decision is null) return;
        try
        {
            var response = new NativeMethods.neoastra_decision_response_t(new NativeMethods.neoastra_decision_response
            {
                size = (uint)sizeof(NativeMethods.neoastra_decision_response),
                version = 1,
                action = allow ? NativeMethods.neoastra_decision_action.NEOASTRA_DECISION_ALLOW : NativeMethods.neoastra_decision_action.NEOASTRA_DECISION_CANCEL,
                selected_index = uint.MaxValue,
            });
            NativeMethods.neoastra_error_t error = default;
            _ = NativeMethods.neoastra_decision_complete(new(decision.DangerousGetHandle()), &response, &error);
            if (error.Handle != 0) new SafeErrorHandle(error.Handle).Dispose();
        }
        finally
        {
            decision.Dispose();
            if (!allow) Interlocked.CompareExchange(ref _sessionEndPhase, 0, 1);
        }
    }

    private void HandleSessionEnd(NativeMethods.neoastra_event value)
    {
        if (value.value == 3)
        {
            lock (_sync) _sessionQueryCancellation?.Cancel();
            Interlocked.CompareExchange(ref _sessionEndPhase, 0, 1);
            return;
        }
        if (value.value != 0)
        {
            lock (_sync) _sessionQueryCancellation?.Cancel();
            if (Interlocked.Exchange(ref _sessionEndPhase, 2) != 2) ForceShutdown();
            return;
        }

        if (Interlocked.CompareExchange(ref _sessionEndPhase, 1, 0) != 0) return;
        QueueLaunchEvent(new NeoLaunchEvent(NeoLaunchReason.SessionEnd));
        SafeDecisionHandle? decision = null;
        var remaining = TimeSpan.FromSeconds(2);
        if (value.decision.Handle != 0)
        {
            NativeMethods.neoastra_decision_retain(value.decision);
            decision = new SafeDecisionHandle(value.decision.Handle);
            if (NativeError.Code(NativeMethods.neoastra_decision_defer(value.decision)) != NeoErrorCode.Success)
            {
                decision.Dispose();
                Interlocked.CompareExchange(ref _sessionEndPhase, 0, 1);
                return;
            }
            var deadline = NativeMethods.neoastra_decision_get_deadline_ns(value.decision);
            var now = (ulong)(Stopwatch.GetTimestamp() * (1_000_000_000d / Stopwatch.Frequency));
            remaining = deadline > now
                ? TimeSpan.FromTicks(checked((long)Math.Min((deadline - now) / 100, (ulong)TimeSpan.FromMinutes(10).Ticks)))
                : TimeSpan.FromMilliseconds(1);
        }

        if (value.native_code != 0)
        {
            _ = RequestQuitCore(NeoQuitReason.SessionEnd, 0, remaining, true, true, CancellationToken.None, decision);
        }
        else
        {
            _ = RunSessionQueryAsync(remaining, decision);
        }
    }

    private async Task RunSessionQueryAsync(TimeSpan timeout, SafeDecisionHandle? decision)
    {
        using var deadline = new CancellationTokenSource(timeout);
        lock (_sync) _sessionQueryCancellation = deadline;
        var request = new NeoQuitRequest(NeoQuitReason.SessionEnd, 0, false, DateTimeOffset.UtcNow + timeout, deadline.Token);
        try { await InvokeOrderedAsync(BeforeQuit, request, deadline.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_sync) if (ReferenceEquals(_sessionQueryCancellation, deadline)) _sessionQueryCancellation = null;
            CompletePlatformQuitDecision(decision, true);
        }
    }

    private void TransitionTo(NeoApplicationState next)
    {
        NeoApplicationState previous;
        lock (_sync)
        {
            previous = _state;
            if (previous == next) return;
            _state = next;
        }
        var handlers = StateChanged;
        if (handlers is null) return;
        var args = new NeoApplicationStateChangedEventArgs(previous, next);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<NeoApplicationStateChangedEventArgs>>())
        {
            try { handler(this, args); }
            catch (Exception exception) { ReportLifecycleFailure("application.state", exception, 0); }
        }
    }

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
        CompleteActiveQuitAsForced();
        BeginStopping();
        Dispatcher.MarkShutdown();
        UnregisterEventCallback();
        DisposeManagedWindows();

        NativeMethods.neoastra_error_t error = default;
        var result = NativeMethods.neoastra_app_detach(DangerousNativeHandle, &error);
        NativeError.ThrowIfFailed(result, error, "detach application");
        ReleaseLogCallback(canFree: true);
        _handle.Dispose();
        CompleteStopped();
        GC.SuppressFinalize(this);
    }

    private void DisposeManagedWindows()
    {
        NeoWindow[] windows;
        lock (_sync)
        {
            windows = _windows.Values.ToArray();
            _windows.Clear();
            _windowsByLabel.Clear();
            _mainWindow = null;
        }

        foreach (var window in windows)
        {
            window.DisposeFromApplication();
        }
    }

    private void ReleaseWithoutDetach()
    {
        CompleteActiveQuitAsForced();
        BeginStopping();
        Dispatcher.MarkShutdown();
        UnregisterEventCallback();
        ReleaseLogCallback(canFree: false);
        DisposeManagedWindows();
        _handle.Dispose();
        CompleteStopped();
        GC.SuppressFinalize(this);
    }

    private void NotifyViewsOfShutdown()
    {
        NeoAstra[] views;
        lock (_sync)
        {
            views = _views.ToArray();
            _views.Clear();
            _viewLabels.Clear();
            _viewsByLabel.Clear();
        }
        foreach (var view in views)
        {
            try { view.NotifyApplicationShutdown(); }
            catch (Exception exception) { ReportLifecycleFailure("application.stopping", exception, view.OwnedWindow?.Id ?? 0); }
        }
    }

    private unsafe void UnregisterEventCallback()
    {
        try
        {
            NativeMethods.neoastra_app_set_event_callback(DangerousNativeHandle, default, null);
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

    internal NativeMethods.neoastra_app_t NativeHandle
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
        var raw = new NativeMethods.neoastra_app_options
        {
            size = (uint)sizeof(NativeMethods.neoastra_app_options),
            version = 1,
            application_name = name.View,
            // Managed code implements the mutable shutdown modes from application events.
            shutdown_mode = NativeMethods.neoastra_app_shutdown_mode.NEOASTRA_APP_SHUTDOWN_EXPLICIT,
            maximum_pending_dispatches = options.MaximumPendingDispatches,
            log_callback = options.LogCallback is null
                ? default
                : (delegate* unmanaged[Cdecl]<void*, NativeMethods.neoastra_log_level_t, NativeMethods.neoastra_string_view_t, NativeMethods.neoastra_string_view_t, ulong, ulong, long, ulong, void>)&NativeLog,
            log_context = logRoot.IsAllocated ? (void*)GCHandle.ToIntPtr(logRoot) : null,
        };
        var nativeOptions = new NativeMethods.neoastra_app_options_t(raw);
        NativeMethods.neoastra_app_t app = default;
        NativeMethods.neoastra_error_t error = default;
        try
        {
            var result = embedded
                ? NativeMethods.neoastra_app_attach(&nativeOptions, &app, &error)
                : NativeMethods.neoastra_app_create(&nativeOptions, &app, &error);
            NativeError.ThrowIfFailed(result, error, embedded ? "attach application" : "create application");
            if (app.Handle == 0)
            {
                throw new NeoAstraException(NeoErrorCode.NativeFailure, "The native backend returned a null application.");
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
        var result = NativeMethods.neoastra_app_set_event_callback(
            new(_handle.DangerousGetHandle()),
            (delegate* unmanaged[Cdecl]<void*, NativeMethods.neoastra_event_t*, void>)&ApplicationEvent,
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
            if (State is not (NeoApplicationState.Stopping or NeoApplicationState.Stopped)) NotifyReady();
        }
        catch (Exception ex)
        {
            _startupException = ExceptionDispatchInfo.Capture(ex);
            Shutdown(-1);
        }
    }

    private void DispatchEvent(NativeMethods.neoastra_event value)
    {
        var type = value.header.Value.type.Value;
        try
        {
            switch (type)
            {
                case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_APPLICATION_ACTIVATED:
                    QueueLaunchEvent(new NeoLaunchEvent(NeoLaunchReason.Activated));
                    return;
                case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_APPLICATION_OPEN_FILE:
                    QueueLaunchEvent(new NeoLaunchEvent(NeoLaunchReason.OpenFiles, files: [Utf8String.Decode(value.text)]));
                    return;
                case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_APPLICATION_OPEN_URL:
                    QueueLaunchEvent(new NeoLaunchEvent(NeoLaunchReason.OpenUrls, urls: [new Uri(Utf8String.Decode(value.uri), UriKind.Absolute)]));
                    return;
                case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_APPLICATION_SESSION_END:
                    HandleSessionEnd(value);
                    return;
            }
        }
        catch (Exception exception)
        {
            // Malformed backend launch data is rejected rather than dispatched.
            ReportLifecycleFailure("application.launch", exception, 0);
            return;
        }

        NeoWindow? window;
        lock (_sync)
        {
            _windows.TryGetValue(value.object_id, out window);
        }

        if (window is null)
        {
            return;
        }

        switch (type)
        {
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_WINDOW_CLOSE_REQUESTED:
                window.OnClosing(value.decision.Handle, (NeoWindowCloseReason)value.value, value.native_code != 0,
                    NativeMethods.neoastra_decision_get_deadline_ns(value.decision),
                    cancellationToken => EvaluateCloseTreeAsync(window, (NeoWindowCloseReason)value.value, value.native_code != 0, cancellationToken));
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_WINDOW_CLOSED:
                OnWindowClosed(window);
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_WINDOW_MOVED:
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_WINDOW_RESIZED:
                window.OnBoundsChanged();
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_WINDOW_FOCUS_CHANGED:
                window.OnFocusChanged(value.value != 0);
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_WINDOW_SCALE_FACTOR_CHANGED:
                window.OnScaleFactorChanged(value.value == 0 ? 1d : value.value / 1000d);
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_WINDOW_STATE_CHANGED:
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
            if (window.Label is not null) _windowsByLabel.Remove(window.Label);
            mode = _shutdownMode;
            main = _mainWindow;
            noWindows = _windows.Count == 0;
        }

        if (mode == NeoApplicationShutdownMode.OnLastWindowClosed && noWindows ||
            mode == NeoApplicationShutdownMode.OnMainWindowClosed && ReferenceEquals(main, window))
        {
            _ = RequestQuitAsync(mode == NeoApplicationShutdownMode.OnMainWindowClosed
                ? NeoQuitReason.MainWindowClosed : NeoQuitReason.LastWindowClosed);
        }
    }

    private void CompleteStopped()
    {
        if (State == NeoApplicationState.Stopped) return;
        if (State != NeoApplicationState.Stopping) TransitionTo(NeoApplicationState.Stopping);
        TransitionTo(NeoApplicationState.Stopped);
        InvokeLifecycleEvent(Stopped, "application.stopped");
    }

    private void InvokeLifecycleEvent(EventHandler? handlers, string category)
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception exception) { ReportLifecycleFailure(category, exception, 0); }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private static Exception CreateOwnedError(NativeMethods.neoastra_result_t result, NativeMethods.neoastra_error_t error, string operation, CancellationToken cancellationToken)
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
    private static unsafe void ApplicationEvent(void* context, NativeMethods.neoastra_event_t* nativeEvent)
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
        NativeMethods.neoastra_log_level_t level,
        NativeMethods.neoastra_string_view_t category,
        NativeMethods.neoastra_string_view_t message,
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
        NativeMethods.neoastra_result_t result,
        NativeMethods.neoastra_environment_t environment,
        NativeMethods.neoastra_error_t error)
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
