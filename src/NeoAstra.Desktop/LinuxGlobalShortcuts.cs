// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NeoAstra.Desktop.GlobalShortcuts;

internal sealed unsafe partial class LinuxX11GlobalShortcutPresenter(NeoDispatcher? dispatcher) : INeoGlobalShortcutPresenter, INeoApplicationBoundDesktopService
{
    private static readonly object XLock = new();
    private static LinuxX11GlobalShortcutPresenter? _errorOwner;
    private readonly Dictionary<string, (uint KeyCode, uint Modifiers)> _registrations = new(StringComparer.Ordinal);
    private readonly Dictionary<(uint KeyCode, uint Modifiers), string> _nativeIds = [];
    private NeoDispatcher? _dispatcher = dispatcher;
    private nint _display;
    private nuint _root;
    private Timer? _timer;
    private bool _grabFailed;
    private bool _disposed;
    private int _pumping;

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Limited, 1, 0, "Native X11 passive key grabs with conflict detection and bounded teardown. Native Wayland compositors intentionally deny this X11-global semantic unless XWayland exposes it; no synthetic input fallback is used.");
    public event EventHandler<string>? Activated;

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_dispatcher is not null && !ReferenceEquals(_dispatcher, application.Dispatcher)) throw new InvalidOperationException("The global-shortcut presenter is already bound to another dispatcher.");
        _dispatcher = application.Dispatcher;
    }

    public NeoDesktopStatus Register(string id, string normalizedAccelerator)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureDisplay();
        if (_display == 0 || !TryTranslate(normalizedAccelerator, out var keySym, out var modifiers)) return _display == 0 ? NeoDesktopStatus.Unsupported : NeoDesktopStatus.Unsupported;
        lock (XLock)
        {
            var keyCode = Native.XKeysymToKeycode(_display, keySym); if (keyCode == 0) return NeoDesktopStatus.Unsupported;
            _grabFailed = false; _errorOwner = this;
            var previous = Native.XSetErrorHandler((nint)(delegate* unmanaged<nint, XErrorEvent*, int>)&ErrorHandler);
            try
            {
                foreach (var mask in LockVariants) Native.XGrabKey(_display, (int)keyCode, modifiers | mask, _root, true, 1, 1);
                _ = Native.XSync(_display, false);
            }
            finally { _ = Native.XSetErrorHandler(previous); _errorOwner = null; }
            if (_grabFailed)
            {
                foreach (var mask in LockVariants) Native.XUngrabKey(_display, (int)keyCode, modifiers | mask, _root);
                _ = Native.XSync(_display, false); return NeoDesktopStatus.Conflict;
            }
            _registrations.Add(id, (keyCode, modifiers)); _nativeIds[(keyCode, modifiers)] = id;
            _timer ??= new Timer(static state => ((LinuxX11GlobalShortcutPresenter)state!).Pump(), this, TimeSpan.Zero, TimeSpan.FromMilliseconds(20));
            return NeoDesktopStatus.Success;
        }
    }

    public bool Unregister(string id)
    {
        lock (XLock)
        {
            if (!_registrations.Remove(id, out var value)) return false;
            _nativeIds.Remove(value);
            foreach (var mask in LockVariants) Native.XUngrabKey(_display, (int)value.KeyCode, value.Modifiers | mask, _root);
            _ = Native.XSync(_display, false); return true;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (XLock)
        {
            if (_disposed) return ValueTask.CompletedTask; _disposed = true; _timer?.Dispose(); _timer = null;
            if (_display != 0)
            {
                foreach (var value in _registrations.Values) foreach (var mask in LockVariants) Native.XUngrabKey(_display, (int)value.KeyCode, value.Modifiers | mask, _root);
                _registrations.Clear(); _nativeIds.Clear(); _ = Native.XSync(_display, false); _ = Native.XCloseDisplay(_display); _display = 0;
            }
            Activated = null;
        }
        return ValueTask.CompletedTask;
    }

    private void EnsureDisplay()
    {
        if (_display != 0) return;
        lock (XLock)
        {
            if (_display != 0) return;
            try { _ = Native.XInitThreads(); _display = Native.XOpenDisplay(0); if (_display != 0) _root = Native.XDefaultRootWindow(_display); }
            catch (DllNotFoundException) { _display = 0; }
        }
    }

    private void Pump()
    {
        if (Interlocked.Exchange(ref _pumping, 1) != 0) return;
        try
        {
            List<string>? ids = null;
            lock (XLock)
            {
                if (_disposed || _display == 0) return;
                var processed = 0;
                while (processed++ < 64 && Native.XPending(_display) > 0)
                {
                    var value = default(XKeyEvent); _ = Native.XNextEvent(_display, &value);
                    if (value.Type != 2) continue;
                    var modifiers = value.State & ~(2u | 16u);
                    if (_nativeIds.TryGetValue((value.KeyCode, modifiers), out var id)) (ids ??= []).Add(id);
                }
            }
            foreach (var id in ids ?? []) Raise(id);
        }
        catch { }
        finally { Volatile.Write(ref _pumping, 0); }
    }

    private void Raise(string id)
    {
        void Invoke() { try { Activated?.Invoke(this, id); } catch { } }
        var value = _dispatcher; if (value is not null && !value.CheckAccess()) { try { _ = value.InvokeAsync(Invoke); } catch { } } else Invoke();
    }

    [UnmanagedCallersOnly]
    private static int ErrorHandler(nint display, XErrorEvent* error)
    {
        try { if (error->ErrorCode == 10 && _errorOwner is { } owner) owner._grabFailed = true; } catch { }
        return 0;
    }

    private static bool TryTranslate(string accelerator, out nuint keySym, out uint modifiers)
    {
        keySym = 0; modifiers = 0; var parts = accelerator.Split('+');
        foreach (var modifier in parts[..^1]) modifiers |= modifier switch { "Ctrl" => 4u, "Alt" => 8u, "Shift" => 1u, "Meta" => 64u, _ => 0 };
        var name = parts[^1] switch { "Escape" => "Escape", "Space" => "space", "Enter" => "Return", "Delete" => "Delete", "Left" => "Left", "Right" => "Right", "Up" => "Up", "Down" => "Down", var value => value };
        keySym = Native.XStringToKeysym(name); return keySym != 0;
    }

    private static readonly uint[] LockVariants = [0, 2, 16, 18];
    [StructLayout(LayoutKind.Sequential)] private struct XErrorEvent { internal int Type; internal nint Display; internal nuint ResourceId; internal nuint Serial; internal byte ErrorCode; internal byte RequestCode; internal byte MinorCode; }
    [StructLayout(LayoutKind.Sequential)] private struct XKeyEvent
    {
        internal int Type; private int _padding; internal nuint Serial; internal int SendEvent; private int _padding2; internal nint Display; internal nuint Window; internal nuint Root; internal nuint Subwindow; internal nuint Time;
        internal int X; internal int Y; internal int XRoot; internal int YRoot; internal uint State; internal uint KeyCode; internal int SameScreen; private fixed byte _remaining[96];
    }
    private static partial class Native
    {
        [LibraryImport("libX11.so.6")] internal static partial int XInitThreads();
        [LibraryImport("libX11.so.6")] internal static partial nint XOpenDisplay(nint displayName);
        [LibraryImport("libX11.so.6")] internal static partial int XCloseDisplay(nint display);
        [LibraryImport("libX11.so.6")] internal static partial nuint XDefaultRootWindow(nint display);
        [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)] internal static partial nuint XStringToKeysym(string name);
        [LibraryImport("libX11.so.6")] internal static partial uint XKeysymToKeycode(nint display, nuint keysym);
        [LibraryImport("libX11.so.6")] internal static partial int XGrabKey(nint display, int keycode, uint modifiers, nuint window, [MarshalAs(UnmanagedType.Bool)] bool ownerEvents, int pointerMode, int keyboardMode);
        [LibraryImport("libX11.so.6")] internal static partial int XUngrabKey(nint display, int keycode, uint modifiers, nuint window);
        [LibraryImport("libX11.so.6")] internal static partial int XSync(nint display, [MarshalAs(UnmanagedType.Bool)] bool discard);
        [LibraryImport("libX11.so.6")] internal static partial nint XSetErrorHandler(nint handler);
        [LibraryImport("libX11.so.6")] internal static partial int XPending(nint display);
        [LibraryImport("libX11.so.6")] internal static partial int XNextEvent(nint display, XKeyEvent* value);
    }
}
