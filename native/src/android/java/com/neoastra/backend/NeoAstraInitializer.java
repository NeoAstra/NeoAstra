package com.neoastra.backend;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.database.Cursor;
import android.net.Uri;

/**
 * Loads the Java side of NeoAstra when the process starts, before any activity: only a load through Java runs the JNI_OnLoad
 * of the native library, which NeoAstra needs before an application creates an environment. It provides no content.
 */
public final class NeoAstraInitializer extends ContentProvider {
    @Override public boolean onCreate() {
        NeoAstraWebHost.load();
        return true;
    }

    @Override public Cursor query(Uri uri, String[] projection, String selection, String[] selectionArgs, String sortOrder) { return null; }
    @Override public String getType(Uri uri) { return null; }
    @Override public Uri insert(Uri uri, ContentValues values) { return null; }
    @Override public int delete(Uri uri, String selection, String[] selectionArgs) { return 0; }
    @Override public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) { return 0; }
}
