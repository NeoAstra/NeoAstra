// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NeoAstra.Desktop;

namespace NeoAstra.Purchases;

// StoreKit 2 through the bridge framework of this package, which an iOS application embeds: one JSON request per call, whose
// callback answers on a task of StoreKit, and a listener of the transactions that the App Store completes outside a request,
// which raises them on the UI dispatcher.
internal sealed partial class IosPurchases : INeoPurchases, INeoPurchasesDispatcherBinding
{
    private const string Bridge = "@rpath/NeoAstraPurchases.framework/NeoAstraPurchases";
    private static readonly object Sync = new();
    private static IosPurchases? s_listening;
    private NeoDispatcher? _dispatcher;

    private IosPurchases(NeoDispatcher? dispatcher)
    {
        _dispatcher = dispatcher;
        if (dispatcher is not null) Listen();
    }

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Native, 1, 0,
        "StoreKit 2: products, purchases with the JWS of their signed transactions, current entitlements, App Store sync, finished transactions, and the transactions that the App Store completes outside a request.");

    public event EventHandler<NeoPurchase>? PurchaseUpdated;

    // The bridge is embedded by the targets of the package; an application without it has no purchases.
    internal static INeoPurchases Create(NeoDispatcher? dispatcher)
        => NativeLibrary.TryLoad(Bridge, out _) ? new IosPurchases(dispatcher) : new UnsupportedPurchases("The StoreKit 2 bridge of the purchases plugin is not embedded in the application.");

    void INeoPurchasesDispatcherBinding.Bind(NeoDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher ??= dispatcher;
        Listen();
    }

    public async ValueTask<NeoDesktopResult<IReadOnlyList<NeoProduct>>> GetProductsAsync(IReadOnlyList<NeoProductRequest> products, CancellationToken cancellationToken = default)
    {
        NeoPurchases.Validate(products);
        var requested = products.ToArray();
        using var response = await CallAsync("products", writer =>
        {
            writer.WriteStartArray("products");
            foreach (var product in requested) { writer.WriteStartObject(); writer.WriteString("id", product.Id); writer.WriteString("type", product.Type.ToString()); writer.WriteEndObject(); }
            writer.WriteEndArray();
        }, cancellationToken).ConfigureAwait(false);
        return NeoPurchases.Products(response.RootElement);
    }

    public async ValueTask<NeoDesktopResult<NeoPurchase>> PurchaseAsync(NeoProductRequest product, NeoPurchaseOptions? options = null, CancellationToken cancellationToken = default)
    {
        NeoPurchases.Validate(product);
        NeoPurchases.Validate(options);
        var account = options?.AccountToken;
        // The App Store applies the introductory offer that the user is eligible for by itself.
        using var response = await CallAsync("purchase", writer =>
        {
            writer.WriteString("id", product.Id);
            if (account is { } token) writer.WriteString("accountToken", token.ToString());
        }, cancellationToken).ConfigureAwait(false);
        return NeoPurchases.Purchase(response.RootElement);
    }

    public async ValueTask<NeoDesktopResult<IReadOnlyList<NeoPurchase>>> GetPurchasesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await CallAsync("list", null, cancellationToken).ConfigureAwait(false);
        return NeoPurchases.PurchaseList(response.RootElement);
    }

    public async ValueTask<NeoDesktopResult<IReadOnlyList<NeoPurchase>>> RestoreAsync(CancellationToken cancellationToken = default)
    {
        using var response = await CallAsync("restore", null, cancellationToken).ConfigureAwait(false);
        return NeoPurchases.PurchaseList(response.RootElement);
    }

    // StoreKit 2 has no consumption: a finished consumable is one that the user can buy again.
    public async ValueTask<NeoDesktopStatus> FinishAsync(NeoPurchase purchase, bool consume, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(purchase);
        if (purchase.TransactionId is not { } id) return NeoDesktopStatus.NotFound;
        using var response = await CallAsync("finish", writer => writer.WriteString("transactionId", id), cancellationToken).ConfigureAwait(false);
        return NeoPurchases.Status(response.RootElement);
    }

    private static async Task<JsonDocument> CallAsync(string operation, Action<Utf8JsonWriter>? write, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", operation);
            write?.Invoke(writer);
            writer.WriteEndObject();
        }
        stream.WriteByte(0);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Send(stream.GetBuffer(), GCHandle.Alloc(completion));
        return JsonDocument.Parse(await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false));
    }

    private static unsafe void Send(byte[] request, GCHandle handle)
    {
        fixed (byte* pointer = request) neoastra_purchases_call(pointer, &Answered, GCHandle.ToIntPtr(handle));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void Answered(nint context, byte* response)
    {
        var handle = GCHandle.FromIntPtr(context);
        try { if (handle.Target is TaskCompletionSource<string> completion) completion.TrySetResult(Marshal.PtrToStringUTF8((nint)response) ?? "{}"); }
        catch { }
        finally { handle.Free(); }
    }

    // One listener per process, as StoreKit has one stream of updates.
    private unsafe void Listen()
    {
        lock (Sync)
        {
            if (s_listening is not null) return;
            s_listening = this;
        }
        neoastra_purchases_listen(&Updated, 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void Updated(nint context, byte* response)
    {
        try
        {
            var owner = s_listening;
            if (owner?._dispatcher is not { } dispatcher) return;
            using var document = JsonDocument.Parse(Marshal.PtrToStringUTF8((nint)response) ?? "{}");
            if (!document.RootElement.TryGetProperty("purchase", out var value)) return;
            var purchase = NeoPurchases.PurchaseOf(value);
            _ = dispatcher.InvokeAsync(() => { try { owner.PurchaseUpdated?.Invoke(owner, purchase); } catch { } });
        }
        catch { }
    }

    [LibraryImport(Bridge)] private static unsafe partial void neoastra_purchases_call(byte* request, delegate* unmanaged[Cdecl]<nint, byte*, void> callback, nint context);
    [LibraryImport(Bridge)] private static unsafe partial void neoastra_purchases_listen(delegate* unmanaged[Cdecl]<nint, byte*, void> callback, nint context);
}
