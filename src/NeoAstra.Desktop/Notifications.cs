// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Desktop.Notifications;

/// <summary>Identifies native notification authority status.</summary>
public enum NeoNotificationPermissionStatus
{
    /// <summary>Status cannot be determined.</summary>
    Unknown,
    /// <summary>Notifications may be displayed.</summary>
    Granted,
    /// <summary>The user or OS denied notifications.</summary>
    Denied,
    /// <summary>The app must request OS authority.</summary>
    NotRequested,
    /// <summary>The platform cannot display notifications.</summary>
    Unsupported,
}

/// <summary>Describes one bounded notification action.</summary>
/// <param name="Id">Opaque application action ID.</param>
/// <param name="Title">Localized action title.</param>
public sealed record NeoNotificationAction(string Id, string Title);

/// <summary>Describes a trusted native notification request.</summary>
public sealed record NeoNotificationRequest
{
    /// <summary>Gets the stable app-local notification ID/tag.</summary>
    public required string Id { get; init; }
    /// <summary>Gets the localized title.</summary>
    public required string Title { get; init; }
    /// <summary>Gets the localized body.</summary>
    public required string Body { get; init; }
    /// <summary>Gets an opaque non-executable bounded activation payload.</summary>
    public string? ActivationData { get; init; }
    /// <summary>Gets at most four action buttons.</summary>
    public IReadOnlyList<NeoNotificationAction> Actions { get; init; } = Array.Empty<NeoNotificationAction>();

    internal void Validate()
    {
        ValidateId(Id, nameof(Id));
        if (string.IsNullOrEmpty(Title) || Title.Length > 256 || Title.Any(static c => c == '\0') || Body is null || Body.Length > 4096 || Body.Any(static c => c == '\0')) throw new ArgumentException("Notification text is malformed.");
        if (ActivationData is { } data && (data.Length > 512 || data.Any(char.IsControl))) throw new ArgumentException("Notification activation data is malformed.", nameof(ActivationData));
        if (Actions is null || Actions.Count > NeoDesktopLimits.MaximumNotificationActions || Actions.Any(static action => action is null || action.Title is null || action.Title.Length is < 1 or > 128 || action.Title.Any(c => c == '\0'))) throw new ArgumentException("Notification actions are malformed.", nameof(Actions));
        foreach (var action in Actions) ValidateId(action.Id, nameof(Actions));
        if (Actions.Select(static action => action.Id).Distinct(StringComparer.Ordinal).Count() != Actions.Count) throw new ArgumentException("Notification action IDs are duplicated.", nameof(Actions));
    }

    internal static void ValidateId(string value, string parameterName)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':'))) throw new ArgumentException("A notification ID is malformed.", parameterName);
    }
}

/// <summary>Contains ordered native notification activation/dismiss information.</summary>
/// <param name="NotificationId">Stable notification ID.</param>
/// <param name="ActionId">Optional action ID.</param>
/// <param name="ActivationData">Opaque bounded data.</param>
/// <param name="Dismissed">Whether this is a dismissal.</param>
public sealed record NeoNotificationActivation(string NotificationId, string? ActionId, string? ActivationData, bool Dismissed);

/// <summary>Provides bounded native notifications and early activation routing.</summary>
public interface INeoNotifications
{
    /// <summary>Gets truthful platform support.</summary>
    NeoCapabilityInfo Support { get; }
    /// <summary>Occurs for ordered activation or dismissal events.</summary>
    event EventHandler<NeoNotificationActivation>? Activated;
    /// <summary>Queries OS authority.</summary>
    ValueTask<NeoNotificationPermissionStatus> GetPermissionStatusAsync(CancellationToken cancellationToken = default);
    /// <summary>Displays or replaces a notification.</summary>
    ValueTask<NeoDesktopStatus> ShowAsync(NeoNotificationRequest request, CancellationToken cancellationToken = default);
    /// <summary>Removes one notification when supported.</summary>
    ValueTask<NeoDesktopStatus> RemoveAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>Deterministic bounded notification fake that can route early activations to Step 5 launch delivery.</summary>
public sealed class NeoFakeNotifications : INeoNotifications, IAsyncDisposable
{
    private readonly Dictionary<string, NeoNotificationRequest> _requests = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly NeoApplication? _application;

    /// <summary>Initializes the fake.</summary>
    /// <param name="application">Optional application receiving extension launch events, including before ready.</param>
    public NeoFakeNotifications(NeoApplication? application = null) => _application = application;

    /// <inheritdoc />
    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Emulated, 1, 0, "Deterministic in-memory notification adapter.");
    /// <inheritdoc />
    public event EventHandler<NeoNotificationActivation>? Activated;
    /// <inheritdoc />
    public ValueTask<NeoNotificationPermissionStatus> GetPermissionStatusAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoNotificationPermissionStatus.Granted); }
    /// <inheritdoc />
    public ValueTask<NeoDesktopStatus> ShowAsync(NeoNotificationRequest request, CancellationToken cancellationToken = default) { ArgumentNullException.ThrowIfNull(request); request.Validate(); cancellationToken.ThrowIfCancellationRequested(); var snapshot = request with { Actions = Array.AsReadOnly(request.Actions.ToArray()) }; lock (_sync) { if (!_requests.ContainsKey(request.Id) && _requests.Count >= 256) return ValueTask.FromResult(NeoDesktopStatus.LimitExceeded); _requests[request.Id] = snapshot; } return ValueTask.FromResult(NeoDesktopStatus.Success); }
    /// <inheritdoc />
    public ValueTask<NeoDesktopStatus> RemoveAsync(string id, CancellationToken cancellationToken = default) { NeoNotificationRequest.ValidateId(id, nameof(id)); cancellationToken.ThrowIfCancellationRequested(); lock (_sync) return ValueTask.FromResult(_requests.Remove(id) ? NeoDesktopStatus.Success : NeoDesktopStatus.NotFound); }

    /// <summary>Queues a trusted native-style activation and routes it through the app launch queue before raising the event.</summary>
    public bool Activate(string notificationId, string? actionId = null, bool dismissed = false)
    {
        NeoNotificationRequest? request;
        lock (_sync) _requests.TryGetValue(notificationId, out request);
        if (request is null) return false;
        if (actionId is not null && !request.Actions.Any(action => action.Id == actionId)) return false;
        var activation = new NeoNotificationActivation(notificationId, actionId, request.ActivationData, dismissed);
        var accepted = _application?.QueueLaunchEvent(new NeoLaunchEvent(NeoLaunchReason.Extension, metadata: new Dictionary<string, string> { ["plugin"] = "neoastra.desktop.notifications", ["notification"] = notificationId, ["action"] = actionId ?? string.Empty, ["dismissed"] = dismissed ? "true" : "false" })) ?? true;
        if (!accepted) return false;
        try { Activated?.Invoke(this, activation); } catch { }
        return true;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() { lock (_sync) { _requests.Clear(); Activated = null; } return ValueTask.CompletedTask; }
}

/// <summary>Creates truthful system notification adapters.</summary>
public static class NeoNotifications
{
    /// <summary>Creates a statically selected adapter using a generic application identity.</summary>
    public static INeoNotifications CreateSystem(NeoDispatcher? dispatcher = null)
        => CreateSystem("neoastra.application", "NeoAstra", dispatcher);

    /// <summary>Creates a statically selected adapter with stable application identity used by native notification services.</summary>
    /// <param name="applicationId">Stable application identifier.</param>
    /// <param name="applicationName">Localized application display name.</param>
    /// <param name="dispatcher">Optional application UI dispatcher.</param>
    /// <returns>The native adapter for the current operating system.</returns>
    public static INeoNotifications CreateSystem(string applicationId, string applicationName, NeoDispatcher? dispatcher = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId); ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        if (OperatingSystem.IsWindows()) return new WindowsNotifications(dispatcher);
        if (OperatingSystem.IsMacOS()) return new MacNotifications(applicationId);
        if (OperatingSystem.IsLinux()) return new LinuxNotifications(applicationId, applicationName);
        return new UnsupportedNotifications("No supported native notification service is available.");
    }
}

internal sealed class UnsupportedNotifications(string details) : INeoNotifications
{
    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.None, 1, 0, details);
#pragma warning disable CS0067
    public event EventHandler<NeoNotificationActivation>? Activated;
#pragma warning restore CS0067
    public ValueTask<NeoNotificationPermissionStatus> GetPermissionStatusAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoNotificationPermissionStatus.Unsupported); }
    public ValueTask<NeoDesktopStatus> ShowAsync(NeoNotificationRequest request, CancellationToken cancellationToken = default) { request.Validate(); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoDesktopStatus.Unsupported); }
    public ValueTask<NeoDesktopStatus> RemoveAsync(string id, CancellationToken cancellationToken = default) { NeoNotificationRequest.ValidateId(id, nameof(id)); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoDesktopStatus.Unsupported); }
}

internal sealed class ProcessNotifications(string executable, bool macOS) : INeoNotifications
{
    public NeoCapabilityInfo Support { get; } = string.IsNullOrEmpty(executable) ? new(NeoSupportLevel.None, 1, 0, "The platform notification helper is unavailable.") : new(NeoSupportLevel.Limited, 1, 0, "Display only through a fixed native helper; actions, activation, persistence, remove, and packaged identity are unavailable.");
#pragma warning disable CS0067
    public event EventHandler<NeoNotificationActivation>? Activated;
#pragma warning restore CS0067
    public ValueTask<NeoNotificationPermissionStatus> GetPermissionStatusAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(string.IsNullOrEmpty(executable) ? NeoNotificationPermissionStatus.Unsupported : NeoNotificationPermissionStatus.Unknown); }
    public async ValueTask<NeoDesktopStatus> ShowAsync(NeoNotificationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); request.Validate();
        if (string.IsNullOrEmpty(executable) || request.Actions.Count != 0) return NeoDesktopStatus.Unsupported;
        var arguments = macOS ? new[] { "-e", "on run argv\ndisplay notification (item 2 of argv) with title (item 1 of argv)\nend run", "--", request.Title, request.Body } : new[] { "--app-name=NeoAstra", "--expire-time=10000", request.Title, request.Body };
        try { var result = await DesktopProcess.RunAsync(executable, arguments, default, TimeSpan.FromSeconds(15), false, cancellationToken).ConfigureAwait(false); return result.ExitCode == 0 ? NeoDesktopStatus.Success : NeoDesktopStatus.Failed; }
        catch (OperationCanceledException) { throw; }
        catch { return NeoDesktopStatus.Failed; }
    }
    public ValueTask<NeoDesktopStatus> RemoveAsync(string id, CancellationToken cancellationToken = default) { NeoNotificationRequest.ValidateId(id, nameof(id)); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoDesktopStatus.Unsupported); }
}
