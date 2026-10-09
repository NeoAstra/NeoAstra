// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using NeoAstra.Interop;
using NeoAstra.Interop.Generated;
using System.Diagnostics;

namespace NeoAstra;

/// <summary>Represents a NeoAstra-owned top-level native window.</summary>
public sealed class NeoWindow : IAsyncDisposable
{
    private readonly SafeWindowHandle _handle;
    private readonly NeoWindow? _owner;
    private NeoRect _bounds;
    private NeoSize _minimumClientSize;
    private NeoSize _maximumClientSize;
    private string _title;
    private bool _isVisible;
    private bool _isFocused;
    private double _scaleFactor = 1d;
    private NeoWindowState _state;
    private NeoWindowTitleBar _titleBar;
    private int _closed;
    private int _disposed;

    internal NeoWindow(NeoApplication application, SafeWindowHandle handle, NeoWindowOptions options)
    {
        Application = application;
        _handle = handle;
        _owner = options.Owner;
        IsModal = options.IsModal;
        Label = options.Label;
        _bounds = new NeoRect(options.X, options.Y, options.Width, options.Height);
        _minimumClientSize = options.MinimumClientSize;
        _maximumClientSize = options.MaximumClientSize;
        _title = options.Title;
        _isVisible = options.IsVisible;
        _state = options.State;
        Id = NativeMethods.neoastra_window_get_id(NativeHandle);
        _scaleFactor = ReadScaleFactor(1d);
        _titleBar = options.TitleBar;
        // The creation flags carry only the style; the remaining title-bar settings need the full native call.
        if (_titleBar != new NeoWindowTitleBar(_titleBar.Style))
        {
            try { ApplyTitleBar(_titleBar); }
            catch (NotSupportedException) { }
        }
    }

    /// <summary>Gets the stable application-local window identifier.</summary>
    public ulong Id { get; }

    /// <summary>Gets the immutable application-local label, when one was assigned at creation.</summary>
    public string? Label { get; }

    /// <summary>Gets or sets the window title.</summary>
    /// <exception cref="ArgumentNullException">The assigned value is <see langword="null"/>.</exception>
    public string Title
    {
        get
        {
            ThrowIfDisposed();
            _title = Utf8String.Decode(NativeMethods.neoastra_window_get_title(NativeHandle));
            return _title;
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            ThrowIfDisposed();
            using var utf8 = new Utf8String(value);
            NativeError.ThrowIfFailed(NativeMethods.neoastra_window_set_title(NativeHandle, utf8.View), default, "set window title");
            _title = value;
        }
    }

    /// <summary>Gets or sets the position of the window, including its frame, in the units of <see cref="ClientSize"/>.</summary>
    /// <remarks>The position is the top-left corner of the window measured from the top-left corner of the primary display, with the vertical axis pointing down on every platform.</remarks>
    public NeoPoint Position
    {
        get => GetBounds().Position;
        set
        {
            var bounds = GetBounds();
            SetBounds(new NeoRect(value.X, value.Y, bounds.Width, bounds.Height));
        }
    }

    /// <summary>Gets or sets the client size, in the units that the platform counts a window in.</summary>
    /// <remarks>
    /// <para>The client area excludes the window frame and a standard title bar, so a value read here can be assigned back without resizing the window. With GTK it includes a title bar that the window draws itself.</para>
    /// <para>
    /// On macOS and with GTK the units are logical: the same on every display, and what a CSS pixel of a page is at 100 percent
    /// zoom. On Windows they are the units that the system gives the process: the pixels of the display for an application
    /// that declares itself aware of display scaling, of which <see cref="ScaleFactor"/> make one logical unit, and logical
    /// units for an application that leaves the scaling to the system.
    /// </para>
    /// <para>
    /// A window may not take the size it is given: its screen, its size limits, or its state can keep it at another one. The
    /// value read back is the size it has. With GTK a window on screen takes a size when it is laid out next, so the value
    /// read back until then is the one that was assigned, and <see cref="ClientSizeChanged"/> tells of another outcome.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    public NeoSize ClientSize
    {
        get => GetBounds().Size;
        set
        {
            if (value.Width <= 0 || value.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Client dimensions must be positive.");
            }

            var bounds = GetBounds();
            SetBounds(new NeoRect(bounds.X, bounds.Y, value.Width, value.Height));
        }
    }

    /// <summary>Gets the rectangle that the window shows on screen, in the units of <see cref="ClientSize"/>: its frame with its title bar, without the borders of the frame that are not drawn.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="Position"/> and <see cref="ClientSize"/> pair the corner of the frame of the window with the size of its
    /// client area, which is another rectangle than the one the window shows. On Windows the frame of a window that can be
    /// resized has borders that are not drawn on its left, right, and bottom sides, so a window whose visible edge is at the
    /// left edge of a display has a position to the left of it, and on Windows and macOS a standard title bar is above the
    /// client area. The value is known for a window that is hidden as well.
    /// </para>
    /// <para>
    /// With GTK, and with a native library from before this member, the value is the rectangle of <see cref="Position"/> and
    /// <see cref="ClientSize"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The window is closed or disposed.</exception>
    /// <exception cref="InvalidOperationException">The member was read on another thread than the one of the window.</exception>
    public unsafe NeoRect FrameBounds
    {
        get
        {
            var bounds = GetBounds();
            var native = new NativeMethods.neoastra_rect_t(default);
            try
            {
                NativeError.ThrowIfFailed(NativeMethods.neoastra_window_get_frame(NativeHandle, &native), default, "get window frame");
            }
            catch (EntryPointNotFoundException)
            {
                // An older native library does not tell what a window shows of its frame.
                return bounds;
            }

            var value = native.Value;
            return value.width > 0 && value.height > 0 ? new NeoRect(value.x, value.y, value.width, value.height) : bounds;
        }
    }

    /// <summary>Gets or sets the native minimum client-size constraint, in the units of <see cref="ClientSize"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is negative.</exception>
    /// <exception cref="ArgumentException">The minimum exceeds the configured maximum.</exception>
    public unsafe NeoSize MinimumClientSize
    {
        get
        {
            ThrowIfDisposed();
            var native = new NativeMethods.neoastra_size_t(default);
            NativeError.ThrowIfFailed(NativeMethods.neoastra_window_get_minimum_size(NativeHandle, &native), default, "get minimum window size");
            _minimumClientSize = new NeoSize(native.Value.width, native.Value.height);
            return _minimumClientSize;
        }
        set
        {
            if (value.Width < 0 || value.Height < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            var native = new NativeMethods.neoastra_size { width = value.Width, height = value.Height };
            NativeError.ThrowIfFailed(NativeMethods.neoastra_window_set_minimum_size(NativeHandle, native), default, "set minimum window size");
            _minimumClientSize = value;
        }
    }

    /// <summary>Gets or sets the native maximum client-size constraint, in the units of <see cref="ClientSize"/>. Zero disables a dimension's maximum.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is negative.</exception>
    /// <exception cref="ArgumentException">The maximum is less than the configured minimum.</exception>
    public unsafe NeoSize MaximumClientSize
    {
        get
        {
            ThrowIfDisposed();
            var native = new NativeMethods.neoastra_size_t(default);
            NativeError.ThrowIfFailed(NativeMethods.neoastra_window_get_maximum_size(NativeHandle, &native), default, "get maximum window size");
            _maximumClientSize = new NeoSize(native.Value.width, native.Value.height);
            return _maximumClientSize;
        }
        set
        {
            if (value.Width < 0 || value.Height < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            var native = new NativeMethods.neoastra_size { width = value.Width, height = value.Height };
            NativeError.ThrowIfFailed(NativeMethods.neoastra_window_set_maximum_size(NativeHandle, native), default, "set maximum window size");
            _maximumClientSize = value;
        }
    }

    /// <summary>Gets whether the window is believed to be visible.</summary>
    public bool IsVisible => _isVisible;

    /// <summary>Gets whether the window currently has keyboard focus.</summary>
    public bool IsFocused => _isFocused;

    /// <summary>Gets whether native closure or disposal has completed for this window.</summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0 || Volatile.Read(ref _disposed) != 0;

    /// <summary>Gets how many pixels of its display the window has for one logical unit: 1.5 at 150 percent, 2 on a Retina display.</summary>
    /// <remarks>
    /// The value is known as soon as the window exists, and <see cref="ScaleFactorChanged"/> reports when it changes. On
    /// Windows it is what the system tells the process: the scale of the display for an application that declares itself
    /// aware of display scaling, and 1 for an application that leaves the scaling to the system. GTK reports whole numbers.
    /// </remarks>
    public double ScaleFactor => _scaleFactor;

    /// <summary>Gets or sets the native window presentation state.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is not defined.</exception>
    public unsafe NeoWindowState State
    {
        get
        {
            ThrowIfDisposed();
            NativeMethods.neoastra_window_state_t native;
            NativeError.ThrowIfFailed(NativeMethods.neoastra_window_get_state(NativeHandle, &native), default, "get window state");
            return (NeoWindowState)native.Value;
        }
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            ThrowIfDisposed();
            NativeError.ThrowIfFailed(NativeMethods.neoastra_window_set_state(NativeHandle, (NativeMethods.neoastra_window_state)value), default, "set window state");
        }
    }

    /// <summary>Gets or sets whether the window has normal platform decorations.</summary>
    public bool HasDecorations
    {
        get => GetAttribute(NativeMethods.neoastra_window_attribute.NEOASTRA_WINDOW_DECORATED);
        set => SetAttribute(NativeMethods.neoastra_window_attribute.NEOASTRA_WINDOW_DECORATED, value);
    }

    /// <summary>Gets or sets whether the user can resize the window.</summary>
    public bool IsResizable
    {
        get => GetAttribute(NativeMethods.neoastra_window_attribute.NEOASTRA_WINDOW_RESIZABLE);
        set => SetAttribute(NativeMethods.neoastra_window_attribute.NEOASTRA_WINDOW_RESIZABLE, value);
    }

    /// <summary>Gets or sets whether the window remains above ordinary windows.</summary>
    public bool IsAlwaysOnTop
    {
        get => GetAttribute(NativeMethods.neoastra_window_attribute.NEOASTRA_WINDOW_ALWAYS_ON_TOP);
        set => SetAttribute(NativeMethods.neoastra_window_attribute.NEOASTRA_WINDOW_ALWAYS_ON_TOP, value);
    }

    /// <summary>Gets or sets whether the window appears in the platform's per-window task switcher.</summary>
    /// <exception cref="NotSupportedException">The active backend does not expose per-window task-switcher membership.</exception>
    public bool ShowInTaskbar
    {
        get => GetAttribute(NativeMethods.neoastra_window_attribute.NEOASTRA_WINDOW_SHOW_IN_TASKBAR);
        set => SetAttribute(NativeMethods.neoastra_window_attribute.NEOASTRA_WINDOW_SHOW_IN_TASKBAR, value);
    }

    /// <summary>Gets or sets how the window presents its title bar.</summary>
    /// <remarks>
    /// <see cref="NeoWindowTitleBarStyle.Overlay"/> and <see cref="NeoWindowTitleBarStyle.Hidden"/> extend the content into
    /// the title-bar area while keeping the platform frame. Use <see cref="GetTitleBarLayout"/> to keep content clear of
    /// native window controls.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The style is not defined or the height is out of range.</exception>
    /// <exception cref="NotSupportedException">The loaded native library does not provide title-bar styles.</exception>
    public NeoWindowTitleBar TitleBar
    {
        get => _titleBar;
        set
        {
            value.Validate(nameof(value));
            ThrowIfDisposed();
            ApplyTitleBar(value);
            _titleBar = value;
        }
    }

    /// <summary>Gets the owner window, if any.</summary>
    public NeoWindow? Owner => _owner;

    /// <summary>Gets whether this window uses owner-modal input semantics without a nested application loop.</summary>
    public bool IsModal { get; }

    internal int OwnerDepth
    {
        get
        {
            var depth = 0;
            for (var owner = _owner; owner is not null; owner = owner._owner) depth++;
            return depth;
        }
    }

    /// <summary>Occurs when the native window receives a close request.</summary>
    public event EventHandler<NeoWindowClosingEventArgs>? Closing;

    /// <summary>Registers ordered asynchronous close handlers. Any cancellation or exception preserves the window.</summary>
    public event Func<NeoWindowCloseRequest, ValueTask>? CloseRequested;

    /// <summary>Occurs once after the native window has closed.</summary>
    public event EventHandler? Closed;

    /// <summary>Occurs when the position or the client size changes.</summary>
    public event EventHandler<NeoWindowBoundsChangedEventArgs>? BoundsChanged;

    /// <summary>Occurs when the window position changes.</summary>
    public event EventHandler<NeoWindowPositionChangedEventArgs>? PositionChanged;

    /// <summary>Occurs when the client size changes, also when the user, the screen, or the state of the window gave it the size.</summary>
    public event EventHandler<NeoWindowClientSizeChangedEventArgs>? ClientSizeChanged;

    /// <summary>Occurs when <see cref="ScaleFactor"/> changes, as when the window moves to a display with another scale.</summary>
    public event EventHandler<NeoWindowScaleFactorChangedEventArgs>? ScaleFactorChanged;

    /// <summary>Occurs when keyboard focus changes.</summary>
    public event EventHandler? FocusChanged;

    /// <summary>Occurs when the window gains keyboard focus.</summary>
    public event EventHandler? Activated;

    /// <summary>Occurs when the window loses keyboard focus.</summary>
    public event EventHandler? Deactivated;

    /// <summary>Occurs when the effective native presentation state changes.</summary>
    public event EventHandler<NeoWindowStateChangedEventArgs>? StateChanged;

    /// <summary>Occurs when the effective native state becomes minimized.</summary>
    public event EventHandler? Minimized;

    /// <summary>Occurs when the effective native state becomes maximized.</summary>
    public event EventHandler? Maximized;

    /// <summary>Occurs when the effective native state becomes normal.</summary>
    public event EventHandler? Restored;

    /// <summary>Occurs when the effective native state enters fullscreen.</summary>
    public event EventHandler? FullscreenEntered;

    /// <summary>Occurs when the effective native state leaves fullscreen.</summary>
    public event EventHandler? FullscreenExited;

    /// <summary>Shows the window.</summary>
    public void Show()
    {
        ThrowIfDisposed();
        NativeError.ThrowIfFailed(NativeMethods.neoastra_window_show(NativeHandle), default, "show window");
        _isVisible = true;
    }

    /// <summary>Hides the window.</summary>
    public void Hide()
    {
        ThrowIfDisposed();
        NativeError.ThrowIfFailed(NativeMethods.neoastra_window_hide(NativeHandle), default, "hide window");
        _isVisible = false;
    }

    /// <summary>Requests foreground activation.</summary>
    public void Activate()
    {
        ThrowIfDisposed();
        NativeError.ThrowIfFailed(NativeMethods.neoastra_window_activate(NativeHandle), default, "activate window");
    }

    /// <summary>Requests foreground activation and places the window in front of ordinary windows.</summary>
    public void BringToFront() => Activate();

    /// <summary>Requests the maximized native state.</summary>
    public void Maximize() => State = NeoWindowState.Maximized;

    /// <summary>Requests the minimized native state.</summary>
    public void Minimize() => State = NeoWindowState.Minimized;

    /// <summary>Requests the normal restored native state.</summary>
    public void Restore() => State = NeoWindowState.Normal;

    /// <summary>Requests the fullscreen native state.</summary>
    public void EnterFullscreen() => State = NeoWindowState.Fullscreen;

    /// <summary>Leaves fullscreen by requesting the normal restored state.</summary>
    public void ExitFullscreen()
    {
        if (State == NeoWindowState.Fullscreen) State = NeoWindowState.Normal;
    }

    /// <summary>Starts a native interactive move using the current pointer event.</summary>
    /// <remarks>Call this synchronously from a trusted pointer-press handler used by a custom title bar.</remarks>
    /// <exception cref="InvalidOperationException">No suitable native pointer event is active.</exception>
    /// <exception cref="NotSupportedException">The active backend does not support native interactive moves.</exception>
    public void BeginDrag()
    {
        ThrowIfDisposed();
        NativeError.ThrowIfFailed(NativeMethods.neoastra_window_begin_drag(NativeHandle), default, "begin interactive window drag");
    }

    /// <summary>Starts a native interactive resize using the current pointer event.</summary>
    /// <param name="edge">The edge or corner being dragged.</param>
    /// <remarks>Call this synchronously from a trusted pointer-press handler used by a custom window frame.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="edge"/> is not defined.</exception>
    /// <exception cref="InvalidOperationException">No suitable native pointer event is active.</exception>
    /// <exception cref="NotSupportedException">The active backend does not support native interactive resizing.</exception>
    public void BeginResize(NeoWindowResizeEdge edge)
    {
        if (!Enum.IsDefined(edge)) throw new ArgumentOutOfRangeException(nameof(edge));
        ThrowIfDisposed();
        NativeError.ThrowIfFailed(NativeMethods.neoastra_window_begin_resize(NativeHandle, (NativeMethods.neoastra_window_resize_edge)edge), default, "begin interactive window resize");
    }

    /// <summary>Gets the title-bar space that content must currently account for.</summary>
    /// <returns>
    /// The effective style, the title-bar height, and the insets covered by native window controls. The insets are zero
    /// while no native controls cover the content, such as in fullscreen.
    /// </returns>
    public unsafe NeoWindowTitleBarLayout GetTitleBarLayout()
    {
        ThrowIfDisposed();
        var native = new NativeMethods.neoastra_title_bar_t(new NativeMethods.neoastra_title_bar
        {
            size = (uint)sizeof(NativeMethods.neoastra_title_bar),
            version = 1,
        });
        try
        {
            NativeError.ThrowIfFailed(NativeMethods.neoastra_window_get_title_bar(NativeHandle, &native), default, "get window title bar");
        }
        catch (EntryPointNotFoundException)
        {
            // An older native library always draws the standard title bar.
            return default;
        }

        var value = native.Value;
        return new NeoWindowTitleBarLayout((NeoWindowTitleBarStyle)value.style.Value, value.height, value.left_inset, value.right_inset);
    }

    /// <summary>Requests that the native window close.</summary>
    public void Close()
    {
        ThrowIfDisposed();
        NativeError.ThrowIfFailed(NativeMethods.neoastra_window_close(NativeHandle), default, "close window");
    }

    /// <summary>Gets a typed borrowed native handle.</summary>
    /// <param name="kind">The requested backend handle kind.</param>
    /// <returns>A borrowed native handle valid while this window remains alive.</returns>
    public unsafe NeoNativeHandle GetNativeHandle(NeoNativeHandleKind kind)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var raw = new NativeMethods.neoastra_native_handle
        {
            size = (uint)sizeof(NativeMethods.neoastra_native_handle),
            version = 1,
            kind = (NativeMethods.neoastra_native_handle_kind)kind,
        };
        var native = new NativeMethods.neoastra_native_handle_t(raw);
        var result = NativeMethods.neoastra_window_get_native_handle(NativeHandle, (NativeMethods.neoastra_native_handle_kind)kind, &native);
        NativeError.ThrowIfFailed(result, default, "get window native handle");
        return new NeoNativeHandle((NeoNativeHandleKind)native.Value.kind.Value, (nint)native.Value.value);
    }

    /// <summary>Authoritatively closes the window if necessary and releases its native reference. Use <see cref="Close"/> to run cancelable close policy.</summary>
    /// <returns>A completed value task.</returns>
    public ValueTask DisposeAsync()
    {
        DisposeCore(requestClose: true);
        return ValueTask.CompletedTask;
    }

    internal NeoApplication Application { get; }

    internal NativeMethods.neoastra_window_t NativeHandle
    {
        get
        {
            ThrowIfDisposed();
            return new(_handle.DangerousGetHandle());
        }
    }

    internal void DisposeFromApplication() => DisposeCore(requestClose: false);

    internal void OnClosing(nint nativeDecision, NeoWindowCloseReason reason, bool canCancel, ulong deadlineNanoseconds,
        Func<CancellationToken, ValueTask<bool>>? evaluate = null)
    {
        if (nativeDecision == 0) return;
        NativeMethods.neoastra_decision_retain(new(nativeDecision));
        var decision = new SafeDecisionHandle(nativeDecision);
        if (NativeError.Code(NativeMethods.neoastra_decision_defer(new(nativeDecision))) != NeoErrorCode.Success)
        {
            decision.Dispose();
            return;
        }
        _ = CompleteNativeCloseAsync(decision, reason, canCancel, deadlineNanoseconds, evaluate);
    }

    internal async ValueTask<bool> EvaluateCloseAsync(NeoWindowCloseReason reason, bool canCancel, CancellationToken cancellationToken)
    {
        var request = new NeoWindowCloseRequest(reason, canCancel, cancellationToken);
        var legacy = new NeoWindowClosingEventArgs();
        try { Closing?.Invoke(this, legacy); }
        catch (Exception exception) { Application?.ReportLifecycleFailure("window.close", exception, Id); request.Cancel(); }
        if (legacy.Cancel) request.Cancel();
        var handlers = CloseRequested;
        if (handlers is not null)
        {
            foreach (var handler in handlers.GetInvocationList().Cast<Func<NeoWindowCloseRequest, ValueTask>>())
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await handler(request).AsTask().WaitAsync(cancellationToken).ConfigureAwait(true);
                }
                catch (Exception exception)
                {
                    Application?.ReportLifecycleFailure("window.close", exception, Id);
                    request.Cancel();
                    break;
                }
                if (request.IsCanceled) break;
            }
        }
        return !request.IsCanceled || !canCancel;
    }

    internal void ForceCloseFromApplication()
    {
        if (Volatile.Read(ref _closed) != 0 || Volatile.Read(ref _disposed) != 0) return;
        NativeError.ThrowIfFailed(NativeMethods.neoastra_window_force_close(NativeHandle), default, "force close window after approved quit");
    }

    private async Task CompleteNativeCloseAsync(SafeDecisionHandle decision, NeoWindowCloseReason reason, bool canCancel, ulong deadlineNanoseconds,
        Func<CancellationToken, ValueTask<bool>>? evaluate)
    {
        var remaining = TimeSpan.FromSeconds(30);
        if (deadlineNanoseconds != 0)
        {
            var now = (ulong)(Stopwatch.GetTimestamp() * (1_000_000_000d / Stopwatch.Frequency));
            remaining = deadlineNanoseconds > now ? TimeSpan.FromTicks(checked((long)Math.Min((deadlineNanoseconds - now) / 100, (ulong)TimeSpan.FromMinutes(10).Ticks))) : TimeSpan.Zero;
        }
        using var deadline = new CancellationTokenSource(remaining <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : remaining);
        await CompleteCloseEvaluationAsync(
            evaluate is null ? token => EvaluateCloseAsync(reason, canCancel, token) : evaluate,
            canCancel,
            deadline.Token,
            allowed => Application.Dispatcher.InvokeAsync(() => CompleteCloseDecision(decision, allowed)),
            decision.Dispose).ConfigureAwait(false);
    }

    internal async Task CompleteCloseEvaluationAsync(Func<CancellationToken, ValueTask<bool>> evaluate, bool canCancel,
        CancellationToken cancellationToken, Func<bool, ValueTask> complete, Action release)
    {
        var allowed = !canCancel;
        try
        {
            allowed = await evaluate(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Application?.ReportLifecycleFailure("window.close", exception, Id);
        }
        try
        {
            await complete(allowed).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Native timeout and application stopping both preserve ordinary unsaved work.
            Application?.ReportLifecycleFailure("window.close-completion", exception, Id);
        }
        finally { release(); }
    }

    private static unsafe void CompleteCloseDecision(SafeDecisionHandle decision, bool allowed)
    {
        var response = new NativeMethods.neoastra_decision_response_t(new NativeMethods.neoastra_decision_response
        {
            size = (uint)sizeof(NativeMethods.neoastra_decision_response),
            version = 1,
            action = allowed ? NativeMethods.neoastra_decision_action.NEOASTRA_DECISION_ALLOW : NativeMethods.neoastra_decision_action.NEOASTRA_DECISION_CANCEL,
            selected_index = uint.MaxValue,
        });
        NativeMethods.neoastra_error_t error = default;
        var result = NativeMethods.neoastra_decision_complete(new(decision.DangerousGetHandle()), &response, &error);
        if (error.Handle != 0) new SafeErrorHandle(error.Handle).Dispose();
        if (NativeError.Code(result) is not (NeoErrorCode.Success or NeoErrorCode.InvalidState or NeoErrorCode.TimedOut))
            throw new NeoAstraException(NativeError.Code(result), "Unable to complete native close decision.", "complete window close");
    }

    internal bool OnClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return false;
        }

        _isVisible = false;
        try { Closed?.Invoke(this, EventArgs.Empty); } catch { }
        return true;
    }

    internal void OnBoundsChanged()
    {
        try
        {
            var oldBounds = _bounds;
            var newBounds = GetBounds();
            if (newBounds != oldBounds)
            {
                try { BoundsChanged?.Invoke(this, new NeoWindowBoundsChangedEventArgs(oldBounds, newBounds)); } catch { }
                if (newBounds.Position != oldBounds.Position)
                    try { PositionChanged?.Invoke(this, new NeoWindowPositionChangedEventArgs(oldBounds.Position, newBounds.Position)); } catch { }
                if (newBounds.Size != oldBounds.Size)
                    try { ClientSizeChanged?.Invoke(this, new NeoWindowClientSizeChangedEventArgs(oldBounds.Size, newBounds.Size)); } catch { }
            }
        }
        catch
        {
            // Native events and user callbacks are contained.
        }
    }

    internal void OnFocusChanged(bool focused)
    {
        if (_isFocused == focused) return;
        _isFocused = focused;
        try { FocusChanged?.Invoke(this, EventArgs.Empty); } catch { }
        try { (focused ? Activated : Deactivated)?.Invoke(this, EventArgs.Empty); } catch { }
    }

    internal void OnScaleFactorChanged(double scaleFactor)
    {
        var old = _scaleFactor;
        // The event carries thousandths; the window has the exact value.
        try { scaleFactor = ReadScaleFactor(scaleFactor); } catch (ObjectDisposedException) { }
        if (scaleFactor == old) return;
        _scaleFactor = scaleFactor;
        try { ScaleFactorChanged?.Invoke(this, new NeoWindowScaleFactorChangedEventArgs(old, scaleFactor)); } catch { }
    }

    internal void OnStateChanged(NeoWindowState state)
    {
        var previous = _state;
        _state = state;
        if (previous == state) return;
        try { StateChanged?.Invoke(this, new(previous, state)); } catch { }
        if (previous == NeoWindowState.Fullscreen && state != NeoWindowState.Fullscreen)
            try { FullscreenExited?.Invoke(this, EventArgs.Empty); } catch { }
        try
        {
            switch (state)
            {
                case NeoWindowState.Normal: Restored?.Invoke(this, EventArgs.Empty); break;
                case NeoWindowState.Minimized: Minimized?.Invoke(this, EventArgs.Empty); break;
                case NeoWindowState.Maximized: Maximized?.Invoke(this, EventArgs.Empty); break;
                case NeoWindowState.Fullscreen: FullscreenEntered?.Invoke(this, EventArgs.Empty); break;
            }
        }
        catch { }
    }

    private unsafe bool GetAttribute(NativeMethods.neoastra_window_attribute attribute)
    {
        ThrowIfDisposed();
        uint value;
        NativeError.ThrowIfFailed(NativeMethods.neoastra_window_get_attribute(NativeHandle, attribute, &value), default, "get window attribute");
        return value != 0;
    }

    private void SetAttribute(NativeMethods.neoastra_window_attribute attribute, bool value)
    {
        ThrowIfDisposed();
        NativeError.ThrowIfFailed(NativeMethods.neoastra_window_set_attribute(NativeHandle, attribute, value ? 1u : 0u), default, "set window attribute");
    }

    private unsafe void ApplyTitleBar(NeoWindowTitleBar value)
    {
        var native = new NativeMethods.neoastra_title_bar_t(new NativeMethods.neoastra_title_bar
        {
            size = (uint)sizeof(NativeMethods.neoastra_title_bar),
            version = 1,
            style = (NativeMethods.neoastra_title_bar_style)value.Style,
            height = value.Height,
            symbol_color = ToNative(value.SymbolColor),
            background_color = ToNative(value.BackgroundColor),
        });
        try
        {
            NativeError.ThrowIfFailed(NativeMethods.neoastra_window_set_title_bar(NativeHandle, &native), default, "set window title bar");
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new NotSupportedException("The loaded native library does not provide title-bar styles.", exception);
        }

        static NativeMethods.neoastra_color ToNative(NeoColor color) => new() { red = color.Red, green = color.Green, blue = color.Blue, alpha = color.Alpha };
    }

    private unsafe double ReadScaleFactor(double fallback)
    {
        double value;
        return NativeError.Code(NativeMethods.neoastra_window_get_scale_factor(NativeHandle, &value)) == NeoErrorCode.Success && double.IsFinite(value) && value > 0
            ? value
            : fallback;
    }

    private unsafe NeoRect GetBounds()
    {
        ThrowIfDisposed();
        var native = new NativeMethods.neoastra_rect_t(default);
        var result = NativeMethods.neoastra_window_get_bounds(NativeHandle, &native);
        NativeError.ThrowIfFailed(result, default, "get window bounds");
        var value = native.Value;
        _bounds = new NeoRect(value.x, value.y, value.width, value.height);
        return _bounds;
    }

    private void SetBounds(NeoRect value)
    {
        ThrowIfDisposed();
        var native = new NativeMethods.neoastra_rect
        {
            x = value.X,
            y = value.Y,
            width = value.Width,
            height = value.Height,
        };
        NativeError.ThrowIfFailed(NativeMethods.neoastra_window_set_bounds(NativeHandle, native), default, "set window bounds");
        _bounds = value;
    }

    private void DisposeCore(bool requestClose)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (requestClose && Volatile.Read(ref _closed) == 0)
        {
            try
            {
                // Disposal cannot retain a usable managed object for asynchronous negotiation.
                // Explicit Close() remains the cancelable path; owned disposal is authoritative.
                NativeMethods.neoastra_window_force_close(new(_handle.DangerousGetHandle()));
            }
            catch
            {
                // Releasing the safe handle remains required even when close cannot be posted.
            }
        }

        _handle.Dispose();
        if (requestClose)
        {
            Application.OnManagedWindowDisposed(this);
        }
        else
        {
            OnClosed();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
