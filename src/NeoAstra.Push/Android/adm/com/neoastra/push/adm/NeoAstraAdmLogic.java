package com.neoastra.push.adm;

import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

/** The decisions of the Amazon Device Messaging service without Android or ADM types, so a plain JVM checks them. */
final class NeoAstraAdmLogic {
    private NeoAstraAdmLogic() { }

    /**
     * The data that the server sent with a message, from the extras of the intent of ADM: its string values, without the MD5 and
     * the priority that ADM adds, which the message ID and nothing else carry.
     */
    static Map<String, String> data(Map<String, ?> extras) {
        Map<String, String> data = new HashMap<>();
        if (extras == null) return data;
        for (Map.Entry<String, ?> entry : extras.entrySet()) {
            if (entry.getValue() instanceof String && !entry.getKey().startsWith("adm_message_")) data.put(entry.getKey(), (String) entry.getValue());
        }
        return data;
    }

    /**
     * The status and code that a registration error of ADM answers: an application without the API key of its Amazon app, or with
     * the key of another package or signature, is not configured.
     */
    static String[] failure(String error) {
        if ("AUTHENTICATION_FAILED".equals(error) || "INVALID_SENDER".equals(error)) return new String[] { "Unsupported", "not-configured" };
        return new String[] { "Failed", "adm-" + error };
    }

    /**
     * The registration of ADM, which answers each start once through the handler of the application, without telling which start
     * it answers: the callers that register while a registration is under way join it. A caller that does not get the answer in
     * time leaves it under way, and the next caller starts ADM again in the same registration; an error while a later start is
     * outstanding answers an earlier start and is passed over, while a registration ID answers the registration whatever start
     * it answers.
     */
    static final class Registration {
        private final Object lock = new Object();
        private Attempt current;

        /** The attempt to wait for, and whether this caller starts ADM. */
        Joined join() {
            synchronized (lock) {
                if (current == null) current = new Attempt();
                else if (!current.stale) return new Joined(current, false);
                current.stale = false;
                current.outstanding++;
                return new Joined(current, true);
            }
        }

        /** An answer of ADM: the registration ID, or the error of one start. */
        void answer(String id, String error) {
            synchronized (lock) {
                Attempt attempt = current;
                if (attempt == null) return;
                attempt.outstanding = Math.max(0, attempt.outstanding - 1);
                if (id == null && attempt.outstanding > 0) return;
                current = null;
                attempt.complete(id, error);
            }
        }

        /** Waits for the answer; false when ADM does not answer in time, after which the next caller starts ADM again. */
        boolean await(Attempt attempt, long timeout, TimeUnit unit) throws InterruptedException {
            if (attempt.answered.await(timeout, unit)) return true;
            synchronized (lock) {
                if (attempt.answered.getCount() == 0) return true;
                attempt.stale = true;
                return false;
            }
        }
    }

    static final class Joined {
        final Attempt attempt;
        final boolean start;

        private Joined(Attempt attempt, boolean start) { this.attempt = attempt; this.start = start; }
    }

    static final class Attempt {
        private final CountDownLatch answered = new CountDownLatch(1);
        private int outstanding;
        private boolean stale;
        private final AtomicReference<String> id = new AtomicReference<>();
        private final AtomicReference<String> error = new AtomicReference<>();

        String id() { return id.get(); }

        String error() { return error.get(); }

        private void complete(String value, String failure) { id.set(value); error.set(failure); answered.countDown(); }
    }
}
