package com.neoastra.purchases;

import android.app.Activity;
import android.content.Context;
import android.os.Looper;

import com.android.billingclient.api.AcknowledgePurchaseParams;
import com.android.billingclient.api.BillingClient;
import com.android.billingclient.api.BillingClientStateListener;
import com.android.billingclient.api.BillingFlowParams;
import com.android.billingclient.api.BillingResult;
import com.android.billingclient.api.ConsumeParams;
import com.android.billingclient.api.PendingPurchasesParams;
import com.android.billingclient.api.ProductDetails;
import com.android.billingclient.api.Purchase;
import com.android.billingclient.api.PurchasesUpdatedListener;
import com.android.billingclient.api.QueryProductDetailsParams;
import com.android.billingclient.api.QueryPurchasesParams;
import com.neoastra.backend.NeoAstraServices;

import org.json.JSONArray;
import org.json.JSONObject;

import java.lang.ref.WeakReference;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

/**
 * The purchases service of the purchases plugin of NeoAstra, which the platform services reach as {@code neoastra.purchases}:
 * the products, purchases, and subscriptions of Google Play through the Play Billing Library. A request blocks the calling
 * thread, which is never the main thread, until Google Play or the user answers; the purchases that Google Play completes
 * outside a request wait in a queue that the managed side takes one at a time.
 */
public final class NeoAstraPurchases implements NeoAstraServices.Service {
    static final String NAME = "neoastra.purchases";
    private static final LinkedBlockingQueue<JSONObject> UPDATES = new LinkedBlockingQueue<>(64);
    private static volatile WeakReference<Activity> resumed = new WeakReference<>(null);
    private static final Object FLOW = new Object();
    private static AtomicReference<Object[]> flow;
    private static CountDownLatch flowAnswered;
    private static BillingClient client;

    static void resumed(Activity activity) { resumed = new WeakReference<>(activity); }

    static void destroyed(Activity activity) { if (resumed.get() == activity) resumed = new WeakReference<>(null); }

    @Override public void call(Context context, JSONObject input, JSONObject output) throws Exception {
        if (Looper.myLooper() == Looper.getMainLooper()) throw new IllegalStateException("A purchase cannot wait on the main thread.");
        String operation = input.getString("operation");
        if ("next".equals(operation)) {
            JSONObject update = UPDATES.poll(Math.max(0, Math.min(30_000, input.optLong("timeout", 20_000))), TimeUnit.MILLISECONDS);
            if (update == null) output.put("status", "NotFound"); else output.put("status", "Success").put("purchase", update);
            return;
        }
        BillingClient billing = connect(context);
        if (billing == null) { output.put("status", "Unsupported").put("code", "not-configured"); return; }
        switch (operation) {
            case "products": products(billing, input, output); return;
            case "purchase": purchase(billing, input, output); return;
            case "list": list(billing, output); return;
            case "finish": finish(billing, input, output); return;
            default: output.put("status", "Unsupported");
        }
    }

    // One client per process, which reconnects by itself; a device without Google Play does not connect.
    private static synchronized BillingClient connect(Context context) throws InterruptedException {
        if (client == null) {
            client = BillingClient.newBuilder(context.getApplicationContext())
                .setListener(LISTENER)
                .enablePendingPurchases(PendingPurchasesParams.newBuilder().enableOneTimeProducts().build())
                .enableAutoServiceReconnection()
                .build();
        }
        if (client.isReady()) return client;
        CountDownLatch connected = new CountDownLatch(1);
        AtomicReference<BillingResult> result = new AtomicReference<>();
        client.startConnection(new BillingClientStateListener() {
            @Override public void onBillingSetupFinished(BillingResult value) { result.set(value); connected.countDown(); }
            @Override public void onBillingServiceDisconnected() { }
        });
        if (!connected.await(30, TimeUnit.SECONDS) || result.get() == null || result.get().getResponseCode() != BillingClient.BillingResponseCode.OK) return null;
        return client;
    }

    // A purchase that the open purchase flow waits for goes to it; any other one is an update.
    private static final PurchasesUpdatedListener LISTENER = (result, purchases) -> {
        synchronized (FLOW) {
            if (flow != null) { flow.set(new Object[] { result, purchases }); flowAnswered.countDown(); return; }
        }
        if (result.getResponseCode() != BillingClient.BillingResponseCode.OK || purchases == null) return;
        for (Purchase purchase : purchases) {
            try { JSONObject value = purchase(purchase); while (!UPDATES.offer(value)) UPDATES.poll(); } catch (Exception ignored) { }
        }
    };

    private static void products(BillingClient billing, JSONObject input, JSONObject output) throws Exception {
        JSONArray requests = input.optJSONArray("products");
        JSONArray found = new JSONArray();
        for (String type : new String[] { "InApp", "Subscription" }) {
            List<QueryProductDetailsParams.Product> list = new ArrayList<>();
            for (int index = 0; requests != null && index < requests.length(); index++) {
                JSONObject request = requests.getJSONObject(index);
                if (type.equals(request.getString("type"))) list.add(QueryProductDetailsParams.Product.newBuilder().setProductId(request.getString("id")).setProductType(googleType(type)).build());
            }
            if (list.isEmpty()) continue;
            List<ProductDetails> details = query(billing, list, output);
            if (details == null) return;
            for (ProductDetails product : details) found.put(product(product));
        }
        output.put("status", "Success").put("products", found);
    }

    private static List<ProductDetails> query(BillingClient billing, List<QueryProductDetailsParams.Product> products, JSONObject output) throws Exception {
        CountDownLatch answered = new CountDownLatch(1);
        AtomicReference<Object[]> result = new AtomicReference<>();
        billing.queryProductDetailsAsync(QueryProductDetailsParams.newBuilder().setProductList(products).build(), (value, details) -> { result.set(new Object[] { value, details.getProductDetailsList() }); answered.countDown(); });
        if (!answered.await(60, TimeUnit.SECONDS)) { output.put("status", "Failed").put("code", "timeout"); return null; }
        BillingResult value = (BillingResult) result.get()[0];
        if (value.getResponseCode() != BillingClient.BillingResponseCode.OK) { failure(value, output); return null; }
        @SuppressWarnings("unchecked") List<ProductDetails> details = (List<ProductDetails>) result.get()[1];
        return details == null ? Collections.emptyList() : details;
    }

    private static JSONObject product(ProductDetails product) throws Exception {
        boolean subscription = BillingClient.ProductType.SUBS.equals(product.getProductType());
        JSONObject value = new JSONObject().put("id", product.getProductId()).put("type", subscription ? "Subscription" : "InApp")
            .put("title", product.getName()).put("description", product.getDescription());
        if (!subscription) {
            ProductDetails.OneTimePurchaseOfferDetails offer = product.getOneTimePurchaseOfferDetails();
            if (offer != null) price(value, offer.getFormattedPrice(), offer.getPriceAmountMicros(), offer.getPriceCurrencyCode());
            return value;
        }
        // The base plan is the offer without an offer ID; its last phase is the price that the subscription renews at.
        JSONArray offers = new JSONArray();
        List<ProductDetails.SubscriptionOfferDetails> details = product.getSubscriptionOfferDetails();
        boolean priced = false;
        for (ProductDetails.SubscriptionOfferDetails offer : details == null ? Collections.<ProductDetails.SubscriptionOfferDetails>emptyList() : details) {
            List<ProductDetails.PricingPhase> phases = offer.getPricingPhases().getPricingPhaseList();
            if (phases.isEmpty()) continue;
            ProductDetails.PricingPhase first = phases.get(0), last = phases.get(phases.size() - 1);
            if (offer.getOfferId() == null && !priced) {
                price(value, last.getFormattedPrice(), last.getPriceAmountMicros(), last.getPriceCurrencyCode());
                value.put("period", last.getBillingPeriod());
                priced = true;
            }
            offers.put(new JSONObject().put("id", offer.getOfferToken()).put("displayPrice", first.getFormattedPrice()).put("period", first.getBillingPeriod()).put("introductory", offer.getOfferId() != null));
        }
        return value.put("offers", offers);
    }

    private static void price(JSONObject value, String display, long micros, String currency) throws Exception {
        value.put("displayPrice", display).put("price", java.math.BigDecimal.valueOf(micros, 6).stripTrailingZeros().toPlainString()).put("currency", currency);
    }

    // The flow of Google Play needs the activity that the user sees; its answer comes to the listener of the client.
    private static void purchase(BillingClient billing, JSONObject input, JSONObject output) throws Exception {
        String type = input.getString("type");
        List<ProductDetails> details = query(billing, Collections.singletonList(QueryProductDetailsParams.Product.newBuilder().setProductId(input.getString("id")).setProductType(googleType(type)).build()), output);
        if (details == null) return;
        if (details.isEmpty()) { output.put("status", "NotFound").put("code", "unknown-product"); return; }
        ProductDetails product = details.get(0);
        BillingFlowParams.ProductDetailsParams.Builder item = BillingFlowParams.ProductDetailsParams.newBuilder().setProductDetails(product);
        if ("Subscription".equals(type)) {
            String offer = input.optString("offer", "");
            List<ProductDetails.SubscriptionOfferDetails> offers = product.getSubscriptionOfferDetails();
            if (offer.isEmpty() && offers != null && !offers.isEmpty()) offer = offers.get(0).getOfferToken();
            if (offer.isEmpty()) { output.put("status", "NotFound").put("code", "unknown-product"); return; }
            item.setOfferToken(offer);
        }
        BillingFlowParams.Builder parameters = BillingFlowParams.newBuilder().setProductDetailsParamsList(Collections.singletonList(item.build()));
        String account = input.optString("accountToken", "");
        if (!account.isEmpty()) parameters.setObfuscatedAccountId(account);
        Activity activity = resumed.get();
        if (activity == null || activity.isFinishing()) { output.put("status", "Failed").put("code", "no-activity"); return; }
        AtomicReference<Object[]> answer = new AtomicReference<>();
        CountDownLatch answered = new CountDownLatch(1);
        synchronized (FLOW) {
            if (flow != null) { output.put("status", "Conflict").put("code", "busy"); return; }
            flow = answer;
            flowAnswered = answered;
        }
        try {
            AtomicReference<BillingResult> launched = new AtomicReference<>();
            CountDownLatch started = new CountDownLatch(1);
            activity.runOnUiThread(() -> { launched.set(billing.launchBillingFlow(activity, parameters.build())); started.countDown(); });
            started.await();
            if (launched.get().getResponseCode() != BillingClient.BillingResponseCode.OK) { failure(launched.get(), output); return; }
            if (!answered.await(5, TimeUnit.MINUTES)) { output.put("status", "Canceled"); return; }
            BillingResult result = (BillingResult) answer.get()[0];
            @SuppressWarnings("unchecked") List<Purchase> purchases = (List<Purchase>) answer.get()[1];
            if (result.getResponseCode() != BillingClient.BillingResponseCode.OK) { failure(result, output); return; }
            for (Purchase purchase : purchases == null ? Collections.<Purchase>emptyList() : purchases) {
                if (purchase.getProducts().contains(product.getProductId())) { output.put("status", "Success").put("purchase", purchase(purchase)); return; }
            }
            output.put("status", "Failed");
        } finally {
            synchronized (FLOW) { flow = null; flowAnswered = null; }
        }
    }

    private static void list(BillingClient billing, JSONObject output) throws Exception {
        JSONArray found = new JSONArray();
        for (String type : new String[] { BillingClient.ProductType.INAPP, BillingClient.ProductType.SUBS }) {
            CountDownLatch answered = new CountDownLatch(1);
            AtomicReference<Object[]> result = new AtomicReference<>();
            billing.queryPurchasesAsync(QueryPurchasesParams.newBuilder().setProductType(type).build(), (value, purchases) -> { result.set(new Object[] { value, purchases }); answered.countDown(); });
            if (!answered.await(60, TimeUnit.SECONDS)) { output.put("status", "Failed").put("code", "timeout"); return; }
            BillingResult value = (BillingResult) result.get()[0];
            if (value.getResponseCode() != BillingClient.BillingResponseCode.OK) { failure(value, output); return; }
            @SuppressWarnings("unchecked") List<Purchase> purchases = (List<Purchase>) result.get()[1];
            for (Purchase purchase : purchases) found.put(purchase(purchase));
        }
        output.put("status", "Success").put("purchases", found);
    }

    // Consuming acknowledges as well; a purchase that is not acknowledged within three days is refunded by Google Play.
    private static void finish(BillingClient billing, JSONObject input, JSONObject output) throws Exception {
        String token = input.getString("token");
        CountDownLatch answered = new CountDownLatch(1);
        AtomicReference<BillingResult> result = new AtomicReference<>();
        if (input.optBoolean("consume")) billing.consumeAsync(ConsumeParams.newBuilder().setPurchaseToken(token).build(), (value, consumed) -> { result.set(value); answered.countDown(); });
        else billing.acknowledgePurchase(AcknowledgePurchaseParams.newBuilder().setPurchaseToken(token).build(), value -> { result.set(value); answered.countDown(); });
        if (!answered.await(60, TimeUnit.SECONDS)) { output.put("status", "Failed").put("code", "timeout"); return; }
        if (result.get().getResponseCode() == BillingClient.BillingResponseCode.OK) output.put("status", "Success");
        else failure(result.get(), output);
    }

    private static JSONObject purchase(Purchase purchase) throws Exception {
        int state = purchase.getPurchaseState();
        JSONObject value = new JSONObject().put("productId", purchase.getProducts().isEmpty() ? "" : purchase.getProducts().get(0)).put("store", "GooglePlay")
            .put("state", state == Purchase.PurchaseState.PENDING ? "Pending" : "Purchased")
            .put("proof", purchase.getPurchaseToken()).put("purchaseDate", purchase.getPurchaseTime()).put("quantity", purchase.getQuantity())
            .put("finished", purchase.isAcknowledged()).put("signedData", purchase.getOriginalJson()).put("signature", purchase.getSignature());
        if (purchase.getOrderId() != null) value.put("transactionId", purchase.getOrderId());
        if (purchase.getAccountIdentifiers() != null && purchase.getAccountIdentifiers().getObfuscatedAccountId() != null) value.put("accountToken", purchase.getAccountIdentifiers().getObfuscatedAccountId());
        return value;
    }

    private static void failure(BillingResult result, JSONObject output) throws Exception {
        switch (result.getResponseCode()) {
            case BillingClient.BillingResponseCode.USER_CANCELED: output.put("status", "Canceled"); return;
            case BillingClient.BillingResponseCode.ITEM_ALREADY_OWNED: output.put("status", "Conflict").put("code", "owned"); return;
            case BillingClient.BillingResponseCode.ITEM_UNAVAILABLE: output.put("status", "NotFound").put("code", "unknown-product"); return;
            case BillingClient.BillingResponseCode.ITEM_NOT_OWNED: output.put("status", "NotFound"); return;
            case BillingClient.BillingResponseCode.BILLING_UNAVAILABLE: output.put("status", "Unsupported").put("code", "not-configured"); return;
            case BillingClient.BillingResponseCode.NETWORK_ERROR:
            case BillingClient.BillingResponseCode.SERVICE_UNAVAILABLE: output.put("status", "Failed").put("code", "network"); return;
            default: output.put("status", "Failed").put("code", "billing-" + result.getResponseCode());
        }
    }

    private static String googleType(String type) { return "Subscription".equals(type) ? BillingClient.ProductType.SUBS : BillingClient.ProductType.INAPP; }
}
