// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using NeoAstra.Interop;
using NeoAstra.Interop.Generated;

namespace NeoAstra;

/// <summary>Represents a NeoAstra-owned top-level native window.</summary>
public sealed unsafe class NeoWindow : IAsyncDisposable
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
    private int _closed;
    private int _disposed;

    internal NeoWindow(NeoApplication application, SafeWindowHandle handle, NeoWindowOptions options)
    {
        Application = application;
        _handle = handle;
        _owner = options.Owner;
        _bounds = new NeoRect(options.X, options.Y, options.Width, options.Height);
        _minimumClientSize = options.MinimumClientSize;
        _maximumClientSize = options.MaximumClientSize;
        _title = options.Title;
        _isVisible = options.IsVisible;
        _state = options.State;
        Id = NativeMethods.neoastra_window_get_id(NativeHandle);
    }

    /// <summary>Gets the stable application-local window identifier.</summary>
    public ulong Id { get; }

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

    /// <summary>Gets or sets the window position in logical units.</summary>
    public NeoPoint Position
    {
        get => GetBounds().Position;
        set
        {
            var bounds = GetBounds();
            SetBounds(new NeoRect(value.X, value.Y, bounds.Width, bounds.Height));
        }
    }

    /// <summary>Gets or sets the client size in logical units.</summary>
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

    /// <summary>Gets or sets the native minimum client-size constraint.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is negative.</exception>
    /// <exception cref="ArgumentException">The minimum exceeds the configured maximum.</exception>
    public NeoSize MinimumClientSize
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

    /// <summary>Gets or sets the native maximum client-size constraint. Zero disables a dimension's maximum.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is negative.</exception>
    /// <exception cref="ArgumentException">The maximum is less than the configured minimum.</exception>
    public NeoSize MaximumClientSize
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

    /// <summary>Gets the current logical-to-physical scale factor.</summary>
    public double ScaleFactor => _scaleFactor;

    /// <summary>Gets or sets the native window presentation state.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is not defined.</exception>
    public NeoWindowState State
    {
        get
        {
            ThrowIfDisposed();
            NativeMethods.neoastra_window_state_t native;
            NativeError.ThrowIfFailed(NativeMethods.neoastra_window_get_state(NativeHandle, &native), default, "get window state");
            _state = (NeoWindowState)native.Value;
            return _state;
        }
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            ThrowIfDisposed();
            NativeError.ThrowIfFailed(NativeMethods.neoastra_window_set_state(NativeHandle, (NativeMethods.neoastra_window_state)value), default, "set window state");
            _state = value;
        }
    }

    /// <summary>Gets the owner window, if any.</summary>
    public NeoWindow? Owner => _owner;

    /// <summary>Occurs when the native window receives a close request.</summary>
    public event EventHandler<NeoWindowClosingEventArgs>? Closing;

    /// <summary>Occurs once after the native window has closed.</summary>
    public event EventHandler? Closed;

    /// <summary>Occurs when the logical position or client size changes.</summary>
    public event EventHandler<NeoWindowBoundsChangedEventArgs>? BoundsChanged;

    /// <summary>Occurs when the effective scale factor changes.</summary>
    public event EventHandler<NeoWindowScaleFactorChangedEventArgs>? ScaleFactorChanged;

    /// <summary>Occurs when keyboard focus changes.</summary>
    public event EventHandler? FocusChanged;

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

    /// <summary>Requests that the native window close.</summary>
    public void Close()
    {
        ThrowIfDisposed();
        NativeError.ThrowIfFailed(NativeMethods.neoastra_window_close(NativeHandle), default, "close window");
    }

    /// <summary>Gets a typed borrowed native handle.</summary>
    /// <param name="kind">The requested backend handle kind.</param>
    /// <returns>A borrowed native handle valid while this window remains alive.</returns>
    public NeoNativeHandle GetNativeHandle(NeoNativeHandleKind kind)
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

    /// <summary>Closes the window if necessary and releases its native reference.</summary>
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

    internal void OnClosing()
    {
        var args = new NeoWindowClosingEventArgs();
        try { Closing?.Invoke(this, args); } catch { }
        // The current native ABI exposes a notification but no cancelable window-close decision.
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
                BoundsChanged?.Invoke(this, new NeoWindowBoundsChangedEventArgs(oldBounds, newBounds));
            }
        }
        catch
        {
            // Native events and user callbacks are contained.
        }
    }

    internal void OnFocusChanged(bool focused)
    {
        _isFocused = focused;
        try { FocusChanged?.Invoke(this, EventArgs.Empty); } catch { }
    }

    internal void OnScaleFactorChanged(double scaleFactor)
    {
        var old = _scaleFactor;
        _scaleFactor = scaleFactor;
        try { ScaleFactorChanged?.Invoke(this, new NeoWindowScaleFactorChangedEventArgs(old, scaleFactor)); } catch { }
    }

    internal void OnStateChanged(NeoWindowState state) => _state = state;

    private NeoRect GetBounds()
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
                NativeMethods.neoastra_window_close(new(_handle.DangerousGetHandle()));
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
