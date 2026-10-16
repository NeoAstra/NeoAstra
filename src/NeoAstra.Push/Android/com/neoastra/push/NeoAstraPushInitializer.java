package com.neoastra.push;

import android.app.Activity;
import android.app.Application;
import android.content.ContentProvider;
import android.content.ContentValues;
import android.database.Cursor;
import android.net.Uri;
import android.os.Bundle;

import com.neoastra.backend.NeoAstraServices;

/**
 * Adds the push service to the platform services of NeoAstra when the process starts, and follows the activities: an activity
 * that the user opened by tapping a notification has the message in the extras of its intent. It provides no content.
 */
public final class NeoAstraPushInitializer extends ContentProvider {
    @Override public boolean onCreate() {
        NeoAstraServices.register(NeoAstraPush.NAME, new NeoAstraPush());
        if (getContext().getApplicationContext() instanceof Application) ((Application) getContext().getApplicationContext()).registerActivityLifecycleCallbacks(new Application.ActivityLifecycleCallbacks() {
            @Override public void onActivityResumed(Activity activity) { NeoAstraPush.resumed(activity); }
            @Override public void onActivityPaused(Activity activity) { NeoAstraPush.paused(); }
            @Override public void onActivityCreated(Activity activity, Bundle state) { }
            @Override public void onActivityStarted(Activity activity) { }
            @Override public void onActivityStopped(Activity activity) { }
            @Override public void onActivitySaveInstanceState(Activity activity, Bundle state) { }
            @Override public void onActivityDestroyed(Activity activity) { }
        });
        return true;
    }

    @Override public Cursor query(Uri uri, String[] projection, String selection, String[] selectionArgs, String sortOrder) { return null; }
    @Override public String getType(Uri uri) { return null; }
    @Override public Uri insert(Uri uri, ContentValues values) { return null; }
    @Override public int delete(Uri uri, String selection, String[] selectionArgs) { return 0; }
    @Override public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) { return 0; }
}
