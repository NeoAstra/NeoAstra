// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Text.Json;
using NeoAstra.Desktop;
using NeoAstra.Purchases;
using NeoAstra.Rpc;

namespace NeoAstra.Tests;

// The purchases plugin reaches StoreKit 2 and the Play Billing Library only on a device; its contract, the JSON that both
// platforms answer, its permissions, and its renderer commands and events are checked on every host.
[TestClass]
public sealed class PurchasesPluginTests
{
    private static readonly NeoProduct Credits = new("credits_100", NeoProductType.InApp, "100 credits", "Credits for actions", "€ 1,99", 1.99m, "EUR");
    private static readonly NeoProduct Monthly = new("premium_monthly", NeoProductType.Subscription, "Premium", "Every feature", "€ 2,49", 2.49m, "EUR") { SubscriptionPeriod = "P1M" };

    [TestMethod]
    public async Task Metadata_MatchesTheEmbeddedManifestAndThePermissionPolicy()
    {
        await using var plugin = new NeoPurchasesPlugin(new NeoFakePurchases());
        var metadata = plugin.Metadata;
        using var stream = typeof(NeoPurchasesPlugin).Assembly.GetManifestResourceStream("NeoAstra.Purchases.neoastra.plugin.json");
        Assert.IsNotNull(stream);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        Assert.AreEqual(NeoPurchasesPlugin.Id, root.GetProperty("id").GetString());
        Assert.AreEqual("none-by-default", root.GetProperty("rendererAuthority").GetString());
        var commands = root.GetProperty("commands").EnumerateArray().ToDictionary(static item => item.GetProperty("name").GetString()!, static item => item.GetProperty("permission").GetString()!, StringComparer.Ordinal);
        var events = root.GetProperty("events").EnumerateArray().ToDictionary(static item => item.GetProperty("name").GetString()!, static item => item.GetProperty("permission").GetString()!, StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(metadata.Commands.Select(static item => item.Name).ToArray(), commands.Keys.ToArray());
        CollectionAssert.AreEquivalent(metadata.Events.Select(static item => item.Name).ToArray(), events.Keys.ToArray());
        Assert.IsTrue(metadata.Commands.All(item => commands[item.Name] == item.Permission));
        Assert.IsTrue(metadata.Events.All(item => events[item.Name] == item.Permission));
        var permissions = root.GetProperty("permissions").EnumerateArray().ToDictionary(static item => item.GetProperty("id").GetString()!, static item => (Operations: item.GetProperty("operations").EnumerateArray().Select(static value => value.GetString()!).ToArray(), Risk: item.GetProperty("risk").GetString()), StringComparer.Ordinal);
        foreach (var permission in NeoPurchasesPlugin.PermissionCatalog.Permissions)
        {
            CollectionAssert.AreEquivalent(permission.Commands.ToArray(), permissions[permission.Id].Operations);
            Assert.AreEqual(permission.Risk.ToString().ToLowerInvariant(), permissions[permission.Id].Risk);
        }
        // Finishing consumes for good, so it is granted on its own.
        Assert.AreEqual(NeoPermissionRisk.High, NeoPurchasesPlugin.PermissionCatalog.Permissions.Single(static item => item.Id == "purchases:finish").Risk);

        var builder = new NeoRpcBuilder();
        builder.AddNeoAstraPurchasesHandlers(new NeoFakePurchases());
        await using var rpc = builder.Build();
        foreach (var command in metadata.Commands)
        {
            Assert.IsTrue(rpc.TryGetCommand(command.Name, out var descriptor));
            Assert.IsNotNull(descriptor);
            var permission = NeoPurchasesPlugin.PermissionCatalog.Permissions.Single(item => item.Id == command.Permission);
            Assert.AreEqual(command.Permission, descriptor.Options.Permission);
            Assert.IsTrue(descriptor.Options.Timeout <= permission.DefaultTimeout);
            Assert.IsTrue(descriptor.Options.MaximumConcurrency <= permission.MaximumConcurrency);
        }
        Assert.IsTrue(metadata.Events.All(item => rpc.TryGetEvent(item.Name, out var descriptor) && descriptor!.Options.Permission == item.Permission));
    }

    [TestMethod]
    public void Wire_ReadsWhatTheStoresAnswer()
    {
        using var products = JsonDocument.Parse("""
            {"status":"Success","products":[
              {"id":"premium_monthly","type":"Subscription","title":"Premium","description":"Every feature","displayPrice":"€ 2,49","price":"2.49","currency":"EUR","period":"P1M",
               "offers":[{"id":"token-1","displayPrice":"€ 0,00","period":"P1W","introductory":true}]}]}
            """);
        var product = NeoPurchases.Products(products.RootElement).Value!.Single();
        Assert.AreEqual(NeoProductType.Subscription, product.Type);
        Assert.AreEqual(2.49m, product.Price);
        Assert.AreEqual("P1M", product.SubscriptionPeriod);
        Assert.AreEqual(new NeoProductOffer("token-1", "€ 0,00", "P1W", true), product.Offers.Single());

        var account = Guid.NewGuid();
        using var purchase = JsonDocument.Parse($$$"""
            {"status":"Success","purchase":{"productId":"premium_monthly","store":"AppStore","state":"Purchased","proof":"eyJhbGciOi.jws.sig","transactionId":"2000000123",
             "originalTransactionId":"2000000100","purchaseDate":1760000000000,"expirationDate":1762592000000,"quantity":1,"accountToken":"{{{account}}}","finished":false}}
            """);
        var bought = NeoPurchases.Purchase(purchase.RootElement).Value!;
        Assert.AreEqual(NeoPurchaseStore.AppStore, bought.Store);
        Assert.AreEqual("eyJhbGciOi.jws.sig", bought.Proof);
        Assert.AreEqual("2000000100", bought.OriginalTransactionId);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1762592000000), bought.ExpirationDate);
        Assert.AreEqual(account, bought.AccountToken);
        Assert.IsFalse(bought.IsFinished);

        using var list = JsonDocument.Parse("""{"status":"Success","purchases":[{"productId":"credits_100","store":"GooglePlay","state":"Pending","proof":"token","purchaseDate":1,"finished":true,"signedData":"{}","signature":"c2ln"}]}""");
        var held = NeoPurchases.PurchaseList(list.RootElement).Value!.Single();
        Assert.AreEqual(NeoPurchaseState.Pending, held.State);
        Assert.AreEqual("c2ln", held.Signature);
        Assert.IsTrue(held.IsFinished);

        using var unavailable = JsonDocument.Parse("""{"status":"Unsupported","code":"not-configured"}""");
        var failure = NeoPurchases.Products(unavailable.RootElement);
        Assert.AreEqual(NeoDesktopStatus.Unsupported, failure.Status);
        Assert.AreEqual(NeoPurchases.NotConfiguredCode, failure.Code);
        using var malformed = JsonDocument.Parse("{}");
        Assert.AreEqual(NeoDesktopStatus.Failed, NeoPurchases.PurchaseList(malformed.RootElement).Status);
    }

    [TestMethod]
    public async Task Renderer_SellsAndFinishesWithTheGrantedPermissions()
    {
        var store = new NeoFakePurchases { Products = [Credits, Monthly] };
        await using var session = Session(store, ["purchases:read", "purchases:buy", "purchases:finish"], out var frames);
        var products = await InvokeAsync(session, frames, "1", "purchases.products", """{"products":[{"id":"credits_100","type":"InApp"},{"id":"premium_monthly","type":"InApp"},{"id":"unknown","type":"Subscription"}]}""");
        // A product of another kind than the one asked for is not the product asked for.
        Assert.AreEqual("credits_100", products.GetProperty("value").EnumerateArray().Single().GetProperty("id").GetString());

        await session.ReceiveAsync("""{"neoastra":1,"kind":"subscribe","id":"s1","event":"purchases.updated"}""");
        var account = Guid.NewGuid();
        var bought = await InvokeAsync(session, frames, "2", "purchases.purchase", $$"""{"id":"credits_100","type":"InApp","accountToken":"{{account}}"}""");
        Assert.AreEqual("Success", bought.GetProperty("status").GetString());
        var purchase = bought.GetProperty("value");
        Assert.AreEqual(account.ToString(), purchase.GetProperty("accountToken").GetString());
        Assert.AreEqual("Conflict", (await InvokeAsync(session, frames, "3", "purchases.purchase", """{"id":"credits_100","type":"InApp"}""")).GetProperty("status").GetString());
        Assert.AreEqual("NotFound", (await InvokeAsync(session, frames, "4", "purchases.purchase", """{"id":"unknown","type":"InApp"}""")).GetProperty("status").GetString());
        Assert.AreEqual(1, (await InvokeAsync(session, frames, "5", "purchases.list", "{}")).GetProperty("value").GetArrayLength());

        // The page gives back the purchase that it received; a consumed one is gone and can be bought again.
        Assert.AreEqual("Success", (await InvokeAsync(session, frames, "6", "purchases.finish", $$$"""{"purchase":{{{purchase.GetRawText()}}},"consume":true}""")).GetProperty("status").GetString());
        Assert.AreEqual(0, (await InvokeAsync(session, frames, "7", "purchases.list", "{}")).GetProperty("value").GetArrayLength());
        Assert.AreEqual("purchases_invalid", (await ErrorAsync(session, frames, "8", "purchases.purchase", """{"id":"","type":"InApp"}""")));

        store.Update(new NeoPurchase("premium_monthly", NeoPurchaseStore.GooglePlay, NeoPurchaseState.Purchased, "renewed", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => Events(frames).Length == 1);
        Assert.AreEqual("renewed", Events(frames)[0].GetProperty("value").GetProperty("proof").GetString());
    }

    [TestMethod]
    public async Task Renderer_DeniesWhatTheCapabilityDocumentDoesNotGrant()
    {
        var store = new NeoFakePurchases { Products = [Credits] };
        await using var session = Session(store, ["purchases:read"], out var frames);
        Assert.AreEqual("permission_denied", await ErrorAsync(session, frames, "1", "purchases.purchase", """{"id":"credits_100","type":"InApp"}"""));
        Assert.AreEqual("permission_denied", await ErrorAsync(session, frames, "2", "purchases.finish", """{"purchase":{"productId":"credits_100","store":"AppStore","state":"Purchased","proof":"p","purchaseDate":"2026-01-01T00:00:00Z"},"consume":true}"""));
        Assert.AreEqual(0, store.Purchases.Count);
    }

    [TestMethod]
    public async Task System_AnswersUnsupportedOnADesktopAndValidatesTheRequests()
    {
        var store = NeoPurchases.CreateSystem();
        Assert.AreEqual(NeoSupportLevel.None, store.Support.SupportLevel);
        Assert.AreEqual(NeoDesktopStatus.Unsupported, (await store.GetProductsAsync([new("credits_100", NeoProductType.InApp)])).Status);
        Assert.AreEqual(NeoDesktopStatus.Unsupported, (await store.PurchaseAsync(new("credits_100", NeoProductType.InApp))).Status);
        Assert.AreEqual(NeoDesktopStatus.Unsupported, (await store.GetPurchasesAsync()).Status);
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.GetProductsAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.GetProductsAsync([.. Enumerable.Range(0, 101).Select(static index => new NeoProductRequest("p" + index, NeoProductType.InApp))]));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.PurchaseAsync(new("credits_100", (NeoProductType)9)));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.PurchaseAsync(new("credits_100", NeoProductType.Subscription), new() { OfferId = "" }));
        Assert.AreEqual(NeoSupportLevel.Native, new AndroidPurchases(null).Support.SupportLevel);
        // A desktop backend has no platform services to reach the Java service with.
        DesktopPlatformCall.AssertNotSupported(() => AndroidPurchases.Call("list", null));
    }

    private static NeoRpcSession Session(INeoPurchases store, string[] permissions, out ConcurrentQueue<string> frames)
    {
        var manifest = NeoCapabilityManifest.Resolve(System.Text.Encoding.UTF8.GetBytes($$"""
            {"$schema":"neoastra-capabilities-v1.schema.json","version":1,"capabilities":[{"id":"main","views":["main"],"permissions":[{{string.Join(",", permissions.Select(static item => $"\"{item}\""))}}]}]}
            """), new NeoPermissionCatalogBuilder().AddNeoAstraPurchasesPermissions().Build(), new() { Platform = NeoCapabilityPlatform.Android, Release = true, Profile = NeoSecurityProfile.ProductionLocalApp });
        var builder = new NeoRpcBuilder(new NeoRpcOptions { CapabilityManifest = manifest, SecurityProfile = manifest.Profile, AuthorizationService = new NeoCapabilityAuthorizationService(manifest) });
        builder.AddNeoAstraPurchasesHandlers(store);
        var rpc = builder.Build();
        var queue = new ConcurrentQueue<string>();
        frames = queue;
        return rpc.OpenSession(new NeoRpcSessionIdentity("main", "document-1") { Platform = NeoCapabilityPlatform.Android, IsMainFrame = true }, (json, _) => { queue.Enqueue(json); return ValueTask.CompletedTask; });
    }

    private static JsonElement[] Events(ConcurrentQueue<string> frames)
        => frames.Select(static json => JsonDocument.Parse(json).RootElement.Clone()).Where(static frame => frame.GetProperty("kind").GetString() == "event").ToArray();

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var index = 0; index < 400 && !predicate(); index++) await Task.Delay(5);
        Assert.IsTrue(predicate());
    }

    private static async Task<JsonElement> InvokeAsync(NeoRpcSession session, ConcurrentQueue<string> frames, string id, string command, string arguments)
    {
        var frame = await ResponseAsync(session, frames, id, command, arguments);
        Assert.IsTrue(frame.TryGetProperty("value", out var value), frame.ToString());
        return value;
    }

    private static async Task<string> ErrorAsync(NeoRpcSession session, ConcurrentQueue<string> frames, string id, string command, string arguments)
    {
        var frame = await ResponseAsync(session, frames, id, command, arguments);
        Assert.IsTrue(frame.TryGetProperty("error", out var error), frame.ToString());
        return error.GetProperty("code").GetString()!;
    }

    private static async Task<JsonElement> ResponseAsync(NeoRpcSession session, ConcurrentQueue<string> frames, string id, string command, string arguments)
    {
        await session.ReceiveAsync($$$"""{"neoastra":1,"kind":"invoke","id":"{{{id}}}","command":"{{{command}}}","args":{{{arguments}}}}""");
        JsonElement? found = null;
        await WaitUntilAsync(() =>
        {
            foreach (var json in frames)
            {
                var frame = JsonDocument.Parse(json).RootElement.Clone();
                if (frame.GetProperty("kind").GetString() == "result" && frame.TryGetProperty("id", out var value) && value.GetString() == id) { found = frame; return true; }
            }
            return false;
        });
        return found!.Value;
    }
}
