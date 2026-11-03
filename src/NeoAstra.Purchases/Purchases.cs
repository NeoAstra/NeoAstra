// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Globalization;
using System.Text.Json;
using NeoAstra.Desktop;

namespace NeoAstra.Purchases;

/// <summary>Identifies the store of a purchase.</summary>
public enum NeoPurchaseStore
{
    /// <summary>The App Store, through StoreKit 2 on iOS.</summary>
    AppStore,
    /// <summary>Google Play, through the Play Billing Library on Android.</summary>
    GooglePlay,
}

/// <summary>Identifies what kind of product a store sells, which a request names because Google Play asks for it.</summary>
public enum NeoProductType
{
    /// <summary>A product bought once: consumable, such as credits, or not, such as an unlock.</summary>
    InApp,
    /// <summary>A subscription that renews itself.</summary>
    Subscription,
}

/// <summary>Contains a product to load from the store.</summary>
/// <param name="Id">The product identifier of the store: the product ID of App Store Connect, the product ID of the Play Console.</param>
/// <param name="Type">The kind of product.</param>
public sealed record NeoProductRequest(string Id, NeoProductType Type);

/// <summary>Contains an offer of a product, such as a base plan or an introductory trial of a subscription.</summary>
/// <param name="Id">The identifier of the offer to purchase it with; on Google Play the offer token of a base plan or offer.</param>
/// <param name="DisplayPrice">The price as the store shows it.</param>
/// <param name="Period">The billing period as an ISO 8601 duration, such as <c>P1M</c>, when the offer has one.</param>
/// <param name="IsIntroductory">Whether the offer is an introductory one, such as a free trial, for which the user is eligible.</param>
public sealed record NeoProductOffer(string Id, string DisplayPrice, string? Period, bool IsIntroductory);

/// <summary>Contains a product as the store sells it to the user, with the prices of the storefront of the user.</summary>
/// <param name="Id">The product identifier.</param>
/// <param name="Type">The kind of product.</param>
/// <param name="Title">The localized name.</param>
/// <param name="Description">The localized description.</param>
/// <param name="DisplayPrice">The price as the store shows it, with its currency.</param>
/// <param name="Price">The price as a number in <paramref name="CurrencyCode"/>.</param>
/// <param name="CurrencyCode">The ISO 4217 currency code of the price.</param>
public sealed record NeoProduct(string Id, NeoProductType Type, string Title, string Description, string DisplayPrice, decimal Price, string CurrencyCode)
{
    /// <summary>Gets the billing period of a subscription as an ISO 8601 duration, such as <c>P1M</c>.</summary>
    public string? SubscriptionPeriod { get; init; }

    /// <summary>Gets the offers of the product; on Google Play a subscription is purchased with one of them.</summary>
    public IReadOnlyList<NeoProductOffer> Offers { get; init; } = [];
}

/// <summary>Describes where a purchase stands.</summary>
public enum NeoPurchaseState
{
    /// <summary>The store charged the user; the application delivers the product.</summary>
    Purchased,
    /// <summary>The store waits for the payment, such as an approval of a parent or a cash payment; the application delivers nothing yet.</summary>
    Pending,
    /// <summary>The store refunded or revoked the purchase; the application withdraws the product.</summary>
    Revoked,
}

/// <summary>Contains a purchase, which the server of the application verifies with the store before it delivers the product.</summary>
/// <param name="ProductId">The product identifier.</param>
/// <param name="Store">The store of the purchase.</param>
/// <param name="State">Where the purchase stands.</param>
/// <param name="Proof">
/// What the server verifies: the JWS representation of the signed transaction of the App Store, which carries every
/// field of the transaction, or the purchase token of Google Play, which the Play Developer API answers about.
/// </param>
/// <param name="PurchaseDate">When the user purchased.</param>
public sealed record NeoPurchase(string ProductId, NeoPurchaseStore Store, NeoPurchaseState State, string Proof, DateTimeOffset PurchaseDate)
{
    /// <summary>Gets the identifier of the transaction, or of the order on Google Play, when the store gives one.</summary>
    public string? TransactionId { get; init; }

    /// <summary>Gets the identifier of the first transaction of a subscription, which its renewals share.</summary>
    public string? OriginalTransactionId { get; init; }

    /// <summary>Gets when a subscription expires, when the store tells it the device.</summary>
    public DateTimeOffset? ExpirationDate { get; init; }

    /// <summary>Gets the number of items purchased.</summary>
    public int Quantity { get; init; } = 1;

    /// <summary>Gets the account of the application that the purchase belongs to, as given to <see cref="NeoPurchaseOptions.AccountToken"/>.</summary>
    public Guid? AccountToken { get; init; }

    /// <summary>Gets whether the application finished the purchase: a finished transaction on the App Store, an acknowledged purchase on Google Play.</summary>
    public bool IsFinished { get; init; }

    /// <summary>Gets the signed purchase data of Google Play, a JSON document, which <see cref="Signature"/> signs.</summary>
    public string? SignedData { get; init; }

    /// <summary>Gets the signature of <see cref="SignedData"/> with the key of the application in the Play Console.</summary>
    public string? Signature { get; init; }
}

/// <summary>Configures one purchase.</summary>
public sealed class NeoPurchaseOptions
{
    /// <summary>Gets or sets the offer to purchase with, from <see cref="NeoProduct.Offers"/>; Google Play needs one for a subscription, and takes the first offer without it.</summary>
    public string? OfferId { get; set; }

    /// <summary>Gets or sets the account of the application that the purchase belongs to, which the store keeps with it and gives back to the server.</summary>
    public Guid? AccountToken { get; set; }
}

/// <summary>Sells the products of the application through the store of the platform.</summary>
/// <remarks>
/// <para>
/// A purchase reaches the application with a proof that its server verifies with the store before it delivers the product,
/// and the application finishes the purchase when it has delivered it: a consumable is consumed then, so that the user can
/// buy it again. A store retries what the application did not finish, and Google Play refunds a purchase that is not
/// acknowledged within three days. <see cref="PurchaseUpdated"/> reports the purchases that the store completes outside a
/// request of the application, such as a renewal, a pending purchase that the store approves, or a purchase on another device.
/// </para>
/// <para>
/// The products are configured in App Store Connect and in the Play Console. The App Store sells them to a build of the
/// application that the device runs, signed for development, with a sandbox account; Google Play sells them to an
/// application whose package was uploaded to a track of the Play Console once, with a license tester account.
/// </para>
/// </remarks>
public interface INeoPurchases
{
    /// <summary>Gets truthful platform support.</summary>
    NeoCapabilityInfo Support { get; }

    /// <summary>Occurs on the UI dispatcher when the store completes, renews, or revokes a purchase outside a request of the application.</summary>
    event EventHandler<NeoPurchase>? PurchaseUpdated;

    /// <summary>Loads products from the store, with the prices of the storefront of the user.</summary>
    /// <param name="products">The products to load, at most 100.</param>
    /// <param name="cancellationToken">Stops waiting for the store.</param>
    /// <returns>The products that the store sells, without the ones that it does not know; <see cref="NeoDesktopStatus.Unsupported"/> with the code <c>not-configured</c> when the store is not available to the application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="products"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A request is malformed, or there are more than 100.</exception>
    ValueTask<NeoDesktopResult<IReadOnlyList<NeoProduct>>> GetProductsAsync(IReadOnlyList<NeoProductRequest> products, CancellationToken cancellationToken = default);

    /// <summary>Shows the purchase sheet of the store for a product and waits for the user.</summary>
    /// <param name="product">The product to purchase.</param>
    /// <param name="options">The offer and the account of the purchase, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops waiting for the user.</param>
    /// <returns>
    /// The purchase, <see cref="NeoPurchaseState.Pending"/> while the store waits for the payment;
    /// <see cref="NeoDesktopStatus.Canceled"/> when the user cancels; <see cref="NeoDesktopStatus.NotFound"/> with the code
    /// <c>unknown-product</c> for a product that the store does not sell; <see cref="NeoDesktopStatus.Conflict"/> with the code
    /// <c>owned</c> for a product that the user already owns; <see cref="NeoDesktopStatus.Denied"/> with the code
    /// <c>not-allowed</c> when the device does not allow purchases.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="product"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="product"/> is malformed.</exception>
    ValueTask<NeoDesktopResult<NeoPurchase>> PurchaseAsync(NeoProductRequest product, NeoPurchaseOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Reads the purchases that the user holds now: the products not consumed and the subscriptions that are active.</summary>
    /// <param name="cancellationToken">Stops waiting for the store.</param>
    /// <returns>The purchases.</returns>
    ValueTask<NeoDesktopResult<IReadOnlyList<NeoPurchase>>> GetPurchasesAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks the store to bring the purchases of the user to this device, which may ask the user to sign in, and reads them.</summary>
    /// <param name="cancellationToken">Stops waiting for the store.</param>
    /// <returns>The purchases, or <see cref="NeoDesktopStatus.Canceled"/> when the user does not sign in.</returns>
    ValueTask<NeoDesktopResult<IReadOnlyList<NeoPurchase>>> RestoreAsync(CancellationToken cancellationToken = default);

    /// <summary>Finishes a purchase that the application delivered: it finishes the transaction, acknowledges it on Google Play, and consumes a consumable.</summary>
    /// <param name="purchase">The purchase.</param>
    /// <param name="consume">Whether the product is a consumable, which the user can buy again once it is consumed.</param>
    /// <param name="cancellationToken">Stops waiting for the store.</param>
    /// <returns><see cref="NeoDesktopStatus.Success"/>, or <see cref="NeoDesktopStatus.NotFound"/> for a purchase that the store does not have unfinished.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="purchase"/> is <see langword="null"/>.</exception>
    ValueTask<NeoDesktopStatus> FinishAsync(NeoPurchase purchase, bool consume, CancellationToken cancellationToken = default);
}

/// <summary>Creates the statically selected purchases adapter and reads what the stores answer.</summary>
public static class NeoPurchases
{
    /// <summary>The most products that one request loads.</summary>
    public const int MaximumProducts = 100;

    /// <summary>The code of a result for a store that is not available to the application.</summary>
    public const string NotConfiguredCode = "not-configured";

    /// <summary>The code of a result for a product that the store does not sell.</summary>
    public const string UnknownProductCode = "unknown-product";

    /// <summary>The code of a result for a product that the user already owns.</summary>
    public const string OwnedCode = "owned";

    /// <summary>The code of a result for a device that does not allow purchases.</summary>
    public const string NotAllowedCode = "not-allowed";

    /// <summary>Creates the adapter of the current operating system: StoreKit 2 on iOS, the Play Billing Library on Android.</summary>
    /// <param name="dispatcher">Optional application UI dispatcher, on which the events occur; the plugin binds it otherwise.</param>
    /// <returns>The adapter, which answers <see cref="NeoDesktopStatus.Unsupported"/> on a desktop.</returns>
    public static INeoPurchases CreateSystem(NeoDispatcher? dispatcher = null)
    {
        if (OperatingSystem.IsIOS()) return IosPurchases.Create(dispatcher);
        if (OperatingSystem.IsAndroid()) return new AndroidPurchases(dispatcher);
        return new UnsupportedPurchases("The stores of a desktop are not available yet.");
    }

    internal static void Validate(NeoProductRequest product)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (string.IsNullOrEmpty(product.Id) || product.Id.Length > 256 || product.Id.Any(char.IsControl)) throw new ArgumentException("A product identifier has 1 to 256 characters without controls.", nameof(product));
        if (!Enum.IsDefined(product.Type)) throw new ArgumentException("The product type is not defined.", nameof(product));
    }

    internal static void Validate(IReadOnlyList<NeoProductRequest> products)
    {
        ArgumentNullException.ThrowIfNull(products);
        if (products.Count > MaximumProducts) throw new ArgumentException($"A request loads at most {MaximumProducts} products.", nameof(products));
        foreach (var product in products) Validate(product);
    }

    internal static void Validate(NeoPurchaseOptions? options)
    {
        if (options?.OfferId is { } offer && (offer.Length is 0 or > 4096 || offer.Any(char.IsControl))) throw new ArgumentException("An offer identifier has 1 to 4096 characters without controls.", nameof(options));
    }

    internal static NeoDesktopResult<T> Failure<T>(NeoDesktopStatus status, string? code) => new(status, default, code);

    // The answers of the Swift bridge on iOS and of the Java service on Android have one JSON form.
    internal static NeoDesktopStatus Status(JsonElement root) =>
        root.TryGetProperty("status", out var status) && Enum.TryParse<NeoDesktopStatus>(status.GetString(), out var value) ? value : NeoDesktopStatus.Failed;

    internal static string? Code(JsonElement root) => root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null;

    internal static NeoDesktopResult<IReadOnlyList<NeoProduct>> Products(JsonElement root)
    {
        var status = Status(root);
        if (status != NeoDesktopStatus.Success) return Failure<IReadOnlyList<NeoProduct>>(status, Code(root));
        return NeoDesktopResult<IReadOnlyList<NeoProduct>>.Success([.. Array(root, "products").Select(Product)]);
    }

    internal static NeoDesktopResult<NeoPurchase> Purchase(JsonElement root)
    {
        var status = Status(root);
        if (status != NeoDesktopStatus.Success) return Failure<NeoPurchase>(status, Code(root));
        return root.TryGetProperty("purchase", out var purchase) && purchase.ValueKind == JsonValueKind.Object ? NeoDesktopResult<NeoPurchase>.Success(PurchaseOf(purchase)) : Failure<NeoPurchase>(NeoDesktopStatus.Failed, null);
    }

    internal static NeoDesktopResult<IReadOnlyList<NeoPurchase>> PurchaseList(JsonElement root)
    {
        var status = Status(root);
        if (status != NeoDesktopStatus.Success) return Failure<IReadOnlyList<NeoPurchase>>(status, Code(root));
        return NeoDesktopResult<IReadOnlyList<NeoPurchase>>.Success([.. Array(root, "purchases").Select(PurchaseOf)]);
    }

    internal static NeoProduct Product(JsonElement value) => new(Text(value, "id")!, Enum.Parse<NeoProductType>(Text(value, "type")!), Text(value, "title") ?? string.Empty, Text(value, "description") ?? string.Empty,
        Text(value, "displayPrice") ?? string.Empty, decimal.TryParse(Text(value, "price"), NumberStyles.Number, CultureInfo.InvariantCulture, out var price) ? price : 0m, Text(value, "currency") ?? string.Empty)
    {
        SubscriptionPeriod = Text(value, "period"),
        Offers = [.. Array(value, "offers").Select(static offer => new NeoProductOffer(Text(offer, "id")!, Text(offer, "displayPrice") ?? string.Empty, Text(offer, "period"), offer.TryGetProperty("introductory", out var introductory) && introductory.ValueKind == JsonValueKind.True))],
    };

    internal static NeoPurchase PurchaseOf(JsonElement value) => new(Text(value, "productId")!, Enum.Parse<NeoPurchaseStore>(Text(value, "store")!), Enum.Parse<NeoPurchaseState>(Text(value, "state")!), Text(value, "proof")!,
        DateTimeOffset.FromUnixTimeMilliseconds(value.TryGetProperty("purchaseDate", out var date) && date.ValueKind == JsonValueKind.Number ? date.GetInt64() : 0))
    {
        TransactionId = Text(value, "transactionId"),
        OriginalTransactionId = Text(value, "originalTransactionId"),
        ExpirationDate = value.TryGetProperty("expirationDate", out var expiration) && expiration.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeMilliseconds(expiration.GetInt64()) : null,
        Quantity = value.TryGetProperty("quantity", out var quantity) && quantity.ValueKind == JsonValueKind.Number ? quantity.GetInt32() : 1,
        AccountToken = Guid.TryParse(Text(value, "accountToken"), out var account) ? account : null,
        IsFinished = value.TryGetProperty("finished", out var finished) && finished.ValueKind == JsonValueKind.True,
        SignedData = Text(value, "signedData"),
        Signature = Text(value, "signature"),
    };

    private static IEnumerable<JsonElement> Array(JsonElement parent, string name) => parent.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];

    private static string? Text(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>A deterministic store for tests, whose products and purchases the test sets.</summary>
public sealed class NeoFakePurchases : INeoPurchases
{
    private readonly object _sync = new();
    private readonly List<NeoPurchase> _purchases = [];

    /// <inheritdoc />
    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Emulated, 1, 0, "In-memory test store.");

    /// <summary>Gets or sets the products that the store sells.</summary>
    public IReadOnlyList<NeoProduct> Products { get; set; } = [];

    /// <summary>Gets or sets the answer of the user to a purchase sheet: <see langword="true"/> to buy, <see langword="false"/> to cancel.</summary>
    public bool AnswerOfTheUser { get; set; } = true;

    /// <inheritdoc />
    public event EventHandler<NeoPurchase>? PurchaseUpdated;

    /// <summary>Gets the purchases that the user holds, finished or not.</summary>
    public IReadOnlyList<NeoPurchase> Purchases { get { lock (_sync) return [.. _purchases]; } }

    /// <inheritdoc />
    public ValueTask<NeoDesktopResult<IReadOnlyList<NeoProduct>>> GetProductsAsync(IReadOnlyList<NeoProductRequest> products, CancellationToken cancellationToken = default)
    {
        NeoPurchases.Validate(products);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(NeoDesktopResult<IReadOnlyList<NeoProduct>>.Success([.. Products.Where(product => products.Any(request => request.Id == product.Id && request.Type == product.Type))]));
    }

    /// <inheritdoc />
    public ValueTask<NeoDesktopResult<NeoPurchase>> PurchaseAsync(NeoProductRequest product, NeoPurchaseOptions? options = null, CancellationToken cancellationToken = default)
    {
        NeoPurchases.Validate(product);
        NeoPurchases.Validate(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Products.Any(item => item.Id == product.Id && item.Type == product.Type)) return ValueTask.FromResult(NeoPurchases.Failure<NeoPurchase>(NeoDesktopStatus.NotFound, NeoPurchases.UnknownProductCode));
        if (!AnswerOfTheUser) return ValueTask.FromResult(NeoPurchases.Failure<NeoPurchase>(NeoDesktopStatus.Canceled, null));
        lock (_sync)
        {
            if (_purchases.Any(item => item.ProductId == product.Id)) return ValueTask.FromResult(NeoPurchases.Failure<NeoPurchase>(NeoDesktopStatus.Conflict, NeoPurchases.OwnedCode));
            var purchase = new NeoPurchase(product.Id, NeoPurchaseStore.AppStore, NeoPurchaseState.Purchased, "fake-proof-" + (_purchases.Count + 1), DateTimeOffset.UtcNow)
            {
                TransactionId = (_purchases.Count + 1).ToString(CultureInfo.InvariantCulture),
                AccountToken = options?.AccountToken,
            };
            _purchases.Add(purchase);
            return ValueTask.FromResult(NeoDesktopResult<NeoPurchase>.Success(purchase));
        }
    }

    /// <inheritdoc />
    public ValueTask<NeoDesktopResult<IReadOnlyList<NeoPurchase>>> GetPurchasesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(NeoDesktopResult<IReadOnlyList<NeoPurchase>>.Success(Purchases));
    }

    /// <inheritdoc />
    public ValueTask<NeoDesktopResult<IReadOnlyList<NeoPurchase>>> RestoreAsync(CancellationToken cancellationToken = default) => GetPurchasesAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<NeoDesktopStatus> FinishAsync(NeoPurchase purchase, bool consume, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(purchase);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var index = _purchases.FindIndex(item => item.Proof == purchase.Proof);
            if (index < 0 || _purchases[index].IsFinished) return ValueTask.FromResult(NeoDesktopStatus.NotFound);
            if (consume) _purchases.RemoveAt(index);
            else _purchases[index] = _purchases[index] with { IsFinished = true };
            return ValueTask.FromResult(NeoDesktopStatus.Success);
        }
    }

    /// <summary>Reports a purchase that the store completes outside a request, such as a renewal.</summary>
    /// <param name="purchase">The purchase.</param>
    public void Update(NeoPurchase purchase)
    {
        ArgumentNullException.ThrowIfNull(purchase);
        lock (_sync) { _purchases.RemoveAll(item => item.Proof == purchase.Proof); _purchases.Add(purchase); }
        PurchaseUpdated?.Invoke(this, purchase);
    }
}

internal sealed class UnsupportedPurchases(string details) : INeoPurchases
{
    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.None, 1, 0, details);
    public event EventHandler<NeoPurchase>? PurchaseUpdated { add { } remove { } }

    public ValueTask<NeoDesktopResult<IReadOnlyList<NeoProduct>>> GetProductsAsync(IReadOnlyList<NeoProductRequest> products, CancellationToken cancellationToken = default)
    {
        NeoPurchases.Validate(products);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(NeoPurchases.Failure<IReadOnlyList<NeoProduct>>(NeoDesktopStatus.Unsupported, null));
    }

    public ValueTask<NeoDesktopResult<NeoPurchase>> PurchaseAsync(NeoProductRequest product, NeoPurchaseOptions? options = null, CancellationToken cancellationToken = default)
    {
        NeoPurchases.Validate(product);
        NeoPurchases.Validate(options);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(NeoPurchases.Failure<NeoPurchase>(NeoDesktopStatus.Unsupported, null));
    }

    public ValueTask<NeoDesktopResult<IReadOnlyList<NeoPurchase>>> GetPurchasesAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoPurchases.Failure<IReadOnlyList<NeoPurchase>>(NeoDesktopStatus.Unsupported, null)); }

    public ValueTask<NeoDesktopResult<IReadOnlyList<NeoPurchase>>> RestoreAsync(CancellationToken cancellationToken = default) => GetPurchasesAsync(cancellationToken);

    public ValueTask<NeoDesktopStatus> FinishAsync(NeoPurchase purchase, bool consume, CancellationToken cancellationToken = default) { ArgumentNullException.ThrowIfNull(purchase); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoDesktopStatus.Unsupported); }
}

// What the platform adapters bind to the UI dispatcher of the application when the plugin attaches.
internal interface INeoPurchasesDispatcherBinding
{
    void Bind(NeoDispatcher dispatcher);
}
