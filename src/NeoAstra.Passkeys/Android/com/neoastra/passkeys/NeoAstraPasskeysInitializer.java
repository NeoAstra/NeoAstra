package com.neoastra.passkeys;

import android.app.Activity;
import android.app.Application;
import android.content.ContentProvider;
import android.content.ContentValues;
import android.database.Cursor;
import android.net.Uri;
import android.os.Bundle;

import com.neoastra.backend.NeoAstraServices;

/**
 * Adds the passkeys service to the platform services of NeoAstra when the process starts, and follows the activities: the
 * sheet of the Credential Manager shows on the one that the user sees. It provides no content.
 */
public final class NeoAstraPasskeysInitializer extends ContentProvider {
    @Override public boolean onCreate() {
        NeoAstraServices.register(NeoAstraPasskeys.NAME, new NeoAstraPasskeys());
        if (getContext().getApplicationContext() instanceof Application) ((Application) getContext().getApplicationContext()).registerActivityLifecycleCallbacks(new Application.ActivityLifecycleCallbacks() {
            @Override public void onActivityResumed(Activity activity) { NeoAstraPasskeys.resumed(activity); }
            @Override public void onActivityPaused(Activity activity) { }
            @Override public void onActivityCreated(Activity activity, Bundle state) { }
            @Override public void onActivityStarted(Activity activity) { }
            @Override public void onActivityStopped(Activity activity) { }
            @Override public void onActivitySaveInstanceState(Activity activity, Bundle state) { }
            @Override public void onActivityDestroyed(Activity activity) { NeoAstraPasskeys.destroyed(activity); }
        });
        return true;
    }

    @Override public Cursor query(Uri uri, String[] projection, String selection, String[] selectionArgs, String sortOrder) { return null; }
    @Override public String getType(Uri uri) { return null; }
    @Override public Uri insert(Uri uri, ContentValues values) { return null; }
    @Override public int delete(Uri uri, String selection, String[] selectionArgs) { return 0; }
    @Override public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) { return 0; }
}
