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
    /// <summary>The Amazon Appstore, through the Amazon Appstore SDK on Android, for an installation from the Amazon Appstore such as on a Fire tablet.</summary>
    Amazon,
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
/// <param name="Id">The identifier of the offer to purchase it with, which <see cref="NeoPurchaseOptions.OfferId"/> takes; on Google Play the offer token of a base plan or offer.</param>
/// <param name="DisplayPrice">The price of the first phase of the offer as the store shows it: the price of a base plan, or the price of a trial, such as nothing.</param>
/// <param name="Period">The billing period of the first phase of the offer as an ISO 8601 duration, such as <c>P1M</c>, when the offer has one.</param>
/// <param name="IsIntroductory">
/// Whether the offer has a free trial or a lower price for its first periods, before the regular price of the subscription:
/// on the App Store the introductory offer, which it lists when the user is eligible for it; on Google Play every offer of a
/// base plan, such as a trial, an introductory, an upgrade, or a win-back offer.
/// </param>
/// <remarks>
/// <para>
/// A subscription of Google Play can have several base plans, such as a monthly and an annual one, each with its own offers:
/// the price and the period of a plan are in the offer of the plan whose <see cref="OfferId"/> is <see langword="null"/>, and
/// its other offers have their own <see cref="OfferId"/> and <see cref="Tags"/>, which pick an offer by what the Play Console
/// says of it, without the identifier of the offer in the code of the application.
/// </para>
/// <para>
/// Google Play lists an offer for new customers or for upgrades only to a user who is eligible for it, but an offer whose
/// eligibility the developer determines to every user: the application decides who gets it.
/// </para>
/// </remarks>
public sealed record NeoProductOffer(string Id, string DisplayPrice, string? Period, bool IsIntroductory)
{
    /// <summary>Gets the identifier of the base plan of the offer in the Play Console, such as <c>monthly</c>; <see langword="null"/> on the App Store, which has no base plans.</summary>
    public string? BasePlanId { get; init; }

    /// <summary>Gets the identifier of the offer within its base plan in the Play Console, such as <c>free-trial-7d</c>; <see langword="null"/> for the offer of the base plan itself, and on the App Store.</summary>
    /// <remarks>A purchase takes <see cref="Id"/>, not this identifier.</remarks>
    public string? OfferId { get; init; }

    /// <summary>Gets the tags of the offer and of its base plan in the Play Console; empty on the App Store.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <inheritdoc />
    public bool Equals(NeoProductOffer? other) => other is not null && Id == other.Id && DisplayPrice == other.DisplayPrice && Period == other.Period && IsIntroductory == other.IsIntroductory
        && BasePlanId == other.BasePlanId && OfferId == other.OfferId && Tags.SequenceEqual(other.Tags);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Id, DisplayPrice, Period, IsIntroductory, BasePlanId, OfferId);
}

/// <summary>Contains a product as the store sells it to the user, with the prices of the storefront of the user.</summary>
/// <param name="Id">The product identifier.</param>
/// <param name="Type">The kind of product.</param>
/// <param name="Title">The localized name.</param>
/// <param name="Description">The localized description.</param>
/// <param name="DisplayPrice">The price as the store shows it, with its currency; on Google Play the price of the first base plan of a subscription.</param>
/// <param name="Price">The price as a number in <paramref name="CurrencyCode"/>; on Google Play the price of the first base plan of a subscription.</param>
/// <param name="CurrencyCode">The ISO 4217 currency code of the price.</param>
/// <remarks>
/// A subscription of Google Play with several base plans has a price and a period per base plan, which <see cref="Offers"/>
/// gives; the price and the period of the product are the ones of its first base plan.
/// </remarks>
public sealed record NeoProduct(string Id, NeoProductType Type, string Title, string Description, string DisplayPrice, decimal Price, string CurrencyCode)
{
    /// <summary>Gets the billing period of a subscription as an ISO 8601 duration, such as <c>P1M</c>; on Google Play the period of its first base plan.</summary>
    public string? SubscriptionPeriod { get; init; }

    /// <summary>
    /// Gets the subscription group of a subscription on the App Store, whose subscriptions replace each other, such as one per
    /// period or level of service; <see langword="null"/> on Google Play, where the plans of a subscription are its base plans in <see cref="Offers"/>.
    /// </summary>
    public string? SubscriptionGroupId { get; init; }

    /// <summary>
    /// Gets the level of service of a subscription in its group on the App Store, where 1 is the highest; <see langword="null"/> on
    /// Google Play, before iOS 16.4, and when the App Store does not tell it.
    /// </summary>
    /// <remarks>
    /// The App Store applies an upgrade to a higher level at once and a downgrade to a lower level at the next renewal, or at the
    /// end of the commitment of a monthly plan with a 12-month commitment; when it applies a change within a level depends on
    /// the durations and the billing plans of both subscriptions. <see cref="NeoPurchase.RenewalProductId"/> tells a change that waits for the next renewal.
    /// </remarks>
    public int? SubscriptionGroupLevel { get; init; }

    /// <summary>Gets the offers of the product: on Google Play the base plans of a subscription and their offers, one of which a subscription is purchased with.</summary>
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
    /// <summary>
    /// Another subscription replaced this one, such as a higher level of service of the same subscription group on the App Store;
    /// the application delivers the subscription that replaced it instead of this one.
    /// </summary>
    Replaced,
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

    /// <summary>
    /// Gets the product that a subscription renews as, when it is another one than <see cref="ProductId"/> and the store tells it the
    /// device: a change of plan that the store applies at the next renewal, such as a downgrade on the App Store, or a replacement
    /// with <see cref="NeoSubscriptionReplacementMode.Deferred"/> on Google Play, which tells it only in the answer of that purchase.
    /// <see langword="null"/> when there is no such change and when the store does not tell it: <see langword="null"/> does not prove that no change waits.
    /// </summary>
    public string? RenewalProductId { get; init; }

    /// <summary>Gets the number of items purchased.</summary>
    public int Quantity { get; init; } = 1;

    /// <summary>Gets the account of the application that the purchase belongs to, as given to <see cref="NeoPurchaseOptions.AccountToken"/>.</summary>
    public Guid? AccountToken { get; init; }

    /// <summary>
    /// Gets whether the application finished the purchase: a finished transaction on the App Store, an acknowledged purchase on
    /// Google Play. The Amazon Appstore does not tell whether a receipt is fulfilled, so it is always <see langword="false"/> there,
    /// and finishing a receipt again does no harm.
    /// </summary>
    public bool IsFinished { get; init; }

    /// <summary>Gets the signed purchase data of Google Play, a JSON document, which <see cref="Signature"/> signs.</summary>
    public string? SignedData { get; init; }

    /// <summary>Gets the signature of <see cref="SignedData"/> with the key of the application in the Play Console.</summary>
    public string? Signature { get; init; }

    /// <summary>
    /// Gets the user of the store that the purchase belongs to, when the server needs it to verify <see cref="Proof"/>: the Amazon
    /// user ID that the Receipt Verification Service of the Amazon Appstore takes with the receipt ID; <see langword="null"/> on the
    /// App Store and Google Play.
    /// </summary>
    public string? StoreUserId { get; init; }
}

/// <summary>Describes how Google Play replaces a subscription that the user owns with the one that the user purchases.</summary>
/// <remarks>
/// Google Play takes only some modes for some plans, and refuses the purchase with an error that it shows the user otherwise:
/// toward a prepaid base plan only <see cref="ChargeFullPrice"/>; between base plans of the same subscription toward an
/// auto-renewing base plan only <see cref="ChargeFullPrice"/> and <see cref="WithoutProration"/>; and no change at all from an
/// installment base plan to another kind of base plan of the same subscription. An installment subscription takes every mode.
/// NeoAstra does not offer the mode KEEP_EXISTING of Google Play, which is for subscriptions with add-ons.
/// </remarks>
public enum NeoSubscriptionReplacementMode
{
    /// <summary>The new plan starts now, and the value of the time left of the old plan, adjusted by the difference of price, moves the next billing date forward.</summary>
    WithTimeProration,
    /// <summary>The new plan starts now with the billing date of the old plan, and the user pays the difference of price for the time left; only for an upgrade whose price per unit of time is higher.</summary>
    ChargeProratedPrice,
    /// <summary>The new plan starts now with the billing date of the old plan, and the user pays its price from the next renewal on.</summary>
    WithoutProration,
    /// <summary>
    /// The new plan starts now, and the user pays its full price now, or the price of its free trial or introductory offer when it
    /// has one; the value of the time left of the old plan carries over or is prorated.
    /// </summary>
    ChargeFullPrice,
    /// <summary>
    /// The old plan goes on until its next renewal, and the new plan starts then, at its price; for a downgrade. Google Play issues
    /// the new purchase at once, which the application finishes as any other: its <see cref="NeoPurchase.ProductId"/> stays the old
    /// product until the renewal, and the answer of the purchase names the new one in <see cref="NeoPurchase.RenewalProductId"/>.
    /// An installment subscription changes at its next payment date instead.
    /// </summary>
    Deferred,
}

/// <summary>Configures one purchase.</summary>
public sealed class NeoPurchaseOptions
{
    /// <summary>
    /// Gets or sets the offer to purchase with, the <see cref="NeoProductOffer.Id"/> of one of <see cref="NeoProduct.Offers"/>. Google Play
    /// needs one for a subscription: it takes the first offer of a subscription with one base plan without it, and answers
    /// <see cref="NeoDesktopStatus.NotFound"/> with the code <c>offer-required</c> for a subscription with several base plans.
    /// </summary>
    public string? OfferId { get; set; }

    /// <summary>
    /// Gets or sets the account of the application that the purchase belongs to, which the store keeps with it and gives back to the
    /// server; Google Play takes the one of <see cref="ReplacedPurchase"/> without it, as it asks for a change of plan.
    /// </summary>
    public Guid? AccountToken { get; set; }

    /// <summary>Gets or sets the purchased subscription of Google Play that the user owns and that the purchase replaces, with <see cref="ReplacementMode"/>.</summary>
    /// <remarks>
    /// <para>
    /// Google Play changes the plan of a subscription without it: a purchase of another base plan or offer of a subscription that
    /// the user owns, or of the same one after a cancellation, replaces it with the default replacement mode of the subscription
    /// in the Play Console, and a purchase of a prepaid base plan tops it up, always with <see cref="NeoSubscriptionReplacementMode.ChargeFullPrice"/>. This purchase overrides that default, and a change
    /// from another subscription needs it.
    /// </para>
    /// <para>
    /// The App Store replaces a subscription of the same subscription group by itself and does not use it, but every platform
    /// checks it, so that the same code runs on both.
    /// </para>
    /// </remarks>
    public NeoPurchase? ReplacedPurchase { get; set; }

    /// <summary>Gets or sets how Google Play replaces <see cref="ReplacedPurchase"/>, which a replacement needs: Google Play takes only some modes for some plans.</summary>
    public NeoSubscriptionReplacementMode? ReplacementMode { get; set; }
}

/// <summary>Sells the products of the application through the store of the platform.</summary>
/// <remarks>
/// <para>
/// A purchase reaches the application with a proof that its server verifies with the store before it delivers the product,
/// and the application finishes the purchase when it has delivered it: a consumable is consumed then, so that the user can
/// buy it again. A store retries what the application did not finish, and Google Play refunds a purchase that is not
/// acknowledged within three days, or within half the duration of a prepaid plan shorter than a week. <see cref="PurchaseUpdated"/> reports the purchases that the store completes outside a
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
    /// <c>not-allowed</c> when the device does not allow purchases; <see cref="NeoDesktopStatus.NotFound"/> with the code
    /// <c>offer-required</c> for a subscription of several base plans of Google Play without <see cref="NeoPurchaseOptions.OfferId"/>,
    /// and without a code for a <see cref="NeoPurchaseOptions.ReplacedPurchase"/> that the user does not hold any more. A change
    /// of plan that the store applies at the next renewal can answer a purchase of the current product, whose
    /// <see cref="NeoPurchase.RenewalProductId"/> names the product purchased.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="product"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="product"/> or <paramref name="options"/> is malformed: a replacement of a product that is not a subscription,
    /// of a purchase that is not purchased, or without a replacement mode; a replacement mode without a replaced purchase; on
    /// Android, a replacement of a purchase of another store.
    /// </exception>
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

    /// <summary>The code of a result for a subscription of several base plans that a purchase does not name an offer of.</summary>
    public const string OfferRequiredCode = "offer-required";

    /// <summary>
    /// Creates the adapter of the current operating system: StoreKit 2 on iOS; on Android the Amazon Appstore SDK for an
    /// installation from the Amazon Appstore, or in the sandbox mode of the Amazon App Tester, of an application that references the
    /// SDK, and the Play Billing Library otherwise.
    /// </summary>
    /// <param name="dispatcher">Optional application UI dispatcher, on which the events occur; the plugin binds it otherwise.</param>
    /// <returns>The adapter, which answers <see cref="NeoDesktopStatus.Unsupported"/> on a desktop.</returns>
    public static INeoPurchases CreateSystem(NeoDispatcher? dispatcher = null)
    {
        if (OperatingSystem.IsIOS()) return IosPurchases.Create(dispatcher);
        if (OperatingSystem.IsAndroid()) return AndroidPurchases.Create(dispatcher);
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

    internal static void Validate(NeoProductRequest product, NeoPurchaseOptions? options)
    {
        Validate(product);
        if (options is null) return;
        if (options.OfferId is { } offer && !IsText(offer, 4096)) throw new ArgumentException("An offer identifier has 1 to 4096 characters without controls.", nameof(options));
        if (options.ReplacementMode is { } mode && !Enum.IsDefined(mode)) throw new ArgumentException("The replacement mode is not defined.", nameof(options));
        if (options.ReplacedPurchase is not { } replaced)
        {
            if (options.ReplacementMode is not null) throw new ArgumentException("A replacement mode needs the replaced purchase.", nameof(options));
            return;
        }
        if (product.Type != NeoProductType.Subscription) throw new ArgumentException("Only a subscription replaces a purchase.", nameof(options));
        // The proof of the App Store is a JWS with its certificate chain, longer than a purchase token of Google Play.
        if (!IsText(replaced.Proof, 65536) || !IsText(replaced.ProductId, 256) || !Enum.IsDefined(replaced.Store)) throw new ArgumentException("The replaced purchase has a product identifier and a proof.", nameof(options));
        if (replaced.State != NeoPurchaseState.Purchased) throw new ArgumentException("Only a purchased subscription is replaced.", nameof(options));
        // Which modes Google Play takes depends on the kinds of the base plans, which only Google Play knows: it refuses the others.
        if (options.ReplacementMode is null) throw new ArgumentException("A replacement needs a replacement mode.", nameof(options));
    }

    // A subscription of several base plans of Google Play is not purchased without an offer that names one of them.
    internal static bool NeedsOffer(NeoProduct product, NeoPurchaseOptions? options)
        => options?.OfferId is null && product.Offers.Select(static offer => offer.BasePlanId).Where(static plan => plan is not null).Distinct(StringComparer.Ordinal).Skip(1).Any();

    private static bool IsText(string? value, int maximum) => !string.IsNullOrEmpty(value) && value.Length <= maximum && !value.Any(char.IsControl);

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
        SubscriptionGroupId = Text(value, "group"),
        // Before iOS 17 the App Store answers 0 for a level that it does not know.
        SubscriptionGroupLevel = value.TryGetProperty("groupLevel", out var level) && level.ValueKind == JsonValueKind.Number && level.GetInt32() > 0 ? level.GetInt32() : null,
        Offers = [.. Array(value, "offers").Select(static offer => new NeoProductOffer(Text(offer, "id")!, Text(offer, "displayPrice") ?? string.Empty, Text(offer, "period"), offer.TryGetProperty("introductory", out var introductory) && introductory.ValueKind == JsonValueKind.True)
        {
            BasePlanId = Text(offer, "basePlanId"),
            OfferId = Text(offer, "offerId"),
            Tags = [.. Array(offer, "tags").Where(static tag => tag.ValueKind == JsonValueKind.String).Select(static tag => tag.GetString()!)],
        })],
    };

    internal static NeoPurchase PurchaseOf(JsonElement value) => new(Text(value, "productId")!, Enum.Parse<NeoPurchaseStore>(Text(value, "store")!), Enum.Parse<NeoPurchaseState>(Text(value, "state")!), Text(value, "proof")!,
        DateTimeOffset.FromUnixTimeMilliseconds(value.TryGetProperty("purchaseDate", out var date) && date.ValueKind == JsonValueKind.Number ? date.GetInt64() : 0))
    {
        TransactionId = Text(value, "transactionId"),
        OriginalTransactionId = Text(value, "originalTransactionId"),
        ExpirationDate = value.TryGetProperty("expirationDate", out var expiration) && expiration.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeMilliseconds(expiration.GetInt64()) : null,
        RenewalProductId = Text(value, "renewalProductId"),
        Quantity = value.TryGetProperty("quantity", out var quantity) && quantity.ValueKind == JsonValueKind.Number ? quantity.GetInt32() : 1,
        AccountToken = Guid.TryParse(Text(value, "accountToken"), out var account) ? account : null,
        IsFinished = value.TryGetProperty("finished", out var finished) && finished.ValueKind == JsonValueKind.True,
        SignedData = Text(value, "signedData"),
        Signature = Text(value, "signature"),
        StoreUserId = Text(value, "storeUserId"),
    };

    private static IEnumerable<JsonElement> Array(JsonElement parent, string name) => parent.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];

    private static string? Text(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>A deterministic store for tests, whose products and purchases the test sets.</summary>
/// <remarks>
/// A second purchase of a product that the user holds answers <see cref="NeoDesktopStatus.Conflict"/>, unless it replaces it: with
/// <see cref="NeoPurchaseOptions.ReplacedPurchase"/>, or, as Google Play does, with an offer of another base plan of the same subscription.
/// </remarks>
public sealed class NeoFakePurchases : INeoPurchases
{
    private readonly object _sync = new();
    private readonly List<NeoPurchase> _purchases = [];
    private readonly Dictionary<string, string?> _basePlans = new(StringComparer.Ordinal);
    private int _sequence;

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
        NeoPurchases.Validate(product, options);
        cancellationToken.ThrowIfCancellationRequested();
        if (Products.FirstOrDefault(item => item.Id == product.Id && item.Type == product.Type) is not { } sold) return ValueTask.FromResult(NeoPurchases.Failure<NeoPurchase>(NeoDesktopStatus.NotFound, NeoPurchases.UnknownProductCode));
        if (NeoPurchases.NeedsOffer(sold, options)) return ValueTask.FromResult(NeoPurchases.Failure<NeoPurchase>(NeoDesktopStatus.NotFound, NeoPurchases.OfferRequiredCode));
        if (!AnswerOfTheUser) return ValueTask.FromResult(NeoPurchases.Failure<NeoPurchase>(NeoDesktopStatus.Canceled, null));
        lock (_sync)
        {
            // A replaced subscription makes way for the new one, as a change of plan does; so does another base plan of the same one.
            var replaced = options?.ReplacedPurchase?.Proof;
            if (replaced is not null && !_purchases.Any(item => item.Proof == replaced)) return ValueTask.FromResult(NeoPurchases.Failure<NeoPurchase>(NeoDesktopStatus.NotFound, null));
            var plan = (options?.OfferId is { } offer ? sold.Offers.FirstOrDefault(item => item.Id == offer) : sold.Offers.FirstOrDefault())?.BasePlanId;
            var replacedPlan = replaced is null ? null : _basePlans.GetValueOrDefault(replaced);
            var held = _purchases.Where(item => item.ProductId == product.Id && item.Proof != replaced).ToArray();
            if (held.Any(item => plan is null || _basePlans.GetValueOrDefault(item.Proof) is not { } owned || owned == plan)) return ValueTask.FromResult(NeoPurchases.Failure<NeoPurchase>(NeoDesktopStatus.Conflict, NeoPurchases.OwnedCode));
            _purchases.RemoveAll(item => item.Proof == replaced || held.Contains(item));
            var sequence = ++_sequence;
            // A deferred replacement keeps the old product and its base plan until the next renewal, as Google Play does.
            var deferred = options?.ReplacementMode == NeoSubscriptionReplacementMode.Deferred ? options.ReplacedPurchase : null;
            var purchase = new NeoPurchase(deferred?.ProductId ?? product.Id, NeoPurchaseStore.AppStore, NeoPurchaseState.Purchased, "fake-proof-" + sequence.ToString(CultureInfo.InvariantCulture), DateTimeOffset.UtcNow)
            {
                TransactionId = sequence.ToString(CultureInfo.InvariantCulture),
                AccountToken = options?.AccountToken,
                RenewalProductId = deferred is not null && deferred.ProductId != product.Id ? product.Id : null,
            };
            _purchases.Add(purchase);
            _basePlans[purchase.Proof] = deferred is null ? plan : replacedPlan;
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
        NeoPurchases.Validate(product, options);
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
