package com.neoastra.location;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.database.Cursor;
import android.net.Uri;

import com.neoastra.backend.NeoAstraServices;

/** Adds the location service to the platform services of NeoAstra when the process starts. It provides no content. */
public final class NeoAstraLocationInitializer extends ContentProvider {
    @Override public boolean onCreate() {
        NeoAstraServices.register(NeoAstraLocation.NAME, new NeoAstraLocation());
        return true;
    }

    @Override public Cursor query(Uri uri, String[] projection, String selection, String[] selectionArgs, String sortOrder) { return null; }
    @Override public String getType(Uri uri) { return null; }
    @Override public Uri insert(Uri uri, ContentValues values) { return null; }
    @Override public int delete(Uri uri, String selection, String[] selectionArgs) { return 0; }
    @Override public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) { return 0; }
}
