// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.Json;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Notifications;

namespace NeoAstra.Push;

/// <summary>Identifies the push service that delivers the messages of a token.</summary>
public enum NeoPushService
{
    /// <summary>The Apple Push Notification service, on iOS.</summary>
    Apns,
    /// <summary>Firebase Cloud Messaging, on Android.</summary>
    Firebase,
}

/// <summary>Contains the address of the device at its push service, which the server of the application sends messages to.</summary>
/// <param name="Token">The token: the hexadecimal device token of APNs, or the registration token of Firebase Cloud Messaging.</param>
/// <param name="Service">The push service of the token.</param>
/// <param name="CanDisplay">Whether the user lets the application show the notifications of the messages; a message reaches the application without it.</param>
public sealed record NeoPushRegistration(string Token, NeoPushService Service, bool CanDisplay);

/// <summary>Contains one message of the push service.</summary>
/// <param name="MessageId">The identifier of the message, when the service gives one.</param>
/// <param name="ActionId">The action of the notification that the user chose, or <see langword="null"/> for the notification itself or a message that arrived.</param>
/// <param name="Payload">The content of the message as a JSON object: the payload of APNs on iOS, with its <c>aps</c> member, and the data and notification of Firebase on Android.</param>
public sealed record NeoPushMessage(string? MessageId, string? ActionId, string Payload);

/// <summary>Receives the messages that the server of the application sends to the device through its push service.</summary>
/// <remarks>
/// <para>
/// iOS gives the device token to the application delegate, which NeoAstra does not own: the application forwards it, and the
/// notification that launched it, to <see cref="NeoApplePush"/>, and declares the <c>aps-environment</c> entitlement. Android
/// receives the messages through Firebase Cloud Messaging: the application references <c>Xamarin.Firebase.Messaging</c> and
/// adds the <c>google-services.json</c> of its Firebase project.
/// </para>
/// <para>
/// A message that arrives while the application is in the foreground is shown and reported by <see cref="MessageReceived"/>; a
/// notification that the user taps is reported by <see cref="MessageOpened"/>, and the one that launched the application is
/// <see cref="LaunchMessage"/>. The events occur on the UI dispatcher.
/// </para>
/// </remarks>
public interface INeoPush
{
    /// <summary>Gets truthful platform support.</summary>
    NeoCapabilityInfo Support { get; }

    /// <summary>Gets the message whose notification launched the application, or <see langword="null"/>.</summary>
    NeoPushMessage? LaunchMessage { get; }

    /// <summary>Occurs when the push service gives the device a new token, which the server of the application needs.</summary>
    event EventHandler<NeoPushRegistration>? TokenChanged;

    /// <summary>Occurs when a message arrives while the application is in the foreground.</summary>
    event EventHandler<NeoPushMessage>? MessageReceived;

    /// <summary>Occurs when the user taps the notification of a message.</summary>
    event EventHandler<NeoPushMessage>? MessageOpened;

    /// <summary>
    /// Gets or sets what decides whether a message that arrives while the application is in the foreground is shown, such as
    /// one about what the user already sees on screen; <see langword="null"/> shows every message. The function runs on the UI
    /// dispatcher, and a message whose decision takes longer than a few seconds, or fails, is shown.
    /// </summary>
    Func<NeoPushMessage, CancellationToken, ValueTask<bool>>? ForegroundPresentation { get; set; }

    /// <summary>Asks the user once to show notifications, registers the device at its push service, and returns its token.</summary>
    /// <param name="cancellationToken">Stops waiting for the push service.</param>
    /// <returns>
    /// The registration; <see cref="NeoDesktopStatus.Unsupported"/> on a platform without push, or with the code <c>not-configured</c>
    /// for an application without the entitlement of APNs or the configuration of Firebase; <see cref="NeoDesktopStatus.Failed"/>
    /// when the push service does not answer.
    /// </returns>
    ValueTask<NeoDesktopResult<NeoPushRegistration>> RegisterAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers the device at its push service and returns its token without asking the user to show notifications, such as for
    /// an application that receives silent messages before the user decides; <see cref="NeoPushRegistration.CanDisplay"/> says
    /// what the user allowed so far, and <see cref="RegisterAsync"/> asks later.
    /// </summary>
    /// <param name="cancellationToken">Stops waiting for the push service.</param>
    /// <returns>The registration, with the statuses and codes of <see cref="RegisterAsync"/>.</returns>
    ValueTask<NeoDesktopResult<NeoPushRegistration>> RegisterWithoutAskingAsync(CancellationToken cancellationToken = default);
}

/// <summary>Creates the statically selected push adapter.</summary>
public static class NeoPush
{
    /// <summary>The code of a result for an application whose push service is not configured.</summary>
    public const string NotConfiguredCode = "not-configured";

    /// <summary>The code of a result for a push service that did not answer in time.</summary>
    public const string TimeoutCode = "timeout";

    /// <summary>Creates the adapter of the current operating system: APNs on iOS, Firebase Cloud Messaging on Android.</summary>
    /// <param name="notifications">The notification service of the application, which reports the notifications of the messages on iOS.</param>
    /// <param name="dispatcher">Optional application UI dispatcher; the plugin binds it otherwise.</param>
    /// <returns>The adapter, which answers <see cref="NeoDesktopStatus.Unsupported"/> on a desktop.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="notifications"/> is <see langword="null"/>.</exception>
    public static INeoPush CreateSystem(INeoNotifications notifications, NeoDispatcher? dispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        if (OperatingSystem.IsIOS()) return IosPush.Create(notifications, dispatcher);
        if (OperatingSystem.IsAndroid()) return AndroidPush.Create(dispatcher);
        return new UnsupportedPush("Push messages are not available on this platform yet.");
    }

    internal static NeoDesktopResult<NeoPushRegistration> Failure(NeoDesktopStatus status, string? code) => new(status, null, code);

    // A payload is a bounded JSON object; anything else is not a message of a push service.
    internal static bool IsPayload(string payload)
    {
        if (payload.Length > 65536) return false;
        try { using var document = JsonDocument.Parse(payload); return document.RootElement.ValueKind == JsonValueKind.Object; }
        catch (JsonException) { return false; }
    }
}

/// <summary>A deterministic push service for tests, whose registration the test sets and whose messages the test delivers.</summary>
public sealed class NeoFakePush : INeoPush
{
    /// <inheritdoc />
    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Emulated, 1, 0, "In-memory test push service.");

    /// <summary>Gets or sets the result that <see cref="RegisterAsync"/> and <see cref="RegisterWithoutAskingAsync"/> answer.</summary>
    public NeoDesktopResult<NeoPushRegistration> Registration { get; set; } = NeoPush.Failure(NeoDesktopStatus.Failed, NeoPush.TimeoutCode);

    /// <inheritdoc />
    public NeoPushMessage? LaunchMessage { get; set; }

    /// <inheritdoc />
    public event EventHandler<NeoPushRegistration>? TokenChanged;

    /// <inheritdoc />
    public event EventHandler<NeoPushMessage>? MessageReceived;

    /// <inheritdoc />
    public event EventHandler<NeoPushMessage>? MessageOpened;

    /// <inheritdoc />
    public Func<NeoPushMessage, CancellationToken, ValueTask<bool>>? ForegroundPresentation { get; set; }

    /// <inheritdoc />
    public ValueTask<NeoDesktopResult<NeoPushRegistration>> RegisterAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Registration); }

    /// <inheritdoc />
    public ValueTask<NeoDesktopResult<NeoPushRegistration>> RegisterWithoutAskingAsync(CancellationToken cancellationToken = default) => RegisterAsync(cancellationToken);

    /// <summary>Reports a new token as the push service would.</summary>
    /// <param name="registration">The registration.</param>
    public void ChangeToken(NeoPushRegistration registration) => TokenChanged?.Invoke(this, registration ?? throw new ArgumentNullException(nameof(registration)));

    /// <summary>Delivers a message that arrives in the foreground.</summary>
    /// <param name="message">The message.</param>
    public void Receive(NeoPushMessage message) => MessageReceived?.Invoke(this, message ?? throw new ArgumentNullException(nameof(message)));

    /// <summary>Delivers a message whose notification the user taps.</summary>
    /// <param name="message">The message.</param>
    public void Open(NeoPushMessage message) => MessageOpened?.Invoke(this, message ?? throw new ArgumentNullException(nameof(message)));
}

internal sealed class UnsupportedPush(string details) : INeoPush
{
    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.None, 1, 0, details);
    public NeoPushMessage? LaunchMessage => null;
    public event EventHandler<NeoPushRegistration>? TokenChanged { add { } remove { } }
    public event EventHandler<NeoPushMessage>? MessageReceived { add { } remove { } }
    public event EventHandler<NeoPushMessage>? MessageOpened { add { } remove { } }
    public Func<NeoPushMessage, CancellationToken, ValueTask<bool>>? ForegroundPresentation { get; set; }
    public ValueTask<NeoDesktopResult<NeoPushRegistration>> RegisterAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoPush.Failure(NeoDesktopStatus.Unsupported, null)); }
    public ValueTask<NeoDesktopResult<NeoPushRegistration>> RegisterWithoutAskingAsync(CancellationToken cancellationToken = default) => RegisterAsync(cancellationToken);
}

// What iOS binds to the UI dispatcher of the application when the plugin attaches.
internal interface INeoPushDispatcherBinding
{
    void Bind(NeoDispatcher dispatcher);
}
