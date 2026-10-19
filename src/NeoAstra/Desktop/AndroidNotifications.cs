// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Globalization;

namespace NeoAstra.Desktop.Notifications;

// The NotificationManager answers from any thread; the permission request waits for the user, so every call runs off the
// main thread, which the request needs to stay free.
internal sealed class AndroidNotifications : INeoNotifications
{
    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Limited, 1, 0,
        "Android notifications in one channel of the application, replaced and removed by ID, with the POST_NOTIFICATIONS permission asked on first display (the application declares it). A notification for later is kept and armed as an alarm, exact when the application may set exact alarms, and armed again after a restart when the application declares RECEIVE_BOOT_COMPLETED. Tapping a notification opens the application; actions, activation, and dismissal are not reported.");

#pragma warning disable CS0067
    public event EventHandler<NeoNotificationActivation>? Activated;
#pragma warning restore CS0067

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
            using var response = AndroidPlatform.Call("notifications", ("operation", "show"), ("id", request.Id), ("title", request.Title), ("body", request.Body), ("deliverAt", deliverAt)); return AndroidPlatform.Status(response); }, cancellationToken).ConfigureAwait(false);
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
