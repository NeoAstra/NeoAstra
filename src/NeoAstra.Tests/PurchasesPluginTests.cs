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
    public void Wire_ReadsTheBasePlansOfAGooglePlaySubscription()
    {
        // One subscription with a monthly and an annual base plan, each with a free trial, and a win-back offer whose
        // eligibility the developer determines: what the Java service answers.
        using var products = JsonDocument.Parse("""
            {"status":"Success","products":[
              {"id":"premium","type":"Subscription","title":"Premium","description":"Every feature","displayPrice":"€ 2,49","price":"2.49","currency":"EUR","period":"P1M",
               "offers":[
                 {"id":"token-monthly","displayPrice":"€ 2,49","period":"P1M","introductory":false,"basePlanId":"monthly"},
                 {"id":"token-monthly-trial","displayPrice":"Free","period":"P1W","introductory":true,"basePlanId":"monthly","offerId":"free-trial-7d"},
                 {"id":"token-annual","displayPrice":"€ 19,99","period":"P1Y","introductory":false,"basePlanId":"annual","tags":[]},
                 {"id":"token-annual-trial","displayPrice":"Free","period":"P1W","introductory":true,"basePlanId":"annual","offerId":"free-trial-7d","tags":["trial"]},
                 {"id":"token-annual-winback","displayPrice":"€ 9,99","period":"P1Y","introductory":true,"basePlanId":"annual","offerId":"winback-50","tags":["winback",7]}]}]}
            """);
        var product = NeoPurchases.Products(products.RootElement).Value!.Single();
        // The product has the price of its first base plan; each plan has its own in its offers.
        Assert.AreEqual("P1M", product.SubscriptionPeriod);
        Assert.AreEqual(5, product.Offers.Count);
        var annual = product.Offers.Single(static offer => offer.BasePlanId == "annual" && offer.OfferId is null);
        Assert.AreEqual(new NeoProductOffer("token-annual", "€ 19,99", "P1Y", false) { BasePlanId = "annual" }, annual);
        // Every offer of a plan has a lower first phase; the offer identifier or the tags tell a trial from a win-back offer.
        Assert.AreEqual(2, product.Offers.Count(static offer => offer.BasePlanId == "annual" && offer.IsIntroductory));
        var trial = product.Offers.Single(static offer => offer.BasePlanId == "annual" && offer.OfferId == "free-trial-7d");
        Assert.AreEqual(new NeoProductOffer("token-annual-trial", "Free", "P1W", true) { BasePlanId = "annual", OfferId = "free-trial-7d", Tags = ["trial"] }, trial);
        Assert.AreEqual(trial, product.Offers.Single(static offer => offer.Tags.Contains("trial")));
        CollectionAssert.AreEqual(new[] { "winback" }, product.Offers.Single(static offer => offer.OfferId == "winback-50").Tags.ToArray());
        Assert.AreNotEqual(trial, trial with { Tags = ["other"] });
        Assert.AreEqual("token-monthly-trial", product.Offers.Single(static offer => offer.BasePlanId == "monthly" && offer.IsIntroductory).Id);

        // The App Store has no base plans: its introductory offer has neither identifier.
        using var apple = JsonDocument.Parse("""
            {"status":"Success","products":[{"id":"premium_annual","type":"Subscription","title":"Premium","description":"","displayPrice":"19,99 €","price":"19.99","currency":"EUR","period":"P1Y",
              "offers":[{"id":"introductory","displayPrice":"0,00 €","period":"P1W","introductory":true}]}]}
            """);
        var yearly = NeoPurchases.Products(apple.RootElement).Value!.Single();
        var introductory = yearly.Offers.Single();
        Assert.IsNull(introductory.BasePlanId);
        Assert.IsNull(introductory.OfferId);
        Assert.AreEqual(0, introductory.Tags.Count);
        Assert.IsNull(yearly.SubscriptionGroupId);
        Assert.IsNull(yearly.SubscriptionGroupLevel);
        Assert.IsNull(product.SubscriptionGroupId);
    }

    [TestMethod]
    public void Wire_ReadsTheSubscriptionGroupsAndTheChangesOfTheAppStore()
    {
        // Its plans are the subscriptions of a group, ranked from 1, the highest level of service.
        using var products = JsonDocument.Parse("""
            {"status":"Success","products":[
              {"id":"premium_monthly","type":"Subscription","title":"Premium","description":"","displayPrice":"2,49 €","price":"2.49","currency":"EUR","period":"P1M","group":"21000001","groupLevel":1},
              {"id":"basic_monthly","type":"Subscription","title":"Basic","description":"","displayPrice":"0,99 €","price":"0.99","currency":"EUR","period":"P1M","group":"21000001","groupLevel":2},
              {"id":"family_monthly","type":"Subscription","title":"Family","description":"","displayPrice":"4,99 €","price":"4.99","currency":"EUR","period":"P1M","group":"21000001","groupLevel":0}]}
            """);
        var plans = NeoPurchases.Products(products.RootElement).Value!;
        Assert.IsTrue(plans.All(static plan => plan.SubscriptionGroupId == "21000001"));
        // Before iOS 17 the App Store answers 0 for a level that it does not know, which is no level.
        Assert.IsNull(plans[2].SubscriptionGroupLevel);
        Assert.AreEqual("premium_monthly", plans.Where(static plan => plan.SubscriptionGroupLevel is not null).MinBy(static plan => plan.SubscriptionGroupLevel)!.Id);

        // After an upgrade the old subscription is replaced; after a downgrade the current one renews as the lower one.
        using var list = JsonDocument.Parse("""
            {"status":"Success","purchases":[
              {"productId":"basic_monthly","store":"AppStore","state":"Replaced","proof":"jws-1","transactionId":"1","originalTransactionId":"1","purchaseDate":1},
              {"productId":"premium_monthly","store":"AppStore","state":"Purchased","proof":"jws-2","transactionId":"2","originalTransactionId":"1","purchaseDate":2,"renewalProductId":"basic_monthly"}]}
            """);
        var purchases = NeoPurchases.PurchaseList(list.RootElement).Value!;
        Assert.AreEqual(NeoPurchaseState.Replaced, purchases[0].State);
        Assert.IsNull(purchases[0].RenewalProductId);
        Assert.AreEqual(NeoPurchaseState.Purchased, purchases[1].State);
        Assert.AreEqual("basic_monthly", purchases[1].RenewalProductId);

        // A replaced subscription is not one that a purchase replaces again.
        Assert.ThrowsExactly<ArgumentException>(() => NeoPurchases.Validate(new("premium_annual", NeoProductType.Subscription), new() { ReplacedPurchase = purchases[0], ReplacementMode = NeoSubscriptionReplacementMode.ChargeFullPrice }));
    }

    [TestMethod]
    public async Task Fake_ReplacesTheSubscriptionThatTheUserOwns()
    {
        var yearly = Monthly with { Id = "premium_annual", SubscriptionPeriod = "P1Y" };
        var store = new NeoFakePurchases { Products = [Monthly, yearly] };
        var request = new NeoProductRequest("premium_monthly", NeoProductType.Subscription);
        var monthly = (await store.PurchaseAsync(request)).Value!;
        // A second purchase of an owned subscription conflicts, unless it replaces the first.
        Assert.AreEqual(NeoDesktopStatus.Conflict, (await store.PurchaseAsync(request)).Status);
        var stranger = monthly with { Proof = "not-held" };
        Assert.AreEqual(NeoDesktopStatus.NotFound, (await store.PurchaseAsync(request, new() { ReplacedPurchase = stranger, ReplacementMode = NeoSubscriptionReplacementMode.WithoutProration })).Status);
        Assert.AreEqual(monthly.Proof, store.Purchases.Single().Proof);

        // Another subscription replaces it with a mode; which modes Google Play takes depends on the kinds of the plans, which it
        // checks itself, so every defined mode passes here.
        var annual = await store.PurchaseAsync(new("premium_annual", NeoProductType.Subscription), new() { ReplacedPurchase = monthly, ReplacementMode = NeoSubscriptionReplacementMode.WithTimeProration });
        Assert.AreEqual(NeoDesktopStatus.Success, annual.Status);
        Assert.AreEqual(annual.Value, store.Purchases.Single());
        var again = new NeoProductRequest("premium_annual", NeoProductType.Subscription);
        var changed = await store.PurchaseAsync(again, new() { ReplacedPurchase = annual.Value, ReplacementMode = NeoSubscriptionReplacementMode.ChargeProratedPrice });
        Assert.AreEqual(NeoDesktopStatus.Success, changed.Status);
        Assert.AreNotEqual(annual.Value!.Proof, changed.Value!.Proof);

        // A replacement needs a mode, a subscription, and a purchased subscription with a proof; a JWS of the App Store is long.
        var owned = changed.Value;
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.PurchaseAsync(again, new() { ReplacedPurchase = owned }));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.PurchaseAsync(new("credits_100", NeoProductType.InApp), new() { ReplacedPurchase = owned, ReplacementMode = NeoSubscriptionReplacementMode.ChargeFullPrice }));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.PurchaseAsync(again, new() { ReplacedPurchase = owned with { Proof = "" }, ReplacementMode = NeoSubscriptionReplacementMode.ChargeFullPrice }));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.PurchaseAsync(again, new() { ReplacedPurchase = owned with { State = NeoPurchaseState.Pending }, ReplacementMode = NeoSubscriptionReplacementMode.ChargeFullPrice }));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.PurchaseAsync(again, new() { ReplacedPurchase = owned, ReplacementMode = (NeoSubscriptionReplacementMode)9 }));
        // A mode without the purchase that it replaces would be dropped without a word.
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.PurchaseAsync(again, new() { ReplacementMode = NeoSubscriptionReplacementMode.ChargeFullPrice }));
        Assert.AreEqual(NeoDesktopStatus.Success, (await store.PurchaseAsync(again, new() { ReplacedPurchase = owned, ReplacementMode = NeoSubscriptionReplacementMode.WithoutProration })).Status);
        var jws = owned with { Proof = new string('j', 8000), Store = NeoPurchaseStore.AppStore };
        Assert.AreEqual(NeoDesktopStatus.NotFound, (await store.PurchaseAsync(again, new() { ReplacedPurchase = jws, ReplacementMode = NeoSubscriptionReplacementMode.WithoutProration })).Status);

        // A deferred replacement issues the new purchase at once, with the old product until the next renewal.
        var current = store.Purchases.Single();
        var downgrade = await store.PurchaseAsync(request, new() { ReplacedPurchase = current, ReplacementMode = NeoSubscriptionReplacementMode.Deferred });
        Assert.AreEqual(NeoDesktopStatus.Success, downgrade.Status);
        Assert.AreEqual("premium_annual", downgrade.Value!.ProductId);
        Assert.AreEqual("premium_monthly", downgrade.Value.RenewalProductId);
        Assert.AreNotEqual(current.Proof, downgrade.Value.Proof);
        Assert.AreEqual(downgrade.Value, store.Purchases.Single());
    }

    [TestMethod]
    public async Task Fake_KeepsTheCurrentBasePlanUntilADeferredReplacementTakesPlace()
    {
        var premium = new NeoProduct("premium", NeoProductType.Subscription, "Premium", "", "€ 2,49", 2.49m, "EUR")
        {
            Offers = [new("token-yearly", "€ 19,99", "P1Y", false) { BasePlanId = "yearly" }, new("token-monthly", "€ 2,49", "P1M", false) { BasePlanId = "monthly" }],
        };
        var basic = new NeoProduct("basic", NeoProductType.Subscription, "Basic", "", "€ 0,99", 0.99m, "EUR") { Offers = [new("token-basic", "€ 0,99", "P1M", false) { BasePlanId = "monthly" }] };
        var store = new NeoFakePurchases { Products = [premium, basic] };
        var yearly = (await store.PurchaseAsync(new("premium", NeoProductType.Subscription), new() { OfferId = "token-yearly" })).Value!;
        var deferred = (await store.PurchaseAsync(new("basic", NeoProductType.Subscription), new() { OfferId = "token-basic", ReplacedPurchase = yearly, ReplacementMode = NeoSubscriptionReplacementMode.Deferred })).Value!;
        Assert.AreEqual("premium", deferred.ProductId);
        Assert.AreEqual("basic", deferred.RenewalProductId);

        // The purchase still holds the yearly plan of premium: the same plan is owned, the monthly one changes the plan.
        Assert.AreEqual(NeoDesktopStatus.Conflict, (await store.PurchaseAsync(new("premium", NeoProductType.Subscription), new() { OfferId = "token-yearly" })).Status);
        var monthly = (await store.PurchaseAsync(new("premium", NeoProductType.Subscription), new() { OfferId = "token-monthly" })).Value!;

        // Within one subscription as well, as Google Play does for installment base plans, a deferred replacement keeps the
        // current base plan until the renewal; the fake does not tell kinds of base plans apart.
        var within = (await store.PurchaseAsync(new("premium", NeoProductType.Subscription), new() { OfferId = "token-yearly", ReplacedPurchase = monthly, ReplacementMode = NeoSubscriptionReplacementMode.Deferred })).Value!;
        Assert.AreEqual("premium", within.ProductId);
        Assert.IsNull(within.RenewalProductId);
        Assert.AreEqual(NeoDesktopStatus.Conflict, (await store.PurchaseAsync(new("premium", NeoProductType.Subscription), new() { OfferId = "token-monthly" })).Status);
        Assert.AreEqual(NeoDesktopStatus.Success, (await store.PurchaseAsync(new("premium", NeoProductType.Subscription), new() { OfferId = "token-yearly" })).Status);
    }

    [TestMethod]
    public void Wire_ReadsADeferredReplacementOfGooglePlay()
    {
        // The new purchase token stays with the old product until the renewal; the answer of the purchase names the new one.
        using var answer = JsonDocument.Parse("""
            {"status":"Success","purchase":{"productId":"premium_annual","store":"GooglePlay","state":"Purchased","proof":"new-token","purchaseDate":1,"finished":false,"renewalProductId":"basic_annual"}}
            """);
        var purchase = NeoPurchases.Purchase(answer.RootElement).Value!;
        Assert.AreEqual("premium_annual", purchase.ProductId);
        Assert.AreEqual("basic_annual", purchase.RenewalProductId);
        Assert.AreEqual("new-token", purchase.Proof);
        Assert.IsFalse(purchase.IsFinished);
    }

    [TestMethod]
    public async Task Renderer_ShowsTheBasePlansAndChangesThePlan()
    {
        var premium = new NeoProduct("premium", NeoProductType.Subscription, "Premium", "Every feature", "€ 2,49", 2.49m, "EUR")
        {
            SubscriptionPeriod = "P1M",
            Offers = [new("token-monthly", "€ 2,49", "P1M", false) { BasePlanId = "monthly" }, new("token-annual-trial", "Free", "P1W", true) { BasePlanId = "annual", OfferId = "free-trial-7d", Tags = ["trial"] }],
        };
        var store = new NeoFakePurchases { Products = [premium] };
        await using var session = Session(store, ["purchases:read", "purchases:buy"], out var frames);
        var offers = (await InvokeAsync(session, frames, "1", "purchases.products", """{"products":[{"id":"premium","type":"Subscription"}]}""")).GetProperty("value")[0].GetProperty("offers");
        Assert.AreEqual("monthly", offers[0].GetProperty("basePlanId").GetString());
        Assert.AreEqual(JsonValueKind.Null, offers[0].GetProperty("offerId").ValueKind);
        Assert.AreEqual("annual", offers[1].GetProperty("basePlanId").GetString());
        Assert.AreEqual("free-trial-7d", offers[1].GetProperty("offerId").GetString());
        Assert.AreEqual("trial", offers[1].GetProperty("tags")[0].GetString());

        // A subscription of several base plans is not bought without the offer of the plan that the user chose.
        var unnamed = await InvokeAsync(session, frames, "2", "purchases.purchase", """{"id":"premium","type":"Subscription"}""");
        Assert.AreEqual("NotFound", unnamed.GetProperty("status").GetString());
        Assert.AreEqual(NeoPurchases.OfferRequiredCode, unnamed.GetProperty("code").GetString());

        // The same base plan again is owned; another base plan of the same subscription changes the plan, as Google Play does with
        // the default replacement mode of the Play Console, and a replacement with a mode overrides that default.
        var monthly = (await InvokeAsync(session, frames, "3", "purchases.purchase", """{"id":"premium","type":"Subscription","offerId":"token-monthly"}""")).GetProperty("value");
        Assert.AreEqual("Conflict", (await InvokeAsync(session, frames, "4", "purchases.purchase", """{"id":"premium","type":"Subscription","offerId":"token-monthly"}""")).GetProperty("status").GetString());
        var annual = (await InvokeAsync(session, frames, "5", "purchases.purchase", """{"id":"premium","type":"Subscription","offerId":"token-annual-trial"}""")).GetProperty("value");
        Assert.AreEqual(annual.GetProperty("proof").GetString(), store.Purchases.Single().Proof);
        Assert.AreEqual("purchases_invalid", await ErrorAsync(session, frames, "6", "purchases.purchase", $$$"""{"id":"premium","type":"Subscription","offerId":"token-monthly","replacedPurchase":{{{annual.GetRawText()}}}}"""));
        var changed = await InvokeAsync(session, frames, "7", "purchases.purchase", $$$"""{"id":"premium","type":"Subscription","offerId":"token-monthly","replacedPurchase":{{{annual.GetRawText()}}},"replacementMode":"WithoutProration"}""");
        Assert.AreEqual("Success", changed.GetProperty("status").GetString());
        Assert.AreEqual(changed.GetProperty("value").GetProperty("proof").GetString(), store.Purchases.Single().Proof);
        Assert.AreEqual("NotFound", (await InvokeAsync(session, frames, "8", "purchases.purchase", $$$"""{"id":"premium","type":"Subscription","offerId":"token-annual-trial","replacedPurchase":{{{monthly.GetRawText()}}},"replacementMode":"ChargeFullPrice"}""")).GetProperty("status").GetString());
        Assert.AreEqual("purchases_invalid", await ErrorAsync(session, frames, "9", "purchases.purchase", $$$"""{"id":"premium","type":"InApp","replacedPurchase":{{{monthly.GetRawText()}}},"replacementMode":"ChargeFullPrice"}"""));
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
        // Google Play replaces only a purchase of its own, which the adapter checks before it reaches the Java service.
        var appStore = new NeoPurchase("premium_monthly", NeoPurchaseStore.AppStore, NeoPurchaseState.Purchased, "jws", DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<ArgumentException>(async () => await new AndroidPurchases(null).PurchaseAsync(new("premium_annual", NeoProductType.Subscription), new() { ReplacedPurchase = appStore, ReplacementMode = NeoSubscriptionReplacementMode.ChargeFullPrice }));
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
