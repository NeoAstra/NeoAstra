package com.neoastra.backend;

import android.app.Activity;
import android.app.AlarmManager;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;

import org.json.JSONObject;

import java.util.Map;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;

/**
 * Delivers the notifications that NeoAstra scheduled for later. Each one is kept in the private preferences of the application
 * and armed as an alarm, exact when the application may set exact alarms (it declares {@code USE_EXACT_ALARM}, or
 * {@code SCHEDULE_EXACT_ALARM} that the user allows) and otherwise one that Android may defer a little. The alarm posts the
 * notification and forgets it. A tap on a notification reaches the application through the intent of its activity. Android drops alarms when the device restarts or the application is updated: an application
 * that declares {@code RECEIVE_BOOT_COMPLETED} gets them armed again then, and one whose moment passed meanwhile shows at once.
 */
public final class NeoAstraNotificationReceiver extends BroadcastReceiver {
    private static final String STORE = "neoastra.notifications.scheduled";
    private static final String ACTION = "com.neoastra.backend.NOTIFICATION";
    private static final String EXTRA_ID = "neoastra.notification.id";
    private static final String EXTRA_DATA = "neoastra.notification.data";
    private static final LinkedBlockingQueue<JSONObject> ACTIVATIONS = new LinkedBlockingQueue<>(64);

    @Override
    public void onReceive(Context context, Intent intent) {
        String action = intent.getAction();
        if (ACTION.equals(action)) {
            Uri data = intent.getData();
            if (data != null) deliver(context, data.getSchemeSpecificPart());
        } else if (Intent.ACTION_BOOT_COMPLETED.equals(action) || Intent.ACTION_MY_PACKAGE_REPLACED.equals(action)
            || "android.app.action.SCHEDULE_EXACT_ALARM_PERMISSION_STATE_CHANGED".equals(action)) {
            rearm(context);
        }
    }

    /** Keeps a notification and arms its alarm, which replaces one scheduled with the same ID. */
    static void schedule(Context context, String id, String title, String body, String data, long at) throws Exception {
        store(context).edit().putString(id, new JSONObject().put("title", title).put("body", body).put("data", data).put("at", at).toString()).commit();
        arm(context, id, at);
    }

    /** Forgets a scheduled notification and its alarm; one that was not scheduled is left as it is. */
    static void cancel(Context context, String id) {
        store(context).edit().remove(id).commit();
        AlarmManager alarms = context.getSystemService(AlarmManager.class);
        PendingIntent operation = operation(context, id, PendingIntent.FLAG_NO_CREATE);
        if (alarms != null && operation != null) { alarms.cancel(operation); operation.cancel(); }
    }

    /** Marks the intent that a tap on a notification starts with its ID and activation data. */
    static void tag(Intent intent, String id, String data) {
        intent.putExtra(EXTRA_ID, id);
        intent.putExtra(EXTRA_DATA, data);
    }

    /**
     * Reports the tap that brought an activity up: its intent (the launcher activity's, or the one that an activity that comes
     * back passes to setIntent) carries the notification. The extras are taken, so a later resume does not report it again.
     */
    static void opened(Activity activity) {
        Intent intent = activity.getIntent();
        Bundle extras = intent == null ? null : intent.getExtras();
        String id = extras == null ? null : extras.getString(EXTRA_ID);
        if (id == null) return;
        String data = extras.getString(EXTRA_DATA, "");
        intent.removeExtra(EXTRA_ID);
        intent.removeExtra(EXTRA_DATA);
        try { ACTIVATIONS.offer(new JSONObject().put("id", id).put("data", data)); } catch (Exception ignored) { }
    }

    /** The next tap, waiting at most {@code timeout} milliseconds, or null. */
    static JSONObject next(long timeout) throws InterruptedException { return ACTIVATIONS.poll(timeout, TimeUnit.MILLISECONDS); }

    private static void deliver(Context context, String id) {
        String value = store(context).getString(id, null);
        if (value == null) return;
        store(context).edit().remove(id).commit();
        try {
            JSONObject entry = new JSONObject(value);
            NeoAstraServices.post(context, id, entry.getString("title"), entry.getString("body"), entry.optString("data", ""));
        } catch (Exception ignored) {
            // A malformed entry is dropped: there is nothing to show.
        }
    }

    private static void rearm(Context context) {
        long now = System.currentTimeMillis();
        for (Map.Entry<String, ?> entry : store(context).getAll().entrySet()) {
            try {
                long at = new JSONObject(String.valueOf(entry.getValue())).getLong("at");
                if (at <= now) deliver(context, entry.getKey());
                else arm(context, entry.getKey(), at);
            } catch (Exception ignored) {
                store(context).edit().remove(entry.getKey()).commit();
            }
        }
    }

    private static void arm(Context context, String id, long at) {
        AlarmManager alarms = context.getSystemService(AlarmManager.class);
        if (alarms == null) return;
        PendingIntent operation = operation(context, id, PendingIntent.FLAG_UPDATE_CURRENT);
        boolean exact = Build.VERSION.SDK_INT < 31 || alarms.canScheduleExactAlarms();
        if (exact) alarms.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at, operation);
        else alarms.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at, operation);
    }

    // The ID is the data of the intent, so that each notification has an alarm of its own.
    private static PendingIntent operation(Context context, String id, int flags) {
        Intent intent = new Intent(ACTION, Uri.fromParts("neoastra-notification", id, null), context, NeoAstraNotificationReceiver.class);
        return PendingIntent.getBroadcast(context, 0, intent, flags | PendingIntent.FLAG_IMMUTABLE);
    }

    private static SharedPreferences store(Context context) {
        return context.getSharedPreferences(STORE, Context.MODE_PRIVATE);
    }
}
