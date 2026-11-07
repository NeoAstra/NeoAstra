package com.neoastra.backend;

import android.app.Activity;
import android.app.ActivityManager;
import android.app.Application;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.content.Context;
import android.graphics.Color;
import android.hardware.display.DisplayManager;
import android.os.Build;
import android.os.Bundle;
import android.util.DisplayMetrics;
import android.view.Display;
import android.view.View;
import android.view.Window;
import android.view.WindowInsetsController;
import android.widget.FrameLayout;
import android.window.OnBackInvokedCallback;
import android.window.OnBackInvokedDispatcher;
import androidx.webkit.WebViewCompat;

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
    private final boolean underSystemBars;

    /**
     * The first major version of the WebView (Chromium 136) that reports the system bars to the page as env(safe-area-inset-*);
     * earlier versions report only display cutouts there, on every version of Android.
     */
    static final int EDGE_TO_EDGE_WEBVIEW = 136;

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
        // size to the page as env(safe-area-inset-*); a WebView before Chromium 136 reports only display cutouts there, so the
        // window keeps clear of the bars with such a WebView even with the opt-in.
        underSystemBars = edgeToEdge(owner);
        content.setFitsSystemWindows(!underSystemBars);
        if (underSystemBars) drawUnderSystemBars(owner);
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
        }
        // Android 11 lets the flags of the decor view override the appearance of the controller (Fire OS on Android 11 showed
        // white symbols on a light page), so the flags say the same there, as AndroidX does.
        if (Build.VERSION.SDK_INT >= 23 && Build.VERSION.SDK_INT <= 30) {
            View decor = activity.getWindow().getDecorView();
            // The navigation bar shows the page only under a window that draws under it; otherwise it keeps the color of the theme.
            int mask = View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR | (underSystemBars && Build.VERSION.SDK_INT >= 26 ? View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR : 0);
            int flags = decor.getSystemUiVisibility();
            decor.setSystemUiVisibility(light ? flags | mask : flags & ~mask);
        }
    }

    public FrameLayout content() { return content; }

    public int width() { return content.getWidth() > 0 ? content.getWidth() : activity.getWindow().getDecorView().getWidth(); }

    public int height() { return content.getHeight() > 0 ? content.getHeight() : activity.getWindow().getDecorView().getHeight(); }

    public float density() { return activity.getResources().getDisplayMetrics().density; }

    /** The identifier of the display that shows the activity, or -1 when it has none. */
    @SuppressWarnings("deprecation")
    public int displayId() {
        Display display = Build.VERSION.SDK_INT >= 30 ? activity.getDisplay() : activity.getWindowManager().getDefaultDisplay();
        return display != null ? display.getDisplayId() : -1;
    }

    /** The values of each display that DisplayManager knows, in a flat array of {@link #DISPLAY_VALUES} values per display. */
    public static final int DISPLAY_VALUES = 7;

    /**
     * The displays, each as its identifier, its size in pixels, its density, its refresh rate, its rotation in degrees, and 1 for the
     * default display or 0, in that order.
     */
    @SuppressWarnings("deprecation")
    public static double[] displays() {
        Context current = NeoAstraServices.context();
        DisplayManager manager = current != null ? current.getSystemService(DisplayManager.class) : null;
        Display[] values = manager != null ? manager.getDisplays() : new Display[0];
        double[] output = new double[values.length * DISPLAY_VALUES];
        for (int index = 0; index < values.length; index++) {
            Display display = values[index];
            DisplayMetrics metrics = new DisplayMetrics();
            display.getRealMetrics(metrics);
            int offset = index * DISPLAY_VALUES;
            output[offset] = display.getDisplayId();
            output[offset + 1] = metrics.widthPixels;
            output[offset + 2] = metrics.heightPixels;
            output[offset + 3] = metrics.density;
            output[offset + 4] = display.getRefreshRate();
            output[offset + 5] = display.getRotation() * 90;
            output[offset + 6] = display.getDisplayId() == Display.DEFAULT_DISPLAY ? 1 : 0;
        }
        return output;
    }

    /** Closes the window: the activity finishes, and its end is reported as the end of the window. */
    public void close() { if (!activity.isFinishing()) activity.finish(); else report(EVENT_CLOSED); }

    public void destroy() {
        window = 0;
        BOUND.remove(activity);
        activity.getApplication().unregisterActivityLifecycleCallbacks(callbacks);
        if (Build.VERSION.SDK_INT >= 33 && back != null) activity.getOnBackInvokedDispatcher().unregisterOnBackInvokedCallback((OnBackInvokedCallback) back);
        back = null;
    }

    /**
     * Whether the window draws under the system bars: the application opted into edge-to-edge windows (NeoAstraAndroidEdgeToEdge=true
     * merges the meta-data) and the WebView reports the bars as env(safe-area-inset-*), which depends on its version, not on the
     * version of Android.
     */
    private static boolean edgeToEdge(Activity activity) {
        if (webViewMajorVersion(activity) < EDGE_TO_EDGE_WEBVIEW) return false;
        try {
            Bundle metaData = activity.getPackageManager().getApplicationInfo(activity.getPackageName(), PackageManager.GET_META_DATA).metaData;
            return metaData != null && metaData.getBoolean("com.neoastra.window.edgeToEdge", false);
        } catch (PackageManager.NameNotFoundException e) {
            return false;
        }
    }

    /**
     * The major version of the current WebView package, the number its version name starts with ("136.0.7103.60" of Google,
     * "150.amazon-webview-v150-…" of Fire OS), or 0 when it is unknown.
     */
    static int webViewMajorVersion(Context context) {
        PackageInfo info = WebViewCompat.getCurrentWebViewPackage(context);
        String name = info != null ? info.versionName : null;
        if (name == null) return 0;
        int end = 0;
        while (end < name.length() && end < 6 && Character.isDigit(name.charAt(end))) end++;
        return end == 0 ? 0 : Integer.parseInt(name.substring(0, end));
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
