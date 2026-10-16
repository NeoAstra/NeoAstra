package com.neoastra.backend;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.app.Application;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.ActivityNotFoundException;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.content.res.AssetManager;
import android.content.res.Configuration;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;

import org.json.JSONArray;
import org.json.JSONObject;

import java.nio.ByteBuffer;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.lang.ref.WeakReference;
import java.security.KeyStore;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/**
 * The platform services of the Android backend: what the desktop services of NeoAstra need from Java. The native library
 * calls {@link #call} with one JSON request and returns the JSON response to NeoAstra. Every member that JNI reaches is
 * public so that R8 keeps its name. The services that wait for the user (a dialog, the notification permission) block the
 * calling thread, which is never the main thread, until the main thread has an answer.
 */
public final class NeoAstraServices {
    private static final String CHANNEL = "neoastra.notifications";
    private static final int PERMISSION_REQUEST = 0x4E41;
    private static volatile Context context;
    private static volatile WeakReference<Activity> resumed = new WeakReference<>(null);
    private static volatile WeakReference<Activity> current = new WeakReference<>(null);
    private static volatile CountDownLatch paused, resumedAgain;

    private NeoAstraServices() { }

    /**
     * Keeps the application context, which the services need, and follows the resumed activity, which dialogs and permission
     * requests need. The initializer calls it when the process starts.
     */
    public static void initialize(Context value) {
        context = value.getApplicationContext();
        if (context instanceof Application) ((Application) context).registerActivityLifecycleCallbacks(new Application.ActivityLifecycleCallbacks() {
            @Override public void onActivityResumed(Activity activity) { resumed = new WeakReference<>(activity); current = resumed; CountDownLatch latch = resumedAgain; if (latch != null) latch.countDown(); }
            @Override public void onActivityPaused(Activity activity) { CountDownLatch latch = paused; if (latch != null) latch.countDown(); }
            @Override public void onActivityCreated(Activity activity, Bundle state) { current = new WeakReference<>(activity); }
            @Override public void onActivityStarted(Activity activity) { }
            @Override public void onActivityStopped(Activity activity) { }
            @Override public void onActivitySaveInstanceState(Activity activity, Bundle state) { }
            @Override public void onActivityDestroyed(Activity activity) { if (resumed.get() == activity) resumed = new WeakReference<>(null); if (current.get() == activity) current = new WeakReference<>(null); }
        });
    }

    /** The activity created or resumed last that is not destroyed: the one that takes a window. */
    static Activity currentActivity() { return current.get(); }

    public static String call(String service, String request) throws Exception {
        Context current = context;
        JSONObject input = new JSONObject(request);
        JSONObject output = new JSONObject();
        if (current == null) return output.put("status", "Unsupported").toString();
        switch (service) {
            case "clipboard": clipboard(current, input, output); break;
            case "theme": theme(current, output); break;
            case "opener": opener(current, input, output); break;
            case "safe-storage": safeStorage(current, input, output); break;
            case "notifications": notifications(current, input, output); break;
            case "dialog": dialog(input, output); break;
            case "assets": assets(current, input, output); break;
            default: output.put("status", "Unsupported"); break;
        }
        return output.toString();
    }

    private static void clipboard(Context current, JSONObject input, JSONObject output) throws Exception {
        ClipboardManager clipboard = current.getSystemService(ClipboardManager.class);
        String operation = input.getString("operation");
        boolean html = "html".equals(input.optString("format"));
        switch (operation) {
            case "read": {
                ClipData clip = clipboard.getPrimaryClip();
                if (clip == null || clip.getItemCount() == 0) { output.put("status", "NotFound"); return; }
                ClipData.Item item = clip.getItemAt(0);
                String text = html ? item.getHtmlText() : (item.coerceToText(current) == null ? null : item.coerceToText(current).toString());
                if (text == null) { output.put("status", "NotFound"); return; }
                output.put("status", "Success").put("text", text);
                return;
            }
            case "write": {
                String text = input.getString("text");
                clipboard.setPrimaryClip(html ? ClipData.newHtmlText("NeoAstra", text, text) : ClipData.newPlainText("NeoAstra", text));
                output.put("status", "Success");
                return;
            }
            case "clear":
                clipboard.clearPrimaryClip();
                output.put("status", "Success");
                return;
            default:
                output.put("status", "Unsupported");
        }
    }

    private static void theme(Context current, JSONObject output) throws Exception {
        int night = current.getResources().getConfiguration().uiMode & Configuration.UI_MODE_NIGHT_MASK;
        String theme = night == Configuration.UI_MODE_NIGHT_YES ? "Dark" : night == Configuration.UI_MODE_NIGHT_NO ? "Light" : "Unknown";
        float animations = Settings.Global.getFloat(current.getContentResolver(), Settings.Global.ANIMATOR_DURATION_SCALE, 1f);
        output.put("status", "Success").put("theme", theme).put("reducedMotion", animations == 0f);
    }

    private static void opener(Context current, JSONObject input, JSONObject output) throws Exception {
        Intent intent = new Intent(Intent.ACTION_VIEW, Uri.parse(input.getString("url"))).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        try {
            current.startActivity(intent);
            output.put("status", "Success");
        } catch (ActivityNotFoundException exception) {
            output.put("status", "NoHandler");
        }
    }

    // Secrets are encrypted with an AES-GCM key of the Android Keystore that never leaves it, and stored, with their
    // initialization vector, in the private preferences of the application.
    private static void safeStorage(Context current, JSONObject input, JSONObject output) throws Exception {
        String space = input.getString("namespace");
        String key = input.getString("key");
        SharedPreferences preferences = current.getSharedPreferences("neoastra.safe-storage." + space, Context.MODE_PRIVATE);
        switch (input.getString("operation")) {
            case "store": {
                Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
                cipher.init(Cipher.ENCRYPT_MODE, secretKey(space));
                byte[] encrypted = cipher.doFinal(Base64.decode(input.getString("data"), Base64.NO_WRAP));
                byte[] iv = cipher.getIV();
                byte[] record = ByteBuffer.allocate(1 + iv.length + encrypted.length).put((byte) iv.length).put(iv).put(encrypted).array();
                boolean written = preferences.edit().putString(key, Base64.encodeToString(record, Base64.NO_WRAP)).commit();
                output.put("status", written ? "Success" : "Failed");
                return;
            }
            case "retrieve": {
                String stored = preferences.getString(key, null);
                if (stored == null) { output.put("status", "NotFound"); return; }
                try {
                    byte[] record = Base64.decode(stored, Base64.NO_WRAP);
                    int length = record[0];
                    Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
                    cipher.init(Cipher.DECRYPT_MODE, secretKey(space), new GCMParameterSpec(128, record, 1, length));
                    byte[] plain = cipher.doFinal(record, 1 + length, record.length - 1 - length);
                    output.put("status", "Success").put("data", Base64.encodeToString(plain, Base64.NO_WRAP));
                } catch (Exception exception) {
                    output.put("status", "Corrupt");
                }
                return;
            }
            case "delete":
                if (!preferences.contains(key)) { output.put("status", "NotFound"); return; }
                output.put("status", preferences.edit().remove(key).commit() ? "Success" : "Failed");
                return;
            case "contains":
                output.put("status", "Success").put("value", preferences.contains(key));
                return;
            default:
                output.put("status", "Unsupported");
        }
    }

    private static SecretKey secretKey(String space) throws Exception {
        String alias = "neoastra.safe-storage." + space;
        KeyStore store = KeyStore.getInstance("AndroidKeyStore");
        store.load(null);
        if (store.containsAlias(alias)) return ((KeyStore.SecretKeyEntry) store.getEntry(alias, null)).getSecretKey();
        KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
        generator.init(new KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setKeySize(256)
            .build());
        return generator.generateKey();
    }

    private static void notifications(Context current, JSONObject input, JSONObject output) throws Exception {
        NotificationManager manager = current.getSystemService(NotificationManager.class);
        switch (input.getString("operation")) {
            case "status": output.put("status", "Success").put("permission", permission(current, manager)); return;
            case "authorize": output.put("status", "Success").put("permission", authorize(current, manager)); return;
            case "show": {
                if (!"Granted".equals(permission(current, manager))) { output.put("status", "Denied"); return; }
                Notification.Builder builder;
                if (Build.VERSION.SDK_INT >= 26) {
                    CharSequence name = current.getApplicationInfo().loadLabel(current.getPackageManager());
                    manager.createNotificationChannel(new NotificationChannel(CHANNEL, name, NotificationManager.IMPORTANCE_DEFAULT));
                    builder = new Notification.Builder(current, CHANNEL);
                } else {
                    builder = new Notification.Builder(current);
                }
                int icon = current.getApplicationInfo().icon;
                String body = input.getString("body");
                builder.setSmallIcon(icon != 0 ? icon : android.R.drawable.ic_dialog_info)
                    .setContentTitle(input.getString("title"))
                    .setContentText(body)
                    .setStyle(new Notification.BigTextStyle().bigText(body))
                    .setAutoCancel(true);
                Intent launch = current.getPackageManager().getLaunchIntentForPackage(current.getPackageName());
                if (launch != null) builder.setContentIntent(PendingIntent.getActivity(current, 0, launch, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT));
                manager.notify(input.getString("id"), 0, builder.build());
                output.put("status", "Success");
                return;
            }
            case "remove": manager.cancel(input.getString("id"), 0); output.put("status", "Success"); return;
            default: output.put("status", "Unsupported");
        }
    }

    private static String permission(Context current, NotificationManager manager) throws Exception {
        if (Build.VERSION.SDK_INT >= 33 && current.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            if (!declared(current, Manifest.permission.POST_NOTIFICATIONS)) return "Denied";
            return current.getSharedPreferences("neoastra.notifications", Context.MODE_PRIVATE).getBoolean("requested", false) ? "Denied" : "NotRequested";
        }
        return manager.areNotificationsEnabled() ? "Granted" : "Denied";
    }

    // Asks for the notification permission on the resumed activity. The answer goes to the activity, which NeoAstra does not
    // own, so the service waits for the activity to resume after the system dialog and reads the permission again; when the
    // system answers without a dialog, the activity does not pause and the service reads it at once.
    private static String authorize(Context current, NotificationManager manager) throws Exception {
        if (Build.VERSION.SDK_INT < 33 || !"NotRequested".equals(permission(current, manager))) return permission(current, manager);
        if (Looper.myLooper() == Looper.getMainLooper()) throw new IllegalStateException("The notification permission cannot be requested on the main thread.");
        CountDownLatch pause = new CountDownLatch(1), resume = new CountDownLatch(1);
        AtomicReference<Boolean> asked = new AtomicReference<>(false);
        paused = pause;
        resumedAgain = resume;
        try {
            CountDownLatch posted = new CountDownLatch(1);
            new Handler(Looper.getMainLooper()).post(() -> {
                Activity activity = resumed.get();
                if (activity != null) { activity.requestPermissions(new String[] { Manifest.permission.POST_NOTIFICATIONS }, PERMISSION_REQUEST); asked.set(true); }
                posted.countDown();
            });
            posted.await(10, TimeUnit.SECONDS);
            if (!asked.get()) return "NotRequested";
            current.getSharedPreferences("neoastra.notifications", Context.MODE_PRIVATE).edit().putBoolean("requested", true).commit();
            if (pause.await(2, TimeUnit.SECONDS)) resume.await(2, TimeUnit.MINUTES);
        } finally {
            paused = null;
            resumedAgain = null;
        }
        return permission(current, manager);
    }

    private static boolean declared(Context current, String permission) throws Exception {
        PackageInfo info = current.getPackageManager().getPackageInfo(current.getPackageName(), PackageManager.GET_PERMISSIONS);
        if (info.requestedPermissions == null) return false;
        for (String value : info.requestedPermissions) if (permission.equals(value)) return true;
        return false;
    }

    // Shows a message on the resumed activity and waits for the role of the button the user chose. Android has three button
    // slots: the roles that cancel take the negative one, the others the positive one, and a third role the neutral one.
    private static void dialog(JSONObject input, JSONObject output) throws Exception {
        if (Looper.myLooper() == Looper.getMainLooper()) throw new IllegalStateException("A dialog cannot wait on the main thread.");
        JSONArray roles = input.getJSONArray("buttons");
        String positive = null, negative = null, neutral = null;
        boolean cancelable = false;
        for (int index = 0; index < roles.length(); index++) {
            String role = roles.getString(index);
            boolean cancel = "Cancel".equals(role) || "No".equals(role);
            if ("Cancel".equals(role)) cancelable = true;
            if (cancel && negative == null) negative = role;
            else if (!cancel && positive == null) positive = role;
            else if (neutral == null) neutral = role;
            else { output.put("status", "Unsupported"); return; }
        }
        CountDownLatch answered = new CountDownLatch(1);
        AtomicReference<String> choice = new AtomicReference<>();
        final String first = positive, second = negative, third = neutral;
        final boolean dismissible = cancelable;
        new Handler(Looper.getMainLooper()).post(() -> {
            Activity activity = resumed.get();
            if (activity == null || activity.isFinishing()) { choice.set(""); answered.countDown(); return; }
            String detail = input.optString("detail", "");
            AlertDialog.Builder builder = new AlertDialog.Builder(activity)
                .setMessage(detail.isEmpty() ? input.optString("message") : input.optString("message") + "\n\n" + detail)
                .setCancelable(dismissible)
                .setOnDismissListener(dialog -> answered.countDown());
            String title = input.optString("title", "");
            if (!title.isEmpty()) builder.setTitle(title);
            switch (input.optString("icon")) {
                case "Warning": case "Error": builder.setIcon(android.R.drawable.ic_dialog_alert); break;
                case "Information": case "Question": builder.setIcon(android.R.drawable.ic_dialog_info); break;
                default: break;
            }
            if (first != null) builder.setPositiveButton(text(first), (dialog, which) -> choice.set(first));
            if (second != null) builder.setNegativeButton(text(second), (dialog, which) -> choice.set(second));
            if (third != null) builder.setNeutralButton(text(third), (dialog, which) -> choice.set(third));
            if (dismissible) builder.setOnCancelListener(dialog -> choice.set("Cancel"));
            builder.show();
        });
        answered.await();
        String value = choice.get();
        // Without a resumed activity there is nowhere to show the dialog.
        if (value == null) output.put("status", "Canceled"); else if (value.isEmpty()) output.put("status", "Failed"); else output.put("status", "Success").put("value", value);
    }

    private static String text(String role) {
        switch (role) {
            case "Accept": return "OK";
            case "Cancel": return "Cancel";
            case "Yes": return "Yes";
            case "No": return "No";
            case "Destructive": return "Delete";
            default: return role;
        }
    }

    // The resource provider of NeoAstra serves files, and Android keeps the assets of an application inside its package: a
    // folder of the assets is copied once per installed version into the private files of the application.
    private static void assets(Context current, JSONObject input, JSONObject output) throws Exception {
        String source = input.getString("source");
        if (!source.matches("[a-z0-9][a-z0-9-]{0,63}")) { output.put("status", "Failed"); return; }
        long version = current.getPackageManager().getPackageInfo(current.getPackageName(), 0).lastUpdateTime;
        File root = new File(current.getFilesDir(), "neoastra-assets");
        File target = new File(root, source + "-" + version);
        File complete = new File(target, ".complete");
        if (!complete.exists()) {
            File[] previous = root.listFiles();
            if (previous != null) for (File file : previous) if (file.getName().startsWith(source + "-")) delete(file);
            AssetManager manager = current.getAssets();
            String[] entries = manager.list(source);
            if (entries == null || entries.length == 0) { output.put("status", "NotFound"); return; }
            copy(manager, source, target);
            if (!complete.createNewFile()) { output.put("status", "Failed"); return; }
        }
        // The files of an application are reached through the link /data/user/0, which the resource provider rejects.
        output.put("status", "Success").put("path", target.getCanonicalPath());
    }

    private static void copy(AssetManager manager, String source, File target) throws IOException {
        String[] entries = manager.list(source);
        if (entries != null && entries.length > 0) {
            if (!target.isDirectory() && !target.mkdirs()) throw new IOException("Could not create " + target);
            for (String entry : entries) copy(manager, source + "/" + entry, new File(target, entry));
            return;
        }
        try (InputStream in = manager.open(source); OutputStream out = new FileOutputStream(target)) {
            byte[] buffer = new byte[16384];
            for (int read; (read = in.read(buffer)) > 0; ) out.write(buffer, 0, read);
        }
    }

    private static void delete(File file) {
        File[] children = file.listFiles();
        if (children != null) for (File child : children) delete(child);
        //noinspection ResultOfMethodCallIgnored
        file.delete();
    }
}
