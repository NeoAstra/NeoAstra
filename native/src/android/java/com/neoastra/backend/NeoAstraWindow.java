package com.neoastra.backend;

import android.app.Activity;
import android.app.ActivityManager;
import android.app.Application;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.os.Build;
import android.os.Bundle;
import android.view.View;
import android.view.Window;
import android.view.WindowInsetsController;
import android.widget.FrameLayout;
import android.window.OnBackInvokedCallback;
import android.window.OnBackInvokedDispatcher;

import java.util.Collections;
import java.util.Set;
import java.util.WeakHashMap;

/**
 * A window of NeoAstra on Android: the content of the current activity, which it replaces with a layout of its own. One
 * activity has at most one window. The window fills the activity, keeps clear of the system bars unless the application
 * opted into edge-to-edge windows, and ends with the activity; back asks the application to close it on Android 13 and later, and finishes the activity before.
 *
 * <p>The native library drives it through JNI on the main thread and receives what it reports through
 * {@link #nativeWindowEvent}, which it registers when Java loads it. Every member that JNI reaches is public so that R8
 * keeps its name.</p>
 */
public final class NeoAstraWindow {
    /** Event kinds reported to the native library. */
    public static final int EVENT_RESIZED = 0, EVENT_CLOSE_REQUESTED = 1, EVENT_CLOSED = 2, EVENT_FOCUS = 3;

    public static native void nativeWindowEvent(long window, int kind, int width, int height, float density);

    // The activities that have a window; all access is on the main thread.
    private static final Set<Activity> BOUND = Collections.newSetFromMap(new WeakHashMap<>());

    private final Activity activity;
    private final FrameLayout content;
    private final Application.ActivityLifecycleCallbacks callbacks;
    private volatile long window;
    private Object back;
    private int width = -1, height = -1;

    /** Creates the window of the current activity, or returns null when there is none or it already has a window. */
    public static NeoAstraWindow create(long window, String title, int background) {
        Activity activity = NeoAstraServices.currentActivity();
        if (activity == null || activity.isFinishing() || BOUND.contains(activity)) return null;
        return new NeoAstraWindow(activity, window, title, background);
    }

    private NeoAstraWindow(Activity owner, long nativeWindow, String title, int background) {
        activity = owner;
        window = nativeWindow;
        content = new FrameLayout(owner);
        // By default the layout keeps its content clear of the system bars, which Android 15 otherwise draws over an
        // application. With the edge-to-edge opt-in the window draws under them, transparent, and the WebView reports their
        // size to the page as env(safe-area-inset-*).
        boolean edgeToEdge = edgeToEdge(owner);
        content.setFitsSystemWindows(!edgeToEdge);
        if (edgeToEdge) drawUnderSystemBars(owner);
        content.addOnLayoutChangeListener((view, left, top, right, bottom, oldLeft, oldTop, oldRight, oldBottom) -> reportSize());
        owner.setContentView(content);
        // The system bars take their appearance from the decor view, which the content view creates.
        setBackground(background);
        setTitle(title);
        BOUND.add(owner);
        callbacks = new Application.ActivityLifecycleCallbacks() {
            @Override public void onActivityDestroyed(Activity value) { if (value == activity) report(EVENT_CLOSED); }
            @Override public void onActivityResumed(Activity value) { if (value == activity) report(EVENT_FOCUS, 1); }
            @Override public void onActivityPaused(Activity value) { if (value == activity) report(EVENT_FOCUS, 0); }
            @Override public void onActivityCreated(Activity value, Bundle state) { }
            @Override public void onActivityStarted(Activity value) { }
            @Override public void onActivityStopped(Activity value) { }
            @Override public void onActivitySaveInstanceState(Activity value, Bundle state) { }
        };
        owner.getApplication().registerActivityLifecycleCallbacks(callbacks);
        if (Build.VERSION.SDK_INT >= 33) {
            OnBackInvokedCallback callback = () -> report(EVENT_CLOSE_REQUESTED);
            owner.getOnBackInvokedDispatcher().registerOnBackInvokedCallback(OnBackInvokedDispatcher.PRIORITY_DEFAULT, callback);
            back = callback;
        }
    }

    /** The title of the activity, which accessibility services read and the list of recent tasks shows. */
    @SuppressWarnings("deprecation")
    public void setTitle(String title) {
        activity.setTitle(title);
        activity.setTaskDescription(Build.VERSION.SDK_INT >= 33 ? new ActivityManager.TaskDescription.Builder().setLabel(title).build() : new ActivityManager.TaskDescription(title));
    }

    public void setVisible(boolean visible) { content.setVisibility(visible ? View.VISIBLE : View.INVISIBLE); }

    /** The background, which shows behind the system bars too: their symbols are dark on a light background. */
    @SuppressWarnings("deprecation")
    public void setBackground(int color) {
        content.setBackgroundColor(color);
        boolean light = Color.alpha(color) > 0 && (0.299 * Color.red(color) + 0.587 * Color.green(color) + 0.114 * Color.blue(color)) > 160;
        if (Build.VERSION.SDK_INT >= 30) {
            WindowInsetsController controller = activity.getWindow().getInsetsController();
            int mask = WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS | WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS;
            if (controller != null) controller.setSystemBarsAppearance(light ? mask : 0, mask);
        } else if (Build.VERSION.SDK_INT >= 23) {
            View decor = activity.getWindow().getDecorView();
            int flags = decor.getSystemUiVisibility();
            decor.setSystemUiVisibility(light ? flags | View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR : flags & ~View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR);
        }
    }

    public FrameLayout content() { return content; }

    public int width() { return content.getWidth() > 0 ? content.getWidth() : activity.getWindow().getDecorView().getWidth(); }

    public int height() { return content.getHeight() > 0 ? content.getHeight() : activity.getWindow().getDecorView().getHeight(); }

    public float density() { return activity.getResources().getDisplayMetrics().density; }

    /** Closes the window: the activity finishes, and its end is reported as the end of the window. */
    public void close() { if (!activity.isFinishing()) activity.finish(); else report(EVENT_CLOSED); }

    public void destroy() {
        window = 0;
        BOUND.remove(activity);
        activity.getApplication().unregisterActivityLifecycleCallbacks(callbacks);
        if (Build.VERSION.SDK_INT >= 33 && back != null) activity.getOnBackInvokedDispatcher().unregisterOnBackInvokedCallback((OnBackInvokedCallback) back);
        back = null;
    }

    /** Whether the application opted into edge-to-edge windows (NeoAstraAndroidEdgeToEdge=true merges the meta-data). */
    private static boolean edgeToEdge(Activity activity) {
        try {
            Bundle metaData = activity.getPackageManager().getApplicationInfo(activity.getPackageName(), PackageManager.GET_META_DATA).metaData;
            return metaData != null && metaData.getBoolean("com.neoastra.window.edgeToEdge", false);
        } catch (PackageManager.NameNotFoundException e) {
            return false;
        }
    }

    @SuppressWarnings("deprecation")
    private static void drawUnderSystemBars(Activity activity) {
        Window window = activity.getWindow();
        if (Build.VERSION.SDK_INT >= 30) {
            window.setDecorFitsSystemWindows(false);
        } else {
            View decor = window.getDecorView();
            decor.setSystemUiVisibility(decor.getSystemUiVisibility() | View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION);
        }
        window.setStatusBarColor(Color.TRANSPARENT);
        window.setNavigationBarColor(Color.TRANSPARENT);
        // The system would otherwise lay a translucent scrim under the navigation buttons.
        if (Build.VERSION.SDK_INT >= 29) window.setNavigationBarContrastEnforced(false);
    }

    private void reportSize() {
        int newWidth = content.getWidth(), newHeight = content.getHeight();
        if (newWidth == width && newHeight == height) return;
        width = newWidth;
        height = newHeight;
        report(EVENT_RESIZED);
    }

    private void report(int kind) { report(kind, 0); }

    private void report(int kind, int value) {
        long current = window;
        if (current == 0) return;
        if (kind == EVENT_CLOSED) window = 0;
        nativeWindowEvent(current, kind, kind == EVENT_FOCUS ? value : width(), height(), density());
    }
}
