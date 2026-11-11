package com.neoastra.purchases.amazon;

import android.content.Context;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;

import com.amazon.device.drm.LicensingService;
import com.amazon.device.iap.ModifySubscriptionListener;
import com.amazon.device.iap.PurchasingListener;
import com.amazon.device.iap.PurchasingService;
import com.amazon.device.iap.model.FulfillmentResult;
import com.amazon.device.iap.model.ModifySubscriptionResponse;
import com.amazon.device.iap.model.Product;
import com.amazon.device.iap.model.ProductDataResponse;
import com.amazon.device.iap.model.ProductType;
import com.amazon.device.iap.model.ProrationMode;
import com.amazon.device.iap.model.PurchaseResponse;
import com.amazon.device.iap.model.PurchaseUpdatesResponse;
import com.amazon.device.iap.model.Receipt;
import com.amazon.device.iap.model.RequestId;
import com.amazon.device.iap.model.UserData;
import com.amazon.device.iap.model.UserDataResponse;
import com.neoastra.backend.NeoAstraServices;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.HashSet;
import java.util.Set;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.locks.ReentrantLock;

/**
 * The Amazon purchases service of the purchases plugin of NeoAstra, which the platform services reach as
 * {@code neoastra.purchases.amazon}: the products, purchases, and subscriptions of the Amazon Appstore through the Appstore SDK.
 * It answers in the form of the service of Google Play. A request blocks the calling thread, which is never the main thread,
 * until the Appstore or the user answers; the receipts that the Appstore reports outside a request, when the application comes
 * back to the foreground, wait in a queue that the managed side takes one at a time.
 */
public final class NeoAstraAmazonPurchases implements NeoAstraServices.Service {
    static final String NAME = "neoastra.purchases.amazon";
    // The answers of the Appstore come to one listener on the main thread, each with the identifier of its request; the answer
    // of a purchase whose request gave up, while the user still had the sheet open, comes as an update.
    private static final NeoAstraAmazonPurchasesLogic.Correlation<Object> ANSWERS = new NeoAstraAmazonPurchasesLogic.Correlation<>(256, NeoAstraAmazonPurchases::late);
    // The Appstore reports a receipt that is not fulfilled again each time the application comes back, also the one that a
    // purchase has just answered: this process reports each state of a receipt once, until it is fulfilled.
    private static final NeoAstraAmazonPurchasesLogic.Updates<JSONObject> UPDATES = new NeoAstraAmazonPurchasesLogic.Updates<>(64, update -> update.optString("proof"));
    // getPurchaseUpdates pages through the history with one cursor of the SDK: whole sequences run one at a time, and the
    // returns of the application to the foreground that come while one runs make one more.
    private static final ReentrantLock PAGING = new ReentrantLock();
    private static final AtomicBoolean SYNC_QUEUED = new AtomicBoolean();
    private static final ExecutorService SYNC = Executors.newSingleThreadExecutor(runnable -> { Thread thread = new Thread(runnable, "NeoAstra Amazon updates"); thread.setDaemon(true); return thread; });
    private static final Object REGISTRATION = new Object();
    private static volatile boolean registered;
    private static volatile String userId;

    @Override public void call(Context context, JSONObject input, JSONObject output) throws Exception {
        String operation = input.getString("operation");
        if ("available".equals(operation)) { available(context, output); return; }
        if (Looper.myLooper() == Looper.getMainLooper()) throw new IllegalStateException("A purchase cannot wait on the main thread.");
        // The managed side starts by waiting for updates, which need the listener of the SDK as much as a request does.
        register(context);
        if ("next".equals(operation)) {
            JSONObject update = UPDATES.next(Math.max(0, Math.min(30_000, input.optLong("timeout", 20_000))), TimeUnit.MILLISECONDS);
            if (update == null) output.put("status", "NotFound"); else output.put("status", "Success").put("purchase", update);
            return;
        }
        switch (operation) {
            case "products": products(input, output); return;
            case "purchase": purchase(input, output); return;
            case "list": list(output); return;
            case "finish": finish(input, output); return;
            default: output.put("status", "Unsupported");
        }
    }

    /**
     * Whether the Appstore sells to this installation: an application that the Amazon Appstore installed, or one in the sandbox
     * mode of the Appstore SDK, which the Amazon App Tester answers for ({@code adb shell setprop debug.amazon.sandboxmode debug}).
     */
    private static void available(Context context, JSONObject output) throws Exception {
        String installer = installer(context);
        String mode = mode();
        boolean sandbox = sandbox(context, mode);
        output.put("status", "Success").put("available", NeoAstraAmazonPurchasesLogic.available(installer, sandbox)).put("sandbox", sandbox).putOpt("installer", installer).putOpt("mode", mode);
    }

    static boolean available(Context context) { return NeoAstraAmazonPurchasesLogic.available(installer(context), sandbox(context, mode())); }

    private static String mode() {
        try { return LicensingService.getAppstoreSDKMode(); } catch (RuntimeException error) { return null; }
    }

    private static boolean sandbox(Context context, String mode) {
        boolean debuggable = (context.getApplicationInfo().flags & android.content.pm.ApplicationInfo.FLAG_DEBUGGABLE) != 0;
        return NeoAstraAmazonPurchasesLogic.sandbox(mode, debuggable, debuggable ? sandboxProperty() : null);
    }

    /**
     * Registers the listener when the process starts, on the main thread, for an installation that the Appstore sells to. The
     * registration starts the SDK, which follows the activities from then on: the purchase sheet of the Appstore waits for an
     * activity that the SDK saw resume, so an SDK started while the activity of the application already runs shows it only
     * after the activity resumes again. A failure leaves the registration to the first request.
     */
    static void start(Context context) {
        try { if (available(context)) register(context); }
        catch (InterruptedException error) { Thread.currentThread().interrupt(); }
        catch (RuntimeException error) { }
    }

    // The property that the Amazon App Tester documents, read as the SDK reads it.
    private static String sandboxProperty() {
        try {
            Object value = Class.forName("android.os.SystemProperties").getMethod("get", String.class).invoke(null, "debug.amazon.sandboxmode");
            return value instanceof String ? (String) value : null;
        } catch (ReflectiveOperationException | RuntimeException error) {
            return null;
        }
    }

    private static String installer(Context context) {
        try {
            PackageManager packages = context.getPackageManager();
            if (Build.VERSION.SDK_INT >= 30) return packages.getInstallSourceInfo(context.getPackageName()).getInstallingPackageName();
            return packages.getInstallerPackageName(context.getPackageName());
        } catch (Exception error) {
            return null;
        }
    }

    // The Appstore SDK asks for its listener on the main thread, once per process, with the pending purchases of children whose
    // parents approve them later. The initializer registers it when the process starts, before any activity; a request registers
    // it only when that failed.
    private static void register(Context context) throws InterruptedException {
        if (registered) return;
        synchronized (REGISTRATION) {
            if (registered) return;
            Context application = context.getApplicationContext();
            Runnable registration = () -> {
                PurchasingService.registerListener(application, LISTENER);
                PurchasingService.registerListener(application, MODIFY);
                PurchasingService.enablePendingPurchases();
            };
            if (Looper.myLooper() == Looper.getMainLooper()) registration.run();
            else {
                CountDownLatch done = new CountDownLatch(1);
                new Handler(Looper.getMainLooper()).post(() -> { try { registration.run(); } finally { done.countDown(); } });
                done.await(10, TimeUnit.SECONDS);
            }
            registered = true;
        }
        // What changed while the application did not run, such as an approval of a parent, comes once the listener is there.
        sync();
    }

    /** Asks the Appstore for the receipts that changed while the application was away, such as renewals and approvals. */
    static void resumed() { if (registered) sync(); }

    private static void sync() {
        if (!SYNC_QUEUED.compareAndSet(false, true)) return;
        SYNC.execute(() -> {
            SYNC_QUEUED.set(false);
            PAGING.lock();
            try {
                for (boolean more = true; more; ) {
                    Object value = await(request(PurchasingService.getPurchaseUpdates(false)), 60, null);
                    if (!(value instanceof PurchaseUpdatesResponse)) return;
                    PurchaseUpdatesResponse response = (PurchaseUpdatesResponse) value;
                    if (response.getRequestStatus() != PurchaseUpdatesResponse.RequestStatus.SUCCESSFUL) return;
                    for (Receipt receipt : response.getReceipts()) {
                        publish(receipt, response.getUserData());
                    }
                    more = response.hasMore();
                }
            } catch (Exception ignored) {
            } finally { PAGING.unlock(); }
        });
    }

    // Whether this state of the receipt is new to this process, which a request answers itself.
    private static boolean report(Receipt receipt) { return UPDATES.report(receipt.getReceiptId(), state(receipt)); }

    // Reports a new state of the receipt as an update, in one step with its dedupe.
    private static void publish(Receipt receipt, UserData user) throws Exception {
        UPDATES.publish(receipt.getReceiptId(), state(receipt), purchase(receipt, user, "Purchased"));
    }

    private static String state(Receipt receipt) {
        return NeoAstraAmazonPurchasesLogic.state(receipt.isCanceled(), receipt.getTermSku(), receipt.isDeferred(), receipt.getDeferredSku());
    }

    private static NeoAstraAmazonPurchasesLogic.Waiter<Object> request(RequestId id) { return ANSWERS.register(id.toString()); }

    private static void answer(RequestId id, Object value) { if (id != null) ANSWERS.answer(id.toString(), value); }

    private static final PurchasingListener LISTENER = new PurchasingListener() {
        @Override public void onUserDataResponse(UserDataResponse response) { answer(response.getRequestId(), response); }
        @Override public void onProductDataResponse(ProductDataResponse response) { answer(response.getRequestId(), response); }
        @Override public void onPurchaseResponse(PurchaseResponse response) { answer(response.getRequestId(), response); }
        @Override public void onPurchaseUpdatesResponse(PurchaseUpdatesResponse response) { answer(response.getRequestId(), response); }
    };

    private static final ModifySubscriptionListener MODIFY = response -> answer(response.getRequestId(), response);

    // A purchase or a change of term that the Appstore answers after its request gave up: its receipts come with PurchaseUpdated,
    // once per state as any other update, so that the application delivers and fulfills them.
    private static void late(Object value) {
        try {
            if (value instanceof PurchaseResponse) {
                PurchaseResponse response = (PurchaseResponse) value;
                remember(response.getUserData());
                Receipt receipt = response.getReceipt();
                if (receipt == null) return;
                if (response.getRequestStatus() == PurchaseResponse.RequestStatus.SUCCESSFUL) publish(receipt, response.getUserData());
            } else if (value instanceof ModifySubscriptionResponse) {
                ModifySubscriptionResponse response = (ModifySubscriptionResponse) value;
                if (response.getRequestStatus() != ModifySubscriptionResponse.RequestStatus.SUCCESSFUL) return;
                remember(response.getUserData());
                for (Receipt receipt : response.getReceipts()) publish(receipt, response.getUserData());
            }
        } catch (Exception ignored) {
        }
    }

    // A request that the Appstore does not answer in time gives up its place; a purchase answered later comes as an update.
    private static Object await(NeoAstraAmazonPurchasesLogic.Waiter<Object> waiter, int seconds, JSONObject output) throws Exception {
        Object value = ANSWERS.await(waiter, seconds, TimeUnit.SECONDS);
        if (value == null && output != null) output.put("status", "Failed").put("code", "timeout");
        return value;
    }

    private static void products(JSONObject input, JSONObject output) throws Exception {
        JSONArray requests = input.optJSONArray("products");
        Set<String> skus = new HashSet<>();
        for (int index = 0; requests != null && index < requests.length(); index++) skus.add(requests.getJSONObject(index).getString("id"));
        if (skus.isEmpty()) { output.put("status", "Success").put("products", new JSONArray()); return; }
        Object value = await(request(PurchasingService.getProductData(skus)), 60, output);
        if (value == null) return;
        ProductDataResponse response = (ProductDataResponse) value;
        if (response.getRequestStatus() != ProductDataResponse.RequestStatus.SUCCESSFUL) { failure(response.getRequestStatus().name(), output); return; }
        JSONArray found = new JSONArray();
        for (Product product : response.getProductData().values()) found.put(product(product));
        output.put("status", "Success").put("products", found);
    }

    // The Appstore gives the price only as text in the currency of the marketplace of the user.
    private static JSONObject product(Product product) throws Exception {
        boolean subscription = product.getProductType() == ProductType.SUBSCRIPTION;
        JSONObject value = new JSONObject().put("id", product.getSku()).put("type", subscription ? "Subscription" : "InApp")
            .put("title", product.getTitle()).put("description", product.getDescription()).put("displayPrice", product.getPrice());
        if (subscription) {
            value.putOpt("period", NeoAstraAmazonPurchasesLogic.period(product.getSubscriptionPeriod()));
            String trial = NeoAstraAmazonPurchasesLogic.period(product.getFreeTrialPeriod());
            // A free trial is the introductory offer of the subscription; the Appstore applies it by itself when the user is eligible.
            if (trial != null) value.put("offers", new JSONArray().put(new JSONObject().put("id", product.getSku()).put("displayPrice", "").put("period", trial).put("introductory", true)));
        }
        return value;
    }

    // The Appstore shows its own purchase sheet. A replacement changes the term of a subscription that the user holds, at once or
    // at its next renewal.
    private static void purchase(JSONObject input, JSONObject output) throws Exception {
        String sku = input.getString("id");
        JSONObject replaced = input.optJSONObject("replaced");
        if (replaced != null) { modify(sku, replaced, output); return; }
        Object value = await(request(PurchasingService.purchase(sku)), 300, output);
        if (value == null) return;
        PurchaseResponse response = (PurchaseResponse) value;
        switch (response.getRequestStatus()) {
            case SUCCESSFUL:
                remember(response.getUserData());
                if (response.getReceipt() == null) { output.put("status", "Failed"); return; }
                report(response.getReceipt());
                output.put("status", "Success").put("purchase", purchase(response.getReceipt(), response.getUserData(), "Purchased"));
                return;
            case PENDING:
                remember(response.getUserData());
                // A pending purchase waits for a parent; its receipt, when there is one, is not to be delivered yet.
                Receipt receipt = response.getReceipt();
                JSONObject pending = receipt != null ? purchase(receipt, response.getUserData(), "Pending")
                    : new JSONObject().put("productId", sku).put("store", "Amazon").put("state", "Pending").put("proof", "").put("purchaseDate", System.currentTimeMillis()).putOpt("storeUserId", userId(response.getUserData()));
                output.put("status", "Success").put("purchase", pending);
                return;
            case ALREADY_PURCHASED: output.put("status", "Conflict").put("code", "owned"); return;
            case INVALID_SKU: output.put("status", "NotFound").put("code", "unknown-product"); return;
            case NOT_SUPPORTED: output.put("status", "Unsupported").put("code", "not-configured"); return;
            // The Appstore answers FAILED to a user who closes the sheet as to any other failure.
            default: output.put("status", "Canceled");
        }
    }

    private static void modify(String sku, JSONObject replaced, JSONObject output) throws Exception {
        ProrationMode mode = "Deferred".equals(replaced.optString("mode")) ? ProrationMode.DEFERRED : ProrationMode.IMMEDIATE;
        Object value = await(request(PurchasingService.modifySubscription(sku, mode)), 300, output);
        if (value == null) return;
        ModifySubscriptionResponse response = (ModifySubscriptionResponse) value;
        switch (response.getRequestStatus()) {
            case SUCCESSFUL:
                remember(response.getUserData());
                Receipt receipt = NeoAstraAmazonPurchasesLogic.modified(response.getReceipts(),
                    candidate -> sku.equals(candidate.getTermSku()) || sku.equals(candidate.getSku()) || sku.equals(candidate.getDeferredSku()),
                    update -> publish(update, response.getUserData()));
                if (receipt == null) { output.put("status", "Failed"); return; }
                report(receipt);
                output.put("status", "Success").put("purchase", purchase(receipt, response.getUserData(), "Purchased"));
                return;
            case INVALID_SKU: output.put("status", "NotFound").put("code", "unknown-product"); return;
            // A change of a subscription that the user does not hold.
            case INVALID_REQUEST: output.put("status", "NotFound"); return;
            case NOT_SUPPORTED: output.put("status", "Unsupported").put("code", "not-configured"); return;
            default: output.put("status", "Canceled");
        }
    }

    // The whole history of the user, of which the receipts that are not canceled are what the user holds.
    private static void list(JSONObject output) throws Exception {
        JSONArray found = new JSONArray();
        boolean reset = true;
        PAGING.lock();
        try {
            for (boolean more = true; more; reset = false) {
                Object value = await(request(PurchasingService.getPurchaseUpdates(reset)), 60, output);
                if (value == null) return;
                PurchaseUpdatesResponse response = (PurchaseUpdatesResponse) value;
                if (response.getRequestStatus() != PurchaseUpdatesResponse.RequestStatus.SUCCESSFUL) { failure(response.getRequestStatus().name(), output); return; }
                remember(response.getUserData());
                for (Receipt receipt : response.getReceipts()) if (!receipt.isCanceled()) found.put(purchase(receipt, response.getUserData(), "Purchased"));
                more = response.hasMore();
            }
        } finally { PAGING.unlock(); }
        output.put("status", "Success").put("purchases", found);
    }

    // The Appstore keeps returning a consumable, and a subscription or an entitlement of a pending purchase, until it is fulfilled;
    // it does not answer the fulfillment.
    private static void finish(JSONObject input, JSONObject output) throws Exception {
        String receipt = input.getString("token");
        if (receipt.isEmpty()) { output.put("status", "NotFound"); return; }
        PurchasingService.notifyFulfillment(receipt, FulfillmentResult.FULFILLED);
        UPDATES.forget(receipt);
        output.put("status", "Success");
    }

    private static JSONObject purchase(Receipt receipt, UserData user, String state) throws Exception {
        // The receipt of a subscription names its parent SKU and the term that the user bought; the term is the product sold.
        String product = receipt.getTermSku() != null && !receipt.getTermSku().isEmpty() ? receipt.getTermSku() : receipt.getSku();
        JSONObject value = new JSONObject().put("productId", product).put("store", "Amazon")
            .put("state", receipt.isCanceled() ? "Revoked" : state).put("proof", receipt.getReceiptId())
            .put("purchaseDate", receipt.getPurchaseDate() == null ? 0 : receipt.getPurchaseDate().getTime())
            .put("transactionId", receipt.getReceiptId()).putOpt("storeUserId", userId(user));
        if (receipt.isDeferred() && receipt.getDeferredSku() != null) value.put("renewalProductId", receipt.getDeferredSku());
        return value;
    }

    private static void remember(UserData user) { if (user != null && user.getUserId() != null) userId = user.getUserId(); }

    private static String userId(UserData user) { return user != null && user.getUserId() != null ? user.getUserId() : userId; }

    private static void failure(String status, JSONObject output) throws Exception {
        if ("NOT_SUPPORTED".equals(status)) output.put("status", "Unsupported").put("code", "not-configured");
        else output.put("status", "Failed").put("code", "appstore-" + status.toLowerCase(java.util.Locale.ROOT));
    }
}
