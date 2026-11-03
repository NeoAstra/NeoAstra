// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Globalization;

namespace NeoAstra.Desktop.Notifications;

// The NotificationManager answers from any thread; the permission request waits for the user, so every call runs off the
// main thread, which the request needs to stay free.
internal sealed class AndroidNotifications : INeoNotifications, INeoApplicationBoundDesktopService, IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private NeoApplication? _application;
    private Task? _taps;

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Limited, 1, 0,
        "Android notifications in one channel of the application, replaced and removed by ID, with the POST_NOTIFICATIONS permission asked on first display (the application declares it). A notification for later is kept and armed as an alarm, exact when the application may set exact alarms, and armed again after a restart when the application declares RECEIVE_BOOT_COMPLETED. Tapping a notification opens the application and, once the adapter is bound to it, reports the activation with its data; actions and dismissal are not reported.");

    public event EventHandler<NeoNotificationActivation>? Activated;

    // A tap reaches Java with the intent of the activity that it brings up; a thread of this adapter takes the taps one at a
    // time, waiting in Java, and reports each on the UI dispatcher. Taps before the binding wait in Java (the one that launched
    // the application among them).
    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_application is not null && !ReferenceEquals(_application, application)) throw new InvalidOperationException("The notification presenter is already bound to an application.");
        _application = application;
        _taps ??= Task.Factory.StartNew(ReadTaps, _stopping.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopping.IsCancellationRequested) return;
        _stopping.Cancel();
        Activated = null;
        if (_taps is not null) try { await _taps.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch (Exception exception) when (exception is TimeoutException or OperationCanceledException) { }
        _stopping.Dispose();
    }

    private void ReadTaps()
    {
        var failures = 0;
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                using var response = AndroidPlatform.Call("notifications", ("operation", "next"), ("timeout", "5000"));
                failures = 0;
                if (AndroidPlatform.Status(response) != NeoDesktopStatus.Success) continue;
                var activation = response.RootElement.GetProperty("activation");
                var id = activation.GetProperty("id").GetString();
                var data = activation.GetProperty("data").GetString();
                if (string.IsNullOrEmpty(id)) continue;
                Report(new NeoNotificationActivation(id, null, string.IsNullOrEmpty(data) ? null : data, false));
            }
            catch (Exception exception) when (exception is NotSupportedException or NeoAstraException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // A platform without the service ends the thread; a passing failure waits before the next try.
                if (exception is NotSupportedException || ++failures > 5) return;
                _stopping.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            }
        }
    }

    private void Report(NeoNotificationActivation activation)
    {
        var application = _application;
        if (application is null) return;
        _ = application.Dispatcher.InvokeAsync(() =>
        {
            if (_stopping.IsCancellationRequested) return;
            var accepted = application.QueueLaunchEvent(new NeoLaunchEvent(NeoLaunchReason.Extension, metadata: new Dictionary<string, string> { ["plugin"] = "neoastra.desktop.notifications", ["notification"] = activation.NotificationId, ["action"] = string.Empty, ["dismissed"] = "false" }));
            if (accepted) try { Activated?.Invoke(this, activation); } catch { }
        });
    }

    public async ValueTask<NeoNotificationPermissionStatus> GetPermissionStatusAsync(CancellationToken cancellationToken = default)
        => await Task.Run(() => Permission("status"), cancellationToken).ConfigureAwait(false);

    public async ValueTask<NeoNotificationPermissionStatus> RequestPermissionAsync(CancellationToken cancellationToken = default)
    {
        var permission = await Task.Run(() => Permission("status"), cancellationToken).ConfigureAwait(false);
        if (permission != NeoNotificationPermissionStatus.NotRequested) return permission;
        return await Task.Factory.StartNew(() => Permission("authorize"), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).WaitAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<NeoDesktopStatus> ShowAsync(NeoNotificationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); request.Validate();
        if (request.Actions.Count != 0) return NeoDesktopStatus.Unsupported;
        var permission = await Task.Run(() => Permission("status"), cancellationToken).ConfigureAwait(false);
        if (permission == NeoNotificationPermissionStatus.NotRequested)
            permission = await Task.Factory.StartNew(() => Permission("authorize"), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).WaitAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        if (permission != NeoNotificationPermissionStatus.Granted) return MacNotifications.PermissionFailureStatus(permission);
        return await Task.Run(() => { var deliverAt = request.IsScheduled(DateTimeOffset.UtcNow) ? request.DeliverAt!.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) : "0";
            using var response = AndroidPlatform.Call("notifications", ("operation", "show"), ("id", request.Id), ("title", request.Title), ("body", request.Body), ("deliverAt", deliverAt), ("data", request.ActivationData ?? string.Empty)); return AndroidPlatform.Status(response); }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<NeoDesktopStatus> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        NeoNotificationRequest.ValidateId(id, nameof(id));
        return await Task.Run(() => { using var response = AndroidPlatform.Call("notifications", ("operation", "remove"), ("id", id)); return AndroidPlatform.Status(response); }, cancellationToken).ConfigureAwait(false);
    }

    private static NeoNotificationPermissionStatus Permission(string operation)
    {
        using var response = AndroidPlatform.Call("notifications", ("operation", operation));
        return Enum.TryParse<NeoNotificationPermissionStatus>(AndroidPlatform.Text(response, "permission"), out var value) ? value : NeoNotificationPermissionStatus.Unknown;
    }
}
