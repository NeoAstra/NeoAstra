package com.neoastra.location;

import android.Manifest;
import android.content.Context;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.location.Location;
import android.location.LocationListener;
import android.location.LocationManager;
import android.location.LocationRequest;
import android.os.Build;
import android.os.Bundle;
import android.os.CancellationSignal;
import android.os.Looper;
import android.os.SystemClock;

import com.neoastra.backend.NeoAstraServices;

import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

/**
 * The location service of the location plugin of NeoAstra, which the platform services reach as {@code neoastra.location}: the
 * permission of the user, and the current location from the location manager of the platform while the application is in use.
 * A request blocks the calling thread, which is never the main thread, until the user answers or a fix arrives.
 */
public final class NeoAstraLocation implements NeoAstraServices.Service {
    static final String NAME = "neoastra.location";
    private static final String FINE = Manifest.permission.ACCESS_FINE_LOCATION, COARSE = Manifest.permission.ACCESS_COARSE_LOCATION;
    private static final long COARSE_INTERVAL = 10 * 60 * 1000;

    @Override public void call(Context context, JSONObject input, JSONObject output) throws Exception {
        LocationManager manager = context.getSystemService(LocationManager.class);
        if (manager == null) { output.put("status", "Unsupported"); return; }
        switch (input.getString("operation")) {
            case "authorization": authorization(context, manager, output); return;
            case "authorize": authorize(context, manager, output); return;
            case "current": current(context, manager, input, output); return;
            default: output.put("status", "Unsupported");
        }
    }

    private static void authorization(Context context, LocationManager manager, JSONObject output) throws Exception {
        boolean fine = granted(context, FINE), coarse = granted(context, COARSE);
        String permission;
        if (fine || coarse) permission = "Granted";
        else if (!declared(context, FINE) && !declared(context, COARSE)) permission = "Denied";
        // Android does not ask again after a refusal, so a missing permission reads as denied once the application asked.
        else permission = context.getSharedPreferences(NAME, Context.MODE_PRIVATE).getBoolean("requested", false) ? "Denied" : "NotRequested";
        output.put("status", "Success").put("permission", permission).put("precise", fine).put("enabled", enabled(manager));
    }

    // Asks for the location permissions that the application declares: both let the user choose a precise or an approximate
    // location from Android 12, and only the coarse one asks for an approximate location.
    private static void authorize(Context context, LocationManager manager, JSONObject output) throws Exception {
        boolean fine = declared(context, FINE), coarse = declared(context, COARSE);
        if ((fine || coarse) && !granted(context, FINE) && !granted(context, COARSE) && !context.getSharedPreferences(NAME, Context.MODE_PRIVATE).getBoolean("requested", false)) {
            context.getSharedPreferences(NAME, Context.MODE_PRIVATE).edit().putBoolean("requested", true).commit();
            List<String> permissions = new ArrayList<>();
            if (fine) permissions.add(FINE);
            if (coarse) permissions.add(COARSE);
            NeoAstraServices.requestPermissions(permissions.toArray(new String[0]));
        }
        authorization(context, manager, output);
    }

    private static void current(Context context, LocationManager manager, JSONObject input, JSONObject output) throws Exception {
        if (!enabled(manager)) { output.put("status", "Denied").put("code", "service-disabled"); return; }
        boolean fine = granted(context, FINE);
        if (!fine && !granted(context, COARSE)) { output.put("status", "Denied").put("code", "permission"); return; }
        long timeout = Math.max(1000, Math.min(120_000, input.optLong("timeout", 30_000)));
        long maximumAge = Math.max(0, input.optLong("maximumAge", 0));
        boolean high = "High".equals(input.optString("accuracy"));
        try {
            // Android gives an approximate location at most every ten minutes, so the approximate fix it last gave is the current one.
            Location location = fine ? (maximumAge > 0 ? lastKnown(manager, maximumAge) : null) : lastKnown(manager, Math.max(maximumAge, COARSE_INTERVAL));
            if (location == null) {
                String provider = provider(manager, high, fine);
                if (provider == null) { output.put("status", "Denied").put("code", "service-disabled"); return; }
                location = fresh(context, manager, provider, high, timeout);
            }
            if (location == null) { output.put("status", "NotFound").put("code", "timeout"); return; }
            write(location, output.put("status", "Success"));
        } catch (SecurityException exception) {
            output.put("status", "Denied").put("code", "permission");
        }
    }

    // The fused provider combines satellites and networks where the platform has it; otherwise satellites give the accurate fix
    // that the precise permission allows, and networks the balanced one.
    private static String provider(LocationManager manager, boolean high, boolean fine) {
        if (Build.VERSION.SDK_INT >= 31 && manager.hasProvider(LocationManager.FUSED_PROVIDER) && manager.isProviderEnabled(LocationManager.FUSED_PROVIDER)) return LocationManager.FUSED_PROVIDER;
        if (fine && high && manager.isProviderEnabled(LocationManager.GPS_PROVIDER)) return LocationManager.GPS_PROVIDER;
        if (manager.isProviderEnabled(LocationManager.NETWORK_PROVIDER)) return LocationManager.NETWORK_PROVIDER;
        if (fine && manager.isProviderEnabled(LocationManager.GPS_PROVIDER)) return LocationManager.GPS_PROVIDER;
        return null;
    }

    private static Location lastKnown(LocationManager manager, long maximumAge) {
        Location best = null;
        long now = SystemClock.elapsedRealtimeNanos();
        for (String provider : manager.getProviders(true)) {
            Location location;
            try { location = manager.getLastKnownLocation(provider); }
            catch (SecurityException | IllegalArgumentException exception) { continue; }
            if (location == null || (now - location.getElapsedRealtimeNanos()) / 1_000_000 > maximumAge) continue;
            if (best == null || location.getElapsedRealtimeNanos() > best.getElapsedRealtimeNanos()) best = location;
        }
        return best;
    }

    @SuppressWarnings("deprecation")
    private static Location fresh(Context context, LocationManager manager, String provider, boolean high, long timeout) throws InterruptedException {
        AtomicReference<Location> result = new AtomicReference<>();
        CountDownLatch received = new CountDownLatch(1);
        if (Build.VERSION.SDK_INT >= 30) {
            CancellationSignal cancellation = new CancellationSignal();
            java.util.function.Consumer<Location> consumer = location -> { result.set(location); received.countDown(); };
            if (Build.VERSION.SDK_INT >= 31) {
                LocationRequest request = new LocationRequest.Builder(0)
                    .setQuality(high ? LocationRequest.QUALITY_HIGH_ACCURACY : LocationRequest.QUALITY_BALANCED_POWER_ACCURACY)
                    .setDurationMillis(timeout)
                    .build();
                manager.getCurrentLocation(provider, request, cancellation, context.getMainExecutor(), consumer);
            } else {
                manager.getCurrentLocation(provider, cancellation, context.getMainExecutor(), consumer);
            }
            if (!received.await(timeout, TimeUnit.MILLISECONDS)) cancellation.cancel();
        } else {
            // Before Android 11 a listener needs every method, which the system calls on the main looper.
            LocationListener listener = new LocationListener() {
                @Override public void onLocationChanged(Location location) { result.set(location); received.countDown(); }
                @Override public void onStatusChanged(String name, int status, Bundle extras) { }
                @Override public void onProviderEnabled(String name) { }
                @Override public void onProviderDisabled(String name) { }
            };
            manager.requestSingleUpdate(provider, listener, Looper.getMainLooper());
            if (!received.await(timeout, TimeUnit.MILLISECONDS)) manager.removeUpdates(listener);
        }
        return result.get();
    }

    private static void write(Location location, JSONObject output) throws Exception {
        output.put("latitude", location.getLatitude()).put("longitude", location.getLongitude()).put("accuracy", (double) location.getAccuracy()).put("timestamp", location.getTime());
        if (location.hasAltitude()) output.put("altitude", location.getAltitude());
        if (Build.VERSION.SDK_INT >= 26 && location.hasVerticalAccuracy()) output.put("altitudeAccuracy", (double) location.getVerticalAccuracyMeters());
        if (location.hasSpeed()) output.put("speed", (double) location.getSpeed());
        if (location.hasBearing()) output.put("course", (double) location.getBearing());
    }

    @SuppressWarnings("deprecation")
    private static boolean enabled(LocationManager manager) {
        if (Build.VERSION.SDK_INT >= 28) return manager.isLocationEnabled();
        return manager.isProviderEnabled(LocationManager.GPS_PROVIDER) || manager.isProviderEnabled(LocationManager.NETWORK_PROVIDER);
    }

    private static boolean granted(Context context, String permission) { return context.checkSelfPermission(permission) == PackageManager.PERMISSION_GRANTED; }

    private static boolean declared(Context context, String permission) throws Exception {
        PackageInfo info = context.getPackageManager().getPackageInfo(context.getPackageName(), PackageManager.GET_PERMISSIONS);
        if (info.requestedPermissions == null) return false;
        for (String value : info.requestedPermissions) if (permission.equals(value)) return true;
        return false;
    }
}
