package com.neoastra.push.adm;

import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.TimeUnit;

/** Checks the decisions of the Amazon Device Messaging service on a plain JVM; AndroidJavaTests compiles and runs it. */
public final class NeoAstraAdmLogicCheck {
    public static void main(String[] arguments) throws Exception {
        // The data of a message is what the server sent, without the keys that ADM adds.
        Map<String, Object> extras = new HashMap<>();
        extras.put("title", "Hello");
        extras.put("body", "World");
        extras.put("adm_message_md5", "abc==");
        extras.put("adm_message_priority", "NORMAL");
        extras.put("count", 3);
        Map<String, String> data = NeoAstraAdmLogic.data(extras);
        expect(2, data.size(), "only the strings that the server sent");
        expect("Hello", data.get("title"), "title");
        expect(null, data.get("adm_message_md5"), "no MD5");
        expect(0, NeoAstraAdmLogic.data(null).size(), "no extras");

        // A key of another package or signature, or none, is a configuration of the application.
        expect("Unsupported", NeoAstraAdmLogic.failure("AUTHENTICATION_FAILED")[0], "authentication failed");
        expect("not-configured", NeoAstraAdmLogic.failure("INVALID_SENDER")[1], "invalid sender");
        expect("Failed", NeoAstraAdmLogic.failure("SERVICE_NOT_AVAILABLE")[0], "service not available");
        expect("adm-SERVICE_NOT_AVAILABLE", NeoAstraAdmLogic.failure("SERVICE_NOT_AVAILABLE")[1], "code of the error");

        // Callers that register while a registration is under way join it, and only the first one starts it.
        NeoAstraAdmLogic.Registration registration = new NeoAstraAdmLogic.Registration();
        NeoAstraAdmLogic.Joined first = registration.join();
        NeoAstraAdmLogic.Joined second = registration.join();
        expect(true, first.start, "the first caller starts");
        expect(false, second.start, "the second caller joins");
        expect(true, first.attempt == second.attempt, "one attempt");
        registration.answer("id-1", null);
        expect(true, registration.await(first.attempt, 1, TimeUnit.SECONDS), "first answered");
        expect(true, registration.await(second.attempt, 1, TimeUnit.SECONDS), "second answered");
        expect("id-1", second.attempt.id(), "both get the registration ID");

        // An error reaches every caller of the attempt; the next registration starts again.
        NeoAstraAdmLogic.Joined failing = registration.join();
        expect(true, failing.start, "a new attempt after an answer");
        registration.answer(null, "INVALID_SENDER");
        expect(true, registration.await(failing.attempt, 1, TimeUnit.SECONDS), "error answered");
        expect("INVALID_SENDER", failing.attempt.error(), "the error");

        // A caller that does not get the answer in time leaves the registration under way: the next caller starts ADM again in it,
        // the late error of the first start is passed over, and the registration ID of the second reaches both.
        NeoAstraAdmLogic.Joined silent = registration.join();
        expect(true, silent.start, "a new registration starts ADM");
        expect(false, registration.await(silent.attempt, 10, TimeUnit.MILLISECONDS), "timeout");
        NeoAstraAdmLogic.Joined retry = registration.join();
        expect(true, retry.start, "after a timeout the next caller starts ADM again");
        expect(true, silent.attempt == retry.attempt, "in the same registration");
        NeoAstraAdmLogic.Joined waiting = registration.join();
        expect(false, waiting.start, "a caller while the retry runs joins");
        registration.answer(null, "SERVICE_NOT_AVAILABLE");
        expect(false, registration.await(retry.attempt, 10, TimeUnit.MILLISECONDS), "the late error of the first start is passed over");
        expect(true, registration.join().attempt == retry.attempt, "still the same registration");
        registration.answer("id-2", null);
        expect("id-2", retry.attempt.id(), "the retry gets the registration ID");
        expect("id-2", silent.attempt.id(), "so does the caller that timed out");
        expect(null, retry.attempt.error(), "without the late error");
        // The late answer of a registration that nobody started again completes it.
        NeoAstraAdmLogic.Joined alone = registration.join();
        expect(false, registration.await(alone.attempt, 10, TimeUnit.MILLISECONDS), "timeout without a retry");
        registration.answer("id-late", null);
        expect("id-late", alone.attempt.id(), "the late registration ID");

        // An answer without an attempt, such as a new registration ID that ADM gives by itself, is harmless.
        registration.answer("id-3", null);
        expect(true, registration.join().start, "still a fresh attempt");

        System.out.println("NeoAstraAdmLogic: ok");
    }

    private static void expect(Object expected, Object actual, String message) {
        if (!java.util.Objects.equals(expected, actual)) {
            System.out.println("FAILED: " + message + ": expected " + expected + ", got " + actual);
            System.exit(1);
        }
    }
}
