// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.Json.Serialization;
using NeoAstra.Desktop;
using NeoAstra.Rpc;

namespace NeoAstra.Purchases;

/// <summary>The purchases plugin of NeoAstra: it owns the purchases service and declares its renderer commands and events, which it grants to no one.</summary>
public sealed class NeoPurchasesPlugin : INeoAstraPlugin
{
    /// <summary>The stable plugin ID.</summary>
    public const string Id = "neoastra.purchases";

    internal static readonly string[] Commands = ["purchases.products", "purchases.list", "purchases.purchase", "purchases.restore", "purchases.finish"];
    internal static readonly string[] Events = ["purchases.updated"];

    private readonly INeoPurchases _purchases;
    private int _disposed;

    /// <summary>Initializes the plugin, which takes ownership of the purchases service.</summary>
    /// <param name="purchases">The purchases service.</param>
    /// <exception cref="ArgumentNullException"><paramref name="purchases"/> is <see langword="null"/>.</exception>
    public NeoPurchasesPlugin(INeoPurchases purchases) => _purchases = purchases ?? throw new ArgumentNullException(nameof(purchases));

    /// <summary>Gets the permission declarations of the renderer commands and events; they grant nothing until a capability document names them.</summary>
    public static NeoPluginPermissionCatalog PermissionCatalog { get; } = CreatePermissionCatalog();

    /// <inheritdoc />
    public NeoPluginMetadata Metadata { get; } = CreateMetadata();

    /// <inheritdoc />
    public INeoPluginAdapter CreateAdapter() => new PurchasesAdapter(_purchases);

    /// <inheritdoc />
    public ValueTask ConfigureAsync(NeoPluginContext context, CancellationToken cancellationToken) { ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }

    /// <inheritdoc />
    public ValueTask ReadyAsync(NeoPluginContext context, CancellationToken cancellationToken) { ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }

    /// <inheritdoc />
    public ValueTask StoppingAsync(NeoPluginContext context, CancellationToken cancellationToken) { ArgumentNullException.ThrowIfNull(context); return ValueTask.CompletedTask; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _purchases is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(false);
    }

    private static NeoPermissionDeclaration Find(string operation) => PermissionCatalog.Permissions.Single(permission => permission.Commands.Contains(operation, StringComparer.Ordinal));

    private static NeoPluginMetadata CreateMetadata() => new(Id, new Version(1, 0, 0), 1, new Version(0, 1, 0),
        commands: Commands.Select(static command => { var permission = Find(command); return new NeoPluginCommandDeclaration(command, permission.Id, null, permission.Risk, Audited: true); }).ToArray(),
        events: Events.Select(static name => { var permission = Find(name); return new NeoPluginEventDeclaration(name, permission.Id, permission.Risk, Audited: true); }).ToArray(),
        permissionCatalog: PermissionCatalog, hasStaticJsonMetadata: true);

    // Reading tells what the user bought; buying shows the sheet of the store, which charges the user only when the user
    // confirms it; finishing tells the store that the application delivered, and consumes a consumable for good.
    private static NeoPluginPermissionCatalog CreatePermissionCatalog() => new(Id, "1.0.0", "0.1.0",
    [
        Permission("purchases:read", ["purchases.products", "purchases.list", "purchases.updated"], NeoPermissionRisk.Sensitive, TimeSpan.FromMinutes(1)),
        Permission("purchases:buy", ["purchases.purchase", "purchases.restore"], NeoPermissionRisk.Sensitive, PurchasesRendererRegistration.SheetTimeout),
        Permission("purchases:finish", ["purchases.finish"], NeoPermissionRisk.High, TimeSpan.FromMinutes(1)),
    ]);

    private static NeoPermissionDeclaration Permission(string id, string[] operations, NeoPermissionRisk risk, TimeSpan timeout) => new(id, 1, operations, risk, NeoScopeFamily.None)
    {
        MaximumConcurrency = 1,
        DefaultTimeout = timeout,
        Redaction = NeoAuditRedaction.Full,
        Documentation = "Official NeoAstra purchases renderer permission. Registration and grants remain explicit.",
    };

    private sealed class PurchasesAdapter(INeoPurchases purchases) : INeoPluginAdapter
    {
        public NeoCapabilityInfo Support => purchases.Support;

        public ValueTask AttachAsync(NeoApplication application, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(application);
            cancellationToken.ThrowIfCancellationRequested();
            if (!application.Dispatcher.CheckAccess()) throw new InvalidOperationException("The purchases adapter must attach on the UI dispatcher.");
            if (purchases is INeoPurchasesDispatcherBinding binding) binding.Bind(application.Dispatcher);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Contains the static registration helpers of the purchases plugin.</summary>
public static class NeoPurchasesPluginExtensions
{
    /// <summary>Adds the purchases plugin through an explicit AOT-safe factory and transfers the ownership of the service to the plugin host. This does not register renderer handlers or grants.</summary>
    /// <param name="builder">The plugin builder.</param>
    /// <param name="purchases">The purchases service.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static NeoPluginBuilder AddNeoAstraPurchases(this NeoPluginBuilder builder, INeoPurchases purchases)
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentNullException.ThrowIfNull(purchases);
        return builder.AddNeoAstraPlugin(() => new NeoPurchasesPlugin(purchases));
    }

    /// <summary>Adds the permission declarations of the purchases plugin to application capability tooling. This grants no renderer authority.</summary>
    /// <param name="builder">The permission catalog builder.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static NeoPermissionCatalogBuilder AddNeoAstraPurchasesPermissions(this NeoPermissionCatalogBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddPlugin(NeoPurchasesPlugin.PermissionCatalog);
    }

    /// <summary>Adds the purchases renderer handlers and events with source-generated JSON metadata. Calling this method grants nothing.</summary>
    /// <param name="builder">The explicit RPC builder.</param>
    /// <param name="purchases">The purchases service.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static NeoRpcBuilder AddNeoAstraPurchasesHandlers(this NeoRpcBuilder builder, INeoPurchases purchases)
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentNullException.ThrowIfNull(purchases);
        new PurchasesRendererRegistration(purchases).Register(builder);
        return builder;
    }
}

internal sealed class PurchasesRendererRegistration(INeoPurchases purchases)
{
    // The purchase sheet of the store waits for the user.
    internal static readonly TimeSpan SheetTimeout = TimeSpan.FromMinutes(5);

    public void Register(NeoRpcBuilder builder)
    {
        var json = PurchasesRendererJsonContext.Default;
        builder.AddCommand("purchases.products", ProductsAsync, json.PurchasesProductsRequest, json.PurchasesProductsResult, Options("purchases:read", TimeSpan.FromMinutes(1)));
        builder.AddCommand("purchases.list", ListAsync, json.PurchasesEmptyRequest, json.PurchasesListResult, Options("purchases:read", TimeSpan.FromMinutes(1)));
        builder.AddCommand("purchases.purchase", PurchaseAsync, json.PurchasesPurchaseRequest, json.PurchasesPurchaseResult, Options("purchases:buy", SheetTimeout));
        builder.AddCommand("purchases.restore", RestoreAsync, json.PurchasesEmptyRequest, json.PurchasesListResult, Options("purchases:buy", SheetTimeout));
        builder.AddCommand("purchases.finish", FinishAsync, json.PurchasesFinishRequest, json.PurchasesStatusResult, Options("purchases:finish", TimeSpan.FromMinutes(1)));
        var updated = builder.AddEvent("purchases.updated", json.NeoPurchase, new() { Permission = "purchases:read" });
        purchases.PurchaseUpdated += (_, purchase) => _ = PublishAsync(updated, purchase);
    }

    private static NeoRpcCommandOptions Options(string permission, TimeSpan timeout) => new() { Permission = permission, Dispatch = NeoRpcDispatchMode.Background, MaximumConcurrency = 1, Timeout = timeout };

    // A page that is gone, or a host that is disposed, takes no event; the store goes on.
    private static async Task PublishAsync(NeoRpcEvent<NeoPurchase> handle, NeoPurchase value)
    {
        try { await handle.PublishAsync(value).ConfigureAwait(false); }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException) { }
    }

    private async ValueTask<PurchasesProductsResult> ProductsAsync(PurchasesProductsRequest request, NeoRpcContext context, CancellationToken token)
    {
        var result = await Guard(() => purchases.GetProductsAsync(request.Products ?? [], token)).ConfigureAwait(false);
        return new(result.Status, result.Value, result.Code);
    }

    private async ValueTask<PurchasesListResult> ListAsync(PurchasesEmptyRequest request, NeoRpcContext context, CancellationToken token)
    {
        var result = await purchases.GetPurchasesAsync(token).ConfigureAwait(false);
        return new(result.Status, result.Value, result.Code);
    }

    private async ValueTask<PurchasesListResult> RestoreAsync(PurchasesEmptyRequest request, NeoRpcContext context, CancellationToken token)
    {
        var result = await purchases.RestoreAsync(token).ConfigureAwait(false);
        return new(result.Status, result.Value, result.Code);
    }

    private async ValueTask<PurchasesPurchaseResult> PurchaseAsync(PurchasesPurchaseRequest request, NeoRpcContext context, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await Guard(() => purchases.PurchaseAsync(new(request.Id, request.Type), new NeoPurchaseOptions { OfferId = request.OfferId, AccountToken = request.AccountToken }, token)).ConfigureAwait(false);
        return new(result.Status, result.Value, result.Code);
    }

    private async ValueTask<PurchasesStatusResult> FinishAsync(PurchasesFinishRequest request, NeoRpcContext context, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Purchase is null || string.IsNullOrEmpty(request.Purchase.Proof) || string.IsNullOrEmpty(request.Purchase.ProductId)) throw new NeoRpcException("purchases_invalid", "The purchase to finish is missing.");
        return new(await purchases.FinishAsync(request.Purchase, request.Consume, token).ConfigureAwait(false));
    }

    private static async ValueTask<T> Guard<T>(Func<ValueTask<T>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (ArgumentException exception) { throw new NeoRpcException("purchases_invalid", exception.Message); }
    }
}

internal sealed record PurchasesEmptyRequest;
internal sealed record PurchasesProductsRequest(IReadOnlyList<NeoProductRequest>? Products);
internal sealed record PurchasesProductsResult(NeoDesktopStatus Status, IReadOnlyList<NeoProduct>? Value, string? Code);
internal sealed record PurchasesPurchaseRequest(string Id, NeoProductType Type, string? OfferId = null, Guid? AccountToken = null);
internal sealed record PurchasesPurchaseResult(NeoDesktopStatus Status, NeoPurchase? Value, string? Code);
internal sealed record PurchasesListResult(NeoDesktopStatus Status, IReadOnlyList<NeoPurchase>? Value, string? Code);
internal sealed record PurchasesFinishRequest(NeoPurchase? Purchase, bool Consume = false);
internal sealed record PurchasesStatusResult(NeoDesktopStatus Status);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(PurchasesEmptyRequest))]
[JsonSerializable(typeof(PurchasesProductsRequest))]
[JsonSerializable(typeof(PurchasesProductsResult))]
[JsonSerializable(typeof(PurchasesPurchaseRequest))]
[JsonSerializable(typeof(PurchasesPurchaseResult))]
[JsonSerializable(typeof(PurchasesListResult))]
[JsonSerializable(typeof(PurchasesFinishRequest))]
[JsonSerializable(typeof(PurchasesStatusResult))]
[JsonSerializable(typeof(NeoPurchase))]
internal sealed partial class PurchasesRendererJsonContext : JsonSerializerContext;
