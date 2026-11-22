package com.neoastra.backend;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * The scripts of isolated worlds of one view and the bindings that only their worlds reach. A binding is a web message listener
 * of its world, added before the script that uses it and removed with it; the host answers a world through the reply channel of
 * the last message of its main frame in the current document, which a navigation forgets. It has no Android type, so that a
 * plain JVM tests it: the WebView is behind {@link Engine}. Every call runs on the main thread, which owns the WebView.
 *
 * @param <W> a world of the WebView
 * @param <H> the handler of an injected script
 * @param <P> the reply channel of a message
 */
final class NeoAstraScriptChannels<W, H, P> {
    /** What the channels ask of the WebView. */
    interface Engine<W, H, P> {
        W world(String name);
        void addListener(W world, String binding, Listener<P> listener);
        void removeListener(W world, String binding);
        H addScript(String script, boolean documentEnd, W world);
        void removeScript(H handler);
        void post(P reply, String message);
        /** Whether a message of this origin can come from the document that the main frame committed last. */
        boolean isCurrentOrigin(String origin);
    }

    /** A message that a world posted through its binding; the message is null when it is not a string. */
    interface Listener<P> {
        void onMessage(String message, String origin, boolean isMainFrame, P reply);
    }

    /** Where the messages of the worlds go: the native side, which raises the event of the view. */
    interface Sink {
        void deliver(String world, String binding, String message, String origin, boolean isMainFrame);
    }

    /** A binding name that the world already has. */
    static final class BindingTakenException extends IllegalStateException {
        private static final long serialVersionUID = 1L;

        BindingTakenException() { super("The world already has a script with this binding."); }
    }

    /** What {@link #post} did. */
    static final int DELIVERED = 0, UNKNOWN = 1, NO_REPLY = 2;

    private static final class Channel<W, H, P> {
        final String worldName;
        final String binding;
        final W world;
        H handler;
        P reply;
        boolean closed;

        Channel(String worldName, String binding, W world) {
            this.worldName = worldName;
            this.binding = binding;
            this.world = world;
        }
    }

    private final Engine<W, H, P> engine;
    private final Sink sink;
    private final Map<Integer, Channel<W, H, P>> scripts = new HashMap<>();

    NeoAstraScriptChannels(Engine<W, H, P> engine, Sink sink) {
        this.engine = engine;
        this.sink = sink;
    }

    /**
     * Adds a script to the world of the name, with the listener of its binding first when it has one. A failure removes what it
     * added and throws; a binding name that the world already has throws before anything is added.
     */
    void add(int identifier, String script, String worldName, String binding, boolean documentEnd) {
        String name = binding == null || binding.isEmpty() ? null : binding;
        if (name != null)
            for (Channel<W, H, P> channel : scripts.values())
                if (name.equals(channel.binding) && channel.worldName.equals(worldName)) throw new BindingTakenException();
        W world = engine.world(worldName);
        Channel<W, H, P> channel = new Channel<>(worldName, name, world);
        boolean listening = false;
        try {
            if (name != null) {
                engine.addListener(world, name, (message, origin, isMainFrame, reply) -> receive(channel, message, origin, isMainFrame, reply));
                listening = true;
            }
            channel.handler = engine.addScript(script, documentEnd, world);
        } catch (RuntimeException exception) {
            channel.closed = true;
            if (listening) {
                try { engine.removeListener(world, name); } catch (RuntimeException ignored) { }
            }
            throw exception;
        }
        scripts.put(identifier, channel);
    }

    private void receive(Channel<W, H, P> channel, String message, String origin, boolean isMainFrame, P reply) {
        if (channel.closed || message == null) return;
        // Only the main frame of the current document gives the host its reply channel; the native side drops other frames.
        if (isMainFrame && engine.isCurrentOrigin(origin)) channel.reply = reply;
        sink.deliver(channel.worldName, channel.binding, message, origin, isMainFrame);
    }

    /**
     * Removes the script and its listener, both even when one of them fails, and throws the first failure. Answers false for an
     * identifier that names no script of a world.
     */
    boolean remove(int identifier) {
        Channel<W, H, P> channel = scripts.remove(identifier);
        if (channel == null) return false;
        close(channel);
        return true;
    }

    private void close(Channel<W, H, P> channel) {
        channel.closed = true;
        channel.reply = null;
        RuntimeException failure = null;
        try { if (channel.handler != null) engine.removeScript(channel.handler); } catch (RuntimeException exception) { failure = exception; }
        try { if (channel.binding != null) engine.removeListener(channel.world, channel.binding); } catch (RuntimeException exception) { if (failure == null) failure = exception; }
        if (failure != null) throw failure;
    }

    /** Delivers a message to the binding of the script in the current document. */
    int post(int identifier, String message) {
        Channel<W, H, P> channel = scripts.get(identifier);
        if (channel == null || channel.binding == null) return UNKNOWN;
        P reply = channel.reply;
        if (reply == null) return NO_REPLY;
        engine.post(reply, message);
        return DELIVERED;
    }

    /** Forgets the reply channels when the main frame navigates: they belong to the document that it leaves. */
    void forgetReplies() {
        for (Channel<W, H, P> channel : scripts.values()) channel.reply = null;
    }

    /** Removes every script and listener, going on after a failure, when the view goes. */
    void clear() {
        List<Channel<W, H, P>> all = new ArrayList<>(scripts.values());
        scripts.clear();
        for (Channel<W, H, P> channel : all) {
            try { close(channel); } catch (RuntimeException ignored) { }
        }
    }
}
