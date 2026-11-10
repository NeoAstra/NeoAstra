package com.neoastra.purchases.amazon;

import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;

/** Checks the decisions of the Amazon purchases service on a plain JVM; AndroidJavaTests compiles and runs it. */
public final class NeoAstraAmazonPurchasesLogicCheck {
    public static void main(String[] arguments) throws Exception {
        // The Appstore sells to its own installations and to the sandbox of the SDK, which only a debuggable build with the
        // property set enters unless the SDK tells its mode.
        expect(true, NeoAstraAmazonPurchasesLogic.available("com.amazon.venezia", false), "installed by the Appstore");
        expect(false, NeoAstraAmazonPurchasesLogic.available("com.android.vending", false), "installed by Google Play");
        expect(false, NeoAstraAmazonPurchasesLogic.available(null, false), "installed by adb");
        expect(true, NeoAstraAmazonPurchasesLogic.available(null, true), "sandbox");
        expect(true, NeoAstraAmazonPurchasesLogic.sandbox(null, true, "debug"), "debuggable with the property");
        expect(true, NeoAstraAmazonPurchasesLogic.sandbox("UNKNOWN", true, "debug"), "mode unknown without a license check");
        expect(false, NeoAstraAmazonPurchasesLogic.sandbox(null, false, "debug"), "release build with the property");
        expect(false, NeoAstraAmazonPurchasesLogic.sandbox(null, true, "none"), "property reset");
        expect(false, NeoAstraAmazonPurchasesLogic.sandbox(null, true, null), "property missing");
        expect(true, NeoAstraAmazonPurchasesLogic.sandbox("SANDBOX", false, null), "the SDK says sandbox");
        expect(false, NeoAstraAmazonPurchasesLogic.sandbox("PRODUCTION", true, "debug"), "the SDK says production");

        expect("P1M", NeoAstraAmazonPurchasesLogic.period("Monthly"), "monthly");
        expect("P1W", NeoAstraAmazonPurchasesLogic.period("WEEKLY"), "weekly in capitals");
        expect("P1Y", NeoAstraAmazonPurchasesLogic.period("Annually"), "annually");
        // The Appstore in production answers the short forms (a Fire HD 10 under Live App Testing: Monthly, Annual, a Weekly trial).
        expect("P1Y", NeoAstraAmazonPurchasesLogic.period("Annual"), "annual of the Appstore");
        expect("P6M", NeoAstraAmazonPurchasesLogic.period("SemiAnnual"), "semiannual of the Appstore");
        expect("P6M", NeoAstraAmazonPurchasesLogic.period("SemiAnnually"), "semiannually of the console");
        expect("P2W", NeoAstraAmazonPurchasesLogic.period("BiWeekly"), "biweekly");
        expect("P2M", NeoAstraAmazonPurchasesLogic.period("BiMonthly"), "bimonthly");
        expect("P3M", NeoAstraAmazonPurchasesLogic.period("Quarterly"), "quarterly");
        expect(null, NeoAstraAmazonPurchasesLogic.period("Daily"), "unknown period");
        expect(null, NeoAstraAmazonPurchasesLogic.period(null), "no period");

        // An answer that comes before its request is registered waits for it.
        NeoAstraAmazonPurchasesLogic.Correlation<String> answers = new NeoAstraAmazonPurchasesLogic.Correlation<>();
        answers.answer("early", "first");
        expect("first", answers.await(answers.register("early"), 1, TimeUnit.SECONDS), "answer before the request");
        NeoAstraAmazonPurchasesLogic.Waiter<String> later = answers.register("later");
        answers.answer("later", "second");
        expect("second", answers.await(later, 1, TimeUnit.SECONDS), "answer after the request");
        // A request that is not answered in time gives up its place, and its late answer does not stay behind.
        NeoAstraAmazonPurchasesLogic.Waiter<String> lost = answers.register("lost");
        expect(null, answers.await(lost, 10, TimeUnit.MILLISECONDS), "timeout");
        answers.answer("lost", "too late");
        expect(0, answers.size(), "nothing left after the late answer");
        // A purchase that the user completes after its request gave up is not lost: its late answer goes to the handler of late
        // answers, which reports it as an update, also after an interrupted wait; an answer without any request does not.
        List<String> late = new ArrayList<>();
        NeoAstraAmazonPurchasesLogic.Correlation<String> purchases = new NeoAstraAmazonPurchasesLogic.Correlation<>(256, late::add);
        NeoAstraAmazonPurchasesLogic.Waiter<String> slow = purchases.register("slow");
        expect(null, purchases.await(slow, 10, TimeUnit.MILLISECONDS), "the purchase sheet stays open past the timeout");
        purchases.answer("slow", "bought later");
        expect("[bought later]", late.toString(), "the late answer of a purchase comes as an update");
        purchases.answer("unasked", "no request");
        expect("[bought later]", late.toString(), "an answer without a request is not an update");
        NeoAstraAmazonPurchasesLogic.Waiter<String> cut = purchases.register("cut");
        Thread.currentThread().interrupt();
        try { purchases.await(cut, 1, TimeUnit.SECONDS); } catch (InterruptedException exception) { }
        purchases.answer("cut", "bought after an interruption");
        expect("[bought later, bought after an interruption]", late.toString(), "the late answer after an interrupted wait comes as an update");
        answers.answer(null, "nobody");
        expect(0, answers.size(), "an answer without an ID is dropped");

        // Answers that never come, and waits that are interrupted, leave no more than the limit behind.
        NeoAstraAmazonPurchasesLogic.Correlation<String> bounded = new NeoAstraAmazonPurchasesLogic.Correlation<>(8);
        for (int index = 0; index < 100; index++) expect(null, bounded.await(bounded.register("never" + index), 1, TimeUnit.MILLISECONDS), "never answered " + index);
        expect(true, bounded.size() <= 8, "bounded after answers that never come: " + bounded.size());
        for (int index = 0; index < 100; index++) bounded.answer("orphan" + index, "orphan");
        expect(true, bounded.size() <= 16, "bounded after answers without a request: " + bounded.size());
        NeoAstraAmazonPurchasesLogic.Correlation<String> interrupted = new NeoAstraAmazonPurchasesLogic.Correlation<>();
        NeoAstraAmazonPurchasesLogic.Waiter<String> waiting = interrupted.register("interrupted");
        Thread.currentThread().interrupt();
        boolean thrown = false;
        try { interrupted.await(waiting, 1, TimeUnit.SECONDS); } catch (InterruptedException exception) { thrown = true; }
        expect(true, thrown, "an interrupted wait throws");
        interrupted.answer("interrupted", "late");
        expect(0, interrupted.size(), "an interrupted wait gives up its place");

        // Requests and answers from several threads in any order all meet. A few threads share the pairs, in a shuffled order, so
        // that a slow machine runs the check in time; the limit holds every answer that comes before its request.
        int count = 2000;
        NeoAstraAmazonPurchasesLogic.Correlation<Integer> race = new NeoAstraAmazonPurchasesLogic.Correlation<>(count);
        List<Runnable> steps = new ArrayList<>();
        AtomicInteger matched = new AtomicInteger();
        List<NeoAstraAmazonPurchasesLogic.Waiter<Integer>> waiters = java.util.Collections.synchronizedList(new ArrayList<>());
        for (int index = 0; index < count; index++) {
            final int value = index;
            steps.add(() -> race.answer("r" + value, value));
            steps.add(() -> waiters.add(race.register("r" + value)));
        }
        java.util.Collections.shuffle(steps, new java.util.Random(42));
        java.util.concurrent.ConcurrentLinkedQueue<Runnable> queue = new java.util.concurrent.ConcurrentLinkedQueue<>(steps);
        CountDownLatch go = new CountDownLatch(1);
        List<Thread> threads = new ArrayList<>();
        for (int index = 0; index < 8; index++) threads.add(new Thread(() -> { await(go); for (Runnable step; (step = queue.poll()) != null; ) step.run(); }));
        for (Thread thread : threads) thread.start();
        go.countDown();
        for (Thread thread : threads) thread.join();
        for (NeoAstraAmazonPurchasesLogic.Waiter<Integer> waiter : waiters) {
            Integer answer = race.await(waiter, 5, TimeUnit.SECONDS);
            if (answer != null && ("r" + answer).equals(waiter.id)) matched.incrementAndGet();
        }
        expect(count, matched.get(), "every answer meets its request");
        expect(0, race.size(), "nothing left after the race");

        // Each state of a receipt is reported once: a cancellation or a change of term is a new state.
        NeoAstraAmazonPurchasesLogic.Updates<String> updates = new NeoAstraAmazonPurchasesLogic.Updates<>(2, update -> update);
        String bought = NeoAstraAmazonPurchasesLogic.state(false, "term", false, null);
        String canceled = NeoAstraAmazonPurchasesLogic.state(true, "term", false, null);
        String deferred = NeoAstraAmazonPurchasesLogic.state(false, "term", true, "other");
        expect(true, updates.report("a", bought), "first state");
        expect(false, updates.report("a", bought), "same state again");
        expect(true, updates.report("a", deferred), "change of term");
        updates.forget("a");
        expect(true, updates.report("a", deferred), "a fulfilled receipt is reported again when it comes back");
        expect(true, updates.report("a", canceled), "cancellation of a reported receipt");
        // A canceled receipt stays canceled: an older answer that still holds it active, which a sync or a late answer can
        // bring after the cancellation, is not reported, neither before nor after the receipt is fulfilled.
        expect(false, updates.report("a", bought), "an older active state after the cancellation");
        updates.forget("a");
        expect(false, updates.report("a", deferred), "an older active state after the canceled receipt is fulfilled");
        // The queue keeps the latest updates; a dropped one is reported again when the Appstore does.
        expect(true, updates.report("b", bought), "receipt b");
        expect(true, updates.report("c", bought), "receipt c");
        updates.post("a");
        updates.post("b");
        updates.post("c");
        expect(true, updates.report("a", canceled), "the dropped receipt is reported again");
        expect(false, updates.report("b", bought), "a queued receipt is not reported twice");
        expect("b", updates.next(1, TimeUnit.SECONDS), "the queue keeps the latest");
        expect("c", updates.next(1, TimeUnit.SECONDS), "in order");
        expect(null, updates.next(10, TimeUnit.MILLISECONDS), "empty");
        // Reporting and queueing are one step, so a cancellation is never followed by the older state that another thread had
        // already marked: the sync worker and the main thread publish the same receipt at once, in any order.
        for (int round = 0; round < 200; round++) {
            NeoAstraAmazonPurchasesLogic.Updates<String> raced = new NeoAstraAmazonPurchasesLogic.Updates<>(8, update -> "r");
            CountDownLatch start = new CountDownLatch(1);
            Thread worker = new Thread(() -> { await(start); raced.publish("r", bought, "Purchased"); });
            worker.start();
            start.countDown();
            raced.publish("r", canceled, "Revoked");
            worker.join();
            List<String> seen = new ArrayList<>();
            for (String next; (next = raced.next(0, TimeUnit.MILLISECONDS)) != null; ) seen.add(next);
            expect("Revoked", seen.get(seen.size() - 1), "the cancellation is the last state, round " + round + ": " + seen);
        }

        System.out.println("NeoAstraAmazonPurchasesLogic: ok");
    }

    private static void await(CountDownLatch latch) {
        try { latch.await(); } catch (InterruptedException exception) { Thread.currentThread().interrupt(); }
    }

    private static void expect(Object expected, Object actual, String message) {
        if (!java.util.Objects.equals(expected, actual)) {
            System.out.println("FAILED: " + message + ": expected " + expected + ", got " + actual);
            System.exit(1);
        }
    }
}
