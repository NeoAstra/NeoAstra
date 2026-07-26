// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using NeoAstra.Interop;
using NeoAstra.Interop.Generated;

namespace NeoAstra;

/// <summary>Represents one browser view hosted by an owned window or borrowed native parent.</summary>
public sealed class NeoAstra : IAsyncDisposable
{
    private readonly SafeViewHandle _handle;
    private readonly NeoAstraHost _host;
    private readonly TimeSpan _decisionTimeout;
    private readonly Dictionary<ulong, NeoDownload> _downloads = [];
    private GCHandle _eventRoot;
    private Uri? _source;
    private string _title = string.Empty;
    private bool _isLoading;
    private bool _canGoBack;
    private bool _canGoForward;
    private int _disposed;

    internal NeoAstra(NeoEnvironment environment, SafeViewHandle handle, NeoAstraHost host, NeoAstraOptions options)
    {
        Environment = environment;
        _handle = handle;
        _host = host;
        _decisionTimeout = options.DecisionTimeout;
        Profile = options.Profile;
        RegisterEventCallback();
    }

    /// <summary>Gets the last source URI reported by the backend.</summary>
    public Uri? Source => _source;

    /// <summary>Gets the last document title reported by the backend.</summary>
    public string Title => _title;

    /// <summary>Gets whether a main-frame navigation is in progress.</summary>
    public bool IsLoading => _isLoading;

    /// <summary>Gets whether backward history navigation is available.</summary>
    public bool CanGoBack => _canGoBack;

    /// <summary>Gets whether forward history navigation is available.</summary>
    public bool CanGoForward => _canGoForward;

    /// <summary>Gets or sets the page zoom factor, where <c>1.0</c> is 100 percent.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is outside the portable range from 0.25 through 5.0.</exception>
    public unsafe double ZoomFactor
    {
        get
        {
            ThrowIfDisposed();
            double factor;
            NativeError.ThrowIfFailed(NativeMethods.neoastra_view_get_zoom_factor(NativeHandle, &factor), default, "get zoom factor");
            return factor;
        }
        set
        {
            if (!double.IsFinite(value) || value < 0.25 || value > 5d)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "The zoom factor must be from 0.25 through 5.0.");
            }

            ThrowIfDisposed();
            NativeError.ThrowIfFailed(NativeMethods.neoastra_view_set_zoom_factor(NativeHandle, value), default, "set zoom factor");
        }
    }

    /// <summary>Gets or sets the single asynchronous navigation policy handler.</summary>
    public Func<NeoNavigationRequest, ValueTask<NeoNavigationDecision>>? NavigationRequested { get; set; }

    /// <summary>Gets or sets the single asynchronous permission policy handler.</summary>
    public Func<NeoPermissionRequest, ValueTask<NeoPermissionDecision>>? PermissionRequested { get; set; }

    /// <summary>Gets or sets the single asynchronous download policy handler.</summary>
    public Func<NeoDownloadRequest, ValueTask<NeoDownloadDecision>>? DownloadRequested { get; set; }

    /// <summary>Gets or sets the single asynchronous new-window policy handler.</summary>
    public Func<NeoNewWindowRequest, ValueTask<NeoNewWindowDecision>>? NewWindowRequested { get; set; }

    /// <summary>Gets or sets the single asynchronous JavaScript-dialog policy handler.</summary>
    public Func<NeoScriptDialogRequest, ValueTask<NeoScriptDialogDecision>>? ScriptDialogRequested { get; set; }

    /// <summary>Gets or sets the single asynchronous file-chooser policy handler.</summary>
    public Func<NeoFileChooserRequest, ValueTask<NeoFileChooserDecision>>? FileChooserRequested { get; set; }

    /// <summary>Gets or sets the single asynchronous HTTP-authentication policy handler.</summary>
    public Func<NeoAuthenticationRequest, ValueTask<NeoAuthenticationDecision>>? AuthenticationRequested { get; set; }

    /// <summary>Gets or sets the single asynchronous client-certificate policy handler.</summary>
    public Func<NeoClientCertificateRequest, ValueTask<NeoClientCertificateDecision>>? ClientCertificateRequested { get; set; }

    /// <summary>Gets or sets the single asynchronous TLS-error policy handler.</summary>
    public Func<NeoTlsErrorRequest, ValueTask<NeoTlsErrorDecision>>? TlsErrorRequested { get; set; }

    /// <summary>Gets or sets the single asynchronous web-content fullscreen policy handler.</summary>
    public Func<NeoFullscreenRequest, ValueTask<NeoFullscreenDecision>>? FullscreenRequested { get; set; }

    /// <summary>Occurs after a navigation succeeds or fails.</summary>
    public event EventHandler<NeoNavigationCompletedEventArgs>? NavigationCompleted;

    /// <summary>Occurs when web content sends a bridge message.</summary>
    public event EventHandler<NeoWebMessageReceivedEventArgs>? MessageReceived;

    /// <summary>Occurs when a browser or web-content process exits or becomes unresponsive.</summary>
    public event EventHandler<NeoProcessFailedEventArgs>? ProcessFailed;

    /// <summary>Occurs when an accepted download starts.</summary>
    public event EventHandler<NeoDownloadEventArgs>? DownloadStarted;

    /// <summary>Occurs when native download progress changes.</summary>
    public event EventHandler<NeoDownloadEventArgs>? DownloadProgressChanged;

    /// <summary>Occurs once when a download completes, fails, or is canceled.</summary>
    public event EventHandler<NeoDownloadEventArgs>? DownloadCompleted;

    /// <summary>Navigates the main frame to an absolute URI.</summary>
    /// <param name="uri">The destination URI.</param>
    /// <param name="cancellationToken">Cancels the call before it is submitted.</param>
    /// <returns>A completed task after the backend accepts the navigation.</returns>
    public unsafe ValueTask NavigateAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateAbsoluteUri(uri, nameof(uri));
        cancellationToken.ThrowIfCancellationRequested();
        using var nativeUri = new Utf8String(uri.AbsoluteUri);
        NativeMethods.neoastra_error_t error = default;
        var result = NativeMethods.neoastra_view_navigate(NativeHandle, nativeUri.View, &error);
        NativeError.ThrowIfFailed(result, error, "navigate", cancellationToken);
        _source = uri;
        _isLoading = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>Loads an HTML document with an optional base URI.</summary>
    /// <param name="html">The HTML source.</param>
    /// <param name="baseUri">An optional absolute base URI.</param>
    /// <param name="cancellationToken">Cancels the call before it is submitted.</param>
    /// <returns>A completed task after the backend accepts the document.</returns>
    public unsafe ValueTask LoadHtmlAsync(string html, Uri? baseUri = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(html);
        if (baseUri is not null) ValidateAbsoluteUri(baseUri, nameof(baseUri));
        cancellationToken.ThrowIfCancellationRequested();
        using var nativeHtml = new Utf8String(html);
        using var nativeBase = new Utf8String(baseUri?.AbsoluteUri);
        NativeMethods.neoastra_error_t error = default;
        var result = NativeMethods.neoastra_view_load_html(NativeHandle, nativeHtml.View, nativeBase.View, &error);
        NativeError.ThrowIfFailed(result, error, "load HTML", cancellationToken);
        _source = baseUri;
        _isLoading = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>Evaluates JavaScript and returns the backend's JSON-encoded result.</summary>
    /// <param name="script">The JavaScript source.</param>
    /// <param name="cancellationToken">Cancels the managed wait and requests native cancellation.</param>
    /// <returns>The JSON-encoded result, or <see langword="null"/> for JavaScript null.</returns>
    public unsafe ValueTask<string?> EvaluateScriptAsync(string script, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(script);
        cancellationToken.ThrowIfCancellationRequested();
        using var nativeScript = new Utf8String(script);
        var operation = new NativeOperation<string?>(cancellationToken, "evaluate script");
        NativeMethods.neoastra_operation_t nativeOperation = default;
        NativeMethods.neoastra_error_t error = default;
        NativeMethods.neoastra_result_t result;
        try
        {
            result = NativeMethods.neoastra_view_evaluate_script_async(
                NativeHandle,
                nativeScript.View,
                (delegate* unmanaged[Cdecl]<void*, NativeMethods.neoastra_result_t, NativeMethods.neoastra_string_view_t, NativeMethods.neoastra_error_t, void>)&ScriptEvaluated,
                (void*)operation.Context,
                &nativeOperation,
                &error);
        }
        catch (Exception ex)
        {
            operation.FailStart(ex);
            return operation.ValueTask;
        }

        if (NativeError.Code(result) != NeoErrorCode.Success)
        {
            var info = NativeError.Read(NativeError.Code(result), error.Handle);
            if (error.Handle != 0) new SafeErrorHandle(error.Handle).Dispose();
            operation.FailStart(NativeError.CreateException(info, "evaluate script", cancellationToken));
        }
        else
        {
            operation.AttachOperation(nativeOperation.Handle);
        }

        return operation.ValueTask;
    }

    /// <summary>Adds a script that is injected into future matching documents.</summary>
    /// <param name="script">The JavaScript source.</param>
    /// <param name="options">Injection options, or <see langword="null"/> for document-start injection.</param>
    /// <param name="cancellationToken">Cancels the managed wait and requests native cancellation.</param>
    /// <returns>A removable script registration.</returns>
    public unsafe ValueTask<NeoUserScript> AddScriptAsync(string script, NeoScriptOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(script);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new NeoScriptOptions();
        options.Validate();
        using var nativeScript = new Utf8String(script);
        using var worldName = new Utf8String(options.WorldName);
        var raw = new NativeMethods.neoastra_script_options
        {
            size = (uint)sizeof(NativeMethods.neoastra_script_options),
            version = 1,
            injection_time = options.InjectAtDocumentEnd
                ? NativeMethods.neoastra_script_injection_time.NEOASTRA_SCRIPT_DOCUMENT_END
                : NativeMethods.neoastra_script_injection_time.NEOASTRA_SCRIPT_DOCUMENT_START,
            main_frame_only = options.MainFrameOnly ? 1u : 0u,
            isolated_world = options.IsolatedWorld ? 1u : 0u,
            world_name = worldName.View,
        };
        var nativeOptions = new NativeMethods.neoastra_script_options_t(raw);
        var operation = new NativeOperation<NeoUserScript>(cancellationToken, this);
        NativeMethods.neoastra_operation_t nativeOperation = default;
        NativeMethods.neoastra_error_t error = default;
        NativeMethods.neoastra_result_t result;
        try
        {
            result = NativeMethods.neoastra_view_add_script_async(
                NativeHandle,
                nativeScript.View,
                &nativeOptions,
                (delegate* unmanaged[Cdecl]<void*, NativeMethods.neoastra_result_t, NativeMethods.neoastra_string_view_t, NativeMethods.neoastra_error_t, void>)&ScriptAdded,
                (void*)operation.Context,
                &nativeOperation,
                &error);
        }
        catch (Exception ex)
        {
            operation.FailStart(ex);
            return operation.ValueTask;
        }

        if (NativeError.Code(result) != NeoErrorCode.Success)
        {
            var info = NativeError.Read(NativeError.Code(result), error.Handle);
            if (error.Handle != 0) new SafeErrorHandle(error.Handle).Dispose();
            operation.FailStart(NativeError.CreateException(info, "add persistent script", cancellationToken));
        }
        else
        {
            operation.AttachOperation(nativeOperation.Handle);
        }

        return operation.ValueTask;
    }

    /// <summary>Posts a JSON value to web content.</summary>
    /// <param name="json">A complete JSON value.</param>
    /// <param name="cancellationToken">Cancels the call before it is submitted.</param>
    /// <returns>A completed task after the backend accepts the message.</returns>
    /// <exception cref="JsonException"><paramref name="json"/> is not valid JSON.</exception>
    public unsafe ValueTask PostMessageAsync(string json, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(json);
        cancellationToken.ThrowIfCancellationRequested();
        using (JsonDocument.Parse(json)) { }
        using var nativeJson = new Utf8String(json);
        NativeMethods.neoastra_error_t error = default;
        var result = NativeMethods.neoastra_view_post_message(NativeHandle, nativeJson.View, 1, &error);
        NativeError.ThrowIfFailed(result, error, "post web message", cancellationToken);
        return ValueTask.CompletedTask;
    }

    /// <summary>Reloads the current document.</summary>
    public void Reload()
    {
        ThrowIfDisposed();
        NativeError.ThrowIfFailed(NativeMethods.neoastra_view_reload(NativeHandle, 0), default, "reload");
    }

    /// <summary>Stops the current navigation.</summary>
    public void Stop()
    {
        ThrowIfDisposed();
        NativeError.ThrowIfFailed(NativeMethods.neoastra_view_stop(NativeHandle), default, "stop navigation");
    }

    /// <summary>Navigates backward in history.</summary>
    public void GoBack()
    {
        ThrowIfDisposed();
        NativeError.ThrowIfFailed(NativeMethods.neoastra_view_go_back(NativeHandle), default, "go back");
    }

    /// <summary>Navigates forward in history.</summary>
    public void GoForward()
    {
        ThrowIfDisposed();
        NativeError.ThrowIfFailed(NativeMethods.neoastra_view_go_forward(NativeHandle), default, "go forward");
    }

    /// <summary>Resets page zoom to 100 percent.</summary>
    public void ResetZoom() => ZoomFactor = 1d;

    /// <summary>Gets a typed borrowed native browser handle.</summary>
    /// <param name="kind">The requested backend handle kind.</param>
    /// <returns>A borrowed native handle valid while this view remains alive.</returns>
    public unsafe NeoNativeHandle GetNativeHandle(NeoNativeHandleKind kind)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var raw = new NativeMethods.neoastra_native_handle
        {
            size = (uint)sizeof(NativeMethods.neoastra_native_handle),
            version = 1,
            kind = (NativeMethods.neoastra_native_handle_kind)kind,
        };
        var native = new NativeMethods.neoastra_native_handle_t(raw);
        NativeError.ThrowIfFailed(
            NativeMethods.neoastra_view_get_native_handle(NativeHandle, (NativeMethods.neoastra_native_handle_kind)kind, &native),
            default,
            "get web view native handle");
        return new NeoNativeHandle((NeoNativeHandleKind)native.Value.kind.Value, (nint)native.Value.value);
    }

    /// <summary>Unregisters events and releases the native view reference.</summary>
    /// <returns>A completed value task.</returns>
    public unsafe ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            NativeMethods.neoastra_view_set_event_callback(new(_handle.DangerousGetHandle()), default, null);
        }
        catch
        {
            // Release remains required if the backend is shutting down.
        }

        if (_eventRoot.IsAllocated) _eventRoot.Free();
        _handle.Dispose();
        return ValueTask.CompletedTask;
    }

    internal NeoEnvironment Environment { get; }

    internal NeoProfile? Profile { get; }

    internal NativeMethods.neoastra_view_t NativeHandle
    {
        get
        {
            ThrowIfDisposed();
            return new(_handle.DangerousGetHandle());
        }
    }

    private unsafe void RegisterEventCallback()
    {
        _eventRoot = GCHandle.Alloc(this);
        var result = NativeMethods.neoastra_view_set_event_callback(
            new(_handle.DangerousGetHandle()),
            (delegate* unmanaged[Cdecl]<void*, NativeMethods.neoastra_event_t*, void>)&ViewEvent,
            (void*)GCHandle.ToIntPtr(_eventRoot));
        if (NativeError.Code(result) != NeoErrorCode.Success)
        {
            _eventRoot.Free();
            _handle.Dispose();
            NativeError.ThrowIfFailed(result, default, "register web view events");
        }
    }

    private void DispatchEvent(NativeMethods.neoastra_event value)
    {
        var type = value.header.Value.type.Value;
        var uri = DecodeUri(value.uri);
        switch (type)
        {
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_NAVIGATION_REQUESTED:
                HandleNavigationDecision(value, uri);
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_PERMISSION_REQUESTED:
                HandlePermissionDecision(value, uri);
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_DOWNLOAD_REQUESTED:
                HandleDownloadDecision(value, uri);
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_NEW_WINDOW_REQUESTED:
                HandleNewWindowDecision(value, uri);
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_SCRIPT_DIALOG_REQUESTED:
                HandleScriptDialogDecision(value, uri);
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_FILE_CHOOSER_REQUESTED:
                HandleFileChooserDecision(value);
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_AUTHENTICATION_REQUESTED:
                HandleAuthenticationDecision(value, uri);
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_CLIENT_CERTIFICATE_REQUESTED:
                HandleClientCertificateDecision(value);
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_CERTIFICATE_ERROR:
                HandleTlsErrorDecision(value, uri);
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_FULLSCREEN_REQUESTED:
                HandleFullscreenDecision(value, uri);
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_DOWNLOAD_STARTED:
                RaiseDownloadEvent(value, DownloadStarted, false);
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_DOWNLOAD_PROGRESS_CHANGED:
                RaiseDownloadEvent(value, DownloadProgressChanged, false);
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_DOWNLOAD_COMPLETED:
                RaiseDownloadEvent(value, DownloadCompleted, true);
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_NAVIGATION_STARTED:
                _source = uri ?? _source;
                _isLoading = true;
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_NAVIGATION_COMPLETED:
                _source = uri ?? _source;
                _isLoading = false;
                RaiseNavigationCompleted(new NeoNavigationCompletedEventArgs(_source, true, NeoErrorCode.Success, value.native_code, value.header.Value.sequence));
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_NAVIGATION_FAILED:
                _isLoading = false;
                RaiseNavigationCompleted(new NeoNavigationCompletedEventArgs(uri ?? _source, false, (NeoErrorCode)(int)value.value, value.native_code, value.header.Value.sequence));
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_SOURCE_CHANGED:
                _source = uri;
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_TITLE_CHANGED:
                _title = Utf8String.Decode(value.text);
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_HISTORY_CHANGED:
                _canGoBack = (value.value & 1) != 0;
                _canGoForward = (value.value & 2) != 0;
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_MESSAGE_RECEIVED:
                try { MessageReceived?.Invoke(this, new NeoWebMessageReceivedEventArgs(Utf8String.Decode(value.text), uri, (value.value & 1) != 0)); } catch { }
                break;
            case NativeMethods.neoastra_event_type.NEOASTRA_EVENT_WEB_PROCESS_TERMINATED:
                try { ProcessFailed?.Invoke(this, DecodeProcessFailure(value.value, value.native_code, Utf8String.Decode(value.text))); } catch { }
                break;
        }
    }

    internal static NeoProcessFailedEventArgs DecodeProcessFailure(ulong value, long nativeCode, string? description)
    {
        const ulong kindMask = 0xffff_ffff;
        const ulong crashed = 1UL << 32;
        const ulong recreateView = 1UL << 33;
        const ulong restartApplication = 1UL << 34;
        var rawKind = (uint)(value & kindMask);
        var candidate = (NeoProcessFailureKind)rawKind;
        var kind = Enum.IsDefined(candidate) ? candidate : NeoProcessFailureKind.Unknown;
        var recovery = (value & restartApplication) != 0
            ? NeoProcessRecoveryAction.RestartApplication
            : (value & recreateView) != 0 ? NeoProcessRecoveryAction.RecreateView : NeoProcessRecoveryAction.None;
        return new NeoProcessFailedEventArgs(
            kind,
            (value & crashed) != 0,
            recovery,
            nativeCode,
            string.IsNullOrEmpty(description) ? null : description);
    }

    private void HandleNavigationDecision(NativeMethods.neoastra_event value, Uri? uri)
    {
        var handler = NavigationRequested;
        if (handler is null || value.decision.Handle == 0 || uri is null) return;
        StartDecision(value.decision.Handle, () => handler(new NeoNavigationRequest(uri, (value.value & 1) != 0, (value.value & 2) != 0)),
            static decision => new DecisionResponse(decision.Action), new DecisionResponse(NeoDecisionAction.Default));
    }

    private void HandlePermissionDecision(NativeMethods.neoastra_event value, Uri? uri)
    {
        var handler = PermissionRequested;
        if (value.decision.Handle == 0) return;
        if (handler is null)
        {
            CompleteImmediate(value.decision.Handle, new DecisionResponse(NeoDecisionAction.Deny));
            return;
        }

        StartDecision(value.decision.Handle,
            () => handler(new NeoPermissionRequest((NeoPermissionKind)(int)value.value, uri)),
            static decision => new DecisionResponse(decision.Action, null, decision.Persist),
            new DecisionResponse(NeoDecisionAction.Deny));
    }

    private void HandleDownloadDecision(NativeMethods.neoastra_event value, Uri? uri)
    {
        if (value.decision.Handle == 0) return;
        var handler = DownloadRequested;
        if (handler is null || uri is null)
        {
            CompleteImmediate(value.decision.Handle, new DecisionResponse(NeoDecisionAction.Cancel));
            return;
        }

        var download = GetOrCreateDownload(value);
        if (download is null)
        {
            CompleteImmediate(value.decision.Handle, new DecisionResponse(NeoDecisionAction.Cancel));
            return;
        }

        StartDecision(value.decision.Handle,
            () => handler(new NeoDownloadRequest(uri, NullIfEmpty(Utf8String.Decode(value.text)), NullIfEmpty(Utf8String.Decode(value.text2)), value.value == ulong.MaxValue ? null : checked((long)value.value), download)),
            static decision => new DecisionResponse(decision.Action, decision.DestinationPath),
            new DecisionResponse(NeoDecisionAction.Cancel));
    }

    private void HandleNewWindowDecision(NativeMethods.neoastra_event value, Uri? uri)
    {
        if (value.decision.Handle == 0) return;
        var handler = NewWindowRequested;
        if (handler is null)
        {
            CompleteImmediate(value.decision.Handle, new DecisionResponse(NeoDecisionAction.Cancel));
            return;
        }

        StartDecision(value.decision.Handle,
            () => handler(new NeoNewWindowRequest(this, value.decision.Handle, uri, NullIfEmpty(Utf8String.Decode(value.text)), (value.value & 1) != 0, null)),
            static decision => new DecisionResponse(decision.Action, TargetView: decision.TargetView),
            new DecisionResponse(NeoDecisionAction.Cancel));
    }

    private void HandleScriptDialogDecision(NativeMethods.neoastra_event value, Uri? uri)
    {
        if (value.decision.Handle == 0) return;
        var handler = ScriptDialogRequested;
        var kind = (NeoScriptDialogKind)(int)value.value;
        var safe = kind == NeoScriptDialogKind.Alert ? NeoDecisionAction.Allow : NeoDecisionAction.Cancel;
        if (handler is null) { CompleteImmediate(value.decision.Handle, new DecisionResponse(safe)); return; }
        StartDecision(value.decision.Handle,
            () => handler(new NeoScriptDialogRequest(kind, Utf8String.Decode(value.text), NullIfEmpty(Utf8String.Decode(value.text2)), uri)),
            static decision => new DecisionResponse(decision.Action, decision.Text), new DecisionResponse(safe));
    }

    private void HandleFileChooserDecision(NativeMethods.neoastra_event value)
    {
        if (value.decision.Handle == 0) return;
        var handler = FileChooserRequested;
        if (handler is null) { CompleteImmediate(value.decision.Handle, new DecisionResponse(NeoDecisionAction.Cancel)); return; }
        var accepted = Utf8String.Decode(value.text).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        StartDecision(value.decision.Handle,
            () => handler(new NeoFileChooserRequest(accepted, (value.value & 1) != 0)),
            static decision => new DecisionResponse(decision.Action, Paths: decision.Paths), new DecisionResponse(NeoDecisionAction.Cancel));
    }

    private void HandleAuthenticationDecision(NativeMethods.neoastra_event value, Uri? uri)
    {
        if (value.decision.Handle == 0) return;
        var handler = AuthenticationRequested;
        if (handler is null) { CompleteImmediate(value.decision.Handle, new DecisionResponse(NeoDecisionAction.Default)); return; }
        var realm = NullIfEmpty(Utf8String.Decode(value.text2));
        var scheme = NullIfEmpty(Utf8String.Decode(value.text3));
        if (scheme is null && realm is not null)
        {
            var separator = realm.IndexOf(' ');
            scheme = separator > 0 ? realm[..separator] : realm;
        }
        StartDecision(value.decision.Handle,
            () => handler(new NeoAuthenticationRequest(uri?.Host ?? Utf8String.Decode(value.text), value.native_code != 0 ? checked((int)value.native_code) : uri?.Port ?? 0, realm, scheme, uri)),
            static decision => new DecisionResponse(decision.Action, decision.UserName, SecondaryText: decision.Password), new DecisionResponse(NeoDecisionAction.Default));
    }

    private void HandleClientCertificateDecision(NativeMethods.neoastra_event value)
    {
        if (value.decision.Handle == 0) return;
        var handler = ClientCertificateRequested;
        if (handler is null) { CompleteImmediate(value.decision.Handle, new DecisionResponse(NeoDecisionAction.Default)); return; }
        StartDecision(value.decision.Handle,
            () => handler(new NeoClientCertificateRequest(Utf8String.Decode(value.text), checked((int)value.native_code), checked((int)value.value), (value.value2 & 1) != 0)),
            static decision => new DecisionResponse(decision.Action, SelectedIndex: decision.SelectedIndex), new DecisionResponse(NeoDecisionAction.Default));
    }

    private void HandleTlsErrorDecision(NativeMethods.neoastra_event value, Uri? uri)
    {
        if (value.decision.Handle == 0) return;
        var handler = TlsErrorRequested;
        if (handler is null) { CompleteImmediate(value.decision.Handle, new DecisionResponse(NeoDecisionAction.Deny)); return; }
        StartDecision(value.decision.Handle,
            () => handler(new NeoTlsErrorRequest(uri, NullIfEmpty(Utf8String.Decode(value.text2)), value.native_code)),
            static decision => new DecisionResponse(decision.Action), new DecisionResponse(NeoDecisionAction.Deny));
    }

    private void HandleFullscreenDecision(NativeMethods.neoastra_event value, Uri? uri)
    {
        if (value.decision.Handle == 0) return;
        var handler = FullscreenRequested;
        if (handler is null) { CompleteImmediate(value.decision.Handle, new DecisionResponse(NeoDecisionAction.Deny)); return; }
        StartDecision(value.decision.Handle,
            () => handler(new NeoFullscreenRequest((value.value & 1) != 0, uri)),
            static decision => new DecisionResponse(decision.Action), new DecisionResponse(NeoDecisionAction.Deny));
    }

    private void StartDecision<T>(nint nativeDecision, Func<ValueTask<T>> handler, Func<T, DecisionResponse> convert, DecisionResponse safeDefault)
    {
        NativeMethods.neoastra_decision_retain(new(nativeDecision));
        var handle = new SafeDecisionHandle(nativeDecision);
        var result = NativeMethods.neoastra_decision_defer(new(nativeDecision));
        if (NativeError.Code(result) != NeoErrorCode.Success)
        {
            handle.Dispose();
            return;
        }

        _ = RunDecisionAsync(handle, handler, convert, safeDefault);
    }

    private async Task RunDecisionAsync<T>(SafeDecisionHandle decision, Func<ValueTask<T>> handler, Func<T, DecisionResponse> convert, DecisionResponse safeDefault)
    {
        var response = await ResolveDecisionAsync(handler, convert, safeDefault, _decisionTimeout).ConfigureAwait(false);

        try
        {
            if (Environment.Application.Dispatcher.CheckAccess())
            {
                CompleteDecision(decision, response);
            }
            else
            {
                await Environment.Application.Dispatcher.InvokeAsync(() => CompleteDecision(decision, response));
            }
        }
        catch
        {
            // Expired decisions and application shutdown are safely contained.
        }
        finally
        {
            decision.Dispose();
        }
    }

    internal static async Task<TResult> ResolveDecisionAsync<T, TResult>(Func<ValueTask<T>> handler, Func<T, TResult> convert, TResult safeDefault, TimeSpan timeout)
    {
        try
        {
            var pending = handler();
            var value = pending.IsCompletedSuccessfully
                ? pending.Result
                : await pending.AsTask().WaitAsync(timeout).ConfigureAwait(false);
            return convert(value);
        }
        catch
        {
            // Timeouts and application-policy failures deliberately use the safe default.
            return safeDefault;
        }
    }

    private static void CompleteImmediate(nint decision, DecisionResponse response)
    {
        using var handle = new SafeDecisionHandle(decision);
        NativeMethods.neoastra_decision_retain(new(decision));
        CompleteDecision(handle, response);
    }

    private static unsafe void CompleteDecision(SafeDecisionHandle decision, DecisionResponse response)
    {
        if (response.Paths is { Count: 0 } || response.Paths?.Any(string.IsNullOrWhiteSpace) == true)
        {
            response = new DecisionResponse(NeoDecisionAction.Cancel);
        }

        NativeMethods.neoastra_view_t targetView = default;
        try { if (response.TargetView is not null) targetView = response.TargetView.NativeHandle; }
        catch (ObjectDisposedException) { response = new DecisionResponse(NeoDecisionAction.Cancel); }

        using var text = new Utf8String(response.Text);
        using var secondaryText = new Utf8String(response.SecondaryText);
        NativeMethods.neoastra_string_view_t* paths = null;
        byte** pathBuffers = null;
        var pathCount = response.Paths?.Count ?? 0;
        try
        {
            if (pathCount != 0)
            {
                paths = (NativeMethods.neoastra_string_view_t*)NativeMemory.Alloc((nuint)pathCount, (nuint)sizeof(NativeMethods.neoastra_string_view_t));
                pathBuffers = (byte**)NativeMemory.AllocZeroed((nuint)pathCount, (nuint)sizeof(byte*));
                for (var index = 0; index < pathCount; index++)
                {
                    var path = response.Paths![index];
                    var length = System.Text.Encoding.UTF8.GetByteCount(path);
                    var buffer = (byte*)NativeMemory.Alloc((nuint)length);
                    pathBuffers[index] = buffer;
                    System.Text.Encoding.UTF8.GetBytes(path, new Span<byte>(buffer, length));
                    paths[index] = new NativeMethods.neoastra_string_view_t(new NativeMethods.neoastra_string_view { data = buffer, length = (ulong)length });
                }
            }

            var raw = new NativeMethods.neoastra_decision_response
            {
                size = (uint)sizeof(NativeMethods.neoastra_decision_response),
                version = 1,
                action = (NativeMethods.neoastra_decision_action)response.Action,
                text = text.View,
                paths = paths,
                path_count = checked((uint)pathCount),
                persist = response.Persist ? 1u : 0u,
                secondary_text = secondaryText.View,
                target_view = targetView,
                selected_index = response.SelectedIndex < 0 ? uint.MaxValue : checked((uint)response.SelectedIndex),
            };
            var native = new NativeMethods.neoastra_decision_response_t(raw);
            NativeMethods.neoastra_error_t error = default;
            var result = NativeMethods.neoastra_decision_complete(new(decision.DangerousGetHandle()), &native, &error);
            if (error.Handle != 0) new SafeErrorHandle(error.Handle).Dispose();
            if (NativeError.Code(result) is not (NeoErrorCode.Success or NeoErrorCode.InvalidState or NeoErrorCode.TimedOut))
            {
                var fallback = new NativeMethods.neoastra_decision_response_t(new NativeMethods.neoastra_decision_response
                {
                    size = (uint)sizeof(NativeMethods.neoastra_decision_response),
                    version = 1,
                    action = NativeMethods.neoastra_decision_action.NEOASTRA_DECISION_CANCEL,
                    selected_index = uint.MaxValue,
                });
                NativeMethods.neoastra_decision_complete(new(decision.DangerousGetHandle()), &fallback, null);
            }
        }
        finally
        {
            if (pathBuffers is not null)
            {
                for (var index = 0; index < pathCount; index++)
                {
                    if (pathBuffers[index] is not null)
                    {
                        NativeMemory.Clear(pathBuffers[index], (nuint)paths[index].Value.length);
                        NativeMemory.Free(pathBuffers[index]);
                    }
                }
                NativeMemory.Free(pathBuffers);
            }
            NativeMemory.Free(paths);
        }
    }

    private NeoDownload? GetOrCreateDownload(NativeMethods.neoastra_event value)
    {
        if (value.download.Handle == 0) return null;
        if (_downloads.TryGetValue(value.object_id, out var existing)) return existing;
        var created = new NeoDownload(value.download.Handle);
        _downloads[value.object_id] = created;
        return created;
    }

    private void RaiseDownloadEvent(NativeMethods.neoastra_event value, EventHandler<NeoDownloadEventArgs>? handler, bool terminal)
    {
        var download = GetOrCreateDownload(value);
        if (download is null) return;
        try { download.Refresh(); } catch { }
        try { handler?.Invoke(this, new NeoDownloadEventArgs(download)); } catch { }
        if (terminal) _downloads.Remove(value.object_id);
    }

    private void RaiseNavigationCompleted(NeoNavigationCompletedEventArgs args)
    {
        try { NavigationCompleted?.Invoke(this, args); } catch { }
    }

    private static Uri? DecodeUri(NativeMethods.neoastra_string_view_t value)
    {
        var text = Utf8String.Decode(value);
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static void ValidateAbsoluteUri(Uri? uri, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(uri, parameterName);
        if (!uri.IsAbsoluteUri) throw new ArgumentException("An absolute URI is required.", parameterName);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void ViewEvent(void* context, NativeMethods.neoastra_event_t* nativeEvent)
    {
        try
        {
            if (nativeEvent is null) return;
            var root = GCHandle.FromIntPtr((nint)context);
            (root.Target as NeoAstra)?.DispatchEvent(nativeEvent->Value);
        }
        catch
        {
            // No managed exception may cross the unmanaged callback boundary.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void ScriptEvaluated(void* context, NativeMethods.neoastra_result_t result, NativeMethods.neoastra_string_view_t value, NativeMethods.neoastra_error_t error)
    {
        try
        {
            var operation = NativeOperation.Get<string?>(context);
            if (operation is null) return;
            if (NativeError.Code(result) == NeoErrorCode.Success)
            {
                var text = Utf8String.Decode(value);
                operation.Complete(string.Equals(text, "null", StringComparison.Ordinal) ? null : text);
            }
            else
            {
                operation.Fail(NativeError.CreateException(NativeError.Read(NativeError.Code(result), error.Handle), "evaluate script"));
            }
        }
        catch (Exception ex)
        {
            NativeOperation.Get<string?>(context)?.Fail(ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void ScriptAdded(void* context, NativeMethods.neoastra_result_t result, NativeMethods.neoastra_string_view_t value, NativeMethods.neoastra_error_t error)
    {
        try
        {
            var operation = NativeOperation.Get<NeoUserScript>(context);
            if (operation is null) return;
            if (NativeError.Code(result) == NeoErrorCode.Success)
            {
                var identifier = Utf8String.Decode(value);
                if (string.IsNullOrEmpty(identifier)) throw new InvalidDataException("The native backend returned an empty script identifier.");
                operation.Complete(new NeoUserScript((NeoAstra)operation.Owner!, identifier));
            }
            else
            {
                operation.Fail(NativeError.CreateException(NativeError.Read(NativeError.Code(result), error.Handle), "add persistent script"));
            }
        }
        catch (Exception ex)
        {
            NativeOperation.Get<NeoUserScript>(context)?.Fail(ex);
        }
    }

    internal void RemoveScript(string identifier)
    {
        ThrowIfDisposed();
        using var nativeIdentifier = new Utf8String(identifier);
        NativeError.ThrowIfFailed(NativeMethods.neoastra_view_remove_script(NativeHandle, nativeIdentifier.View), default, "remove persistent script");
    }

    private readonly record struct DecisionResponse(
        NeoDecisionAction Action,
        string? Text = null,
        bool Persist = false,
        string? SecondaryText = null,
        IReadOnlyList<string>? Paths = null,
        NeoAstra? TargetView = null,
        int SelectedIndex = -1);
}
