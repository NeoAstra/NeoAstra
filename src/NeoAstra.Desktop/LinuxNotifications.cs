// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace NeoAstra.Desktop.Notifications;

/// <summary>Freedesktop notification presenter using the session D-Bus protocol through the trusted GLib client.</summary>
internal sealed class LinuxNotifications : INeoNotifications, INeoApplicationBoundDesktopService, IAsyncDisposable
{
    private readonly string _gdbus = DesktopProcess.FindTrustedExecutable("/usr/bin/gdbus", "/usr/local/bin/gdbus");
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _byNativeId = [];
    private readonly HashSet<uint> _suppressedClose = [];
    private NeoDispatcher? _dispatcher;
    private NeoApplication? _application;
    private Process? _monitor;
    private TaskCompletionSource? _monitorReady;
    private bool _disposed;

    internal LinuxNotifications() : this("neoastra.application", "NeoAstra") { }
    internal LinuxNotifications(string applicationId, string applicationName)
    {
        if (string.IsNullOrWhiteSpace(applicationId) || applicationId.Length > 256 || applicationId.Any(char.IsControl)) throw new ArgumentException("The notification application ID is malformed.", nameof(applicationId));
        if (string.IsNullOrWhiteSpace(applicationName) || applicationName.Length > 256 || applicationName.Any(char.IsControl)) throw new ArgumentException("The notification application name is malformed.", nameof(applicationName));
        _applicationId = applicationId; _applicationName = applicationName;
    }
    private readonly string _applicationId;
    private readonly string _applicationName;

    public NeoCapabilityInfo Support => string.IsNullOrEmpty(_gdbus)
        ? new(NeoSupportLevel.None, 1, 0, "The trusted GLib D-Bus client is unavailable.")
        : new(NeoSupportLevel.Native, 1, 0, "Freedesktop Notifications D-Bus actions, ordered activation/dismissal signals, replacement, and explicit removal. Runtime availability depends on the desktop notification service.");
    public event EventHandler<NeoNotificationActivation>? Activated;

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_application is not null && !ReferenceEquals(_application, application)) throw new InvalidOperationException("The notification presenter is already bound to an application.");
        _application = application; _dispatcher = application.Dispatcher;
    }

    public ValueTask<NeoNotificationPermissionStatus> GetPermissionStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(string.IsNullOrEmpty(_gdbus) ? NeoNotificationPermissionStatus.Unsupported : NeoNotificationPermissionStatus.Unknown);
    }

    public async ValueTask<NeoDesktopStatus> ShowAsync(NeoNotificationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); request.Validate();
        if (string.IsNullOrEmpty(_gdbus)) return NeoDesktopStatus.Unsupported;
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try { await EnsureMonitorAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { return NeoDesktopStatus.Failed; }
            Entry? previous; lock (_sync) _byId.TryGetValue(request.Id, out previous);
            lock (_sync) if (previous is null && _byId.Count >= 256) return NeoDesktopStatus.LimitExceeded;
            var actions = new List<string>(2 + request.Actions.Count * 2) { "default", "Open" };
            foreach (var action in request.Actions) { actions.Add(action.Id); actions.Add(action.Title); }
            var hints = "{'desktop-entry': <" + VariantString(_applicationId) + ">}";
            var args = new[] { "call", "--session", "--dest", "org.freedesktop.Notifications", "--object-path", "/org/freedesktop/Notifications", "--method", "org.freedesktop.Notifications.Notify", _applicationName, (previous?.NativeId ?? 0).ToString(CultureInfo.InvariantCulture), string.Empty, request.Title, request.Body, VariantArray(actions), hints, "-1" };
            DesktopProcessResult result;
            try { result = await DesktopProcess.RunAsync(_gdbus, args, default, TimeSpan.FromSeconds(15), true, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { return NeoDesktopStatus.Failed; }
            if (result.ExitCode != 0 || !TryParseNativeId(Encoding.UTF8.GetString(result.Output), out var nativeId)) return NeoDesktopStatus.Failed;
            var snapshot = request with { Actions = Array.AsReadOnly(request.Actions.ToArray()) };
            lock (_sync)
            {
                if (_disposed) return NeoDesktopStatus.Failed;
                if (previous is not null) _byNativeId.Remove(previous.NativeId);
                _byId[request.Id] = new(nativeId, snapshot); _byNativeId[nativeId] = request.Id;
            }
            return NeoDesktopStatus.Success;
        }
        finally { _mutation.Release(); }
    }

    public async ValueTask<NeoDesktopStatus> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        NeoNotificationRequest.ValidateId(id, nameof(id));
        if (string.IsNullOrEmpty(_gdbus)) return NeoDesktopStatus.Unsupported;
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Entry? entry; lock (_sync) { if (!_byId.TryGetValue(id, out entry)) return NeoDesktopStatus.NotFound; _suppressedClose.Add(entry.NativeId); }
            try
            {
                var result = await DesktopProcess.RunAsync(_gdbus, ["call", "--session", "--dest", "org.freedesktop.Notifications", "--object-path", "/org/freedesktop/Notifications", "--method", "org.freedesktop.Notifications.CloseNotification", entry.NativeId.ToString(CultureInfo.InvariantCulture)], default, TimeSpan.FromSeconds(15), false, cancellationToken).ConfigureAwait(false);
                if (result.ExitCode != 0) { lock (_sync) _suppressedClose.Remove(entry.NativeId); return NeoDesktopStatus.Failed; }
            }
            catch (OperationCanceledException) { lock (_sync) _suppressedClose.Remove(entry.NativeId); throw; }
            catch { lock (_sync) _suppressedClose.Remove(entry.NativeId); return NeoDesktopStatus.Failed; }
            lock (_sync) { if (_byId.TryGetValue(id, out var current) && current.NativeId == entry.NativeId) _byId.Remove(id); _byNativeId.Remove(entry.NativeId); _suppressedClose.Remove(entry.NativeId); }
            return NeoDesktopStatus.Success;
        }
        finally { _mutation.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _mutation.WaitAsync().ConfigureAwait(false);
        try
        {
            Process? monitor;
            lock (_sync) { if (_disposed) return; _disposed = true; monitor = _monitor; _monitor = null; _monitorReady?.TrySetCanceled(); _monitorReady = null; _byId.Clear(); _byNativeId.Clear(); _suppressedClose.Clear(); Activated = null; }
            if (monitor is not null) { try { if (!monitor.HasExited) monitor.Kill(entireProcessTree: true); } catch { } try { await monitor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { } monitor.Dispose(); }
        }
        finally { _mutation.Release(); _mutation.Dispose(); }
    }

    private async Task EnsureMonitorAsync(CancellationToken cancellationToken)
    {
        Task ready;
        lock (_sync)
        {
            if (_monitor is not null) { ready = _monitorReady!.Task; }
            else
            {
            var start = new ProcessStartInfo(_gdbus) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (var argument in new[] { "monitor", "--session", "--dest", "org.freedesktop.Notifications", "--object-path", "/org/freedesktop/Notifications" }) start.ArgumentList.Add(argument);
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _monitorReady = started;
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { started.TrySetResult(); OnMonitorLine(e.Data); } };
            process.ErrorDataReceived += static (_, _) => { };
            process.Exited += (_, _) => { lock (_sync) { if (ReferenceEquals(_monitor, process)) { _monitor = null; _monitorReady = null; } } started.TrySetException(new InvalidOperationException("The notification signal monitor exited.")); try { process.Dispose(); } catch { } };
            if (!process.Start()) throw new InvalidOperationException("The notification signal monitor did not start.");
            process.BeginOutputReadLine(); process.BeginErrorReadLine(); _monitor = process; ready = started.Task;
            }
        }
        await ready.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
    }

    private void OnMonitorLine(string line)
    {
        try
        {
            var action = line.Contains(".ActionInvoked", StringComparison.Ordinal);
            var closed = line.Contains(".NotificationClosed", StringComparison.Ordinal);
            if (!action && !closed || !TryParseNativeId(line, out var nativeId)) return;
            Entry? entry; string? id;
            lock (_sync)
            {
                if (_disposed || !_byNativeId.TryGetValue(nativeId, out id) || !_byId.TryGetValue(id, out entry)) return;
                if (closed) { _byNativeId.Remove(nativeId); _byId.Remove(id); if (_suppressedClose.Remove(nativeId)) return; }
            }
            string? actionId = null;
            if (action)
            {
                actionId = ParseQuotedAfterId(line); if (actionId == "default") actionId = null; else if (actionId is not null && !entry.Request.Actions.Any(value => value.Id == actionId)) return;
                lock (_sync) { if (_byNativeId.Remove(nativeId)) _byId.Remove(id!); else return; }
            }
            Publish(new(id!, actionId, entry.Request.ActivationData, closed));
        }
        catch { }
    }

    private void Publish(NeoNotificationActivation activation)
    {
        void Invoke()
        {
            var accepted = _application?.QueueLaunchEvent(new NeoLaunchEvent(NeoLaunchReason.Extension, metadata: new Dictionary<string, string> { ["plugin"] = "neoastra.desktop.notifications", ["notification"] = activation.NotificationId, ["action"] = activation.ActionId ?? string.Empty, ["dismissed"] = activation.Dismissed ? "true" : "false" })) ?? true;
            if (accepted) try { Activated?.Invoke(this, activation); } catch { }
        }
        var dispatcher = _dispatcher; if (dispatcher is not null && !dispatcher.CheckAccess()) { try { _ = dispatcher.InvokeAsync(Invoke); } catch { } } else Invoke();
    }

    internal static string VariantArray(IEnumerable<string> values) => "[" + string.Join(",", values.Select(static value => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'")) + "]";
    internal static string VariantString(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
    internal static bool TryParseNativeId(string value, out uint id)
    {
        id = 0; var marker = value.IndexOf("uint32", StringComparison.Ordinal); if (marker < 0) return false; var span = value.AsSpan(marker + 6).TrimStart(); var length = 0; while (length < span.Length && char.IsAsciiDigit(span[length])) length++; return length != 0 && uint.TryParse(span[..length], CultureInfo.InvariantCulture, out id) && id != 0;
    }
    private static string? ParseQuotedAfterId(string value)
    {
        var marker = value.IndexOf("uint32", StringComparison.Ordinal); if (marker < 0) return null; var start = value.IndexOf('\'', marker); if (start < 0) return null; var end = value.IndexOf('\'', start + 1); return end > start ? value[(start + 1)..end] : null;
    }
    private sealed record Entry(uint NativeId, NeoNotificationRequest Request);
}
