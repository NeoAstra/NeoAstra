// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Notifications;

namespace NeoAstra.Push;

/// <summary>Takes what iOS gives the application delegate for push messages, which the application forwards because NeoAstra does not own its delegate.</summary>
/// <remarks>
/// Forward <c>RegisteredForRemoteNotifications</c> to <see cref="DidRegister"/>, <c>FailedToRegisterForRemoteNotifications</c>
/// to <see cref="DidFailToRegister"/>, and the <c>NotificationResponse</c> of the connection options of a scene that the user
/// opened with a notification to <see cref="DidLaunch"/>, and <c>DidReceiveRemoteNotification</c> with its completion handler to
/// <see cref="DidReceiveRemoteNotification"/>. Call them on the main thread, as iOS does.
/// </remarks>
public static class NeoApplePush
{
    private static readonly object Sync = new();
    private static IosPush? s_current;
    private static NeoPushMessage? s_launch;

    /// <summary>Takes the device token that iOS gives <c>application:didRegisterForRemoteNotificationsWithDeviceToken:</c>.</summary>
    /// <param name="deviceToken">The bytes of the device token.</param>
    /// <exception cref="ArgumentException"><paramref name="deviceToken"/> is empty or larger than 256 bytes.</exception>
    public static void DidRegister(ReadOnlySpan<byte> deviceToken)
    {
        if (deviceToken.IsEmpty || deviceToken.Length > 256) throw new ArgumentException("A device token has 1 to 256 bytes.", nameof(deviceToken));
        var token = Convert.ToHexStringLower(deviceToken);
        Current()?.Registered(token, null);
    }

    /// <summary>Takes the error that iOS gives <c>application:didFailToRegisterForRemoteNotificationsWithError:</c>.</summary>
    /// <param name="description">The localized description of the error.</param>
    /// <exception cref="ArgumentNullException"><paramref name="description"/> is <see langword="null"/>.</exception>
    public static void DidFailToRegister(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        Current()?.Registered(null, description);
    }

    /// <summary>Takes the notification response with which the user opened a scene of the application, such as the one that launched it.</summary>
    /// <param name="response">The handle of the <c>UNNotificationResponse</c>, or zero when the scene was not opened with a notification.</param>
    public static void DidLaunch(nint response)
    {
        if (response == 0 || IosPush.Read(response) is not { } message) return;
        IosPush? current;
        lock (Sync) { current = s_current; if (current is null) s_launch = message; }
        current?.Opened(message, launch: true);
    }

    /// <summary>
    /// Takes a message that iOS gives <c>application:didReceiveRemoteNotification:fetchCompletionHandler:</c>: a silent one
    /// (<c>content-available</c> without an alert), which can wake the application in the background, is reported by
    /// <see cref="INeoPush.MessageReceived"/>; one with an alert is reported as before, when it is shown.
    /// </summary>
    /// <param name="userInfo">The handle of the <c>userInfo</c> dictionary of the message.</param>
    /// <param name="completion">
    /// Calls the completion handler of iOS: at once for a message that is not reported, otherwise a few seconds later, so that
    /// the application can fetch what changed.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="completion"/> is <see langword="null"/>.</exception>
    public static void DidReceiveRemoteNotification(nint userInfo, Action completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (userInfo == 0 || Current() is not { } current || !current.Silent(userInfo)) { completion(); return; }
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => { try { completion(); } catch { } }, TaskScheduler.Default);
    }

    internal static void Attach(IosPush push)
    {
        NeoPushMessage? launch;
        lock (Sync) { s_current = push; launch = s_launch; s_launch = null; }
        if (launch is not null) push.Opened(launch, launch: true);
    }

    internal static void Detach(IosPush push) { lock (Sync) if (ReferenceEquals(s_current, push)) s_current = null; }

    private static IosPush? Current() { lock (Sync) return s_current; }
}

// The Apple Push Notification service through UIKit: the application registers, iOS gives the token to the application delegate,
// which forwards it, and the notification service of NeoAstra, which owns the delegate of the notification center, reports the
// notifications that it did not show. A notification of APNs carries the `aps` member in its payload.
internal sealed partial class IosPush : INeoPush, INeoPushDispatcherBinding, IAsyncDisposable
{
    private static nint s_stackBlock;
    private static unsafe BlockDescriptor* s_descriptor;

    private readonly object _sync = new();
    private readonly INeoExternalNotificationSource? _source;
    private readonly INeoNotifications? _notifications;
    private NeoDispatcher? _dispatcher;
    private TaskCompletionSource<(string? Token, string? Error)>? _registration;
    private string? _token;
    private bool _canDisplay;
    private int _disposed;

    private IosPush(INeoNotifications? notifications, NeoDispatcher? dispatcher)
    {
        var source = notifications as INeoExternalNotificationSource;
        _notifications = notifications;
        _source = source;
        _dispatcher = dispatcher;
        Support = source is null
            ? new(NeoSupportLevel.Limited, 1, 0, "The device token of APNs; the notification service does not report the notifications of the messages.")
            : new(NeoSupportLevel.Native, 1, 0, "The Apple Push Notification service: the device token, the messages that arrive in the foreground, and the notifications that the user taps (aps-environment entitlement).");
        if (source is not null)
        {
            source.ExternalReceived += OnReceived;
            source.ExternalActivated += OnActivated;
        }
        NeoApplePush.Attach(this);
    }

    public NeoCapabilityInfo Support { get; }

    public NeoPushMessage? LaunchMessage { get; private set; }

    public event EventHandler<NeoPushRegistration>? TokenChanged;
    public event EventHandler<NeoPushMessage>? MessageReceived;
    public event EventHandler<NeoPushMessage>? MessageOpened;

    // The notification service decides the presentation of what it did not show itself: a push message by this function, any
    // other notification as before.
    public Func<NeoPushMessage, CancellationToken, ValueTask<bool>>? ForegroundPresentation
    {
        get => _presentation;
        set
        {
            _presentation = value;
            if (_source is not null) _source.ExternalPresentation = value is null ? null : (notification, cancellationToken) => Message(notification) is { } message ? value(message, cancellationToken) : ValueTask.FromResult(true);
        }
    }
    private Func<NeoPushMessage, CancellationToken, ValueTask<bool>>? _presentation;

    internal static INeoPush Create(INeoNotifications notifications, NeoDispatcher? dispatcher) => new IosPush(notifications, dispatcher);

    void INeoPushDispatcherBinding.Bind(NeoDispatcher dispatcher) { ArgumentNullException.ThrowIfNull(dispatcher); _dispatcher ??= dispatcher; }

    public ValueTask<NeoDesktopResult<NeoPushRegistration>> RegisterAsync(CancellationToken cancellationToken = default) => RegisterAsync(ask: true, cancellationToken);

    public ValueTask<NeoDesktopResult<NeoPushRegistration>> RegisterWithoutAskingAsync(CancellationToken cancellationToken = default) => RegisterAsync(ask: false, cancellationToken);

    private async ValueTask<NeoDesktopResult<NeoPushRegistration>> RegisterAsync(bool ask, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("The iOS push service must be bound to the UI dispatcher before use.");
        // APNs registers the application without the permission of the user, which only showing the notifications needs.
        _canDisplay = ask ? await AuthorizeAsync(dispatcher, cancellationToken).ConfigureAwait(false) : await CanDisplayAsync(cancellationToken).ConfigureAwait(false);
        var registration = BeginRegistration();
        await Invoke(dispatcher, () => Send(Send(Class("UIApplication"), "sharedApplication"), "registerForRemoteNotifications"), cancellationToken).ConfigureAwait(false);
        try
        {
            var (token, error) = await registration.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (token is not null) return NeoDesktopResult<NeoPushRegistration>.Success(Registration(token, _canDisplay));
            // Without the aps-environment entitlement iOS does not register the application.
            return error is not null && error.Contains("aps-environment", StringComparison.Ordinal)
                ? NeoPush.Failure(NeoDesktopStatus.Unsupported, NeoPush.NotConfiguredCode)
                : NeoPush.Failure(NeoDesktopStatus.Failed, null);
        }
        catch (TimeoutException) { return NeoPush.Failure(NeoDesktopStatus.Failed, NeoPush.TimeoutCode); }
    }

    // The callers that register while APNs has not answered wait for the same answer, which alone ends the registration: a
    // caller that gives up or times out leaves it to the others.
    internal Task<(string? Token, string? Error)> BeginRegistration()
    {
        lock (_sync) return (_registration ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    }

    // iOS asks once and then answers at once.
    private static async Task<bool> AuthorizeAsync(NeoDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var authorization = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = GCHandle.Alloc(authorization);
        try
        {
            await Invoke(dispatcher, () => RequestAuthorization(handle), cancellationToken).ConfigureAwait(false);
            return await authorization.Task.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) { return false; }
    }

    private async Task<bool> CanDisplayAsync(CancellationToken cancellationToken)
        => _notifications is not null && await _notifications.GetPermissionStatusAsync(cancellationToken).ConfigureAwait(false) == NeoNotificationPermissionStatus.Granted;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        NeoApplePush.Detach(this);
        if (_source is not null) { _source.ExternalReceived -= OnReceived; _source.ExternalActivated -= OnActivated; }
        TokenChanged = null; MessageReceived = null; MessageOpened = null;
        TaskCompletionSource<(string? Token, string? Error)>? registration;
        lock (_sync) { registration = _registration; _registration = null; }
        registration?.TrySetResult((null, null));
        return ValueTask.CompletedTask;
    }

    internal void Registered(string? token, string? error)
    {
        TaskCompletionSource<(string? Token, string? Error)>? registration;
        bool changed;
        lock (_sync) { registration = _registration; _registration = null; changed = token is not null && !string.Equals(token, _token, StringComparison.Ordinal); if (token is not null) _token = token; }
        registration?.TrySetResult((token, error));
        if (changed && Volatile.Read(ref _disposed) == 0) Raise(() => TokenChanged?.Invoke(this, Registration(token!, _canDisplay)));
    }

    internal void Opened(NeoPushMessage message, bool launch)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (launch) LaunchMessage = message;
        Raise(() => MessageOpened?.Invoke(this, message));
    }

    // A message of APNs without an alert: the notification center never sees it, so it is reported here.
    internal bool Silent(nint userInfo)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        var payload = Json(userInfo);
        if (!NeoPush.IsPayload(payload)) return false;
        using (var document = JsonDocument.Parse(payload))
            if (!document.RootElement.TryGetProperty("aps", out var aps) || aps.ValueKind != JsonValueKind.Object || aps.TryGetProperty("alert", out _)) return false;
        var message = new NeoPushMessage(null, null, payload);
        Raise(() => MessageReceived?.Invoke(this, message));
        return true;
    }

    private void OnReceived(object? sender, NeoExternalNotification notification) { if (Message(notification) is { } message) Raise(() => MessageReceived?.Invoke(this, message)); }

    private void OnActivated(object? sender, NeoExternalNotification notification) { if (Message(notification) is { } message) Opened(message, launch: false); }

    private static void Raise(Action action) { try { action(); } catch { } }

    // Only a notification of APNs carries `aps`; another notification that NeoAstra did not show is not a push message.
    private static NeoPushMessage? Message(NeoExternalNotification notification)
    {
        if (!NeoPush.IsPayload(notification.Payload)) return null;
        using var document = JsonDocument.Parse(notification.Payload);
        return document.RootElement.TryGetProperty("aps", out _) ? new(notification.NotificationId, notification.ActionId, notification.Payload) : null;
    }

    internal static NeoPushMessage? Read(nint response)
    {
        var request = Send(Send(response, "notification"), "request");
        var id = Text(Send(request, "identifier"));
        var action = Text(Send(response, "actionIdentifier"));
        var payload = Json(Send(Send(request, "content"), "userInfo"));
        if (id is null) return null;
        var message = Message(new NeoExternalNotification(id, action is null or "com.apple.UNNotificationDefaultActionIdentifier" ? null : action, payload));
        return message;
    }

    private static ValueTask Invoke(NeoDispatcher dispatcher, Action action, CancellationToken cancellationToken)
    {
        if (!dispatcher.CheckAccess()) return dispatcher.InvokeAsync(action, cancellationToken);
        action();
        return ValueTask.CompletedTask;
    }

    // UNAuthorizationOptionBadge, Sound, and Alert; the block answers on a queue of the notification center.
    private static unsafe void RequestAuthorization(GCHandle handle)
    {
        EnsureBlockRuntime();
        var block = new Block { Isa = s_stackBlock, Invoke = (nint)(delegate* unmanaged[Cdecl]<Block*, byte, nint, void>)&Authorized, Descriptor = s_descriptor, Context = GCHandle.ToIntPtr(handle) };
        SendAuthorization(Send(Class("UNUserNotificationCenter"), "currentNotificationCenter"), Selector("requestAuthorizationWithOptions:completionHandler:"), 0x7, &block);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void Authorized(Block* block, byte granted, nint error)
    {
        var handle = GCHandle.FromIntPtr(block->Context);
        try { if (handle.Target is TaskCompletionSource<bool> completion) completion.TrySetResult(granted != 0); }
        catch { }
        finally { handle.Free(); }
    }

    private static unsafe void EnsureBlockRuntime()
    {
        if (s_stackBlock != 0) return;
        s_stackBlock = NativeLibrary.GetExport(NativeLibrary.GetMainProgramHandle(), "_NSConcreteStackBlock");
        var descriptor = (BlockDescriptor*)NativeMemory.Alloc((nuint)sizeof(BlockDescriptor));
        *descriptor = new() { Size = (nuint)sizeof(Block) };
        s_descriptor = descriptor;
    }

    private static readonly Lazy<NeoApnsEnvironment> s_environment = new(static () =>
    {
        byte[]? profile = null;
        try { if (Text(Send(Send(Class("NSBundle"), "mainBundle"), "bundlePath")) is { } bundle && Path.Combine(bundle, "embedded.mobileprovision") is var path && File.Exists(path)) profile = File.ReadAllBytes(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return ApnsEnvironment(profile, RuntimeInformation.RuntimeIdentifier.StartsWith("iossimulator", StringComparison.Ordinal));
    });
    private static readonly Lazy<string?> s_topic = new(static () => Text(Send(Send(Class("NSBundle"), "mainBundle"), "bundleIdentifier")));

    private static NeoPushRegistration Registration(string token, bool canDisplay) => new(token, NeoPushService.Apns, canDisplay) { ApnsEnvironment = s_environment.Value, Topic = s_topic.Value };

    // The provisioning profile is a signed property list whose entitlements name the environment of APNs; the signature is the
    // concern of iOS, which installed the application, so the property list is read as the text it holds.
    internal static NeoApnsEnvironment ApnsEnvironment(byte[]? profile, bool simulator)
    {
        if (profile is null) return simulator ? NeoApnsEnvironment.Development : NeoApnsEnvironment.Production;
        var text = Encoding.Latin1.GetString(profile);
        var key = text.IndexOf("<key>aps-environment</key>", StringComparison.Ordinal);
        var start = key < 0 ? -1 : text.IndexOf("<string>", key, StringComparison.Ordinal);
        var end = start < 0 ? -1 : text.IndexOf("</string>", start, StringComparison.Ordinal);
        return end > start && text.AsSpan(start + "<string>".Length, end - start - "<string>".Length).Trim().SequenceEqual("production") ? NeoApnsEnvironment.Production : NeoApnsEnvironment.Development;
    }

    private static string? Text(nint value) => value == 0 ? null : Marshal.PtrToStringUTF8(Send(value, "UTF8String"));

    private static unsafe string Json(nint dictionary)
    {
        var serialization = Class("NSJSONSerialization");
        if (dictionary == 0 || (Send(serialization, Selector("isValidJSONObject:"), dictionary) & 0xFF) == 0) return "{}";
        var data = Send(serialization, Selector("dataWithJSONObject:options:error:"), dictionary, 0, 0);
        var length = data == 0 ? 0 : (long)Send(data, "length");
        if (length is <= 0 or > 65536) return "{}";
        return Encoding.UTF8.GetString((byte*)Send(data, "bytes"), (int)length);
    }

    [StructLayout(LayoutKind.Sequential)] private unsafe struct Block { internal nint Isa; internal int Flags, Reserved; internal nint Invoke; internal BlockDescriptor* Descriptor; internal nint Context; }
    [StructLayout(LayoutKind.Sequential)] private struct BlockDescriptor { internal nuint Reserved, Size; }

    private const string Runtime = "/usr/lib/libobjc.A.dylib";
    [LibraryImport(Runtime, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Class(string name);
    [LibraryImport(Runtime, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Selector(string name);
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")] private static partial nint Send(nint receiver, nint selector);
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")] private static partial nint Send(nint receiver, nint selector, nint argument);
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")] private static partial nint Send(nint receiver, nint selector, nint first, nint second, nint third);
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")] private static unsafe partial void SendAuthorization(nint receiver, nint selector, nuint options, Block* block);

    private static nint Send(nint receiver, string selector) => Send(receiver, Selector(selector));
}
