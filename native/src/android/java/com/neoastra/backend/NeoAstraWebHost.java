package com.neoastra.backend;

import android.graphics.Bitmap;
import android.net.Uri;
import android.view.ViewGroup;
import android.webkit.RenderProcessGoneDetail;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.FrameLayout;

import androidx.annotation.NonNull;
import androidx.webkit.JavaScriptReplyProxy;
import androidx.webkit.ScriptHandler;
import androidx.webkit.WebMessageCompat;
import androidx.webkit.WebViewCompat;
import androidx.webkit.WebViewFeature;

import java.io.ByteArrayInputStream;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.util.Arrays;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Map;

/**
 * The Java side of the Android backend of NeoAstra: what the native library cannot do without Java classes.
 *
 * <p>The native library drives one instance per view through JNI and receives what the view reports through the native
 * methods below, which it registers when Java loads it. Every member that JNI reaches is public so that R8 keeps its name.
 * All calls except {@link #nativeResource} run on the main thread, which owns the WebView.</p>
 */
public final class NeoAstraWebHost {
    static { System.loadLibrary("neoastra_native"); }

    /** Event kinds reported to the native library. */
    public static final int EVENT_STARTED = 0, EVENT_FINISHED = 1, EVENT_FAILED = 2, EVENT_TITLE = 3, EVENT_PROCESS_GONE = 4, EVENT_HISTORY = 5;

    /** A response of a custom-scheme request, built by the native library. */
    public static final class Response {
        public final int status;
        public final String reason;
        public final String mimeType;
        public final String headers;
        public final byte[] body;
        public final String filePath;

        public Response(int status, String reason, String mimeType, String headers, byte[] body, String filePath) {
            this.status = status;
            this.reason = reason;
            this.mimeType = mimeType;
            this.headers = headers;
            this.body = body;
            this.filePath = filePath;
        }
    }

    public static native void nativeMessage(long view, String data, String origin, boolean isMainFrame);
    public static native Response nativeResource(long view, String url, String method, String headers, boolean isMainFrame);
    public static native void nativeEvent(long view, int kind, String url, String text, int value);
    public static native void nativeEvaluated(long token, String json);

    private final WebView webView;
    private final ViewGroup parent;
    private final String projectedSuffix;
    private final Map<Integer, ScriptHandler> scripts = new HashMap<>();
    private volatile long view;
    private volatile JavaScriptReplyProxy reply;
    private int nextScript = 1;

    /** Loads the class, and with it the native library, which registers its native methods in JNI_OnLoad. */
    public static void load() { }

    /** Whether the WebView of this device has what the backend needs. */
    public static boolean isSupported() {
        return WebViewFeature.isFeatureSupported(WebViewFeature.WEB_MESSAGE_LISTENER)
            && WebViewFeature.isFeatureSupported(WebViewFeature.DOCUMENT_START_SCRIPT);
    }

    /**
     * Creates the WebView of a view inside a parent of the application.
     *
     * @param listenerOrigins the origin rules of the bridge in the projected form, or null when the bridge is off
     * @param projectedSuffix the host suffix that marks a projected custom-scheme origin, such as ".invalid"
     */
    public static NeoAstraWebHost create(ViewGroup parent, long view, String[] listenerOrigins, String projectedSuffix) {
        return new NeoAstraWebHost(parent, view, listenerOrigins, projectedSuffix);
    }

    private NeoAstraWebHost(ViewGroup parent, long nativeView, String[] listenerOrigins, String suffix) {
        this.parent = parent;
        this.view = nativeView;
        this.projectedSuffix = suffix;
        webView = new WebView(parent.getContext());
        webView.getSettings().setJavaScriptEnabled(true);
        webView.getSettings().setDomStorageEnabled(true);
        webView.getSettings().setAllowFileAccess(false);
        webView.getSettings().setAllowContentAccess(false);
        webView.getSettings().setMixedContentMode(android.webkit.WebSettings.MIXED_CONTENT_NEVER_ALLOW);
        webView.setWebViewClient(new Client());
        webView.setWebChromeClient(new Chrome());
        if (listenerOrigins != null) {
            WebViewCompat.addWebMessageListener(webView, "_neoastra_transport_v1", new HashSet<>(Arrays.asList(listenerOrigins)), new WebViewCompat.WebMessageListener() {
                @Override public void onPostMessage(@NonNull WebView source, @NonNull WebMessageCompat message, @NonNull Uri origin, boolean isMainFrame, @NonNull JavaScriptReplyProxy proxy) {
                    if (isMainFrame) reply = proxy;
                    long current = view;
                    String data = message.getData();
                    if (current != 0 && data != null) nativeMessage(current, data, origin.toString(), isMainFrame);
                }
            });
        }
        parent.addView(webView, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
    }

    public void setBounds(int x, int y, int width, int height, boolean fill) {
        ViewGroup.LayoutParams params = fill
            ? new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT)
            : new FrameLayout.LayoutParams(Math.max(width, 1), Math.max(height, 1));
        if (!fill && params instanceof FrameLayout.LayoutParams) {
            ((FrameLayout.LayoutParams) params).leftMargin = x;
            ((FrameLayout.LayoutParams) params).topMargin = y;
        }
        webView.setLayoutParams(params);
    }

    public void navigate(String url) { reply = null; webView.loadUrl(url); }

    public void loadHtml(String html, String baseUrl) { reply = null; webView.loadDataWithBaseURL(baseUrl, html, "text/html", "utf-8", null); }

    public void command(int command) {
        switch (command) {
            case 0: webView.stopLoading(); break;
            case 1: case 2: webView.reload(); break;
            case 3: webView.goBack(); break;
            case 4: webView.goForward(); break;
            default: break;
        }
    }

    public boolean canGoBack() { return webView.canGoBack(); }

    public boolean canGoForward() { return webView.canGoForward(); }

    public void evaluate(String script, long token) { webView.evaluateJavascript(script, value -> nativeEvaluated(token, value)); }

    /** Adds a script that runs at document start in the frames whose origin matches a rule, and returns its identifier. */
    public int addScript(String script, String[] rules) {
        ScriptHandler handler = WebViewCompat.addDocumentStartJavaScript(webView, script, new HashSet<>(Arrays.asList(rules)));
        int identifier = nextScript++;
        scripts.put(identifier, handler);
        return identifier;
    }

    public boolean removeScript(int identifier) {
        ScriptHandler handler = scripts.remove(identifier);
        if (handler == null) return false;
        handler.remove();
        return true;
    }

    /** Delivers a message to the main frame through the reply proxy of its last message. */
    public boolean postMessage(String message) {
        JavaScriptReplyProxy proxy = reply;
        if (proxy == null) return false;
        proxy.postMessage(message);
        return true;
    }

    public WebView webView() { return webView; }

    public void destroy() {
        view = 0;
        reply = null;
        for (ScriptHandler handler : scripts.values()) handler.remove();
        scripts.clear();
        parent.removeView(webView);
        webView.destroy();
    }

    private boolean projected(Uri uri) {
        String host = uri.getHost();
        return "https".equals(uri.getScheme()) && host != null && host.endsWith(projectedSuffix);
    }

    private final class Client extends WebViewClient {
        @Override public WebResourceResponse shouldInterceptRequest(WebView source, WebResourceRequest request) {
            Uri uri = request.getUrl();
            if (!projected(uri)) return null;
            long current = view;
            StringBuilder headers = new StringBuilder();
            for (Map.Entry<String, String> header : request.getRequestHeaders().entrySet()) headers.append(header.getKey()).append(": ").append(header.getValue()).append("\r\n");
            Response response = current == 0 ? null : nativeResource(current, uri.toString(), request.getMethod(), headers.toString(), request.isForMainFrame());
            // A projected origin is never looked up on the network, whatever the native library answers.
            if (response == null) return new WebResourceResponse("text/plain", "utf-8", 404, "Not Found", new HashMap<>(), new ByteArrayInputStream(new byte[0]));
            Map<String, String> headerMap = new HashMap<>();
            for (String line : response.headers.split("\r?\n")) {
                int separator = line.indexOf(':');
                if (separator > 0) headerMap.put(line.substring(0, separator).trim(), line.substring(separator + 1).trim());
            }
            InputStream body;
            try { body = response.filePath != null ? new FileInputStream(response.filePath) : new ByteArrayInputStream(response.body != null ? response.body : new byte[0]); }
            catch (IOException exception) { return new WebResourceResponse("text/plain", "utf-8", 500, "Internal Server Error", new HashMap<>(), new ByteArrayInputStream(new byte[0])); }
            // WebResourceResponse takes the type and the encoding from its arguments, not from a Content-Type header.
            String contentType = response.mimeType != null && !response.mimeType.isEmpty() ? response.mimeType : null;
            for (Map.Entry<String, String> header : headerMap.entrySet()) if (contentType == null && header.getKey().equalsIgnoreCase("Content-Type")) contentType = header.getValue();
            String mime = "application/octet-stream";
            String encoding = null;
            if (contentType != null) {
                String[] parts = contentType.split(";");
                if (!parts[0].trim().isEmpty()) mime = parts[0].trim();
                for (int index = 1; index < parts.length; index++) {
                    String parameter = parts[index].trim();
                    if (parameter.regionMatches(true, 0, "charset=", 0, 8)) encoding = parameter.substring(8).replace("\"", "").trim();
                }
            }
            String reason = response.reason != null && !response.reason.isEmpty() ? response.reason : "OK";
            return new WebResourceResponse(mime, encoding, response.status, reason, headerMap, body);
        }

        @Override public void onPageStarted(WebView source, String url, Bitmap favicon) {
            reply = null;
            long current = view;
            if (current != 0) nativeEvent(current, EVENT_STARTED, url, null, 0);
        }

        @Override public void onPageFinished(WebView source, String url) {
            long current = view;
            if (current != 0) nativeEvent(current, EVENT_FINISHED, url, null, 0);
        }

        @Override public void doUpdateVisitedHistory(WebView source, String url, boolean isReload) {
            long current = view;
            if (current != 0) nativeEvent(current, EVENT_HISTORY, url, null, (source.canGoBack() ? 1 : 0) | (source.canGoForward() ? 2 : 0));
        }

        @Override public boolean onRenderProcessGone(WebView source, RenderProcessGoneDetail detail) {
            long current = view;
            if (current != 0) nativeEvent(current, EVENT_PROCESS_GONE, null, null, detail.didCrash() ? 1 : 0);
            // The view is dead: the application recreates it, and returning true keeps the process alive.
            return true;
        }
    }

    private final class Chrome extends WebChromeClient {
        @Override public void onReceivedTitle(WebView source, String title) {
            long current = view;
            if (current != 0) nativeEvent(current, EVENT_TITLE, null, title, 0);
        }
    }
}
