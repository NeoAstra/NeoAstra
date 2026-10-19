package com.neoastra.backend;

import android.app.AlarmManager;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.net.Uri;
import android.os.Build;

import org.json.JSONObject;

import java.util.Map;

/**
 * Delivers the notifications that NeoAstra scheduled for later. Each one is kept in the private preferences of the application
 * and armed as an alarm, exact when the application may set exact alarms (it declares {@code USE_EXACT_ALARM}, or
 * {@code SCHEDULE_EXACT_ALARM} that the user allows) and otherwise one that Android may defer a little. The alarm posts the
 * notification and forgets it. Android drops alarms when the device restarts or the application is updated: an application
 * that declares {@code RECEIVE_BOOT_COMPLETED} gets them armed again then, and one whose moment passed meanwhile shows at once.
 */
public final class NeoAstraNotificationReceiver extends BroadcastReceiver {
    private static final String STORE = "neoastra.notifications.scheduled";
    private static final String ACTION = "com.neoastra.backend.NOTIFICATION";

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
    static void schedule(Context context, String id, String title, String body, long at) throws Exception {
        store(context).edit().putString(id, new JSONObject().put("title", title).put("body", body).put("at", at).toString()).commit();
        arm(context, id, at);
    }

    /** Forgets a scheduled notification and its alarm; one that was not scheduled is left as it is. */
    static void cancel(Context context, String id) {
        store(context).edit().remove(id).commit();
        AlarmManager alarms = context.getSystemService(AlarmManager.class);
        PendingIntent operation = operation(context, id, PendingIntent.FLAG_NO_CREATE);
        if (alarms != null && operation != null) { alarms.cancel(operation); operation.cancel(); }
    }

    private static void deliver(Context context, String id) {
        String value = store(context).getString(id, null);
        if (value == null) return;
        store(context).edit().remove(id).commit();
        try {
            JSONObject entry = new JSONObject(value);
            NeoAstraServices.post(context, id, entry.getString("title"), entry.getString("body"));
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
