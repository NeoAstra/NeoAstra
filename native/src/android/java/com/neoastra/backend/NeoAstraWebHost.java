package com.neoastra.backend;

import android.Manifest;
import android.app.Activity;
import android.content.ClipData;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Bitmap;
import android.net.Uri;
import android.os.Message;
import android.provider.MediaStore;
import android.view.ViewGroup;
import android.webkit.JsPromptResult;
import android.webkit.JsResult;
import android.webkit.PermissionRequest;
import android.webkit.RenderProcessGoneDetail;
import android.webkit.ValueCallback;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.FrameLayout;

import androidx.annotation.NonNull;
import androidx.core.content.FileProvider;
import androidx.webkit.JavaScriptReplyProxy;
import androidx.webkit.ScriptHandler;
import androidx.webkit.WebMessageCompat;
import androidx.webkit.WebViewCompat;
import androidx.webkit.WebViewFeature;

import java.io.ByteArrayInputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
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
    /** Asks the host about a navigation and answers whether the WebView must not load it now. */
    public static native boolean nativeNavigation(long view, String url, boolean isMainFrame, boolean hasGesture);
    public static native void nativeNewWindow(long view, String url, boolean hasGesture);
    /** Asks the host about a JavaScript dialog; the native library answers the result through JNI. */
    public static native void nativeScriptDialog(long view, int kind, String message, String defaultText, String url, JsResult result);
    public static native void nativeFileChooser(long view, String acceptedTypes, boolean multiple, NeoAstraWebHost host, ValueCallback<Uri[]> callback, WebChromeClient.FileChooserParams parameters);
    public static native void nativePermission(long view, String origin, int[] kinds, NeoAstraWebHost host, PermissionRequest request);

    /** Kinds of NEOASTRA_PERMISSION_* that a WebView asks for. */
    private static final int PERMISSION_CAMERA = 2, PERMISSION_MICROPHONE = 3;

    private final WebView webView;
    private final ViewGroup parent;
    private final String projectedSuffix;
    private final Map<Integer, ScriptHandler> scripts = new HashMap<>();
    private volatile long view;
    private volatile JavaScriptReplyProxy reply;
    private int nextScript = 1;
    private volatile boolean engineDialogs = true;
    private final NeoAstraKeyboard keyboard;

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
        // A link with a target and window.open after a gesture ask for a new window, which the host decides about.
        webView.getSettings().setSupportMultipleWindows(true);
        webView.getSettings().setJavaScriptCanOpenWindowsAutomatically(false);
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
        keyboard = NeoAstraKeyboard.attach(webView, parent);
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

    /** Whether the WebView shows its own JavaScript dialogs; while it does not, the host answers them. */
    public void setEngineDialogs(boolean value) { engineDialogs = value; }

    /** Gives the page the files that the host chose, or none. */
    public static void deliverFiles(ValueCallback<Uri[]> callback, String[] paths) {
        if (paths == null) { callback.onReceiveValue(null); return; }
        Uri[] files = new Uri[paths.length];
        for (int index = 0; index < paths.length; index++) files[index] = Uri.fromFile(new File(paths[index]));
        callback.onReceiveValue(files);
    }

    /**
     * Shows the picker of the system, which the host asked for with its default answer; an image input with the capture attribute
     * opens the camera instead, which writes the photo to a file of NeoAstra's file provider that the page then receives.
     */
    public void chooseWithSystem(ValueCallback<Uri[]> callback, WebChromeClient.FileChooserParams parameters) {
        if (parameters.isCaptureEnabled() && acceptsImages(parameters.getAcceptTypes())) { takePhoto(callback); return; }
        Intent intent;
        try { intent = parameters.createIntent(); }
        catch (RuntimeException exception) { callback.onReceiveValue(null); return; }
        Context context = webView.getContext();
        NeoAstraResultActivity.startForResult(intent, (resultCode, data, grants) -> callback.onReceiveValue(copyChosen(context, parseChosen(resultCode, data))));
    }

    // FileChooserParams.parseResult reads only the single data URI; a multiple choice of the picker comes as clip data instead.
    private static Uri[] parseChosen(int resultCode, Intent data) {
        ClipData clip = resultCode == Activity.RESULT_OK && data != null ? data.getClipData() : null;
        if (clip == null || clip.getItemCount() == 0) return WebChromeClient.FileChooserParams.parseResult(resultCode, data);
        Uri[] chosen = new Uri[clip.getItemCount()];
        for (int index = 0; index < chosen.length; index++) chosen[index] = clip.getItemAt(index).getUri();
        return chosen;
    }

    // The read permission of a chosen document belongs to the result activity, which finishes at once, while the WebView reads
    // the file later: the files are copied into the cache folder neoastra-chooser while the permission holds, and the page gets
    // the copies. The folder keeps only the latest choice.
    private static Uri[] copyChosen(Context context, Uri[] chosen) {
        if (chosen == null || chosen.length == 0) return chosen;
        File directory = new File(context.getCacheDir(), "neoastra-chooser");
        File[] previous = directory.listFiles();
        if (previous != null) for (File file : previous) file.delete();
        if (!directory.isDirectory() && !directory.mkdirs()) return null;
        List<Uri> copies = new ArrayList<>();
        for (int index = 0; index < chosen.length; index++) {
            Uri source = chosen[index];
            if (source == null) continue;
            File target = new File(directory, index + "-" + displayName(context, source));
            try (InputStream input = context.getContentResolver().openInputStream(source); java.io.OutputStream output = new java.io.FileOutputStream(target)) {
                if (input == null) continue;
                byte[] buffer = new byte[64 * 1024];
                for (int read; (read = input.read(buffer)) > 0;) output.write(buffer, 0, read);
                copies.add(Uri.fromFile(target));
            } catch (IOException | SecurityException exception) {
                target.delete();
            }
        }
        return copies.isEmpty() ? null : copies.toArray(new Uri[0]);
    }

    private static String displayName(Context context, Uri uri) {
        String name = null;
        try (android.database.Cursor cursor = context.getContentResolver().query(uri, new String[] { android.provider.OpenableColumns.DISPLAY_NAME }, null, null, null)) {
            if (cursor != null && cursor.moveToFirst()) name = cursor.getString(0);
        } catch (RuntimeException exception) {
            name = null;
        }
        if (name == null || name.isEmpty()) name = uri.getLastPathSegment();
        if (name == null || name.isEmpty()) name = "file";
        return name.replaceAll("[\\\\/:*?\"<>|]", "_");
    }

    private static boolean acceptsImages(String[] types) {
        if (types == null || types.length == 0) return false;
        for (String type : types) if (type != null && (type.startsWith("image/") || type.isEmpty())) return true;
        return false;
    }

    // An application that declares the camera permission (for getUserMedia) must hold it before another application takes the
    // photo for it; one that does not declare it needs none.
    private void takePhoto(ValueCallback<Uri[]> callback) {
        Context context = webView.getContext();
        if (declaresCamera(context) && context.checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            NeoAstraResultActivity.requestPermissions(new String[] { Manifest.permission.CAMERA }, (resultCode, data, grants) -> {
                if (context.checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) launchCamera(context, callback);
                else callback.onReceiveValue(null);
            });
            return;
        }
        launchCamera(context, callback);
    }

    private static boolean declaresCamera(Context context) {
        try {
            String[] requested = context.getPackageManager().getPackageInfo(context.getPackageName(), PackageManager.GET_PERMISSIONS).requestedPermissions;
            return requested != null && Arrays.asList(requested).contains(Manifest.permission.CAMERA);
        } catch (PackageManager.NameNotFoundException exception) {
            return false;
        }
    }

    private static void launchCamera(Context context, ValueCallback<Uri[]> callback) {
        File photo;
        Uri output;
        try {
            File directory = new File(context.getCacheDir(), "neoastra-camera");
            if (!directory.isDirectory() && !directory.mkdirs()) throw new IOException("The camera directory cannot be created.");
            photo = File.createTempFile("photo-", ".jpg", directory);
            output = FileProvider.getUriForFile(context, context.getPackageName() + ".neoastra.files", photo);
        } catch (IOException | IllegalArgumentException exception) {
            callback.onReceiveValue(null);
            return;
        }
        Intent intent = new Intent(MediaStore.ACTION_IMAGE_CAPTURE)
            .putExtra(MediaStore.EXTRA_OUTPUT, output)
            .addFlags(Intent.FLAG_GRANT_WRITE_URI_PERMISSION | Intent.FLAG_GRANT_READ_URI_PERMISSION);
        try {
            NeoAstraResultActivity.startForResult(intent, (resultCode, data, grants) -> {
                if (resultCode == Activity.RESULT_OK && photo.length() > 0) callback.onReceiveValue(new Uri[] { output });
                else { photo.delete(); callback.onReceiveValue(null); }
            });
        } catch (RuntimeException exception) {
            photo.delete();
            callback.onReceiveValue(null);
        }
    }

    /**
     * Answers a request of the page for a camera or a microphone. An allowed request also needs the runtime permission of the
     * application, which the system asks the user for once; the page gets the devices that the user allowed.
     */
    public void answerPermission(PermissionRequest request, boolean allowed) {
        if (!allowed) { request.deny(); return; }
        Context context = webView.getContext();
        List<String> resources = new ArrayList<>(), missing = new ArrayList<>();
        for (String resource : request.getResources()) {
            String permission = PermissionRequest.RESOURCE_VIDEO_CAPTURE.equals(resource) ? Manifest.permission.CAMERA : PermissionRequest.RESOURCE_AUDIO_CAPTURE.equals(resource) ? Manifest.permission.RECORD_AUDIO : null;
            if (permission == null) continue;
            resources.add(resource);
            if (context.checkSelfPermission(permission) != PackageManager.PERMISSION_GRANTED) missing.add(permission);
        }
        if (missing.isEmpty()) { grant(request, resources); return; }
        NeoAstraResultActivity.requestPermissions(missing.toArray(new String[0]), (resultCode, data, grants) -> {
            List<String> granted = new ArrayList<>();
            for (String resource : resources) {
                String permission = PermissionRequest.RESOURCE_VIDEO_CAPTURE.equals(resource) ? Manifest.permission.CAMERA : Manifest.permission.RECORD_AUDIO;
                if (context.checkSelfPermission(permission) == PackageManager.PERMISSION_GRANTED) granted.add(resource);
            }
            grant(request, granted);
        });
    }

    private static void grant(PermissionRequest request, List<String> resources) {
        if (resources.isEmpty()) request.deny(); else request.grant(resources.toArray(new String[0]));
    }

    public void destroy() {
        view = 0;
        reply = null;
        for (ScriptHandler handler : scripts.values()) handler.remove();
        scripts.clear();
        if (keyboard != null) keyboard.detach();
        parent.removeView(webView);
        webView.destroy();
    }

    private boolean projected(Uri uri) {
        String host = uri.getHost();
        return "https".equals(uri.getScheme()) && host != null && host.endsWith(projectedSuffix);
    }

    private final class Client extends WebViewClient {
        // Android asks before it follows a link, a redirect, or a script navigation, but not before a load that the backend
        // starts itself, so an allowed navigation that the host decided later is loaded again without being asked again.
        @Override public boolean shouldOverrideUrlLoading(WebView source, WebResourceRequest request) {
            long current = view;
            if (current == 0) return true;
            return nativeNavigation(current, request.getUrl().toString(), request.isForMainFrame(), request.hasGesture());
        }

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
        // Android names the address of a new window only to the WebView that opens it: a probe takes the first address,
        // reports it, and is destroyed, since the backend creates no popup view.
        @Override public boolean onCreateWindow(WebView source, boolean isDialog, boolean isUserGesture, Message result) {
            final WebView probe = new WebView(source.getContext());
            probe.setWebViewClient(new WebViewClient() {
                private boolean delivered;
                private void deliver(WebView target, String url) {
                    if (delivered || url == null) return;
                    delivered = true;
                    long current = view;
                    if (current != 0) nativeNewWindow(current, url, isUserGesture);
                    target.stopLoading();
                    target.post(target::destroy);
                }
                @Override public boolean shouldOverrideUrlLoading(WebView target, WebResourceRequest request) { deliver(target, request.getUrl().toString()); return true; }
                @Override public void onPageStarted(WebView target, String url, Bitmap favicon) { deliver(target, url); }
            });
            WebView.WebViewTransport transport = (WebView.WebViewTransport) result.obj;
            transport.setWebView(probe);
            result.sendToTarget();
            return true;
        }

        @Override public boolean onJsAlert(WebView source, String url, String message, JsResult result) { return dialog(0, message, null, url, result); }
        @Override public boolean onJsConfirm(WebView source, String url, String message, JsResult result) { return dialog(1, message, null, url, result); }
        @Override public boolean onJsPrompt(WebView source, String url, String message, String defaultValue, JsPromptResult result) { return dialog(2, message, defaultValue, url, result); }
        @Override public boolean onJsBeforeUnload(WebView source, String url, String message, JsResult result) { return dialog(3, message, null, url, result); }

        private boolean dialog(int kind, String message, String defaultText, String url, JsResult result) {
            long current = view;
            if (engineDialogs || current == 0) return false;
            nativeScriptDialog(current, kind, message, defaultText, url, result);
            return true;
        }

        @Override public boolean onShowFileChooser(WebView source, ValueCallback<Uri[]> callback, FileChooserParams parameters) {
            long current = view;
            if (current == 0) return false;
            nativeFileChooser(current, String.join(",", parameters.getAcceptTypes()), parameters.getMode() == FileChooserParams.MODE_OPEN_MULTIPLE, NeoAstraWebHost.this, callback, parameters);
            return true;
        }

        @Override public void onPermissionRequest(PermissionRequest request) {
            long current = view;
            if (current == 0) { request.deny(); return; }
            List<Integer> kinds = new ArrayList<>();
            for (String resource : request.getResources()) {
                if (PermissionRequest.RESOURCE_AUDIO_CAPTURE.equals(resource)) kinds.add(PERMISSION_MICROPHONE);
                else if (PermissionRequest.RESOURCE_VIDEO_CAPTURE.equals(resource)) kinds.add(PERMISSION_CAMERA);
            }
            if (kinds.isEmpty()) { request.deny(); return; }
            int[] values = new int[kinds.size()];
            for (int index = 0; index < values.length; index++) values[index] = kinds.get(index);
            Uri origin = request.getOrigin();
            nativePermission(current, origin == null ? null : origin.toString(), values, NeoAstraWebHost.this, request);
        }

        @Override public void onReceivedTitle(WebView source, String title) {
            long current = view;
            if (current != 0) nativeEvent(current, EVENT_TITLE, null, title, 0);
        }
    }
}
