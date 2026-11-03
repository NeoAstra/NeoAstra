package com.neoastra.backend;

import java.util.ArrayList;
import java.util.List;

/** Checks NeoAstraDebuggingSwitch on a plain JVM; AndroidJavaTests compiles and runs it, and a failure exits with 1. */
public final class NeoAstraDebuggingSwitchCheck {
    private static final List<Boolean> calls = new ArrayList<>();
    private static boolean failNext;

    public static void main(String[] arguments) {
        NeoAstraDebuggingSwitch views = new NeoAstraDebuggingSwitch(NeoAstraDebuggingSwitchCheck::apply);

        // The switch is on while any view asks, and it is taken only on the first and the last change.
        views.change(true);
        views.change(true);
        expect(calls.equals(List.of(true)), "two views switch it on once: " + calls);
        views.change(false);
        expect(calls.equals(List.of(true)), "one view of two stops: the switch stays: " + calls);
        views.change(false);
        expect(calls.equals(List.of(true, false)), "the last view stops: the switch goes off: " + calls);

        // A switch that fails leaves the count as it was, so that the view can ask again.
        calls.clear();
        failNext = true;
        expectThrows(() -> views.change(true), "a failing switch throws");
        expect(views.views() == 0, "a failed switch is not counted: " + views.views());
        views.change(true);
        expect(calls.equals(List.of(true)) && views.views() == 1, "asking again switches it on: " + calls + " " + views.views());

        // A view that goes while the switch fails no longer counts, and the next view switches it on again.
        calls.clear();
        failNext = true;
        expectThrows(() -> views.change(false), "a failing switch off throws");
        views.forget();
        expect(views.views() == 0, "a forgotten view no longer counts: " + views.views());
        views.change(true);
        expect(calls.equals(List.of(true)), "the next view switches it on again: " + calls);

        System.out.println("NeoAstraDebuggingSwitch: ok");
    }

    private static void apply(boolean value) {
        if (failNext) { failNext = false; throw new IllegalStateException("switch failed"); }
        calls.add(value);
    }

    private static void expect(boolean condition, String message) {
        if (!condition) { System.out.println("FAILED: " + message); System.exit(1); }
    }

    private static void expectThrows(Runnable action, String message) {
        try { action.run(); } catch (RuntimeException expected) { return; }
        expect(false, message);
    }
}
