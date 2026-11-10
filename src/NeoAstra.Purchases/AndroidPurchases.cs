// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text;
using System.Text.Json;
using NeoAstra.Desktop;

namespace NeoAstra.Purchases;

// The Play Billing Library through the Java service `neoastra.purchases` of this package, or the Amazon Appstore SDK through
// `neoastra.purchases.amazon`, which the platform services of NeoAstra reach; both answer in one form. Java blocks until the
// store or the user answers, so every call leaves the calling thread; a thread of this adapter takes the purchases that the
// store completes outside a request, waiting in Java, and raises them on the UI dispatcher. The Java side of a store is there
// only when the application references its library.
internal sealed class AndroidPurchases : INeoPurchases, INeoPurchasesDispatcherBinding, IAsyncDisposable
{
    internal const string Service = "neoastra.purchases";
    internal const string AmazonService = "neoastra.purchases.amazon";
    private readonly CancellationTokenSource _stopping = new();
    private readonly string _service;
    private readonly NeoPurchaseStore _store;
    private NeoDispatcher? _dispatcher;
    private Task? _updates;

    internal AndroidPurchases(NeoDispatcher? dispatcher) : this(dispatcher, Service, NeoPurchaseStore.GooglePlay) { }

    internal AndroidPurchases(NeoDispatcher? dispatcher, string service, NeoPurchaseStore store)
    {
        _service = service;
        _store = store;
        Support = store == NeoPurchaseStore.Amazon
            ? new(NeoSupportLevel.Native, 1, 0, "The Amazon Appstore SDK: products, purchases, pending purchases of children, changes of subscription terms, fulfillment, and the receipts that the Appstore reports when the application returns (com.amazon.device:amazon-appstore-sdk).")
            : new(NeoSupportLevel.Native, 1, 0, "The Play Billing Library: products with their base plans and offers, purchases with offers and plan changes, acknowledgement and consumption, and the purchases that Google Play completes outside a request (Xamarin.Android.Google.BillingClient).");
        _dispatcher = dispatcher;
        if (dispatcher is not null) Start();
    }

    // The Amazon Appstore sells to an installation that it made, or to one in the sandbox mode of its SDK, whose purchases the
    // Amazon App Tester answers; every other installation buys on Google Play.
    internal static AndroidPurchases Create(NeoDispatcher? dispatcher)
        => IsAmazonAvailable() ? new(dispatcher, AmazonService, NeoPurchaseStore.Amazon) : new(dispatcher);

    internal static bool IsAmazonAvailable()
    {
        try
        {
            using var response = Call(AmazonService, "available", null);
            return IsAvailable(response.RootElement);
        }
        catch (Exception exception) when (exception is NotSupportedException or NeoAstraException or JsonException) { return false; }
    }

    internal static bool IsAvailable(JsonElement root)
        => NeoPurchases.Status(root) == NeoDesktopStatus.Success && root.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.True;

    public NeoCapabilityInfo Support { get; }

    public event EventHandler<NeoPurchase>? PurchaseUpdated;

    void INeoPurchasesDispatcherBinding.Bind(NeoDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher ??= dispatcher;
        Start();
    }

    public ValueTask<NeoDesktopResult<IReadOnlyList<NeoProduct>>> GetProductsAsync(IReadOnlyList<NeoProductRequest> products, CancellationToken cancellationToken = default)
    {
        NeoPurchases.Validate(products);
        var requested = products.ToArray();
        return Run(() => Read("products", writer =>
        {
            writer.WriteStartArray("products");
            foreach (var product in requested) { writer.WriteStartObject(); writer.WriteString("id", product.Id); writer.WriteString("type", product.Type.ToString()); writer.WriteEndObject(); }
            writer.WriteEndArray();
        }, NeoPurchases.Products), cancellationToken);
    }

    public ValueTask<NeoDesktopResult<NeoPurchase>> PurchaseAsync(NeoProductRequest product, NeoPurchaseOptions? options = null, CancellationToken cancellationToken = default)
    {
        NeoPurchases.Validate(product, options);
        var offer = options?.OfferId;
        var replaced = options?.ReplacedPurchase;
        // Google Play asks for the account of the replaced subscription with a change of plan.
        var account = options?.AccountToken ?? replaced?.AccountToken;
        var mode = options?.ReplacementMode;
        if (replaced is not null && replaced.Store != _store) throw new ArgumentException(_store == NeoPurchaseStore.Amazon ? "The Amazon Appstore replaces only a purchase of the Amazon Appstore." : "Google Play replaces only a purchase of Google Play.", nameof(options));
        return Run(() => Read("purchase", writer =>
        {
            writer.WriteString("id", product.Id);
            writer.WriteString("type", product.Type.ToString());
            if (offer is not null) writer.WriteString("offer", offer);
            if (account is { } token) writer.WriteString("accountToken", token.ToString());
            if (replaced is not null)
            {
                writer.WriteStartObject("replaced");
                writer.WriteString("productId", replaced.ProductId);
                writer.WriteString("token", replaced.Proof);
                // Validate requires a mode with a replacement.
                writer.WriteString("mode", mode.GetValueOrDefault().ToString());
                writer.WriteEndObject();
            }
        }, NeoPurchases.Purchase), cancellationToken);
    }

    public ValueTask<NeoDesktopResult<IReadOnlyList<NeoPurchase>>> GetPurchasesAsync(CancellationToken cancellationToken = default)
        => Run(() => Read("list", null, NeoPurchases.PurchaseList), cancellationToken);

    // Google Play keeps the purchases with the account of the device, which the list already has.
    public ValueTask<NeoDesktopResult<IReadOnlyList<NeoPurchase>>> RestoreAsync(CancellationToken cancellationToken = default) => GetPurchasesAsync(cancellationToken);

    public async ValueTask<NeoDesktopStatus> FinishAsync(NeoPurchase purchase, bool consume, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(purchase);
        var token = purchase.Proof;
        return await Run(() => Read("finish", writer => { writer.WriteString("token", token); writer.WriteBoolean("consume", consume); }, NeoPurchases.Status), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopping.IsCancellationRequested) return;
        _stopping.Cancel();
        PurchaseUpdated = null;
        if (_updates is not null) try { await _updates.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch (Exception exception) when (exception is TimeoutException or OperationCanceledException) { }
        _stopping.Dispose();
    }

    private void Start() => _updates ??= Task.Factory.StartNew(ReadUpdates, _stopping.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static ValueTask<T> Run<T>(Func<T> call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(Task.Run(call, cancellationToken).WaitAsync(cancellationToken));
    }

    private void ReadUpdates()
    {
        var failures = 0;
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                using var response = Call(_service, "next", writer => writer.WriteNumber("timeout", 5000));
                failures = 0;
                var status = NeoPurchases.Status(response.RootElement);
                if (status == NeoDesktopStatus.Unsupported) return;
                if (status == NeoDesktopStatus.Success && response.RootElement.TryGetProperty("purchase", out var value)) Raise(NeoPurchases.PurchaseOf(value));
            }
            catch (NotSupportedException) { return; }
            catch (Exception exception) when (exception is NeoAstraException or JsonException or ArgumentException or InvalidOperationException)
            {
                // A failed read is retried a few times; a service that keeps failing is not there.
                if (++failures > 5) return;
                _stopping.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            }
        }
    }

    private void Raise(NeoPurchase purchase)
    {
        if (_dispatcher is not { } dispatcher) return;
        try { _ = dispatcher.InvokeAsync(() => { if (!_stopping.IsCancellationRequested) try { PurchaseUpdated?.Invoke(this, purchase); } catch { } }); }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException) { }
    }

    private T Read<T>(string operation, Action<Utf8JsonWriter>? write, Func<JsonElement, T> read)
    {
        using var response = Call(_service, operation, write);
        return read(response.RootElement);
    }

    internal static JsonDocument Call(string operation, Action<Utf8JsonWriter>? write) => Call(Service, operation, write);

    internal static JsonDocument Call(string service, string operation, Action<Utf8JsonWriter>? write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", operation);
            write?.Invoke(writer);
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(NeoPlatformServices.Call(service, Encoding.UTF8.GetString(stream.ToArray())));
    }
}
