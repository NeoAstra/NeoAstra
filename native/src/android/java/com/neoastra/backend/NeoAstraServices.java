package com.neoastra.backend;

import android.content.ActivityNotFoundException;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.res.Configuration;
import android.net.Uri;
import android.provider.Settings;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;

import org.json.JSONObject;

import java.nio.ByteBuffer;
import java.security.KeyStore;

import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/**
 * The platform services of the Android backend: what the desktop services of NeoAstra need from Java. The native library
 * calls {@link #call} with one JSON request and returns the JSON response to NeoAstra. Every member that JNI reaches is
 * public so that R8 keeps its name.
 */
public final class NeoAstraServices {
    private static volatile Context context;

    private NeoAstraServices() { }

    /** Keeps the application context, which the services need. The initializer calls it when the process starts. */
    public static void initialize(Context value) { context = value.getApplicationContext(); }

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
}
