package com.neoastra.purchases.amazon;

import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;
import java.util.function.Consumer;
import java.util.function.Function;

/** The decisions of the Amazon purchases service without Android or Appstore types, so a plain JVM checks them. */
final class NeoAstraAmazonPurchasesLogic {
    static final String APPSTORE = "com.amazon.venezia";

    private NeoAstraAmazonPurchasesLogic() { }

    /**
     * Whether the Appstore SDK runs in its sandbox, which the Amazon App Tester answers for: as the SDK tells it after a license
     * check, and otherwise as the SDK decides, for a debuggable build on a device whose debug.amazon.sandboxmode is debug.
     */
    static boolean sandbox(String sdkMode, boolean debuggable, String sandboxProperty) {
        if ("SANDBOX".equalsIgnoreCase(sdkMode)) return true;
        if ("PRODUCTION".equalsIgnoreCase(sdkMode)) return false;
        return debuggable && "debug".equals(sandboxProperty);
    }

    /** Whether the Appstore sells to this installation: one that the Amazon Appstore made, or one in the sandbox of the SDK. */
    static boolean available(String installer, boolean sandbox) { return APPSTORE.equals(installer) || sandbox; }

    /**
     * The period of a subscription of the Appstore as an ISO 8601 duration, or null for one that it does not name. The Developer
     * Console names the terms SemiAnnually and Annually, while the Appstore answers SemiAnnual and Annual: both are taken.
     */
    static String period(String period) {
        if (period == null) return null;
        switch (period.toLowerCase(java.util.Locale.ROOT)) {
            case "weekly": return "P1W";
            case "biweekly": return "P2W";
            case "monthly": return "P1M";
            case "bimonthly": return "P2M";
            case "quarterly": return "P3M";
            case "semiannual":
            case "semiannually": return "P6M";
            case "annual":
            case "annually": return "P1Y";
            default: return null;
        }
    }

    /** The state of a receipt that its report depends on: a cancellation or a change of term is a new state. */
    static String state(boolean canceled, String termSku, boolean deferred, String deferredSku) {
        return canceled + "|" + termSku + "|" + deferred + "|" + deferredSku;
    }

    private static boolean canceled(String state) { return state.startsWith("true|"); }

    /**
     * The answers of the Appstore to their requests. The SDK gives the identifier of a request only once it is sent and answers on
     * the main thread, so an answer can come before its request is registered: it waits for it. A request that is not answered in
     * time, or whose wait is interrupted, gives up its place, and its answer goes to the handler of late answers when it comes,
     * such as a purchase that the user completes after the request gave up. Answers without a request and requests given up are
     * remembered only up to a limit, so that answers that never come leave nothing behind for good.
     */
    static final class Correlation<T> {
        private final Object lock = new Object();
        private final Map<String, Waiter<T>> pending = new HashMap<>();
        private final Map<String, T> early;
        private final Map<String, Boolean> abandoned;
        private final Consumer<T> late;

        Correlation() { this(256); }

        Correlation(int limit) { this(limit, null); }

        Correlation(int limit, Consumer<T> late) {
            early = bounded(limit);
            abandoned = bounded(limit);
            this.late = late;
        }

        private static <V> Map<String, V> bounded(int limit) {
            return new LinkedHashMap<String, V>() {
                @Override protected boolean removeEldestEntry(Map.Entry<String, V> eldest) { return size() > limit; }
            };
        }

        Waiter<T> register(String id) {
            Waiter<T> waiter = new Waiter<>(id);
            synchronized (lock) {
                T answer = early.remove(id);
                if (answer == null) pending.put(id, waiter);
                else waiter.complete(answer);
            }
            return waiter;
        }

        // A waiter is completed under the lock, so that its completion and its abandonment exclude each other.
        // The handler of late answers runs outside the lock.
        void answer(String id, T value) {
            if (id == null) return;
            synchronized (lock) {
                if (abandoned.remove(id) == null) {
                    Waiter<T> waiter = pending.remove(id);
                    if (waiter == null) early.put(id, value);
                    else waiter.complete(value);
                    return;
                }
            }
            if (late != null) late.accept(value);
        }

        /** The answer, or null when the Appstore does not answer in time. */
        T await(Waiter<T> waiter, long timeout, TimeUnit unit) throws InterruptedException {
            boolean answered;
            try { answered = waiter.answered.await(timeout, unit); }
            catch (InterruptedException exception) { abandon(waiter); throw exception; }
            return answered ? waiter.answer.get() : abandon(waiter);
        }

        private T abandon(Waiter<T> waiter) {
            synchronized (lock) {
                // The answer may have come between the wait and the lock.
                if (waiter.answered.getCount() == 0) return waiter.answer.get();
                pending.remove(waiter.id);
                abandoned.put(waiter.id, Boolean.TRUE);
                return null;
            }
        }

        int size() { synchronized (lock) { return pending.size() + early.size() + abandoned.size(); } }
    }

    static final class Waiter<T> {
        final String id;
        private final CountDownLatch answered = new CountDownLatch(1);
        private final AtomicReference<T> answer = new AtomicReference<>();

        Waiter(String id) { this.id = id; }

        private void complete(T value) { answer.set(value); answered.countDown(); }
    }

    /**
     * The receipts that the Appstore reports outside a request, which it reports again each time the application comes back while
     * they are not fulfilled: each state of a receipt is reported once in this process, and the queue keeps the latest when nobody
     * takes them; a receipt that the queue drops is reported again when the Appstore does. A canceled receipt stays canceled, so
     * an older answer that still holds it active, from a sync or a late answer that another thread handles, is not reported after
     * the cancellation; the receipts canceled in this process are remembered up to a limit, also once they are fulfilled.
     */
    static final class Updates<T> {
        private final Object lock = new Object();
        private final Map<String, String> reported = new HashMap<>();
        private final Map<String, Boolean> canceled = new LinkedHashMap<String, Boolean>() {
            @Override protected boolean removeEldestEntry(Map.Entry<String, Boolean> eldest) { return size() > 1024; }
        };
        private final LinkedBlockingQueue<T> queue;
        private final Function<T, String> receiptOf;

        Updates(int capacity, Function<T, String> receiptOf) {
            queue = new LinkedBlockingQueue<>(capacity);
            this.receiptOf = receiptOf;
        }

        /** Whether this state of the receipt is new to this process, which reports it from now on. */
        boolean report(String receipt, String state) {
            synchronized (lock) {
                if (canceled.containsKey(receipt) && !canceled(state)) return false;
                if (canceled(state)) canceled.put(receipt, Boolean.TRUE);
                return !state.equals(reported.put(receipt, state));
            }
        }

        /** Reports and queues a new state of the receipt in one step, so that no other thread comes between; false when it is not new. */
        boolean publish(String receipt, String state, T update) {
            synchronized (lock) {
                if (!report(receipt, state)) return false;
                post(update);
                return true;
            }
        }

        /** Forgets a receipt that is fulfilled. */
        void forget(String receipt) { synchronized (lock) { reported.remove(receipt); } }

        void post(T update) {
            synchronized (lock) {
                while (!queue.offer(update)) {
                    T dropped = queue.poll();
                    if (dropped != null) reported.remove(receiptOf.apply(dropped));
                }
            }
        }

        T next(long timeout, TimeUnit unit) throws InterruptedException { return queue.poll(timeout, unit); }
    }
}
