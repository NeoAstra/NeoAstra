package com.neoastra.push.adm;

import android.app.Activity;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;

import com.amazon.device.messaging.ADM;
import com.neoastra.backend.NeoAstraNotificationReceiver;
import com.neoastra.backend.NeoAstraServices;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.HashMap;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * The Amazon Device Messaging service of the push plugin of NeoAstra, which the platform services reach as
 * {@code neoastra.push.adm}: the registration ID of ADM on a Fire tablet, which never receives Firebase Cloud Messaging, and a
 * queue of what happens while nobody asks, in the form of the service of Firebase. ADM delivers the data of a message as the
 * string extras of an intent, without a notification: a message is shown as the meta-data of the manifest of the application
 * names the keys of its data (the convention of the service of Firebase, {@code com.neoastra.push.title} and the others).
 */
public final class NeoAstraAdmPush implements NeoAstraServices.Service {
    static final String NAME = "neoastra.push.adm";
    private static final String CHANNEL = "neoastra.push";
    private static final String EXTRA = "neoastra.push.adm.message";
    private static final LinkedBlockingQueue<JSONObject> EVENTS = new LinkedBlockingQueue<>(64);
    private static final AtomicInteger FOREGROUND = new AtomicInteger();
    private static final ConcurrentHashMap<String, CompletableFuture<Boolean>> DECISIONS = new ConcurrentHashMap<>();
    private static volatile boolean resumedOnce;
    private static volatile JSONObject launch;
    private static volatile boolean deciding;
    // A registration that is under way waits for the answer of ADM, which comes to the handler.
    private static final NeoAstraAdmLogic.Registration REGISTRATION = new NeoAstraAdmLogic.Registration();

    @Override public void call(Context context, JSONObject input, JSONObject output) throws Exception {
        switch (input.getString("operation")) {
            case "available": output.put("status", "Success").put("available", supported(context)); return;
            case "register": register(context, output); return;
            case "next": {
                JSONObject event = EVENTS.poll(Math.max(0, Math.min(30_000, input.optLong("timeout", 20_000))), TimeUnit.MILLISECONDS);
                if (event == null) output.put("status", "NotFound"); else output.put("status", "Success").put("event", event);
                return;
            }
            case "filter": deciding = input.optBoolean("enabled", false); output.put("status", "Success"); return;
            case "present": {
                CompletableFuture<Boolean> decision = DECISIONS.remove(input.getString("id"));
                if (decision != null) decision.complete(input.optBoolean("show", true));
                output.put("status", decision == null ? "NotFound" : "Success");
                return;
            }
            case "launch": {
                JSONObject message = launch;
                if (message == null) output.put("status", "NotFound"); else output.put("status", "Success").put("message", message);
                return;
            }
            default: output.put("status", "Unsupported");
        }
    }

    // ADM is a library of Fire OS, which a device without it does not have: the stub that the application compiles against is
    // not packaged.
    static boolean supported(Context context) {
        try { Class.forName("com.amazon.device.messaging.ADM"); return new ADM(context).isSupported(); }
        catch (ClassNotFoundException | RuntimeException | LinkageError error) { return false; }
    }

    // Fire OS shows notifications without asking before Android 13; the registration ID stays until ADM gives a new one.
    private static void register(Context context, JSONObject output) throws Exception {
        if (!supported(context)) { output.put("status", "Unsupported").put("code", "not-configured"); return; }
        ADM adm = new ADM(context);
        String id = adm.getRegistrationId();
        if (id == null) {
            NeoAstraAdmLogic.Joined joined = REGISTRATION.join();
            if (joined.start) adm.startRegister();
            if (!REGISTRATION.await(joined.attempt, 30, TimeUnit.SECONDS)) { output.put("status", "Failed").put("code", "timeout"); return; }
            String error = joined.attempt.error();
            // An application without the API key of its Amazon app (assets/api_key.txt), or with one of another signature, is not configured.
            if (error != null) { String[] failure = NeoAstraAdmLogic.failure(error); output.put("status", failure[0]).put("code", failure[1]); return; }
            id = joined.attempt.id();
        }
        NotificationManager manager = context.getSystemService(NotificationManager.class);
        output.put("status", "Success").put("token", id).put("canDisplay", manager != null && manager.areNotificationsEnabled());
    }

    static void registered(String id) {
        REGISTRATION.answer(id, null);
        try { post(new JSONObject().put("kind", "token").put("token", id)); } catch (Exception ignored) { }
    }

    static void registrationError(String error) { REGISTRATION.answer(null, error); }

    static void received(Context context, Intent intent) {
        try {
            // ADM adds the MD5 of the message, which identifies it, and its priority to the data that the server sent.
            Bundle extras = intent.getExtras();
            Map<String, Object> values = new HashMap<>();
            if (extras != null) for (String key : extras.keySet()) values.put(key, extras.get(key));
            String md5 = extras == null ? null : extras.getString("adm_message_md5");
            Map<String, String> data = NeoAstraAdmLogic.data(values);
            JSONObject payload = new JSONObject().put("data", new JSONObject(data));
            JSONObject value = new JSONObject().putOpt("messageId", md5).put("payload", payload);
            String id = deciding && FOREGROUND.get() > 0 ? UUID.randomUUID().toString() : null;
            CompletableFuture<Boolean> decision = id == null ? null : new CompletableFuture<>();
            if (decision != null) DECISIONS.put(id, decision);
            post(new JSONObject().put("kind", "received").put("message", value).putOpt("decision", id));
            if (decision != null) {
                boolean show;
                try { show = decision.get(4, TimeUnit.SECONDS); } catch (Exception exception) { show = true; } finally { DECISIONS.remove(id); }
                if (!show) return;
            }
            String[] shown = presentation(context, data);
            if (shown != null) show(context, shown[2] != null ? shown[2] : UUID.randomUUID().toString(), shown[0], shown[1], shown[3], value);
        } catch (Exception ignored) { }
    }

    // The notification of a message, as the meta-data of the manifest names its data keys: title, body, title_key, body_key, args,
    // tag and group under com.neoastra.push.*, as for a data message of Firebase.
    static String[] presentation(Context context, Map<String, String> data) {
        Bundle meta;
        try { meta = context.getPackageManager().getApplicationInfo(context.getPackageName(), PackageManager.GET_META_DATA).metaData; }
        catch (PackageManager.NameNotFoundException exception) { return null; }
        if (meta == null) return null;
        String[] args = arguments(value(data, meta, "args"));
        String bodyKey = value(data, meta, "body_key");
        String body = bodyKey != null ? resolve(context, bodyKey, args) : null;
        if (body == null) body = value(data, meta, "body");
        if (body == null || body.isEmpty()) return null;
        String title = value(data, meta, "title");
        String titleKey = value(data, meta, "title_key");
        if (title == null && titleKey != null) title = resolve(context, titleKey, args);
        if (title == null) title = context.getApplicationInfo().loadLabel(context.getPackageManager()).toString();
        return new String[] { title, body, value(data, meta, "tag"), value(data, meta, "group") };
    }

    private static String value(Map<String, String> data, Bundle meta, String name) {
        String key = meta.getString("com.neoastra.push." + name);
        String value = key == null ? null : data.get(key);
        return value == null || value.isEmpty() ? null : value;
    }

    private static String[] arguments(String json) {
        if (json == null) return new String[0];
        try {
            JSONArray array = new JSONArray(json);
            String[] values = new String[array.length()];
            for (int i = 0; i < values.length; i++) values[i] = array.optString(i, "");
            return values;
        } catch (Exception exception) { return new String[0]; }
    }

    private static String resolve(Context context, String key, String[] args) {
        int id = context.getResources().getIdentifier(key.replaceAll("[^A-Za-z0-9_]", "_"), "string", context.getPackageName());
        if (id == 0) return null;
        try { return context.getString(id, (Object[]) args); }
        catch (java.util.IllegalFormatException exception) { return null; }
    }

    // A tapped notification of this service launches the activity of the application with the message.
    static void resumed(Activity activity) {
        FOREGROUND.incrementAndGet();
        boolean first = !resumedOnce;
        resumedOnce = true;
        Intent intent = activity.getIntent();
        String own = intent == null ? null : intent.getStringExtra(EXTRA);
        if (own == null) return;
        try {
            JSONObject message = new JSONObject(own);
            intent.removeExtra(EXTRA);
            if (first) launch = message;
            post(new JSONObject().put("kind", "opened").put("message", message));
        } catch (Exception ignored) { }
    }

    static void paused() { FOREGROUND.updateAndGet(value -> Math.max(0, value - 1)); }

    private static void show(Context context, String id, String title, String body, String group, JSONObject message) {
        NotificationManager manager = context.getSystemService(NotificationManager.class);
        if (manager == null || !manager.areNotificationsEnabled()) return;
        Notification.Builder builder;
        if (Build.VERSION.SDK_INT >= 26) {
            manager.createNotificationChannel(new NotificationChannel(CHANNEL, context.getApplicationInfo().loadLabel(context.getPackageManager()), NotificationManager.IMPORTANCE_HIGH));
            builder = new Notification.Builder(context, CHANNEL);
        } else {
            builder = new Notification.Builder(context);
        }
        builder.setSmallIcon(NeoAstraServices.smallIcon(context)).setContentTitle(title).setContentText(body).setStyle(new Notification.BigTextStyle().bigText(body)).setAutoCancel(true);
        if (group != null) builder.setGroup(group);
        Intent launchIntent = context.getPackageManager().getLaunchIntentForPackage(context.getPackageName());
        String tag = NAME + ":" + id;
        if (launchIntent != null) {
            launchIntent.putExtra(EXTRA, message.toString());
            NeoAstraNotificationReceiver.identify(launchIntent, tag);
            builder.setContentIntent(PendingIntent.getActivity(context, tag.hashCode(), launchIntent, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT));
        }
        manager.notify(tag, 0, builder.build());
    }

    private static void post(JSONObject event) {
        while (!EVENTS.offer(event)) EVENTS.poll();
    }
}
