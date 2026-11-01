// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.Json;
using System.Text.Json.Serialization;
using NeoAstra.Desktop;
using NeoAstra.Rpc;

namespace NeoAstra.Push;

/// <summary>The push plugin of NeoAstra: it owns the push service and declares its renderer commands and events, which it grants to no one.</summary>
public sealed class NeoPushPlugin : INeoAstraPlugin
{
    /// <summary>The stable plugin ID.</summary>
    public const string Id = "neoastra.push";

    internal static readonly string[] Commands = ["push.register", "push.launch-message", "push.present"];
    internal static readonly string[] Events = ["push.token-changed", "push.message-received", "push.message-opened", "push.foreground-message"];

    private readonly INeoPush _push;
    private int _disposed;

    /// <summary>Initializes the plugin, which takes ownership of the push service.</summary>
    /// <param name="push">The push service.</param>
    /// <exception cref="ArgumentNullException"><paramref name="push"/> is <see langword="null"/>.</exception>
    public NeoPushPlugin(INeoPush push) => _push = push ?? throw new ArgumentNullException(nameof(push));

    /// <summary>Gets the permission declarations of the renderer commands and events; they grant nothing until a capability document names them.</summary>
    public static NeoPluginPermissionCatalog PermissionCatalog { get; } = CreatePermissionCatalog();

    /// <inheritdoc />
    public NeoPluginMetadata Metadata { get; } = CreateMetadata();

    /// <inheritdoc />
    public INeoPluginAdapter CreateAdapter() => new PushAdapter(_push);

    /// <inheritdoc />
    public ValueTask ConfigureAsync(NeoPluginContext context, CancellationToken cancellationToken) { ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }

    /// <inheritdoc />
    public ValueTask ReadyAsync(NeoPluginContext context, CancellationToken cancellationToken) { ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }

    /// <inheritdoc />
    public ValueTask StoppingAsync(NeoPluginContext context, CancellationToken cancellationToken) { ArgumentNullException.ThrowIfNull(context); return ValueTask.CompletedTask; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _push is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(false);
    }

    private static NeoPermissionDeclaration Find(string operation) => PermissionCatalog.Permissions.Single(permission => permission.Commands.Contains(operation, StringComparer.Ordinal));

    private static NeoPluginMetadata CreateMetadata() => new(Id, new Version(1, 0, 0), 1, new Version(0, 1, 0),
        commands: Commands.Select(static command => { var permission = Find(command); return new NeoPluginCommandDeclaration(command, permission.Id, null, permission.Risk, Audited: true); }).ToArray(),
        events: Events.Select(static name => { var permission = Find(name); return new NeoPluginEventDeclaration(name, permission.Id, permission.Risk, Audited: true); }).ToArray(),
        permissionCatalog: PermissionCatalog, hasStaticJsonMetadata: true);

    private static NeoPluginPermissionCatalog CreatePermissionCatalog() => new(Id, "1.0.0", "0.1.0",
    [
        // The token addresses the device; registering asks the user to show notifications unless the page registers without asking.
        Permission("push:register", ["push.register", "push.token-changed"], PushRendererRegistration.RegisterTimeout),
        Permission("push:receive", ["push.launch-message", "push.message-received", "push.message-opened"], TimeSpan.FromSeconds(30)),
        // A page that decides whether a message in the foreground is shown sees it first.
        Permission("push:foreground", ["push.foreground-message", "push.present"], TimeSpan.FromSeconds(30)),
    ]);

    private static NeoPermissionDeclaration Permission(string id, string[] operations, TimeSpan timeout) => new(id, 1, operations, NeoPermissionRisk.Sensitive, NeoScopeFamily.None)
    {
        MaximumConcurrency = 4,
        DefaultTimeout = timeout,
        Redaction = NeoAuditRedaction.Full,
        Documentation = "Official NeoAstra push renderer permission. Registration and grants remain explicit.",
    };

    private sealed class PushAdapter(INeoPush push) : INeoPluginAdapter
    {
        public NeoCapabilityInfo Support => push.Support;

        public ValueTask AttachAsync(NeoApplication application, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(application);
            cancellationToken.ThrowIfCancellationRequested();
            if (!application.Dispatcher.CheckAccess()) throw new InvalidOperationException("The push adapter must attach on the UI dispatcher.");
            if (push is INeoPushDispatcherBinding binding) binding.Bind(application.Dispatcher);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Contains the static registration helpers of the push plugin.</summary>
public static class NeoPushPluginExtensions
{
    /// <summary>Adds the push plugin through an explicit AOT-safe factory and transfers the ownership of the service to the plugin host. This does not register renderer handlers or grants.</summary>
    /// <param name="builder">The plugin builder.</param>
    /// <param name="push">The push service.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static NeoPluginBuilder AddNeoAstraPush(this NeoPluginBuilder builder, INeoPush push)
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentNullException.ThrowIfNull(push);
        return builder.AddNeoAstraPlugin(() => new NeoPushPlugin(push));
    }

    /// <summary>Adds the permission declarations of the push plugin to application capability tooling. This grants no renderer authority.</summary>
    /// <param name="builder">The permission catalog builder.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static NeoPermissionCatalogBuilder AddNeoAstraPushPermissions(this NeoPermissionCatalogBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddPlugin(NeoPushPlugin.PermissionCatalog);
    }

    /// <summary>Adds the push renderer handlers and events with source-generated JSON metadata. Calling this method grants nothing.</summary>
    /// <param name="builder">The explicit RPC builder.</param>
    /// <param name="push">The push service.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static NeoRpcBuilder AddNeoAstraPushHandlers(this NeoRpcBuilder builder, INeoPush push) => AddNeoAstraPushHandlers(builder, push, new NeoPushRendererOptions());

    /// <summary>Adds the push renderer handlers and events with options, such as letting the pages decide the foreground presentation. Calling this method grants nothing.</summary>
    /// <param name="builder">The explicit RPC builder.</param>
    /// <param name="push">The push service.</param>
    /// <param name="options">The options of the handlers.</param>
    /// <returns>The builder.</returns>
    /// <remarks>
    /// With <see cref="NeoPushRendererOptions.PagesDecideForegroundPresentation"/>, the handlers set <see cref="INeoPush.ForegroundPresentation"/>,
    /// replacing what the application set: a message that arrives in the foreground goes to the pages that listen to
    /// <c>push.foreground-message</c> with a decision ID, the first page that answers <c>push.present</c> decides, and a message is
    /// shown when no page listens or none answers within <see cref="NeoPushRendererOptions.ForegroundDecisionTimeout"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The foreground decision timeout is not positive or longer than ten seconds.</exception>
    public static NeoRpcBuilder AddNeoAstraPushHandlers(this NeoRpcBuilder builder, INeoPush push, NeoPushRendererOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentNullException.ThrowIfNull(push); ArgumentNullException.ThrowIfNull(options);
        // iOS and Android hold the presentation only a few seconds before they show the message anyway.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ForegroundDecisionTimeout, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.ForegroundDecisionTimeout, TimeSpan.FromSeconds(10), nameof(options));
        new PushRendererRegistration(push, options).Register(builder);
        return builder;
    }
}

/// <summary>Configures the push renderer handlers.</summary>
public sealed class NeoPushRendererOptions
{
    /// <summary>Gets or sets whether the pages decide whether a message that arrives in the foreground is shown; <see langword="false"/> by default.</summary>
    public bool PagesDecideForegroundPresentation { get; set; }

    /// <summary>Gets or sets how long a message in the foreground waits for the decision of a page before it is shown; three seconds by default, at most ten.</summary>
    public TimeSpan ForegroundDecisionTimeout { get; set; } = TimeSpan.FromSeconds(3);
}

internal sealed class PushRendererRegistration(INeoPush push, NeoPushRendererOptions options)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>> _decisions = new(StringComparer.Ordinal);
    private NeoRpcEvent<PushForegroundDto>? _foreground;

    // Registering waits for the question of the system and then for the push service.
    internal static readonly TimeSpan RegisterTimeout = TimeSpan.FromMinutes(3);

    public void Register(NeoRpcBuilder builder)
    {
        var json = PushRendererJsonContext.Default;
        builder.AddCommand("push.register", RegisterAsync, json.PushRegisterRequest, json.PushRegistrationResult, Options("push:register", RegisterTimeout));
        builder.AddCommand("push.launch-message", LaunchMessageAsync, json.PushEmptyRequest, json.PushLaunchResult, Options("push:receive", TimeSpan.FromSeconds(30)));
        var tokenChanged = builder.AddEvent("push.token-changed", json.PushRegistrationDto, new() { Permission = "push:register" });
        var received = builder.AddEvent("push.message-received", json.PushMessageDto, new() { Permission = "push:receive" });
        var opened = builder.AddEvent("push.message-opened", json.PushMessageDto, new() { Permission = "push:receive" });
        builder.AddCommand("push.present", PresentAsync, json.PushPresentRequest, json.PushPresentResult, Options("push:foreground", TimeSpan.FromSeconds(30)));
        _foreground = builder.AddEvent("push.foreground-message", json.PushForegroundDto, new() { Permission = "push:foreground" });
        if (options.PagesDecideForegroundPresentation) push.ForegroundPresentation = DecideAsync;
        push.TokenChanged += (_, registration) => _ = PublishAsync(tokenChanged, PushRegistrationDto.From(registration));
        push.MessageReceived += (_, message) => _ = PublishAsync(received, PushMessageDto.From(message));
        push.MessageOpened += (_, message) => _ = PublishAsync(opened, PushMessageDto.From(message));
    }

    private static NeoRpcCommandOptions Options(string permission, TimeSpan timeout) => new() { Permission = permission, Dispatch = NeoRpcDispatchMode.Background, MaximumConcurrency = 4, Timeout = timeout };

    // A page that is gone, or a host that is disposed, takes no event; the push service goes on.
    private static async Task PublishAsync<T>(NeoRpcEvent<T> handle, T value)
    {
        try { await handle.PublishAsync(value).ConfigureAwait(false); }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException) { }
    }

    // A page that only receives silent messages registers without asking the user to show notifications.
    private async ValueTask<PushRegistrationResult> RegisterAsync(PushRegisterRequest request, NeoRpcContext context, CancellationToken token)
    {
        var result = request.Ask == false ? await push.RegisterWithoutAskingAsync(token).ConfigureAwait(false) : await push.RegisterAsync(token).ConfigureAwait(false);
        return new(result.Status, result.Value is { } value ? PushRegistrationDto.From(value) : null, result.Code);
    }

    // The pages that listen see the message first; the first answer decides, and without one the message is shown.
    private async ValueTask<bool> DecideAsync(NeoPushMessage message, CancellationToken token)
    {
        var id = Guid.NewGuid().ToString("N");
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _decisions[id] = answer;
        try
        {
            var listening = _foreground is { } foreground ? await foreground.PublishAsync(new PushForegroundDto(id, PushMessageDto.From(message)), token).ConfigureAwait(false) : 0;
            return listening == 0 || await answer.Task.WaitAsync(options.ForegroundDecisionTimeout, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { return true; }
        finally { _decisions.TryRemove(id, out _); }
    }

    private ValueTask<PushPresentResult> PresentAsync(PushPresentRequest request, NeoRpcContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PushPresentResult(request.DecisionId is { } id && _decisions.TryGetValue(id, out var answer) && answer.TrySetResult(request.Show)));
    }

    private ValueTask<PushLaunchResult> LaunchMessageAsync(PushEmptyRequest request, NeoRpcContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PushLaunchResult(push.LaunchMessage is { } message ? PushMessageDto.From(message) : null));
    }
}

internal sealed record PushEmptyRequest;
internal sealed record PushRegistrationDto(string Token, NeoPushService Service, bool CanDisplay, NeoApnsEnvironment? ApnsEnvironment, string? Topic)
{
    internal static PushRegistrationDto From(NeoPushRegistration value) => new(value.Token, value.Service, value.CanDisplay, value.ApnsEnvironment, value.Topic);
}
internal sealed record PushRegistrationResult(NeoDesktopStatus Status, PushRegistrationDto? Value, string? Code);
// The payload goes to the page as the JSON object it is, not as a string of it.
internal sealed record PushMessageDto(string? MessageId, string? ActionId, JsonElement Payload)
{
    internal static PushMessageDto From(NeoPushMessage value)
    {
        using var document = JsonDocument.Parse(NeoPush.IsPayload(value.Payload) ? value.Payload : "{}");
        return new(value.MessageId, value.ActionId, document.RootElement.Clone());
    }
}
internal sealed record PushLaunchResult(PushMessageDto? Value);
internal sealed record PushRegisterRequest(bool? Ask);
internal sealed record PushForegroundDto(string DecisionId, PushMessageDto Message);
internal sealed record PushPresentRequest(string? DecisionId, bool Show);
internal sealed record PushPresentResult(bool Accepted);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(PushEmptyRequest))]
[JsonSerializable(typeof(PushRegistrationDto))]
[JsonSerializable(typeof(PushRegistrationResult))]
[JsonSerializable(typeof(PushMessageDto))]
[JsonSerializable(typeof(PushLaunchResult))]
[JsonSerializable(typeof(PushRegisterRequest))]
[JsonSerializable(typeof(PushForegroundDto))]
[JsonSerializable(typeof(PushPresentRequest))]
[JsonSerializable(typeof(PushPresentResult))]
internal sealed partial class PushRendererJsonContext : JsonSerializerContext;
