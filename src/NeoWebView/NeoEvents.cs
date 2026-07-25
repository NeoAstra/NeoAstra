// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

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
public sealed record NeoDownloadRequest(Uri Source, string? SuggestedFileName, string? MimeType, long? ExpectedLength);

/// <summary>Describes the action to take for a download request.</summary>
/// <param name="Action">The requested action.</param>
/// <param name="DestinationPath">An explicit destination path.</param>
public readonly record struct NeoDownloadDecision(NeoDecisionAction Action, string? DestinationPath = null)
{
    /// <summary>Cancels the download.</summary>
    public static NeoDownloadDecision Cancel => new(NeoDecisionAction.Cancel);
    /// <summary>Uses backend default handling.</summary>
    public static NeoDownloadDecision Default => new(NeoDecisionAction.Default);
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
/// <param name="TargetUri">The target URI, when known.</param>
/// <param name="FrameName">The requested frame name.</param>
/// <param name="IsUserInitiated">Whether a user gesture initiated the request.</param>
/// <param name="RequestedBounds">The requested logical bounds, when supplied.</param>
public sealed record NeoNewWindowRequest(Uri? TargetUri, string? FrameName, bool IsUserInitiated, NeoRect? RequestedBounds);

/// <summary>Describes the action to take for a new-window request.</summary>
/// <param name="Action">The requested action.</param>
public readonly record struct NeoNewWindowDecision(NeoDecisionAction Action)
{
    /// <summary>Cancels the request. This is the safe default.</summary>
    public static NeoNewWindowDecision Cancel => new(NeoDecisionAction.Cancel);
    /// <summary>Opens the requested URI externally.</summary>
    public static NeoNewWindowDecision OpenExternal => new(NeoDecisionAction.OpenExternal);
    /// <summary>Navigates the current view.</summary>
    public static NeoNewWindowDecision NavigateCurrent => new(NeoDecisionAction.Allow);
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
