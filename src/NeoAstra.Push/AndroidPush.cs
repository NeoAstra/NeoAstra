// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text;
using System.Text.Json;
using NeoAstra.Desktop;

namespace NeoAstra.Push;

// Firebase Cloud Messaging through the Java service `neoastra.push` of this package, which the platform services of NeoAstra
// reach. Java queues what happens while nobody asks; a thread of this adapter takes it one event at a time, waiting in Java,
// and raises the events on the UI dispatcher. The Java side is there only when the application references Firebase.
internal sealed class AndroidPush : INeoPush, INeoPushDispatcherBinding, IAsyncDisposable
{
    internal const string Service = "neoastra.push";
    private readonly CancellationTokenSource _stopping = new();
    private NeoDispatcher? _dispatcher;
    private Task? _events;
    private NeoPushMessage? _launch;
    private bool _launchRead;

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Native, 1, 0,
        "Firebase Cloud Messaging: the registration token, the messages that arrive, shown in the foreground, and the notifications that the user taps (Xamarin.Firebase.Messaging, google-services.json, android.permission.POST_NOTIFICATIONS).");

    public NeoPushMessage? LaunchMessage
    {
        get
        {
            if (_launchRead) return _launch;
            try { using var response = Call("launch", null); _launch = Status(response.RootElement) == NeoDesktopStatus.Success ? Message(response.RootElement.GetProperty("message")) : null; }
            catch (Exception exception) when (exception is NotSupportedException or NeoAstraException or JsonException) { _launch = null; }
            _launchRead = true;
            return _launch;
        }
    }

    public event EventHandler<NeoPushRegistration>? TokenChanged;
    public event EventHandler<NeoPushMessage>? MessageReceived;
    public event EventHandler<NeoPushMessage>? MessageOpened;

    // Java asks for the decision of a message that arrives in the foreground only while there is a function to decide it.
    public Func<NeoPushMessage, CancellationToken, ValueTask<bool>>? ForegroundPresentation
    {
        get => _presentation;
        set
        {
            _presentation = value;
            try { using var _ = Call("filter", writer => writer.WriteBoolean("enabled", value is not null)); }
            catch (Exception exception) when (exception is NotSupportedException or NeoAstraException or JsonException) { }
        }
    }
    private Func<NeoPushMessage, CancellationToken, ValueTask<bool>>? _presentation;

    // An application without the plugin binds the dispatcher here: the events of Java are read from then on.
    internal static AndroidPush Create(NeoDispatcher? dispatcher)
    {
        var push = new AndroidPush();
        if (dispatcher is not null) ((INeoPushDispatcherBinding)push).Bind(dispatcher);
        return push;
    }

    internal bool IsBound => _events is not null;

    void INeoPushDispatcherBinding.Bind(NeoDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher ??= dispatcher;
        _events ??= Task.Factory.StartNew(ReadEvents, _stopping.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public ValueTask<NeoDesktopResult<NeoPushRegistration>> RegisterAsync(CancellationToken cancellationToken = default)
        => new(Task.Run(() => Register(ask: true), cancellationToken).WaitAsync(cancellationToken));

    public ValueTask<NeoDesktopResult<NeoPushRegistration>> RegisterWithoutAskingAsync(CancellationToken cancellationToken = default)
        => new(Task.Run(() => Register(ask: false), cancellationToken).WaitAsync(cancellationToken));

    public async ValueTask DisposeAsync()
    {
        if (_stopping.IsCancellationRequested) return;
        _stopping.Cancel();
        TokenChanged = null; MessageReceived = null; MessageOpened = null;
        // The thread waits in Java for at most a few seconds before it sees that the adapter stops.
        if (_events is not null) try { await _events.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch (Exception exception) when (exception is TimeoutException or OperationCanceledException) { }
        _stopping.Dispose();
    }

    private static NeoDesktopResult<NeoPushRegistration> Register(bool ask)
    {
        using var response = Call("register", writer => writer.WriteBoolean("ask", ask));
        var root = response.RootElement;
        var status = Status(root);
        if (status != NeoDesktopStatus.Success) return NeoPush.Failure(status, root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null);
        return NeoDesktopResult<NeoPushRegistration>.Success(new(root.GetProperty("token").GetString()!, NeoPushService.Firebase, root.TryGetProperty("canDisplay", out var display) && display.ValueKind == JsonValueKind.True));
    }

    private void ReadEvents()
    {
        var failures = 0;
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                using var response = Call("next", writer => writer.WriteNumber("timeout", 5000));
                failures = 0;
                if (Status(response.RootElement) == NeoDesktopStatus.Success) Dispatch(response.RootElement.GetProperty("event").Clone());
                else if (Status(response.RootElement) == NeoDesktopStatus.Unsupported) return;
            }
            catch (NotSupportedException) { return; }
            catch (Exception exception) when (exception is NeoAstraException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // A failed read is retried a few times; a service that keeps failing is not there.
                if (++failures > 5) return;
                _stopping.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            }
        }
    }

    private void Dispatch(JsonElement value)
    {
        Action? raise = value.GetProperty("kind").GetString() switch
        {
            "token" when value.GetProperty("token").GetString() is { } token => () => TokenChanged?.Invoke(this, new(token, NeoPushService.Firebase, false)),
            "received" => () =>
            {
                var message = Message(value.GetProperty("message"));
                MessageReceived?.Invoke(this, message);
                if (value.TryGetProperty("decision", out var decision) && decision.GetString() is { } id) _ = PresentAsync(id, message);
            },
            "opened" => () => MessageOpened?.Invoke(this, Message(value.GetProperty("message"))),
            _ => null,
        };
        if (raise is null || _dispatcher is not { } dispatcher) return;
        try { _ = dispatcher.InvokeAsync(() => { if (!_stopping.IsCancellationRequested) try { raise(); } catch { } }); }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException) { }
    }

    // The decision goes back to Java, which waits a few seconds for it before it shows the message anyway.
    private async Task PresentAsync(string id, NeoPushMessage message)
    {
        var show = true;
        try
        {
            if (_presentation is { } decide)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3.5));
                show = await decide(message, timeout.Token).AsTask().WaitAsync(timeout.Token).ConfigureAwait(true);
            }
        }
        catch { show = true; }
        try { using var _ = Call("present", writer => { writer.WriteString("id", id); writer.WriteBoolean("show", show); }); }
        catch (Exception exception) when (exception is NotSupportedException or NeoAstraException or JsonException) { }
    }

    internal static NeoPushMessage Message(JsonElement value)
        => new(value.TryGetProperty("messageId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null, null,
            value.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object ? payload.GetRawText() : "{}");

    private static NeoDesktopStatus Status(JsonElement root) =>
        root.TryGetProperty("status", out var status) && Enum.TryParse<NeoDesktopStatus>(status.GetString(), out var value) ? value : NeoDesktopStatus.Failed;

    internal static JsonDocument Call(string operation, Action<Utf8JsonWriter>? write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", operation);
            write?.Invoke(writer);
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(NeoPlatformServices.Call(Service, Encoding.UTF8.GetString(stream.ToArray())));
    }
}
