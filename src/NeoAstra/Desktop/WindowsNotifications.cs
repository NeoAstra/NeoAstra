// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NeoAstra.Desktop.Notifications;

internal sealed unsafe partial class WindowsNotifications(NeoDispatcher? dispatcher) : INeoNotifications, IAsyncDisposable, INeoApplicationBoundDesktopService
{
    private const uint WmUser = 0x0400;
    private const uint CallbackMessage = 0x8000 + 37;
    private const int WindowProcedureIndex = -4;
    private static readonly ConcurrentDictionary<nint, WindowsNotifications> Presenters = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _nativeIds = [];
    private readonly HashSet<uint> _staleNativeIds = [];
    private NeoDispatcher? _dispatcher = dispatcher;
    private NeoApplication? _application;
    private nint _window;
    private nint _previousProcedure;
    private uint _nextId;
    private bool _disposed;

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Limited, 1, 0,
        "Native Win32 notification-area balloons with stable process-local IDs, transactional replacement/removal, click and dismissal routing, early launch queuing, and native title/body limits of 63/255 UTF-16 units. Action buttons and persistence require packaged toast identity and report unsupported.");

    public event EventHandler<NeoNotificationActivation>? Activated;

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_dispatcher is not null && !ReferenceEquals(_dispatcher, application.Dispatcher)) throw new InvalidOperationException("The notification presenter is already bound to another dispatcher.");
        _dispatcher = application.Dispatcher; _application = application;
    }

    public ValueTask<NeoNotificationPermissionStatus> GetPermissionStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(NeoNotificationPermissionStatus.Granted);
    }

    public ValueTask<NeoDesktopStatus> ShowAsync(NeoNotificationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); request.Validate();
        if (request.Actions.Count != 0) return ValueTask.FromResult(NeoDesktopStatus.Unsupported);
        if (request.Title.Length > 63 || request.Body.Length > 255) return ValueTask.FromResult(NeoDesktopStatus.LimitExceeded);
        var value = _dispatcher ?? throw new InvalidOperationException("The Windows notification presenter must be bound to the NeoAstra UI dispatcher before use.");
        return value.InvokeAsync(() => ShowOnDispatcher(request, cancellationToken), cancellationToken);
    }

    public ValueTask<NeoDesktopStatus> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        NeoNotificationRequest.ValidateId(id, nameof(id));
        var value = _dispatcher ?? throw new InvalidOperationException("The Windows notification presenter must be bound to the NeoAstra UI dispatcher before use.");
        return value.InvokeAsync(() => RemoveOnDispatcher(id, cancellationToken), cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Activated = null;
        var value = _dispatcher;
        if (value is not null && !value.CheckAccess()) return value.InvokeAsync(DisposeOnDispatcher);
        DisposeOnDispatcher();
        return ValueTask.CompletedTask;
    }

    private NeoDesktopStatus ShowOnDispatcher(NeoNotificationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed, this); EnsureWindow(); CleanupStale();
        if (!_entries.TryGetValue(request.Id, out var existing) && _entries.Count >= 256) return NeoDesktopStatus.LimitExceeded;
        var nativeId = NextNativeId();
        var data = CreateData(nativeId, request);
        // Add replacements under a fresh generation ID before touching the old balloon.
        // Delayed callbacks for the old generation then cannot activate the new request.
        var transition = CompleteReplacement(existing?.NativeId, nativeId, Native.ShellNotifyIcon(0, &data), DeleteNative,
            ignored => { var previousData = CreateData(existing!.NativeId, existing.Request); return Native.ShellNotifyIcon(0, &previousData); });
        if (transition != NativeReplacementResult.Committed)
        {
            if (transition == NativeReplacementResult.Indeterminate)
            {
                _staleNativeIds.Add(nativeId); if (existing is not null) { _staleNativeIds.Add(existing.NativeId); _entries.Remove(request.Id); _nativeIds.Remove(existing.NativeId); }
            }
            return NeoDesktopStatus.Failed;
        }
        if (existing is not null) _nativeIds.Remove(existing.NativeId);
        _entries[request.Id] = new(nativeId, request with { Actions = Array.AsReadOnly(request.Actions.ToArray()) }); _nativeIds[nativeId] = request.Id;
        return NeoDesktopStatus.Success;
    }

    private NeoDesktopStatus RemoveOnDispatcher(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(id, out var entry)) return NeoDesktopStatus.NotFound;
        if (!DeleteNative(entry.NativeId)) { _staleNativeIds.Add(entry.NativeId); _entries.Remove(id); _nativeIds.Remove(entry.NativeId); return NeoDesktopStatus.Failed; }
        _entries.Remove(id); _nativeIds.Remove(entry.NativeId);
        return NeoDesktopStatus.Success;
    }

    private bool DeleteNative(uint id)
    {
        var data = new NotifyIconData { Size = (uint)sizeof(NotifyIconData), Window = _window, Id = id };
        return Native.ShellNotifyIcon(2, &data);
    }

    private void EnsureWindow()
    {
        if (_window != 0) return;
        _window = Native.CreateWindowEx(0, "STATIC", string.Empty, 0, 0, 0, 0, 0, -3, 0, 0, 0);
        if (_window == 0) throw new InvalidOperationException("Unable to create the notification message window.");
        _previousProcedure = Native.SetWindowLongPtr(_window, WindowProcedureIndex, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProcedure);
        if (_previousProcedure == 0) { _ = Native.DestroyWindow(_window); _window = 0; throw new InvalidOperationException("Unable to attach the notification window procedure."); }
        Presenters[_window] = this;
    }

    private void DisposeOnDispatcher()
    {
        if (_disposed) return; _disposed = true;
        foreach (var entry in _entries.Values) DeleteNative(entry.NativeId);
        foreach (var id in _staleNativeIds) DeleteNative(id);
        _entries.Clear(); _nativeIds.Clear(); _staleNativeIds.Clear();
        if (_window != 0)
        {
            Presenters.TryRemove(_window, out _);
            if (_previousProcedure != 0) _ = Native.SetWindowLongPtr(_window, WindowProcedureIndex, _previousProcedure);
            _ = Native.DestroyWindow(_window); _window = 0; _previousProcedure = 0;
        }
    }

    private void Receive(uint id, bool dismissed)
    {
        if (!_nativeIds.TryGetValue(id, out var notificationId) || !_entries.TryGetValue(notificationId, out var entry)) return;
        _ = DeleteNative(id); _nativeIds.Remove(id); _entries.Remove(notificationId);
        var activation = new NeoNotificationActivation(notificationId, null, entry.Request.ActivationData, dismissed);
        var accepted = _application?.QueueLaunchEvent(new NeoLaunchEvent(NeoLaunchReason.Extension, metadata: new Dictionary<string, string>
        {
            ["plugin"] = "neoastra.desktop.notifications", ["notification"] = notificationId, ["action"] = string.Empty, ["dismissed"] = dismissed ? "true" : "false",
        })) ?? true;
        if (!accepted) return;
        try { Activated?.Invoke(this, activation); } catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (Presenters.TryGetValue(window, out var presenter))
            {
                if (message == CallbackMessage)
                {
                    var notification = unchecked((uint)lParam.ToInt64());
                    if (notification == WmUser + 5) presenter.Receive((uint)wParam, dismissed: false);
                    else if (notification is WmUser + 3 or WmUser + 4) presenter.Receive((uint)wParam, dismissed: true);
                    return 0;
                }
                if (presenter._previousProcedure != 0) return Native.CallWindowProc(presenter._previousProcedure, window, message, wParam, lParam);
            }
        }
        catch { }
        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    private NotifyIconData CreateData(uint id, NeoNotificationRequest request)
    {
        var data = new NotifyIconData
        {
            Size = (uint)sizeof(NotifyIconData), Window = _window, Id = id, Flags = 0x0001 | 0x0002 | 0x0004 | 0x0010,
            CallbackMessage = CallbackMessage, Icon = Native.LoadIcon(0, 32512), InfoFlags = 0x0001,
        };
        Copy(request.Title, data.InfoTitle, 64); Copy(request.Body, data.Info, 256); Copy(request.Title, data.Tip, 128);
        return data;
    }

    private static void Copy(string value, char* destination, int capacity)
    {
        var length = Math.Min(value.Length, capacity - 1); value.AsSpan(0, length).CopyTo(new Span<char>(destination, capacity)); destination[length] = '\0';
    }

    private uint NextNativeId()
    {
        do { _nextId = unchecked(_nextId + 1); } while (_nextId == 0 || _nativeIds.ContainsKey(_nextId) || _staleNativeIds.Contains(_nextId));
        return _nextId;
    }

    internal static NativeReplacementResult CompleteReplacement(uint? previousId, uint newId, bool addSucceeded, Func<uint, bool> delete, Func<uint, bool> restore)
    {
        if (!addSucceeded) return NativeReplacementResult.Unchanged;
        if (previousId is not { } previous || delete(previous)) return NativeReplacementResult.Committed;
        var removedNew = delete(newId);
        var restoredOld = restore(previous);
        return removedNew && restoredOld ? NativeReplacementResult.Unchanged : NativeReplacementResult.Indeterminate;
    }

    private void CleanupStale() { foreach (var id in _staleNativeIds.ToArray()) if (DeleteNative(id)) _staleNativeIds.Remove(id); }

    internal enum NativeReplacementResult { Committed, Unchanged, Indeterminate }

    private sealed record Entry(uint NativeId, NeoNotificationRequest Request);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        internal uint Size; internal nint Window; internal uint Id; internal uint Flags; internal uint CallbackMessage; internal nint Icon;
        internal fixed char Tip[128]; internal uint State; internal uint StateMask; internal fixed char Info[256]; internal uint VersionOrTimeout;
        internal fixed char InfoTitle[64]; internal uint InfoFlags; internal Guid ItemGuid; internal nint BalloonIcon;
    }

    private static partial class Native
    {
        [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool ShellNotifyIcon(uint message, NotifyIconData* data);
        [LibraryImport("user32.dll", EntryPoint = "LoadIconW")] internal static partial nint LoadIcon(nint instance, nint resource);
        [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)] internal static partial nint CreateWindowEx(uint extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] internal static partial nint SetWindowLongPtr(nint window, int index, nint value);
        [LibraryImport("user32.dll")] internal static partial nint CallWindowProc(nint previous, nint window, uint message, nuint wParam, nint lParam);
        [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")] internal static partial nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool DestroyWindow(nint window);
    }
}
