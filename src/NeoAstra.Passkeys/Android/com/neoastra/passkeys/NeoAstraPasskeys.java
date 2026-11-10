package com.neoastra.passkeys;

import android.app.Activity;
import android.content.Context;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.content.pm.ServiceInfo;
import android.os.Build;
import android.os.CancellationSignal;
import android.os.Handler;
import android.os.Looper;

import androidx.credentials.CreateCredentialResponse;
import androidx.credentials.CreatePublicKeyCredentialRequest;
import androidx.credentials.CreatePublicKeyCredentialResponse;
import androidx.credentials.Credential;
import androidx.credentials.CredentialManager;
import androidx.credentials.CredentialManagerCallback;
import androidx.credentials.CredentialProvider;
import androidx.credentials.GetCredentialRequest;
import androidx.credentials.GetCredentialResponse;
import androidx.credentials.GetPublicKeyCredentialOption;
import androidx.credentials.PublicKeyCredential;
import androidx.credentials.exceptions.CreateCredentialCancellationException;
import androidx.credentials.exceptions.CreateCredentialException;
import androidx.credentials.exceptions.GetCredentialCancellationException;
import androidx.credentials.exceptions.GetCredentialException;
import androidx.credentials.exceptions.NoCredentialException;
import androidx.credentials.exceptions.domerrors.InvalidStateError;
import androidx.credentials.exceptions.domerrors.NotAllowedError;
import androidx.credentials.exceptions.domerrors.SecurityError;
import androidx.credentials.exceptions.publickeycredential.CreatePublicKeyCredentialDomException;
import androidx.credentials.exceptions.publickeycredential.GetPublicKeyCredentialDomException;

import com.neoastra.backend.NeoAstraServices;

import org.json.JSONObject;

import java.lang.ref.WeakReference;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

/**
 * The passkeys service of the passkeys plugin of NeoAstra, which the platform services reach as {@code neoastra.passkeys}: the
 * Credential Manager creates a passkey or signs in with the WebAuthn options of the server, on the activity that the user sees.
 * A request blocks the calling thread, which is never the main thread, until the user answers the sheet. The operation
 * {@code support} names the provider that the Credential Manager would use on this device, without an activity or a wait.
 */
public final class NeoAstraPasskeys implements NeoAstraServices.Service {
    static final String NAME = "neoastra.passkeys";
    private static volatile WeakReference<Activity> resumed = new WeakReference<>(null);
    // The callbacks of the Credential Manager run on the main thread.
    private static final Executor MAIN = command -> new Handler(Looper.getMainLooper()).post(command);

    static void resumed(Activity activity) { resumed = new WeakReference<>(activity); }

    static void destroyed(Activity activity) { if (resumed.get() == activity) resumed = new WeakReference<>(null); }

    @Override public void call(Context context, JSONObject input, JSONObject output) throws Exception {
        String operation = input.getString("operation");
        if ("support".equals(operation)) { output.put("status", "Success").putOpt("provider", provider(context)); return; }
        if (Looper.myLooper() == Looper.getMainLooper()) throw new IllegalStateException("A passkey cannot wait on the main thread.");
        Activity activity = resumed.get();
        if (activity == null || activity.isFinishing()) { output.put("status", "Failed").put("code", "no-activity"); return; }
        // Without a provider the Credential Manager fails with a configuration error; the device has no passkeys.
        if (provider(activity) == null) { output.put("status", "Unsupported"); return; }
        String options = input.getString("options");
        switch (operation) {
            case "create": create(activity, options, output); return;
            case "get": get(activity, options, output); return;
            default: output.put("status", "Unsupported");
        }
    }

    /**
     * Names the provider that the Credential Manager of AndroidX chooses on this device, as its {@code CredentialProviderFactory}
     * (androidx.credentials 1.6) does without its restricted classes: {@code system} for the credential service of Android 14 and
     * later, except on a TV or in a car, and otherwise the class of the one provider that a library declares in the manifest of the
     * application (the Play services of credentials-play-services-auth) and that is available on the device; {@code null} without one,
     * as on a device without Google Play services before Android 14.
     */
    static String provider(Context context) {
        PackageManager packages = context.getPackageManager();
        boolean tvOrCar = packages.hasSystemFeature("android.software.leanback") || packages.hasSystemFeature("android.hardware.type.automotive");
        if (!tvOrCar && Build.VERSION.SDK_INT >= 34 && context.getSystemService("credential") != null) return "system";
        String found = null;
        for (String name : declaredProviders(context)) {
            try {
                Object instance = Class.forName(name).getConstructor(Context.class).newInstance(context);
                if (!(instance instanceof CredentialProvider) || !((CredentialProvider) instance).isAvailableOnDevice()) continue;
                // The Credential Manager uses none when two are available.
                if (found != null) return null;
                found = name;
            } catch (ReflectiveOperationException | RuntimeException | LinkageError error) {
                // A provider that cannot be created is not available, as for the Credential Manager.
            }
        }
        return found;
    }

    // The provider classes that the services of the manifest of the application declare with the key of the Credential Manager.
    private static List<String> declaredProviders(Context context) {
        List<String> names = new ArrayList<>();
        try {
            PackageInfo info = context.getPackageManager().getPackageInfo(context.getPackageName(), PackageManager.GET_META_DATA | PackageManager.GET_SERVICES);
            if (info.services == null) return names;
            for (ServiceInfo service : info.services) {
                String name = service.metaData != null ? service.metaData.getString("androidx.credentials.CREDENTIAL_PROVIDER_KEY") : null;
                if (name != null) names.add(name);
            }
        } catch (PackageManager.NameNotFoundException error) {
            // The package of the application itself is always found.
        }
        return names;
    }

    private static void create(Activity activity, String options, JSONObject output) throws Exception {
        CountDownLatch answered = new CountDownLatch(1);
        AtomicReference<Object> result = new AtomicReference<>();
        CancellationSignal cancellation = new CancellationSignal();
        activity.runOnUiThread(() -> CredentialManager.create(activity).createCredentialAsync(activity, new CreatePublicKeyCredentialRequest(options), cancellation, MAIN,
            new CredentialManagerCallback<CreateCredentialResponse, CreateCredentialException>() {
                @Override public void onResult(CreateCredentialResponse response) { result.set(response); answered.countDown(); }
                @Override public void onError(CreateCredentialException error) { result.set(error); answered.countDown(); }
            }));
        if (!answered.await(3, TimeUnit.MINUTES)) { cancellation.cancel(); output.put("status", "Canceled"); return; }
        Object value = result.get();
        if (value instanceof CreatePublicKeyCredentialResponse) { output.put("status", "Success").put("response", ((CreatePublicKeyCredentialResponse) value).getRegistrationResponseJson()); return; }
        if (value instanceof CreateCredentialCancellationException) { output.put("status", "Canceled"); return; }
        if (value instanceof CreatePublicKeyCredentialDomException) {
            Object error = ((CreatePublicKeyCredentialDomException) value).getDomError();
            // The device already holds a passkey that the options exclude.
            if (error instanceof InvalidStateError) { output.put("status", "Conflict").put("code", "excluded"); return; }
            if (error instanceof SecurityError) { output.put("status", "Denied").put("code", "not-associated"); return; }
            if (error instanceof NotAllowedError) { output.put("status", "Canceled"); return; }
        }
        output.put("status", "Failed").putOpt("code", value instanceof CreateCredentialException ? ((CreateCredentialException) value).getType() : null);
    }

    private static void get(Activity activity, String options, JSONObject output) throws Exception {
        CountDownLatch answered = new CountDownLatch(1);
        AtomicReference<Object> result = new AtomicReference<>();
        CancellationSignal cancellation = new CancellationSignal();
        GetCredentialRequest request = new GetCredentialRequest(Collections.singletonList(new GetPublicKeyCredentialOption(options)));
        activity.runOnUiThread(() -> CredentialManager.create(activity).getCredentialAsync(activity, request, cancellation, MAIN,
            new CredentialManagerCallback<GetCredentialResponse, GetCredentialException>() {
                @Override public void onResult(GetCredentialResponse response) { result.set(response); answered.countDown(); }
                @Override public void onError(GetCredentialException error) { result.set(error); answered.countDown(); }
            }));
        if (!answered.await(3, TimeUnit.MINUTES)) { cancellation.cancel(); output.put("status", "Canceled"); return; }
        Object value = result.get();
        if (value instanceof GetCredentialResponse) {
            Credential credential = ((GetCredentialResponse) value).getCredential();
            if (credential instanceof PublicKeyCredential) { output.put("status", "Success").put("response", ((PublicKeyCredential) credential).getAuthenticationResponseJson()); return; }
            output.put("status", "Failed"); return;
        }
        if (value instanceof GetCredentialCancellationException) { output.put("status", "Canceled"); return; }
        if (value instanceof NoCredentialException) { output.put("status", "NotFound").put("code", "no-credential"); return; }
        if (value instanceof GetPublicKeyCredentialDomException) {
            Object error = ((GetPublicKeyCredentialDomException) value).getDomError();
            if (error instanceof SecurityError) { output.put("status", "Denied").put("code", "not-associated"); return; }
            if (error instanceof NotAllowedError) { output.put("status", "Canceled"); return; }
        }
        output.put("status", "Failed").putOpt("code", value instanceof GetCredentialException ? ((GetCredentialException) value).getType() : null);
    }
}
