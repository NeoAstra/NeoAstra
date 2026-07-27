// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NeoAstra.Desktop.GlobalShortcuts;

internal sealed unsafe partial class WindowsGlobalShortcutPresenter(NeoDispatcher? dispatcher) : INeoGlobalShortcutPresenter, INeoApplicationBoundDesktopService
{
    private const uint WmHotKey = 0x0312;
    private const int WindowProcedureIndex = -4;
    private static readonly ConcurrentDictionary<nint, WindowsGlobalShortcutPresenter> Presenters = new();
    private readonly Dictionary<string, int> _nativeIds = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _ids = [];
    private NeoDispatcher? _dispatcher = dispatcher;
    private nint _window;
    private nint _previousProcedure;
    private int _nextId;
    private bool _disposed;

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Native, 1, 0,
        "Win32 RegisterHotKey with conflict reporting, no-repeat semantics, a UI-thread message-only owner window, ordered activation, and deterministic unregister on disposal.");

    public event EventHandler<string>? Activated;

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_dispatcher is not null && !ReferenceEquals(_dispatcher, application.Dispatcher)) throw new InvalidOperationException("The shortcut presenter is already bound to another dispatcher.");
        _dispatcher = application.Dispatcher;
    }

    public NeoDesktopStatus Register(string id, string normalizedAccelerator) => Invoke(() => RegisterOnDispatcher(id, normalizedAccelerator));

    public bool Unregister(string id) => Invoke(() => UnregisterOnDispatcher(id));

    public ValueTask DisposeAsync()
    {
        Activated = null;
        var value = _dispatcher;
        if (value is null) { _disposed = true; return ValueTask.CompletedTask; }
        if (!value.CheckAccess()) return value.InvokeAsync(DisposeOnDispatcher);
        DisposeOnDispatcher();
        return ValueTask.CompletedTask;
    }

    private NeoDesktopStatus RegisterOnDispatcher(string id, string accelerator)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureWindow();
        if (!TryParse(accelerator, out var modifiers, out var key)) return NeoDesktopStatus.Denied;
        var nativeId = checked(++_nextId);
        if (!Native.RegisterHotKey(_window, nativeId, modifiers | 0x4000, key)) return Marshal.GetLastPInvokeError() == 1409 ? NeoDesktopStatus.Conflict : NeoDesktopStatus.Failed;
        _nativeIds.Add(id, nativeId); _ids.Add(nativeId, id);
        return NeoDesktopStatus.Success;
    }

    private bool UnregisterOnDispatcher(string id)
    {
        if (!_nativeIds.TryGetValue(id, out var nativeId)) return false;
        var result = Native.UnregisterHotKey(_window, nativeId);
        _nativeIds.Remove(id); _ids.Remove(nativeId);
        return result;
    }

    private void EnsureWindow()
    {
        if (_window != 0) return;
        _window = Native.CreateWindowEx(0, "STATIC", string.Empty, 0, 0, 0, 0, 0, -3, 0, 0, 0);
        if (_window == 0) throw new InvalidOperationException("Unable to create the global-shortcut message window.");
        _previousProcedure = Native.SetWindowLongPtr(_window, WindowProcedureIndex, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProcedure);
        if (_previousProcedure == 0)
        {
            _ = Native.DestroyWindow(_window); _window = 0;
            throw new InvalidOperationException("Unable to attach the global-shortcut window procedure.");
        }
        Presenters[_window] = this;
    }

    private void DisposeOnDispatcher()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var nativeId in _nativeIds.Values) _ = Native.UnregisterHotKey(_window, nativeId);
        _nativeIds.Clear(); _ids.Clear();
        if (_window != 0)
        {
            Presenters.TryRemove(_window, out _);
            if (_previousProcedure != 0) _ = Native.SetWindowLongPtr(_window, WindowProcedureIndex, _previousProcedure);
            _ = Native.DestroyWindow(_window);
            _window = 0; _previousProcedure = 0;
        }
    }

    private T Invoke<T>(Func<T> callback)
    {
        var value = _dispatcher ?? throw new InvalidOperationException("The Windows global-shortcut presenter must be bound to the NeoAstra UI dispatcher before use.");
        if (!value.CheckAccess()) throw new InvalidOperationException("Global shortcut mutations must be called on the NeoAstra UI dispatcher; renderer handlers use UI-thread dispatch.");
        return callback();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (Presenters.TryGetValue(window, out var presenter))
            {
                if (message == WmHotKey && presenter._ids.TryGetValue(checked((int)wParam), out var id))
                {
                    try { presenter.Activated?.Invoke(presenter, id); } catch { }
                    return 0;
                }
                if (presenter._previousProcedure != 0) return Native.CallWindowProc(presenter._previousProcedure, window, message, wParam, lParam);
            }
        }
        catch { }
        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    private static bool TryParse(string value, out uint modifiers, out uint key)
    {
        modifiers = 0; key = 0;
        var parts = value.Split('+');
        for (var index = 0; index < parts.Length - 1; index++) modifiers |= parts[index] switch { "Alt" => 1u, "Ctrl" => 2u, "Shift" => 4u, "Meta" => 8u, _ => 0u };
        var name = parts[^1];
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0])) { key = char.ToUpperInvariant(name[0]); return true; }
        if (name.Length is 2 or 3 && name[0] == 'F' && int.TryParse(name.AsSpan(1), out var function) && function is >= 1 and <= 24) { key = checked((uint)(0x6f + function)); return true; }
        key = name switch { "Escape" => 0x1b, "Delete" => 0x2e, "Enter" => 0x0d, "Space" => 0x20, "Tab" => 0x09, "Home" => 0x24, "End" => 0x23, "Pageup" => 0x21, "Pagedown" => 0x22, "Left" => 0x25, "Up" => 0x26, "Right" => 0x27, "Down" => 0x28, _ => 0 };
        return key != 0;
    }

    private static partial class Native
    {
        [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
        [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool UnregisterHotKey(nint window, int id);
        [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)] internal static partial nint CreateWindowEx(uint extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] internal static partial nint SetWindowLongPtr(nint window, int index, nint value);
        [LibraryImport("user32.dll")] internal static partial nint CallWindowProc(nint previous, nint window, uint message, nuint wParam, nint lParam);
        [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")] internal static partial nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool DestroyWindow(nint window);
    }
}
