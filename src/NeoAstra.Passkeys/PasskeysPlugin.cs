// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.Json;
using System.Text.Json.Serialization;
using NeoAstra.Desktop;
using NeoAstra.Rpc;

namespace NeoAstra.Passkeys;

/// <summary>The passkeys plugin of NeoAstra: it owns the passkeys service and declares its renderer commands, which it grants to no one.</summary>
public sealed class NeoPasskeysPlugin : INeoAstraPlugin
{
    /// <summary>The stable plugin ID.</summary>
    public const string Id = "neoastra.passkeys";

    internal static readonly string[] Commands = ["passkeys.create", "passkeys.get"];

    private readonly INeoPasskeys _passkeys;
    private int _disposed;

    /// <summary>Initializes the plugin, which takes ownership of the passkeys service.</summary>
    /// <param name="passkeys">The passkeys service.</param>
    /// <exception cref="ArgumentNullException"><paramref name="passkeys"/> is <see langword="null"/>.</exception>
    public NeoPasskeysPlugin(INeoPasskeys passkeys) => _passkeys = passkeys ?? throw new ArgumentNullException(nameof(passkeys));

    /// <summary>Gets the permission declarations of the renderer commands; they grant nothing until a capability document names them.</summary>
    public static NeoPluginPermissionCatalog PermissionCatalog { get; } = CreatePermissionCatalog();

    /// <inheritdoc />
    public NeoPluginMetadata Metadata { get; } = CreateMetadata();

    /// <inheritdoc />
    public INeoPluginAdapter CreateAdapter() => new PasskeysAdapter(_passkeys);

    /// <inheritdoc />
    public ValueTask ConfigureAsync(NeoPluginContext context, CancellationToken cancellationToken) { ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }

    /// <inheritdoc />
    public ValueTask ReadyAsync(NeoPluginContext context, CancellationToken cancellationToken) { ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }

    /// <inheritdoc />
    public ValueTask StoppingAsync(NeoPluginContext context, CancellationToken cancellationToken) { ArgumentNullException.ThrowIfNull(context); return ValueTask.CompletedTask; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _passkeys is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(false);
    }

    private static NeoPluginMetadata CreateMetadata() => new(Id, new Version(1, 0, 0), 1, new Version(0, 1, 0),
        commands: Commands.Select(static command =>
        {
            var permission = PermissionCatalog.Permissions.Single(permission => permission.Commands.Contains(command, StringComparer.Ordinal));
            return new NeoPluginCommandDeclaration(command, permission.Id, null, permission.Risk, Audited: true);
        }).ToArray(),
        permissionCatalog: PermissionCatalog, hasStaticJsonMetadata: true);

    // A passkey signs for the relying party of its options, which the platform allows only for an associated domain; creating
    // one also stores it in the password manager of the user.
    private static NeoPluginPermissionCatalog CreatePermissionCatalog() => new(Id, "1.0.0", "0.1.0",
    [
        Permission("passkeys:create", "passkeys.create"),
        Permission("passkeys:get", "passkeys.get"),
    ]);

    private static NeoPermissionDeclaration Permission(string id, string command) => new(id, 1, [command], NeoPermissionRisk.Sensitive, NeoScopeFamily.None)
    {
        MaximumConcurrency = 1,
        DefaultTimeout = PasskeysRendererRegistration.Timeout,
        Redaction = NeoAuditRedaction.Full,
        Documentation = "Official NeoAstra passkeys renderer permission. Registration and grants remain explicit.",
    };

    private sealed class PasskeysAdapter(INeoPasskeys passkeys) : INeoPluginAdapter
    {
        public NeoCapabilityInfo Support => passkeys.Support;

        public ValueTask AttachAsync(NeoApplication application, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(application);
            cancellationToken.ThrowIfCancellationRequested();
            if (!application.Dispatcher.CheckAccess()) throw new InvalidOperationException("The passkeys adapter must attach on the UI dispatcher.");
            if (passkeys is INeoPasskeysDispatcherBinding binding) binding.Bind(application.Dispatcher);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Contains the static registration helpers of the passkeys plugin.</summary>
public static class NeoPasskeysPluginExtensions
{
    /// <summary>Adds the passkeys plugin through an explicit AOT-safe factory and transfers the ownership of the service to the plugin host. This does not register renderer handlers or grants.</summary>
    /// <param name="builder">The plugin builder.</param>
    /// <param name="passkeys">The passkeys service.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static NeoPluginBuilder AddNeoAstraPasskeys(this NeoPluginBuilder builder, INeoPasskeys passkeys)
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentNullException.ThrowIfNull(passkeys);
        return builder.AddNeoAstraPlugin(() => new NeoPasskeysPlugin(passkeys));
    }

    /// <summary>Adds the permission declarations of the passkeys plugin to application capability tooling. This grants no renderer authority.</summary>
    /// <param name="builder">The permission catalog builder.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static NeoPermissionCatalogBuilder AddNeoAstraPasskeysPermissions(this NeoPermissionCatalogBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddPlugin(NeoPasskeysPlugin.PermissionCatalog);
    }

    /// <summary>Adds the passkeys renderer handlers with source-generated JSON metadata. Calling this method grants nothing.</summary>
    /// <param name="builder">The explicit RPC builder.</param>
    /// <param name="passkeys">The passkeys service.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static NeoRpcBuilder AddNeoAstraPasskeysHandlers(this NeoRpcBuilder builder, INeoPasskeys passkeys)
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentNullException.ThrowIfNull(passkeys);
        new PasskeysRendererRegistration(passkeys).Register(builder);
        return builder;
    }
}

internal sealed class PasskeysRendererRegistration(INeoPasskeys passkeys)
{
    // The sheet of the platform waits for the user.
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public void Register(NeoRpcBuilder builder)
    {
        var json = PasskeysRendererJsonContext.Default;
        builder.AddCommand("passkeys.create", (request, context, token) => RunAsync(request, creation: true, token), json.PasskeysRequest, json.PasskeysResult, Options("passkeys:create"));
        builder.AddCommand("passkeys.get", (request, context, token) => RunAsync(request, creation: false, token), json.PasskeysRequest, json.PasskeysResult, Options("passkeys:get"));
    }

    private static NeoRpcCommandOptions Options(string permission) => new() { Permission = permission, Dispatch = NeoRpcDispatchMode.Background, MaximumConcurrency = 1, Timeout = Timeout };

    private async ValueTask<PasskeysResult> RunAsync(PasskeysRequest request, bool creation, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = request.Options.ValueKind == JsonValueKind.Object ? request.Options.GetRawText() : throw new NeoRpcException("passkeys_invalid", "The passkey options are not a JSON object.");
        NeoDesktopResult<string> result;
        try { result = creation ? await passkeys.CreateAsync(options, token).ConfigureAwait(false) : await passkeys.GetAsync(options, token).ConfigureAwait(false); }
        catch (ArgumentException exception) { throw new NeoRpcException("passkeys_invalid", exception.Message); }
        if (result.Value is not { } value) return new(result.Status, null, result.Code);
        using var document = JsonDocument.Parse(value);
        return new(result.Status, document.RootElement.Clone(), result.Code);
    }
}

// The options and the answer go to and from the page as the JSON objects of WebAuthn.
internal sealed record PasskeysRequest(JsonElement Options);
internal sealed record PasskeysResult(NeoDesktopStatus Status, JsonElement? Value, string? Code);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(PasskeysRequest))]
[JsonSerializable(typeof(PasskeysResult))]
internal sealed partial class PasskeysRendererJsonContext : JsonSerializerContext;
