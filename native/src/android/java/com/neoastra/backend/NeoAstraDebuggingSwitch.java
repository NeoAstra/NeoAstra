package com.neoastra.backend;

import java.util.function.Consumer;

/**
 * Counts the views that ask for the debugging of their web contents, which Android switches for the whole process: the switch
 * is on while any view asks for it. The count changes only once the switch was taken, so a switch that failed can be asked for
 * again. It has no Android type, so that a plain JVM tests it.
 */
final class NeoAstraDebuggingSwitch {
    private final Consumer<Boolean> apply;
    private int views;

    NeoAstraDebuggingSwitch(Consumer<Boolean> apply) {
        this.apply = apply;
    }

    /** A view starts or stops asking. */
    synchronized void change(boolean asks) {
        int next = views + (asks ? 1 : -1);
        if (asks && next == 1) apply.accept(true);
        else if (!asks && next == 0) apply.accept(false);
        views = next;
    }

    /** A view that goes no longer counts even when the switch failed; the switch stays as Android left it. */
    synchronized void forget() {
        views--;
    }

    synchronized int views() {
        return views;
    }
}
