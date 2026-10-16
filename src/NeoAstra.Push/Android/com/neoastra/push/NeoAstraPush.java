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

import org.json.JSONObject;

import java.util.Map;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * The push service of the push plugin of NeoAstra, which the platform services reach as {@code neoastra.push}: the token of
 * Firebase Cloud Messaging, and a queue of what happens while nobody asks (a new token, a message that arrives, a notification
 * that the user taps), which the managed side takes one event at a time. A message that arrives while the application is in the
 * foreground is shown here, since Firebase shows a notification only while the application is in the background.
 */
public final class NeoAstraPush implements NeoAstraServices.Service {
    static final String NAME = "neoastra.push";
    private static final String CHANNEL = "neoastra.push";
    private static final String EXTRA = "neoastra.push.message";
    private static final LinkedBlockingQueue<JSONObject> EVENTS = new LinkedBlockingQueue<>(64);
    private static final AtomicInteger FOREGROUND = new AtomicInteger();
    private static volatile boolean resumedOnce;
    private static volatile JSONObject launch;

    @Override public void call(Context context, JSONObject input, JSONObject output) throws Exception {
        switch (input.getString("operation")) {
            case "register": register(context, output); return;
            case "next": {
                JSONObject event = EVENTS.poll(Math.max(0, Math.min(30_000, input.optLong("timeout", 20_000))), TimeUnit.MILLISECONDS);
                if (event == null) output.put("status", "NotFound"); else output.put("status", "Success").put("event", event);
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
            post(new JSONObject().put("kind", "received").put("message", value));
            RemoteMessage.Notification notification = message.getNotification();
            if (notification != null && FOREGROUND.get() > 0) show(context, message.getMessageId(), notification, value);
        } catch (Exception ignored) { }
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

    private static void show(Context context, String id, RemoteMessage.Notification notification, JSONObject message) {
        NotificationManager manager = context.getSystemService(NotificationManager.class);
        if (manager == null || !manager.areNotificationsEnabled()) return;
        Notification.Builder builder;
        if (Build.VERSION.SDK_INT >= 26) {
            manager.createNotificationChannel(new NotificationChannel(CHANNEL, context.getApplicationInfo().loadLabel(context.getPackageManager()), NotificationManager.IMPORTANCE_HIGH));
            builder = new Notification.Builder(context, CHANNEL);
        } else {
            builder = new Notification.Builder(context);
        }
        int icon = context.getApplicationInfo().icon;
        builder.setSmallIcon(icon != 0 ? icon : android.R.drawable.ic_dialog_info).setContentTitle(notification.getTitle()).setContentText(notification.getBody()).setAutoCancel(true);
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
