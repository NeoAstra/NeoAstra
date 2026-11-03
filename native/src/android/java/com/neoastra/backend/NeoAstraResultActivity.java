package com.neoastra.backend;

import android.app.Activity;
import android.content.ActivityNotFoundException;
import android.content.Context;
import android.content.Intent;
import android.os.Bundle;

import java.util.HashMap;
import java.util.Map;

/**
 * An invisible activity that asks the system for something whose answer only an activity receives: the result of a picker or
 * the runtime permissions of the application. NeoAstra does not own the activity of the application, so it starts this one on
 * top of it, which finishes as soon as it has the answer.
 */
public final class NeoAstraResultActivity extends Activity {
    /** Receives the answer on the main thread: the result code and data of a picker, or the grants of the permissions. */
    interface Result { void deliver(int resultCode, Intent data, int[] grants); }

    private static final String ID = "neoastra.id", INTENT = "neoastra.intent", PERMISSIONS = "neoastra.permissions";
    private static final Map<Integer, Result> PENDING = new HashMap<>();
    private static int next = 1;
    private int id;

    /** Starts an activity for its result, such as the picker of a file chooser. */
    static void startForResult(Intent intent, Result result) { start(result, launch -> launch.putExtra(INTENT, intent)); }

    /** Asks for runtime permissions of the application. */
    static void requestPermissions(String[] permissions, Result result) { start(result, launch -> launch.putExtra(PERMISSIONS, permissions)); }

    private interface Extras { void put(Intent launch); }

    private static void start(Result result, Extras extras) {
        Activity current = NeoAstraServices.currentActivity();
        int identifier = next++;
        PENDING.put(identifier, result);
        Context context = current != null ? current : NeoAstraServices.context();
        Intent launch = new Intent(context, NeoAstraResultActivity.class).putExtra(ID, identifier);
        if (current == null) launch.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        extras.put(launch);
        try { context.startActivity(launch); }
        catch (RuntimeException exception) { finish(identifier, Activity.RESULT_CANCELED, null, new int[0]); }
    }

    private static void finish(int identifier, int resultCode, Intent data, int[] grants) {
        Result result = PENDING.remove(identifier);
        if (result != null) result.deliver(resultCode, data, grants);
    }

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        id = getIntent().getIntExtra(ID, 0);
        if (state != null) return;
        Intent intent = getIntent().getParcelableExtra(INTENT);
        String[] permissions = getIntent().getStringArrayExtra(PERMISSIONS);
        if (intent != null) {
            try { startActivityForResult(intent, 1); }
            catch (ActivityNotFoundException exception) { complete(Activity.RESULT_CANCELED, null, new int[0]); }
        } else if (permissions != null) {
            requestPermissions(permissions, 1);
        } else {
            complete(Activity.RESULT_CANCELED, null, new int[0]);
        }
    }

    @Override protected void onActivityResult(int request, int resultCode, Intent data) { complete(resultCode, data, new int[0]); }

    @Override public void onRequestPermissionsResult(int request, String[] permissions, int[] grants) { complete(Activity.RESULT_OK, null, grants); }

    @Override protected void onDestroy() {
        // An activity that the system ends before it answered answers nothing, which cancels the request.
        if (isFinishing()) finish(id, Activity.RESULT_CANCELED, null, new int[0]);
        super.onDestroy();
    }

    private void complete(int resultCode, Intent data, int[] grants) {
        finish(id, resultCode, data, grants);
        finish();
        overridePendingTransition(0, 0);
    }
}
