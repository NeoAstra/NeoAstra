// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NeoAstra.Desktop.GlobalShortcuts;

internal sealed unsafe partial class MacGlobalShortcutPresenter(NeoDispatcher? dispatcher) : INeoGlobalShortcutPresenter, INeoApplicationBoundDesktopService
{
    private const uint Signature = 0x4E415354; // NAST
    private static readonly ConcurrentDictionary<uint, MacGlobalShortcutPresenter> Owners = new();
    private static int s_nextNativeId;
    private readonly Dictionary<string, (uint Id, nint Reference)> _registrations = new(StringComparer.Ordinal);
    private NeoDispatcher? _dispatcher = dispatcher;
    private nint _eventHandler;
    private bool _disposed;

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Native, 1, 0, "Native Carbon application hot keys with exact conflict results and bounded app-owned teardown. macOS reserves system shortcuts before delivery.");
    public event EventHandler<string>? Activated;

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_dispatcher is not null && !ReferenceEquals(_dispatcher, application.Dispatcher)) throw new InvalidOperationException("The global-shortcut presenter is already bound to another dispatcher.");
        _dispatcher = application.Dispatcher;
    }

    public NeoDesktopStatus Register(string id, string normalizedAccelerator)
    {
        var value = _dispatcher ?? throw new InvalidOperationException("The global-shortcut presenter must be bound to the UI dispatcher before use.");
        if (!value.CheckAccess()) throw new InvalidOperationException("Global shortcut mutations must be called on the NeoAstra UI dispatcher; renderer handlers use UI-thread dispatch.");
        return RegisterOnDispatcher(id, normalizedAccelerator);
    }

    public bool Unregister(string id)
    {
        var value = _dispatcher ?? throw new InvalidOperationException("The global-shortcut presenter must be bound to the UI dispatcher before use.");
        if (!value.CheckAccess()) throw new InvalidOperationException("Global shortcut mutations must be called on the NeoAstra UI dispatcher; renderer handlers use UI-thread dispatch.");
        return UnregisterOnDispatcher(id);
    }

    public ValueTask DisposeAsync()
    {
        var value = _dispatcher;
        if (value is not null && !value.CheckAccess()) return value.InvokeAsync(DisposeOnDispatcher);
        DisposeOnDispatcher(); return ValueTask.CompletedTask;
    }

    private NeoDesktopStatus RegisterOnDispatcher(string id, string accelerator)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureHandler();
        if (!TryTranslate(accelerator, out var key, out var modifiers)) return NeoDesktopStatus.Unsupported;
        var nativeId = AllocateNativeId();
        var hotKeyId = new EventHotKeyId { Signature = Signature, Id = nativeId };
        nint reference = 0;
        var status = Native.RegisterEventHotKey(key, modifiers, hotKeyId, Native.GetApplicationEventTarget(), 0, &reference);
        if (status != 0 || reference == 0) return status == -9878 ? NeoDesktopStatus.Conflict : NeoDesktopStatus.Failed;
        _registrations.Add(id, (nativeId, reference)); Owners[nativeId] = this;
        return NeoDesktopStatus.Success;
    }

    private bool UnregisterOnDispatcher(string id)
    {
        if (!_registrations.Remove(id, out var registration)) return false;
        Owners.TryRemove(registration.Id, out _);
        return Native.UnregisterEventHotKey(registration.Reference) == 0;
    }

    private void EnsureHandler()
    {
        if (_eventHandler != 0) return;
        var eventType = new EventTypeSpec { EventClass = 0x6B657962, EventKind = 5 };
        fixed (nint* handler = &_eventHandler)
        {
            if (Native.InstallApplicationEventHandler((nint)(delegate* unmanaged<nint, nint, nint, int>)&EventCallback, 1, &eventType, 0, handler) != 0)
                throw new InvalidOperationException("Unable to install the macOS hot-key event handler.");
        }
    }

    private void DisposeOnDispatcher()
    {
        if (_disposed) return; _disposed = true;
        foreach (var registration in _registrations.Values) { Owners.TryRemove(registration.Id, out _); _ = Native.UnregisterEventHotKey(registration.Reference); }
        _registrations.Clear();
        if (_eventHandler != 0) { _ = Native.RemoveEventHandler(_eventHandler); _eventHandler = 0; }
        Activated = null;
    }

    [UnmanagedCallersOnly]
    private static int EventCallback(nint next, nint eventRef, nint userData)
    {
        try
        {
            var id = default(EventHotKeyId); uint size = 0;
            if (Native.GetEventParameter(eventRef, 0x2D2D2D2D, 0x686B6964, 0, (uint)sizeof(EventHotKeyId), &size, &id) == 0 && id.Signature == Signature && Owners.TryGetValue(id.Id, out var owner)) owner.Raise(id.Id);
        }
        catch { }
        return 0;
    }

    private void Raise(uint nativeId)
    {
        var id = _registrations.FirstOrDefault(pair => pair.Value.Id == nativeId).Key;
        if (id is null) return;
        void Invoke() { try { Activated?.Invoke(this, id); } catch { } }
        var value = _dispatcher; if (value is not null && !value.CheckAccess()) { try { _ = value.InvokeAsync(Invoke); } catch { } } else Invoke();
    }

    private static bool TryTranslate(string accelerator, out uint key, out uint modifiers)
    {
        key = 0; modifiers = 0;
        var parts = accelerator.Split('+');
        foreach (var modifier in parts[..^1]) modifiers |= modifier switch { "Ctrl" => 1u << 12, "Alt" => 1u << 11, "Shift" => 1u << 9, "Meta" => 1u << 8, _ => 0 };
        var name = parts[^1];
        if (name.Length == 1 && name[0] is >= 'A' and <= 'Z') { key = LetterKeyCodes[name[0] - 'A']; return true; }
        if (name.Length == 1 && name[0] is >= '0' and <= '9') { key = DigitKeyCodes[name[0] - '0']; return true; }
        if (name.StartsWith('F') && int.TryParse(name.AsSpan(1), out var function) && function is >= 1 and <= 12) { key = FunctionKeyCodes[function - 1]; return true; }
        return SpecialKeys.TryGetValue(name, out key);
    }

    internal static uint AllocateNativeId()
    {
        var value = unchecked((uint)Interlocked.Increment(ref s_nextNativeId));
        return value == 0 ? unchecked((uint)Interlocked.Increment(ref s_nextNativeId)) : value;
    }

    private static readonly uint[] LetterKeyCodes = [0, 11, 8, 2, 14, 3, 5, 4, 34, 38, 40, 37, 46, 45, 31, 35, 12, 15, 1, 17, 32, 9, 13, 7, 16, 6];
    private static readonly uint[] DigitKeyCodes = [29, 18, 19, 20, 21, 23, 22, 26, 28, 25];
    private static readonly uint[] FunctionKeyCodes = [122, 120, 99, 118, 96, 97, 98, 100, 101, 109, 103, 111];
    private static readonly IReadOnlyDictionary<string, uint> SpecialKeys = new Dictionary<string, uint>(StringComparer.Ordinal) { ["Escape"] = 53, ["Space"] = 49, ["Enter"] = 36, ["Tab"] = 48, ["Delete"] = 51, ["Left"] = 123, ["Right"] = 124, ["Down"] = 125, ["Up"] = 126 };

    [StructLayout(LayoutKind.Sequential)] private struct EventHotKeyId { internal uint Signature; internal uint Id; }
    [StructLayout(LayoutKind.Sequential)] private struct EventTypeSpec { internal uint EventClass; internal uint EventKind; }
    private static partial class Native
    {
        [LibraryImport("Carbon.framework/Carbon")] internal static partial nint GetApplicationEventTarget();
        [LibraryImport("Carbon.framework/Carbon")] internal static partial int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyId id, nint target, uint options, nint* reference);
        [LibraryImport("Carbon.framework/Carbon")] internal static partial int UnregisterEventHotKey(nint reference);
        [LibraryImport("Carbon.framework/Carbon")] internal static partial int InstallApplicationEventHandler(nint handler, uint count, EventTypeSpec* types, nint userData, nint* installedHandler);
        [LibraryImport("Carbon.framework/Carbon")] internal static partial int RemoveEventHandler(nint handler);
        [LibraryImport("Carbon.framework/Carbon")] internal static partial int GetEventParameter(nint eventRef, uint name, uint desiredType, nint actualType, uint bufferSize, uint* actualSize, EventHotKeyId* value);
    }
}
