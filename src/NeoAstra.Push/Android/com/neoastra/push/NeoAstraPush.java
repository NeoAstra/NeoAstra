package com.neoastra.push;

import android.Manifest;
import android.app.Activity;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;

import com.google.android.gms.tasks.Tasks;
import com.google.firebase.FirebaseApp;
import com.google.firebase.messaging.FirebaseMessaging;
import com.google.firebase.messaging.RemoteMessage;
import com.neoastra.backend.NeoAstraServices;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.Map;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * The push service of the push plugin of NeoAstra, which the platform services reach as {@code neoastra.push}: the token of
 * Firebase Cloud Messaging, and a queue of what happens while nobody asks (a new token, a message that arrives, a notification
 * that the user taps), which the managed side takes one event at a time. A message that arrives while the application is in the
 * foreground is shown here, since Firebase shows a notification only while the application is in the background. A data message
 * (no notification) is shown here too, also while the application does not run, when the application names in the meta-data of
 * its manifest the keys of its data that hold the title and the body, or the names of string resources for them (see
 * {@link #presentation}).
 */
public final class NeoAstraPush implements NeoAstraServices.Service {
    static final String NAME = "neoastra.push";
    private static final String CHANNEL = "neoastra.push";
    private static final String EXTRA = "neoastra.push.message";
    private static final LinkedBlockingQueue<JSONObject> EVENTS = new LinkedBlockingQueue<>(64);
    private static final AtomicInteger FOREGROUND = new AtomicInteger();
    private static volatile boolean resumedOnce;
    private static volatile JSONObject launch;
    // The managed side decides whether a message that arrives in the foreground is shown (ForegroundPresentation).
    private static volatile boolean deciding;
    private static final ConcurrentHashMap<String, CompletableFuture<Boolean>> DECISIONS = new ConcurrentHashMap<>();

    @Override public void call(Context context, JSONObject input, JSONObject output) throws Exception {
        switch (input.getString("operation")) {
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

    // Asks once to show notifications, from Android 13, and waits for the token of Firebase.
    private static void register(Context context, JSONObject output) throws Exception {
        if (!configured(context)) { output.put("status", "Unsupported").put("code", "not-configured"); return; }
        if (Build.VERSION.SDK_INT >= 33 && context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
            && declared(context, Manifest.permission.POST_NOTIFICATIONS) && !context.getSharedPreferences(NAME, Context.MODE_PRIVATE).getBoolean("requested", false)) {
            context.getSharedPreferences(NAME, Context.MODE_PRIVATE).edit().putBoolean("requested", true).commit();
            NeoAstraServices.requestPermissions(Manifest.permission.POST_NOTIFICATIONS);
        }
        String token;
        try { token = Tasks.await(FirebaseMessaging.getInstance().getToken(), 30, TimeUnit.SECONDS); }
        catch (TimeoutException exception) { output.put("status", "Failed").put("code", "timeout"); return; }
        catch (ExecutionException exception) { output.put("status", "Failed"); return; }
        NotificationManager manager = context.getSystemService(NotificationManager.class);
        output.put("status", "Success").put("token", token).put("canDisplay", manager != null && manager.areNotificationsEnabled());
    }

    // Firebase starts itself from the resources that the google-services.json of the application gives; without them it is not there.
    private static boolean configured(Context context) {
        try { return !FirebaseApp.getApps(context).isEmpty() || FirebaseApp.initializeApp(context) != null; }
        catch (RuntimeException exception) { return false; }
    }

    static void token(String token) {
        try { post(new JSONObject().put("kind", "token").put("token", token)); } catch (Exception ignored) { }
    }

    static void received(Context context, RemoteMessage message) {
        try {
            JSONObject value = message(message);
            // In the foreground the managed side may keep a message from being shown; it has a few seconds, then it is shown.
            String id = deciding && FOREGROUND.get() > 0 ? UUID.randomUUID().toString() : null;
            CompletableFuture<Boolean> decision = id == null ? null : new CompletableFuture<>();
            if (decision != null) DECISIONS.put(id, decision);
            post(new JSONObject().put("kind", "received").put("message", value).putOpt("decision", id));
            if (decision != null) {
                boolean show;
                try { show = decision.get(4, TimeUnit.SECONDS); } catch (Exception exception) { show = true; } finally { DECISIONS.remove(id); }
                if (!show) return;
            }
            RemoteMessage.Notification notification = message.getNotification();
            if (notification != null) { if (FOREGROUND.get() > 0) show(context, message.getMessageId(), notification.getTitle(), notification.getBody(), null, value); }
            else {
                String[] shown = presentation(context, message.getData());
                if (shown != null) show(context, shown[2] != null ? shown[2] : message.getMessageId(), shown[0], shown[1], shown[3], value);
            }
        } catch (Exception ignored) { }
    }

    /**
     * The notification of a data message, as the meta-data of the manifest names its data keys: {@code com.neoastra.push.title} and
     * {@code com.neoastra.push.body} hold the text, {@code com.neoastra.push.title_key} and {@code com.neoastra.push.body_key} the
     * name of a string resource of the application (its characters other than letters, digits and {@code _} become {@code _}, so
     * {@code push.item.added} is {@code push_item_added}), formatted with the JSON array of strings under
     * {@code com.neoastra.push.args}; {@code com.neoastra.push.tag} identifies the notification (a later one with the same tag
     * replaces it) and {@code com.neoastra.push.group} groups it. A message without a body is not shown; a missing title is the
     * label of the application. Returns title, body, tag and group, or null.
     */
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

    // A key that names no string, or a string whose placeholders the arguments do not fill, shows nothing rather than a raw key.
    private static String resolve(Context context, String key, String[] args) {
        int id = context.getResources().getIdentifier(key.replaceAll("[^A-Za-z0-9_]", "_"), "string", context.getPackageName());
        if (id == 0) return null;
        try { return context.getString(id, (Object[]) args); }
        catch (java.util.IllegalFormatException exception) { return null; }
    }

    // A tapped notification launches the activity of the application with the message: a notification of Firebase with its data and
    // its google.* keys, one of this service with the message itself. An activity that receives a new intent passes it to setIntent.
    static void resumed(Activity activity) {
        FOREGROUND.incrementAndGet();
        boolean first = !resumedOnce;
        resumedOnce = true;
        Intent intent = activity.getIntent();
        Bundle extras = intent == null ? null : intent.getExtras();
        if (extras == null) return;
        try {
            JSONObject message = null;
            String own = extras.getString(EXTRA);
            if (own != null) message = new JSONObject(own);
            else if (extras.getString("google.message_id") != null) {
                JSONObject data = new JSONObject();
                for (String key : extras.keySet()) {
                    if (key.startsWith("google.") || key.startsWith("gcm.") || key.equals("from") || key.equals("collapse_key")) continue;
                    Object value = extras.get(key);
                    if (value instanceof String) data.put(key, value);
                }
                message = new JSONObject().put("messageId", extras.getString("google.message_id")).put("payload", new JSONObject().put("data", data));
            }
            if (message == null) return;
            intent.removeExtra(EXTRA);
            intent.removeExtra("google.message_id");
            if (first) launch = message;
            post(new JSONObject().put("kind", "opened").put("message", message));
        } catch (Exception ignored) { }
    }

    static void paused() { FOREGROUND.updateAndGet(value -> Math.max(0, value - 1)); }

    private static JSONObject message(RemoteMessage message) throws Exception {
        JSONObject data = new JSONObject();
        for (Map.Entry<String, String> entry : message.getData().entrySet()) data.put(entry.getKey(), entry.getValue());
        JSONObject payload = new JSONObject().put("data", data);
        RemoteMessage.Notification notification = message.getNotification();
        if (notification != null) payload.put("notification", new JSONObject().putOpt("title", notification.getTitle()).putOpt("body", notification.getBody()));
        return new JSONObject().putOpt("messageId", message.getMessageId()).put("payload", payload);
    }

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
        int code = id == null ? 0 : id.hashCode();
        if (launchIntent != null) {
            launchIntent.putExtra(EXTRA, message.toString());
            builder.setContentIntent(PendingIntent.getActivity(context, code, launchIntent, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT));
        }
        manager.notify(NAME, code, builder.build());
    }

    // The queue keeps the latest events when nobody takes them.
    private static void post(JSONObject event) {
        while (!EVENTS.offer(event)) EVENTS.poll();
    }

    private static boolean declared(Context context, String permission) throws Exception {
        PackageInfo info = context.getPackageManager().getPackageInfo(context.getPackageName(), PackageManager.GET_PERMISSIONS);
        if (info.requestedPermissions == null) return false;
        for (String value : info.requestedPermissions) if (permission.equals(value)) return true;
        return false;
    }
}
