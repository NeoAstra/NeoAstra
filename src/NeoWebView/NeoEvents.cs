// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using NeoWebView.Interop;
using NeoWebView.Interop.Generated;

namespace NeoWebView;

/// <summary>Represents a browser cookie.</summary>
public sealed class NeoCookie
{
    /// <summary>Initializes a cookie.</summary>
    /// <param name="name">The cookie name.</param>
    /// <param name="value">The cookie value.</param>
    /// <param name="domain">The cookie domain.</param>
    /// <param name="path">The cookie path.</param>
    /// <exception cref="ArgumentException">A required value is empty or invalid.</exception>
    public NeoCookie(string name, string value, string domain, string path = "/")
    {
        Name = name;
        Value = value;
        Domain = domain;
        Path = path;
        Validate();
    }

    /// <summary>Gets the cookie name.</summary>
    public string Name { get; }

    /// <summary>Gets or sets the cookie value.</summary>
    public string Value { get; set; }

    /// <summary>Gets the cookie domain.</summary>
    public string Domain { get; }

    /// <summary>Gets the cookie path.</summary>
    public string Path { get; }

    /// <summary>Gets or sets the expiration instant, or <see langword="null"/> for a session cookie.</summary>
    public DateTimeOffset? Expires { get; set; }

    /// <summary>Gets or sets whether the cookie is restricted to secure transports.</summary>
    public bool IsSecure { get; set; }

    /// <summary>Gets or sets whether scripts are prevented from reading the cookie.</summary>
    public bool IsHttpOnly { get; set; }

    /// <summary>Gets or sets the SameSite policy.</summary>
    public NeoCookieSameSite SameSite { get; set; }

    /// <summary>Gets whether this is a session cookie.</summary>
    public bool IsSession => Expires is null;

    internal void Validate()
    {
        if (string.IsNullOrEmpty(Name) || Name.Any(static c => char.IsControl(c) || c is ';' or '='))
        {
            throw new ArgumentException("The cookie name is invalid.", nameof(Name));
        }

        ArgumentNullException.ThrowIfNull(Value);
        if (string.IsNullOrWhiteSpace(Domain))
        {
            throw new ArgumentException("The cookie domain must not be empty.", nameof(Domain));
        }

        if (string.IsNullOrEmpty(Path) || Path[0] != '/')
        {
            throw new ArgumentException("The cookie path must start with '/'.", nameof(Path));
        }

        if (!Enum.IsDefined(SameSite))
        {
            throw new ArgumentOutOfRangeException(nameof(SameSite));
        }
    }
}

/// <summary>Provides data for a cancelable window close request.</summary>
public sealed class NeoWindowClosingEventArgs : EventArgs
{
    /// <summary>Gets or sets whether the close request should be canceled when supported by the backend.</summary>
    public bool Cancel { get; set; }
}

/// <summary>Provides data when a window's logical bounds change.</summary>
/// <param name="oldBounds">The previous bounds.</param>
/// <param name="newBounds">The new bounds.</param>
public sealed class NeoWindowBoundsChangedEventArgs(NeoRect oldBounds, NeoRect newBounds) : EventArgs
{
    /// <summary>Gets the previous bounds.</summary>
    public NeoRect OldBounds { get; } = oldBounds;
    /// <summary>Gets the new bounds.</summary>
    public NeoRect NewBounds { get; } = newBounds;
}

/// <summary>Provides data when a window scale factor changes.</summary>
/// <param name="oldScaleFactor">The previous scale factor.</param>
/// <param name="newScaleFactor">The new scale factor.</param>
public sealed class NeoWindowScaleFactorChangedEventArgs(double oldScaleFactor, double newScaleFactor) : EventArgs
{
    /// <summary>Gets the previous scale factor.</summary>
    public double OldScaleFactor { get; } = oldScaleFactor;
    /// <summary>Gets the new scale factor.</summary>
    public double NewScaleFactor { get; } = newScaleFactor;
}

/// <summary>Describes a navigation policy request.</summary>
/// <param name="Uri">The target URI.</param>
/// <param name="IsMainFrame">Whether the request targets the main frame.</param>
/// <param name="IsUserInitiated">Whether a user gesture initiated the request.</param>
public sealed record NeoNavigationRequest(Uri Uri, bool IsMainFrame = true, bool IsUserInitiated = false);

/// <summary>Describes the action to take for a navigation request.</summary>
/// <param name="Action">The requested action.</param>
public readonly record struct NeoNavigationDecision(NeoDecisionAction Action)
{
    /// <summary>Use normal browser handling.</summary>
    public static NeoNavigationDecision Default => new(NeoDecisionAction.Default);
    /// <summary>Allow the navigation.</summary>
    public static NeoNavigationDecision Allow => new(NeoDecisionAction.Allow);
    /// <summary>Cancel the navigation.</summary>
    public static NeoNavigationDecision Cancel => new(NeoDecisionAction.Cancel);
    /// <summary>Open the target using an external application.</summary>
    public static NeoNavigationDecision OpenExternal => new(NeoDecisionAction.OpenExternal);
}

/// <summary>Describes a permission request.</summary>
/// <param name="Kind">The portable permission kind.</param>
/// <param name="Origin">The requesting origin, when available.</param>
/// <param name="IsUserInitiated">Whether a user gesture initiated the request.</param>
/// <param name="CanPersist">Whether the backend can persist the response.</param>
public sealed record NeoPermissionRequest(NeoPermissionKind Kind, Uri? Origin, bool IsUserInitiated = false, bool CanPersist = false);

/// <summary>Describes the action to take for a permission request.</summary>
/// <param name="Action">The requested action.</param>
/// <param name="Persist">Whether to persist the action.</param>
public readonly record struct NeoPermissionDecision(NeoDecisionAction Action, bool Persist = false)
{
    /// <summary>Use normal browser handling.</summary>
    public static NeoPermissionDecision Default => new(NeoDecisionAction.Default);
    /// <summary>Allows the permission once.</summary>
    public static NeoPermissionDecision AllowOnce => new(NeoDecisionAction.Allow);
    /// <summary>Denies the permission once.</summary>
    public static NeoPermissionDecision DenyOnce => new(NeoDecisionAction.Deny);
    /// <summary>Allows and persists the permission.</summary>
    public static NeoPermissionDecision AllowAndPersist => new(NeoDecisionAction.Allow, true);
    /// <summary>Denies and persists the permission.</summary>
    public static NeoPermissionDecision DenyAndPersist => new(NeoDecisionAction.Deny, true);
}

/// <summary>Describes a download request.</summary>
/// <param name="Source">The source URI.</param>
/// <param name="SuggestedFileName">The suggested destination file name.</param>
/// <param name="MimeType">The MIME type, when known.</param>
/// <param name="ExpectedLength">The expected byte length, or <see langword="null"/>.</param>
/// <param name="Download">The tracked download.</param>
public sealed record NeoDownloadRequest(Uri Source, string? SuggestedFileName, string? MimeType, long? ExpectedLength, NeoDownload Download);

/// <summary>Describes the action to take for a download request.</summary>
/// <param name="Action">The requested action.</param>
/// <param name="DestinationPath">An explicit destination path.</param>
public readonly record struct NeoDownloadDecision(NeoDecisionAction Action, string? DestinationPath = null)
{
    /// <summary>Cancels the download.</summary>
    public static NeoDownloadDecision Cancel => new(NeoDecisionAction.Cancel);
    /// <summary>Uses backend default handling.</summary>
    public static NeoDownloadDecision Default => new(NeoDecisionAction.Default);
    /// <summary>Reports that the host handled the download outside the browser.</summary>
    public static NeoDownloadDecision HandledExternal => new(NeoDecisionAction.HandledExternal);
    /// <summary>Accepts a download at an explicit path.</summary>
    /// <param name="path">The destination path.</param>
    /// <returns>A download decision.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public static NeoDownloadDecision Accept(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new(NeoDecisionAction.Download, path);
    }
}

/// <summary>Describes a browser request to open a new window.</summary>
public sealed class NeoNewWindowRequest
{
    private readonly NeoWebView _opener;
    private readonly SafeDecisionHandle _nativeDecision;
    private int _creationStarted;

    internal NeoNewWindowRequest(NeoWebView opener, nint nativeDecision, Uri? targetUri, string? frameName, bool isUserInitiated, NeoRect? requestedBounds)
    {
        _opener = opener;
        NativeMethods.neo_webview_decision_retain(new(nativeDecision));
        _nativeDecision = new SafeDecisionHandle(nativeDecision);
        TargetUri = targetUri;
        FrameName = frameName;
        IsUserInitiated = isUserInitiated;
        RequestedBounds = requestedBounds;
    }

    /// <summary>Gets the requested target URI, when known.</summary>
    public Uri? TargetUri { get; }
    /// <summary>Gets the requested frame name.</summary>
    public string? FrameName { get; }
    /// <summary>Gets whether a user gesture initiated the request.</summary>
    public bool IsUserInitiated { get; }
    /// <summary>Gets the requested logical bounds, when supplied.</summary>
    public NeoRect? RequestedBounds { get; }

    /// <summary>Creates an opener-compatible view for this popup request. This method may be called only once.</summary>
    /// <param name="host">The host that will own the popup view.</param>
    /// <param name="options">View options, or <see langword="null"/> for defaults.</param>
    /// <param name="cancellationToken">Cancels the native creation operation.</param>
    /// <returns>The tracked popup view.</returns>
    /// <exception cref="InvalidOperationException">Creation was already started, or the popup decision is no longer active.</exception>
    /// <exception cref="ArgumentException"><paramref name="options"/> specifies a profile other than the opener profile.</exception>
    public ValueTask<NeoWebView> CreateViewAsync(NeoWebViewHost host, NeoWebViewOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _creationStarted, 1) != 0)
        {
            throw new InvalidOperationException("A popup target has already been created for this request.");
        }

        options ??= new NeoWebViewOptions();
        options.Profile ??= _opener.Profile;
        if (!ReferenceEquals(options.Profile, _opener.Profile))
        {
            throw new ArgumentException("A popup view must use the opener's profile.", nameof(options));
        }

        var pending = _opener.Environment.CreatePopupWebViewAsync(host, options, _nativeDecision.DangerousGetHandle(), cancellationToken);
        GC.KeepAlive(_nativeDecision);
        return pending;
    }
}

/// <summary>Describes the action to take for a new-window request.</summary>
/// <param name="Action">The requested action.</param>
/// <param name="TargetView">An opener-compatible tracked target view.</param>
public readonly record struct NeoNewWindowDecision(NeoDecisionAction Action, NeoWebView? TargetView = null)
{
    /// <summary>Cancels the request. This is the safe default.</summary>
    public static NeoNewWindowDecision Cancel => new(NeoDecisionAction.Cancel);
    /// <summary>Opens the requested URI externally.</summary>
    public static NeoNewWindowDecision OpenExternal => new(NeoDecisionAction.OpenExternal);
    /// <summary>Navigates the current view.</summary>
    public static NeoNewWindowDecision NavigateCurrent => new(NeoDecisionAction.Allow);
    /// <summary>Associates an opener-compatible tracked view with the request.</summary>
    /// <param name="view">The view created by <see cref="NeoNewWindowRequest.CreateViewAsync"/>.</param>
    /// <returns>A popup decision.</returns>
    public static NeoNewWindowDecision UseView(NeoWebView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new(NeoDecisionAction.Allow, view);
    }
}

/// <summary>Identifies a JavaScript dialog kind.</summary>
public enum NeoScriptDialogKind
{
    /// <summary>An informational alert.</summary>
    Alert,
    /// <summary>An accept-or-cancel confirmation.</summary>
    Confirm,
    /// <summary>A text-input prompt.</summary>
    Prompt,
    /// <summary>A confirmation before leaving a document.</summary>
    BeforeUnload,
}

/// <summary>Describes a JavaScript dialog request.</summary>
public sealed record NeoScriptDialogRequest(NeoScriptDialogKind Kind, string Message, string? DefaultText, Uri? Origin);

/// <summary>Describes the response to a JavaScript dialog.</summary>
public readonly record struct NeoScriptDialogDecision(NeoDecisionAction Action, string? Text = null)
{
    /// <summary>Accepts the dialog.</summary>
    public static NeoScriptDialogDecision Accept => new(NeoDecisionAction.Allow);
    /// <summary>Accepts a prompt with the supplied text.</summary>
    public static NeoScriptDialogDecision AcceptPrompt(string text) => new(NeoDecisionAction.Allow, text ?? throw new ArgumentNullException(nameof(text)));
    /// <summary>Cancels the dialog.</summary>
    public static NeoScriptDialogDecision Cancel => new(NeoDecisionAction.Cancel);
}

/// <summary>Describes a file chooser request.</summary>
public sealed record NeoFileChooserRequest(IReadOnlyList<string> AcceptedTypes, bool AllowsMultipleSelection);

/// <summary>Describes the response to a file chooser.</summary>
public readonly record struct NeoFileChooserDecision(NeoDecisionAction Action, IReadOnlyList<string>? Paths = null)
{
    /// <summary>Cancels the chooser.</summary>
    public static NeoFileChooserDecision Cancel => new(NeoDecisionAction.Cancel);
    /// <summary>Selects one or more local paths.</summary>
    public static NeoFileChooserDecision Select(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Length == 0 || paths.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("At least one nonempty path is required.", nameof(paths));
        return new(NeoDecisionAction.Allow, paths);
    }
}

/// <summary>Describes an HTTP authentication challenge.</summary>
public sealed record NeoAuthenticationRequest(string Host, int Port, string? Realm, string? Scheme, Uri? Uri);

/// <summary>Describes an HTTP authentication response.</summary>
public readonly record struct NeoAuthenticationDecision(NeoDecisionAction Action, string? UserName = null, string? Password = null)
{
    /// <summary>Uses normal browser handling.</summary>
    public static NeoAuthenticationDecision Default => new(NeoDecisionAction.Default);
    /// <summary>Cancels authentication.</summary>
    public static NeoAuthenticationDecision Cancel => new(NeoDecisionAction.Cancel);
    /// <summary>Supplies credentials for this challenge.</summary>
    public static NeoAuthenticationDecision Credentials(string userName, string password)
        => new(NeoDecisionAction.Allow, userName ?? throw new ArgumentNullException(nameof(userName)), password ?? throw new ArgumentNullException(nameof(password)));
}

/// <summary>Describes a client-certificate selection request.</summary>
public sealed record NeoClientCertificateRequest(string Host, int Port, int CandidateCount, bool IsProxy);

/// <summary>Describes a client-certificate selection response.</summary>
public readonly record struct NeoClientCertificateDecision(NeoDecisionAction Action, int SelectedIndex = -1)
{
    /// <summary>Uses normal browser certificate selection.</summary>
    public static NeoClientCertificateDecision Default => new(NeoDecisionAction.Default);
    /// <summary>Cancels certificate selection.</summary>
    public static NeoClientCertificateDecision Cancel => new(NeoDecisionAction.Cancel);
    /// <summary>Selects a zero-based certificate candidate.</summary>
    public static NeoClientCertificateDecision Select(int index) => index < 0 ? throw new ArgumentOutOfRangeException(nameof(index)) : new(NeoDecisionAction.Allow, index);
}

/// <summary>Describes a server TLS certificate error.</summary>
public sealed record NeoTlsErrorRequest(Uri? Uri, string? CertificateSubject, long NativeErrorCode);

/// <summary>Describes a TLS-error response.</summary>
public readonly record struct NeoTlsErrorDecision(NeoDecisionAction Action)
{
    /// <summary>Rejects the certificate. This is the safe default.</summary>
    public static NeoTlsErrorDecision Deny => new(NeoDecisionAction.Deny);
    /// <summary>Allows this request despite the certificate error.</summary>
    public static NeoTlsErrorDecision Allow => new(NeoDecisionAction.Allow);
}

/// <summary>Describes a web-content fullscreen request.</summary>
public sealed record NeoFullscreenRequest(bool IsEntering, Uri? Source);

/// <summary>Describes a fullscreen response.</summary>
public readonly record struct NeoFullscreenDecision(NeoDecisionAction Action)
{
    /// <summary>Allows fullscreen.</summary>
    public static NeoFullscreenDecision Allow => new(NeoDecisionAction.Allow);
    /// <summary>Denies fullscreen. This is the safe default.</summary>
    public static NeoFullscreenDecision Deny => new(NeoDecisionAction.Deny);
}

/// <summary>Identifies a download lifecycle state.</summary>
public enum NeoDownloadState
{
    /// <summary>The destination decision is pending.</summary>
    Requested,
    /// <summary>The download is transferring data.</summary>
    InProgress,
    /// <summary>The download completed successfully.</summary>
    Completed,
    /// <summary>The download was canceled.</summary>
    Canceled,
    /// <summary>The download failed.</summary>
    Failed,
}

/// <summary>Identifies a response to a browser decision request.</summary>
public enum NeoDecisionAction
{
    /// <summary>Use documented backend default handling.</summary>
    Default,
    /// <summary>Allow the request.</summary>
    Allow,
    /// <summary>Deny the request.</summary>
    Deny,
    /// <summary>Cancel the request.</summary>
    Cancel,
    /// <summary>Open the target externally.</summary>
    OpenExternal,
    /// <summary>Accept a download.</summary>
    Download,
    /// <summary>The host handled the request outside the browser.</summary>
    HandledExternal,
}

/// <summary>Provides data when a navigation finishes.</summary>
public sealed class NeoNavigationCompletedEventArgs : EventArgs
{
    /// <summary>Initializes navigation completion data.</summary>
    public NeoNavigationCompletedEventArgs(Uri? uri, bool isSuccess, NeoErrorCode errorCode, long nativeErrorCode, ulong navigationId)
    {
        Uri = uri;
        IsSuccess = isSuccess;
        ErrorCode = errorCode;
        NativeErrorCode = nativeErrorCode;
        NavigationId = navigationId;
    }

    /// <summary>Gets the final URI, when known.</summary>
    public Uri? Uri { get; }
    /// <summary>Gets whether navigation succeeded.</summary>
    public bool IsSuccess { get; }
    /// <summary>Gets the portable error code.</summary>
    public NeoErrorCode ErrorCode { get; }
    /// <summary>Gets the backend-native error code.</summary>
    public long NativeErrorCode { get; }
    /// <summary>Gets the navigation identifier.</summary>
    public ulong NavigationId { get; }
}

/// <summary>Provides data when a browser or web-content process exits or becomes unresponsive.</summary>
public sealed class NeoProcessFailedEventArgs : EventArgs
{
    /// <summary>Initializes process-failure data.</summary>
    /// <param name="kind">The portable failure category.</param>
    /// <param name="isCrash">Whether the backend identified an abnormal crash.</param>
    /// <param name="recoveryAction">The recommended portable recovery action.</param>
    /// <param name="nativeCode">The backend exit code or termination reason, when available.</param>
    /// <param name="processDescription">The backend process description, when available.</param>
    public NeoProcessFailedEventArgs(
        NeoProcessFailureKind kind,
        bool isCrash,
        NeoProcessRecoveryAction recoveryAction,
        long nativeCode,
        string? processDescription)
    {
        Kind = kind;
        IsCrash = isCrash;
        RecoveryAction = recoveryAction;
        NativeCode = nativeCode;
        ProcessDescription = processDescription;
    }

    /// <summary>Gets the portable failure category.</summary>
    public NeoProcessFailureKind Kind { get; }
    /// <summary>Gets whether the backend identified an abnormal crash.</summary>
    public bool IsCrash { get; }
    /// <summary>Gets the recommended recovery action.</summary>
    public NeoProcessRecoveryAction RecoveryAction { get; }
    /// <summary>Gets the backend exit code or termination reason, when available.</summary>
    public long NativeCode { get; }
    /// <summary>Gets the backend process description, when available.</summary>
    public string? ProcessDescription { get; }
}

/// <summary>Provides a message sent by web content.</summary>
public sealed class NeoWebMessageReceivedEventArgs : EventArgs
{
    /// <summary>Initializes web-message data.</summary>
    public NeoWebMessageReceivedEventArgs(string json, Uri? sourceOrigin, bool isMainFrame)
    {
        Json = json;
        SourceOrigin = sourceOrigin;
        IsMainFrame = isMainFrame;
    }

    /// <summary>Gets the JSON or text payload.</summary>
    public string Json { get; }
    /// <summary>Gets the source origin, when available.</summary>
    public Uri? SourceOrigin { get; }
    /// <summary>Gets whether the main frame sent the message.</summary>
    public bool IsMainFrame { get; }
}
