package com.neoastra.backend;

import java.util.ArrayList;
import java.util.List;

/** Checks NeoAstraScriptChannels on a plain JVM; AndroidJavaTests compiles and runs it, and a failure exits with 1. */
public final class NeoAstraScriptChannelsCheck {
    /** Records what the channels ask of the WebView, and fails the calls that the check asks it to. */
    private static final class Engine implements NeoAstraScriptChannels.Engine<String, String, String> {
        final List<String> calls = new ArrayList<>();
        final List<NeoAstraScriptChannels.Listener<String>> listeners = new ArrayList<>();
        final List<String> posted = new ArrayList<>();
        boolean failScript, failRemoveScript, failRemoveListener;
        String currentOrigin = "https://example.test";

        @Override public String world(String name) { calls.add("world " + name); return "W:" + name; }
        @Override public void addListener(String world, String binding, NeoAstraScriptChannels.Listener<String> listener) {
            calls.add("listen " + world + " " + binding);
            listeners.add(listener);
        }
        @Override public void removeListener(String world, String binding) {
            calls.add("unlisten " + world + " " + binding);
            if (failRemoveListener) throw new IllegalStateException("listener removal failed");
        }
        @Override public String addScript(String script, boolean documentEnd, String world) {
            if (failScript) throw new IllegalArgumentException("script refused");
            calls.add("script " + world + " " + documentEnd);
            return "H:" + script;
        }
        @Override public void removeScript(String handler) {
            calls.add("unscript " + handler);
            if (failRemoveScript) throw new IllegalStateException("script removal failed");
        }
        @Override public void post(String proxy, String message) { posted.add(proxy + " " + message); }
        @Override public boolean isCurrentOrigin(String origin) { return currentOrigin.equals(origin); }
    }

    private static final List<String> delivered = new ArrayList<>();

    public static void main(String[] arguments) {
        Engine engine = new Engine();
        NeoAstraScriptChannels<String, String, String> channels = new NeoAstraScriptChannels<>(engine,
            (world, binding, message, origin, isMainFrame) -> delivered.add(world + " " + binding + " " + message + " " + origin + " " + isMainFrame));

        // The listener of the binding exists in the world before the script that uses it.
        channels.add(1, "toolbar", "codealta.annotations", "codeAltaAnnotations", true);
        expect(engine.calls.equals(List.of("world codealta.annotations", "listen W:codealta.annotations codeAltaAnnotations", "script W:codealta.annotations true")), "a binding is listened to before its script runs: " + engine.calls);
        expect(channels.post(1, "early") == NeoAstraScriptChannels.NO_REPLY, "the host cannot reach a world that has not spoken in this document");

        // One binding name once per world; another world may use it.
        try { channels.add(2, "again", "codealta.annotations", "codeAltaAnnotations", false); expect(false, "a binding name is used once per world"); }
        catch (NeoAstraScriptChannels.BindingTakenException expected) { }
        engine.calls.clear();
        channels.add(3, "other", "codealta.other", "codeAltaAnnotations", false);
        expect(engine.calls.size() == 3, "another world may use the same binding name: " + engine.calls);

        // A frame other than the main one is reported to the native side, which drops it, but its reply channel is never kept.
        NeoAstraScriptChannels.Listener<String> listener = engine.listeners.get(0);
        listener.onMessage("from-frame", "https://example.test", false, "frame-proxy");
        expect(channels.post(1, "x") == NeoAstraScriptChannels.NO_REPLY, "the reply channel of a subframe is not kept");
        listener.onMessage("hello", "https://example.test", true, "proxy-1");
        expect(delivered.get(delivered.size() - 1).equals("codealta.annotations codeAltaAnnotations hello https://example.test true"), "a message names its world and binding: " + delivered);
        expect(channels.post(1, "ping") == NeoAstraScriptChannels.DELIVERED && engine.posted.equals(List.of("proxy-1 ping")), "the host answers through the reply channel of the main frame: " + engine.posted);

        // A navigation forgets the reply channels; a late message of the previous document does not bring its channel back.
        channels.forgetReplies();
        expect(channels.post(1, "stale") == NeoAstraScriptChannels.NO_REPLY, "a navigation forgets the reply channel");
        engine.currentOrigin = "https://next.test";
        listener.onMessage("late", "https://example.test", true, "proxy-old");
        expect(channels.post(1, "stale") == NeoAstraScriptChannels.NO_REPLY, "a message of another origin than the document does not give its channel");
        listener.onMessage("hello", "https://next.test", true, "proxy-2");
        expect(channels.post(1, "fresh") == NeoAstraScriptChannels.DELIVERED && engine.posted.get(engine.posted.size() - 1).equals("proxy-2 fresh"), "the new document gives the new channel: " + engine.posted);

        // The WebView gives each document a reply channel of its own: after a navigation to the same origin, a late message of the
        // previous document is still delivered, but its channel does not come back, and the new document's channel replaces it.
        channels.forgetReplies();
        int late = delivered.size();
        listener.onMessage("late", "https://next.test", true, "proxy-2");
        expect(delivered.size() == late + 1, "a late message of the previous document is still delivered: " + delivered);
        expect(channels.post(1, "stale") == NeoAstraScriptChannels.NO_REPLY, "the channel of a document of the same origin that the main frame left does not come back");
        listener.onMessage("hello", "https://next.test", true, "proxy-4");
        expect(channels.post(1, "same") == NeoAstraScriptChannels.DELIVERED && engine.posted.get(engine.posted.size() - 1).equals("proxy-4 same"), "the new document of the same origin gives its channel: " + engine.posted);
        listener.onMessage("again", "https://next.test", true, "proxy-4");
        expect(channels.post(1, "kept") == NeoAstraScriptChannels.DELIVERED && engine.posted.get(engine.posted.size() - 1).equals("proxy-4 kept"), "the current document keeps its channel: " + engine.posted);
        expect(channels.post(42, "x") == NeoAstraScriptChannels.UNKNOWN, "an unknown script has no channel");

        // A script that the WebView refuses leaves no listener behind.
        engine.calls.clear();
        engine.failScript = true;
        expectThrows(() -> channels.add(4, "refused", "codealta.refused", "refusedBinding", false), "a refused script fails");
        engine.failScript = false;
        expect(engine.calls.equals(List.of("world codealta.refused", "listen W:codealta.refused refusedBinding", "unlisten W:codealta.refused refusedBinding")), "the listener of a refused script is removed: " + engine.calls);
        expect(channels.post(4, "x") == NeoAstraScriptChannels.UNKNOWN, "a refused script is not kept");
        channels.add(4, "accepted", "codealta.refused", "refusedBinding", false);

        // Removing a script removes its listener even when the removal of the script fails, and its messages stop.
        engine.calls.clear();
        engine.failRemoveScript = true;
        expectThrows(() -> channels.remove(1), "a failing removal is reported");
        engine.failRemoveScript = false;
        expect(engine.calls.equals(List.of("unscript H:toolbar", "unlisten W:codealta.annotations codeAltaAnnotations")), "both are removed: " + engine.calls);
        int before = delivered.size();
        listener.onMessage("after", "https://next.test", true, "proxy-3");
        expect(delivered.size() == before, "a removed binding delivers nothing");
        expect(channels.post(1, "x") == NeoAstraScriptChannels.UNKNOWN, "a removed script has no channel");
        expect(!channels.remove(1), "a script is removed once");
        // The binding name is free again once its script is gone.
        channels.add(5, "toolbar", "codealta.annotations", "codeAltaAnnotations", true);

        // A script without a binding has no listener and no channel.
        engine.calls.clear();
        channels.add(6, "plain", "codealta.plain", null, false);
        expect(engine.calls.equals(List.of("world codealta.plain", "script W:codealta.plain false")), "a script without a binding is only a script: " + engine.calls);
        expect(channels.post(6, "x") == NeoAstraScriptChannels.UNKNOWN, "a script without a binding has no channel");
        expect(!channels.remove(99), "a script of the page is not one of the channels");

        // Clearing removes every script and listener, and goes on after a failure.
        engine.calls.clear();
        engine.failRemoveListener = true;
        channels.clear();
        engine.failRemoveListener = false;
        expect(engine.calls.contains("unscript H:plain") && engine.calls.contains("unscript H:accepted") && engine.calls.contains("unlisten W:codealta.other codeAltaAnnotations"), "clearing removes all: " + engine.calls);
        expect(channels.post(5, "x") == NeoAstraScriptChannels.UNKNOWN, "nothing is left");

        System.out.println("NeoAstraScriptChannels: ok");
    }

    private static void expect(boolean condition, String message) {
        if (!condition) { System.out.println("FAILED: " + message); System.exit(1); }
    }

    private static void expectThrows(Runnable action, String message) {
        try { action.run(); } catch (RuntimeException expected) { return; }
        expect(false, message);
    }
}
