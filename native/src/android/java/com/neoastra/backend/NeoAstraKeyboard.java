package com.neoastra.backend;

import android.app.Activity;
import android.content.Context;
import android.content.ContextWrapper;
import android.content.pm.PackageManager;
import android.graphics.Rect;
import android.os.Build;
import android.os.Bundle;
import android.view.ViewGroup;
import android.view.ViewTreeObserver;
import android.view.Window;
import android.view.WindowInsets;
import android.view.WindowInsetsAnimation;
import android.view.WindowManager;
import android.webkit.WebView;

import androidx.annotation.NonNull;
import androidx.webkit.ScriptHandler;
import androidx.webkit.WebViewCompat;
import androidx.webkit.WebViewFeature;

import java.util.Collections;

import java.util.List;

/**
 * The keyboard over the page, when the application opted into it (NeoAstraAndroidKeyboardOverlay=true merges the
 * meta-data). Android would otherwise pan the window to show the focused field, which moves the whole page under the status
 * bar, since an edge-to-edge window is not resized for the keyboard, and Chromium scrolls its visual viewport the same way.
 * The window neither pans nor resizes, the keyboard overlays the content of the page, and the page learns
 * how much of the view the keyboard covers, as on iOS: the CSS variable --neoastra-keyboard-height on the root element and a
 * neoastrakeyboard event with the height in CSS pixels and the duration of the animation of the keyboard in seconds.
 */
final class NeoAstraKeyboard {
    private final WebView webView;
    private final ViewGroup parent;
    private final ViewTreeObserver.OnGlobalLayoutListener layout = () -> report(0);
    private final ScriptHandler overlay;
    private int reported = -1;

    /** Starts reporting the keyboard of a WebView when the application opted into it; otherwise returns null. */
    static NeoAstraKeyboard attach(WebView webView, ViewGroup parent) {
        Activity activity = activity(webView.getContext());
        if (activity == null || !enabled(activity)) return null;
        return new NeoAstraKeyboard(activity, webView, parent);
    }

    private NeoAstraKeyboard(Activity activity, WebView view, ViewGroup group) {
        webView = view;
        parent = group;
        Window window = activity.getWindow();
        window.setSoftInputMode((window.getAttributes().softInputMode & ~WindowManager.LayoutParams.SOFT_INPUT_MASK_ADJUST)
            | WindowManager.LayoutParams.SOFT_INPUT_ADJUST_NOTHING);
        // Chromium would still shrink the visual viewport and scroll it to the focused field; with the VirtualKeyboard API
        // the keyboard overlays the content of every page that the view loads.
        overlay = WebViewFeature.isFeatureSupported(WebViewFeature.DOCUMENT_START_SCRIPT)
            ? WebViewCompat.addDocumentStartJavaScript(webView, "if(navigator.virtualKeyboard)navigator.virtualKeyboard.overlaysContent=true;", Collections.singleton("*"))
            : null;
        // The animation of the keyboard reports its final height when it starts, with its duration; a change without an
        // animation shows in the next layout. The callback sits on the parent, so the WebView keeps its own.
        if (Build.VERSION.SDK_INT >= 30) {
            parent.setWindowInsetsAnimationCallback(new WindowInsetsAnimation.Callback(WindowInsetsAnimation.Callback.DISPATCH_MODE_CONTINUE_ON_SUBTREE) {
                @NonNull @Override
                public WindowInsetsAnimation.Bounds onStart(@NonNull WindowInsetsAnimation animation, @NonNull WindowInsetsAnimation.Bounds bounds) {
                    if ((animation.getTypeMask() & WindowInsets.Type.ime()) != 0) report(animation.getDurationMillis() / 1000.0);
                    return bounds;
                }

                @NonNull @Override
                public WindowInsets onProgress(@NonNull WindowInsets insets, @NonNull List<WindowInsetsAnimation> animations) { return insets; }
            });
        }
        webView.getViewTreeObserver().addOnGlobalLayoutListener(layout);
    }

    void detach() {
        if (overlay != null) overlay.remove();
        webView.getViewTreeObserver().removeOnGlobalLayoutListener(layout);
        if (Build.VERSION.SDK_INT >= 30) parent.setWindowInsetsAnimationCallback(null);
    }

    private void report(double duration) {
        int covered = covered();
        if (covered == reported) return;
        reported = covered;
        long height = Math.round(covered / webView.getResources().getDisplayMetrics().density);
        webView.evaluateJavascript("document.documentElement.style.setProperty('--neoastra-keyboard-height','" + height + "px');"
            + "window.dispatchEvent(new CustomEvent('neoastrakeyboard',{detail:{height:" + height + ",duration:" + duration + "}}));", null);
    }

    /** The physical pixels of the WebView, from its bottom up, that the keyboard covers. */
    private int covered() {
        int[] location = new int[2];
        int keyboardTop;
        if (Build.VERSION.SDK_INT >= 30) {
            WindowInsets insets = webView.getRootWindowInsets();
            if (insets == null) return 0;
            webView.getLocationInWindow(location);
            keyboardTop = webView.getRootView().getHeight() - insets.getInsets(WindowInsets.Type.ime()).bottom;
        } else {
            Rect visible = new Rect();
            webView.getWindowVisibleDisplayFrame(visible);
            webView.getLocationOnScreen(location);
            keyboardTop = visible.bottom;
        }
        return Math.max(0, Math.min(webView.getHeight(), location[1] + webView.getHeight() - keyboardTop));
    }

    private static Activity activity(Context context) {
        while (context instanceof ContextWrapper) {
            if (context instanceof Activity) return (Activity) context;
            context = ((ContextWrapper) context).getBaseContext();
        }
        return null;
    }

    private static boolean enabled(Activity activity) {
        try {
            Bundle metaData = activity.getPackageManager().getApplicationInfo(activity.getPackageName(), PackageManager.GET_META_DATA).metaData;
            return metaData != null && metaData.getBoolean("com.neoastra.webview.keyboardOverlay", false);
        } catch (PackageManager.NameNotFoundException e) {
            return false;
        }
    }
}
