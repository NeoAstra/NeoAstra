// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.Json.Serialization.Metadata;

namespace NeoAstra.Rpc;

/// <summary>Contains stable framework RPC error codes.</summary>
public static class NeoRpcErrorCodes
{
    /// <summary>The frame or arguments were invalid.</summary>
    public const string InvalidRequest = "invalid_request";
    /// <summary>The requested command is not registered.</summary>
    public const string CommandNotFound = "command_not_found";
    /// <summary>The request ID is already active or was already used in this session.</summary>
    public const string DuplicateRequest = "duplicate_request";
    /// <summary>The caller lacks the command permission.</summary>
    public const string PermissionDenied = "permission_denied";
    /// <summary>The caller is outside the permitted scope.</summary>
    public const string ScopeDenied = "scope_denied";
    /// <summary>The frame exceeded a configured size bound.</summary>
    public const string PayloadTooLarge = "payload_too_large";
    /// <summary>A concurrency or resource limit was exhausted.</summary>
    public const string TooManyRequests = "too_many_requests";
    /// <summary>The command exceeded its configured deadline.</summary>
    public const string Timeout = "timeout";
    /// <summary>The caller or session canceled the operation.</summary>
    public const string OperationCanceled = "operation_canceled";
    /// <summary>The frontend connection was closed.</summary>
    public const string ConnectionClosed = "connection_closed";
    /// <summary>The frontend contract or protocol is incompatible.</summary>
    public const string ProtocolMismatch = "protocol_mismatch";
    /// <summary>A declared contract value could not be serialized.</summary>
    public const string SerializationFailed = "serialization_failed";
    /// <summary>An unclassified application failure was safely redacted.</summary>
    public const string InternalError = "internal_error";
}

/// <summary>Represents a stable, safe RPC error payload.</summary>
public readonly record struct NeoRpcError
{
    /// <summary>Initializes an RPC error.</summary>
    /// <param name="code">The stable machine-readable code.</param>
    /// <param name="message">The bounded user-safe message.</param>
    /// <param name="correlationId">The optional diagnostic correlation identifier.</param>
    /// <exception cref="ArgumentException">A value is malformed, unsafe, or exceeds its wire bound.</exception>
    public NeoRpcError(string code, string message, string? correlationId)
    {
        if (!NeoRpcValidation.IsErrorCode(code)) throw new ArgumentException("The RPC error code must use bounded lowercase colon-separated identifiers.", nameof(code));
        if (!NeoRpcValidation.IsSafeMessage(message)) throw new ArgumentException("The RPC error message must be non-empty, bounded, and free of control characters.", nameof(message));
        if (!NeoRpcValidation.IsCorrelationId(correlationId)) throw new ArgumentException("The correlation ID must be bounded printable ASCII.", nameof(correlationId));
        Code = code;
        Message = message;
        CorrelationId = correlationId;
    }

    /// <summary>Gets the stable machine-readable code.</summary>
    public string Code { get; }
    /// <summary>Gets the safe display message.</summary>
    public string Message { get; }
    /// <summary>Gets the optional diagnostic correlation identifier.</summary>
    public string? CorrelationId { get; }
    /// <summary>Gets whether a later retry can reasonably succeed.</summary>
    public bool Retryable { get; init; }
}

/// <summary>Represents an explicit application error that may cross the RPC boundary.</summary>
public sealed class NeoRpcException : Exception
{
    /// <summary>Initializes an explicit RPC exception.</summary>
    /// <param name="code">The stable application error code.</param>
    /// <param name="message">The safe client-facing message.</param>
    /// <exception cref="ArgumentException">A value is malformed, unsafe, or exceeds its wire bound.</exception>
    public NeoRpcException(string code, string message) : this(code, message, retryable: false) { }

    /// <summary>Initializes an explicit RPC exception.</summary>
    /// <param name="code">The stable application error code.</param>
    /// <param name="message">The safe client-facing message.</param>
    /// <param name="retryable">Whether a later retry can reasonably succeed.</param>
    /// <exception cref="ArgumentException">A value is malformed, unsafe, or exceeds its wire bound.</exception>
    public NeoRpcException(string code, string message, bool retryable) : base(message)
    {
        if (!NeoRpcValidation.IsErrorCode(code)) throw new ArgumentException("The RPC error code is malformed.", nameof(code));
        if (!NeoRpcValidation.IsSafeMessage(message)) throw new ArgumentException("The RPC error message must be non-empty, bounded, and free of control characters.", nameof(message));
        Code = code;
        Retryable = retryable;
    }

    /// <summary>Gets the stable application error code.</summary>
    public string Code { get; }
    /// <summary>Gets whether a later retry can reasonably succeed.</summary>
    public bool Retryable { get; }
}

/// <summary>Maps an application exception to an explicitly safe RPC error.</summary>
public interface INeoRpcErrorMapper
{
    /// <summary>Attempts to map an application exception.</summary>
    /// <param name="exception">The contained application exception.</param>
    /// <param name="context">The immutable invocation context.</param>
    /// <param name="error">Receives the safe mapped error.</param>
    /// <returns><see langword="true"/> when a mapping was produced.</returns>
    bool TryMap(Exception exception, NeoRpcContext context, out NeoRpcError error);
}

/// <summary>Authorizes commands and event subscriptions against trusted host context.</summary>
public interface INeoRpcAuthorizationService
{
    /// <summary>Authorizes one declared RPC operation.</summary>
    /// <param name="request">The immutable authorization request.</param>
    /// <param name="cancellationToken">Cancels authorization during session teardown.</param>
    /// <returns>The explicit authorization decision.</returns>
    ValueTask<NeoRpcAuthorizationResult> AuthorizeAsync(NeoRpcAuthorizationRequest request, CancellationToken cancellationToken);
}

/// <summary>Describes an authorization request using trusted invocation identity.</summary>
/// <param name="Context">The trusted invocation context.</param>
/// <param name="Operation">The stable command or event wire name.</param>
/// <param name="Permission">The declared permission, when one exists.</param>
/// <param name="IsSubscription">Whether the request creates an event subscription.</param>
/// <param name="Arguments">The bounded command arguments, or an undefined value for subscriptions.</param>
public readonly record struct NeoRpcAuthorizationRequest(NeoRpcContext Context, string Operation, string? Permission, bool IsSubscription, System.Text.Json.JsonElement Arguments);

/// <summary>Represents an authorization decision.</summary>
public readonly record struct NeoRpcAuthorizationResult
{
    private NeoRpcAuthorizationResult(bool allowed, string? errorCode, string? decisionCode, NeoRpcAuthorizationDecision? decision)
    {
        IsAllowed = allowed;
        ErrorCode = errorCode;
        DecisionCode = decisionCode;
        Decision = decision;
    }

    /// <summary>Gets whether dispatch is allowed.</summary>
    public bool IsAllowed { get; }
    /// <summary>Gets the stable denial code.</summary>
    public string? ErrorCode { get; }
    /// <summary>Gets the detailed stable host-side decision code.</summary>
    public string? DecisionCode { get; }
    /// <summary>Gets validated immutable authorization state for an allowed operation.</summary>
    public NeoRpcAuthorizationDecision? Decision { get; }
    /// <summary>Creates an allowed result.</summary>
    public static NeoRpcAuthorizationResult Allow() => new(true, null, NeoCapabilityDecisionCodes.Allowed, null);
    /// <summary>Creates an allowed result with validated immutable capability state.</summary>
    /// <param name="decision">The capability decision attached to the invocation context.</param>
    public static NeoRpcAuthorizationResult Allow(NeoRpcAuthorizationDecision decision) { ArgumentNullException.ThrowIfNull(decision); return new(true, null, decision.Code, decision); }
    /// <summary>Creates a permission denial.</summary>
    public static NeoRpcAuthorizationResult DenyPermission() => DenyPermission(NeoCapabilityDecisionCodes.PermissionMissing);
    /// <summary>Creates a permission denial with a detailed host-side reason.</summary>
    /// <param name="decisionCode">Stable capability decision code.</param>
    public static NeoRpcAuthorizationResult DenyPermission(string decisionCode) => new(false, NeoRpcErrorCodes.PermissionDenied, decisionCode, null);
    /// <summary>Creates a scope denial.</summary>
    public static NeoRpcAuthorizationResult DenyScope() => DenyScope(NeoCapabilityDecisionCodes.ScopeDenied);
    /// <summary>Creates a scope denial with a detailed host-side reason.</summary>
    /// <param name="decisionCode">Stable capability decision code.</param>
    public static NeoRpcAuthorizationResult DenyScope(string decisionCode) => new(false, NeoRpcErrorCodes.ScopeDenied, decisionCode, null);
}

/// <summary>Receives bounded RPC lifecycle diagnostics without taking a logging dependency.</summary>
public interface INeoRpcDiagnosticSink
{
    /// <summary>Records one diagnostic. Implementations must not throw.</summary>
    /// <param name="diagnostic">The bounded diagnostic value.</param>
    void Write(NeoRpcDiagnostic diagnostic);
}

/// <summary>Describes a bounded RPC diagnostic.</summary>
/// <param name="Level">The diagnostic severity.</param>
/// <param name="Code">The stable diagnostic code.</param>
/// <param name="Message">The bounded message.</param>
/// <param name="CorrelationId">The optional correlation identifier.</param>
public readonly record struct NeoRpcDiagnostic(NeoRpcDiagnosticLevel Level, string Code, string Message, string? CorrelationId);

/// <summary>Defines RPC diagnostic severity.</summary>
public enum NeoRpcDiagnosticLevel
{
    /// <summary>Low-level diagnostic information.</summary>
    Debug,
    /// <summary>Normal lifecycle information.</summary>
    Information,
    /// <summary>A contained invalid operation or degraded condition.</summary>
    Warning,
    /// <summary>A contained operation failure.</summary>
    Error,
}

/// <summary>Registers generated service commands and events into a builder.</summary>
public interface INeoRpcServiceRegistration
{
    /// <summary>Adds the generated registration to a builder.</summary>
    /// <param name="builder">The destination builder.</param>
    void Register(NeoRpcBuilder builder);
}

/// <summary>Dispatches work through a trusted host scheduler.</summary>
public interface INeoRpcDispatcher
{
    /// <summary>Invokes asynchronous work on the scheduler.</summary>
    /// <param name="callback">The work to invoke.</param>
    /// <param name="cancellationToken">Cancels while queued or executing.</param>
    /// <returns>A task that completes with the work.</returns>
    ValueTask<object?> InvokeAsync(Func<ValueTask<object?>> callback, CancellationToken cancellationToken);
}

/// <summary>Represents trusted immutable identity used to open one document RPC session.</summary>
public sealed class NeoRpcSessionIdentity
{
    /// <summary>Initializes trusted session identity.</summary>
    /// <param name="viewLabel">The immutable application-assigned view label.</param>
    /// <param name="documentSessionId">The host-generated document-session ID.</param>
    /// <exception cref="ArgumentException">An identifier is empty, malformed, or too long.</exception>
    public NeoRpcSessionIdentity(string viewLabel, string documentSessionId)
    {
        if (string.IsNullOrWhiteSpace(viewLabel) || viewLabel.Length > 128 || viewLabel.Any(char.IsControl))
            throw new ArgumentException("The view label must be non-empty, bounded, and free of control characters.", nameof(viewLabel));
        NeoRpcValidation.ValidateId(documentSessionId, nameof(documentSessionId));
        ViewLabel = viewLabel;
        DocumentSessionId = documentSessionId;
    }

    /// <summary>Gets the immutable view label.</summary>
    public string ViewLabel { get; }
    /// <summary>Gets the opaque host-generated document-session ID.</summary>
    public string DocumentSessionId { get; }
    /// <summary>Gets or sets the backend-authenticated sender origin.</summary>
    public Uri? SourceOrigin { get; init; }
    /// <summary>Gets or sets whether the backend authenticated the main frame as sender.</summary>
    public bool IsMainFrame { get; init; }
    /// <summary>Gets or sets whether the application explicitly trusts every script in the view.</summary>
    public bool WholeViewTrust { get; init; }
    /// <summary>Gets the trusted host platform.</summary>
    public NeoCapabilityPlatform Platform { get; init; } = GetCurrentPlatform();
    /// <summary>Gets or sets an opaque application document identifier.</summary>
    public string? DocumentId { get; init; }
    /// <summary>Gets or sets the negotiated protocol minor version.</summary>
    public int ProtocolMinor { get; init; }
    /// <summary>Gets or sets the negotiated immutable feature set.</summary>
    public IReadOnlyList<string> Features { get; init; } = Array.Empty<string>();
    /// <summary>Gets or sets the generated application contract hash.</summary>
    public string ContractHash { get; init; } = string.Empty;
    /// <summary>Gets or sets an optional per-view or per-session service provider.</summary>
    public IServiceProvider? Services { get; init; }
    /// <summary>Gets or sets an optional trusted UI dispatcher.</summary>
    public INeoRpcDispatcher? Dispatcher { get; init; }

    private static NeoCapabilityPlatform GetCurrentPlatform()
    {
        if (OperatingSystem.IsWindows()) return NeoCapabilityPlatform.Windows;
        if (OperatingSystem.IsMacOS()) return NeoCapabilityPlatform.MacOS;
        if (OperatingSystem.IsLinux()) return NeoCapabilityPlatform.Linux;
        throw new PlatformNotSupportedException("NeoAstra RPC capabilities require Windows, macOS, or Linux.");
    }
}

/// <summary>Provides immutable trusted state for one RPC invocation.</summary>
public readonly struct NeoRpcContext
{
    private readonly WeakReference<global::NeoAstra.NeoAstra>? _view;
    private readonly WeakReference<NeoWindow>? _window;

    internal NeoRpcContext(
        NeoRpcSessionIdentity identity,
        string correlationId,
        CancellationToken cancellationToken,
        NeoRpcResourceCollection resources,
        global::NeoAstra.NeoAstra? view = null,
        NeoWindow? window = null)
    {
        ViewLabel = identity.ViewLabel;
        DocumentSessionId = identity.DocumentSessionId;
        DocumentId = identity.DocumentId;
        SourceOrigin = identity.SourceOrigin;
        IsMainFrame = identity.IsMainFrame;
        WholeViewTrust = identity.WholeViewTrust;
        Platform = identity.Platform;
        CorrelationId = correlationId;
        CancellationToken = cancellationToken;
        Services = identity.Services;
        ProtocolMinor = identity.ProtocolMinor;
        Features = identity.Features;
        ContractHash = identity.ContractHash;
        Dispatcher = identity.Dispatcher;
        Resources = resources;
        Authorization = null;
        _view = view is null ? null : new(view);
        _window = window is null ? null : new(window);
    }

    /// <summary>Gets the immutable application view label.</summary>
    public string ViewLabel { get; }
    /// <summary>Gets the host-generated document-session ID.</summary>
    public string DocumentSessionId { get; }
    /// <summary>Gets the opaque application document identifier.</summary>
    public string? DocumentId { get; }
    /// <summary>Gets the backend-authenticated source origin, when available.</summary>
    public Uri? SourceOrigin { get; }
    /// <summary>Gets whether the backend authenticated the main frame as sender.</summary>
    public bool IsMainFrame { get; }
    /// <summary>Gets whether all scripts in the view were explicitly trusted.</summary>
    public bool WholeViewTrust { get; }
    /// <summary>Gets the trusted host platform.</summary>
    public NeoCapabilityPlatform Platform { get; }
    /// <summary>Gets the host-generated correlation ID.</summary>
    public string CorrelationId { get; }
    /// <summary>Gets the request cancellation token.</summary>
    public CancellationToken CancellationToken { get; }
    /// <summary>Gets the optional per-view or per-session service provider.</summary>
    public IServiceProvider? Services { get; }
    /// <summary>Gets the negotiated protocol minor version.</summary>
    public int ProtocolMinor { get; }
    /// <summary>Gets the negotiated feature set.</summary>
    public IReadOnlyList<string> Features { get; }
    /// <summary>Gets the generated application contract hash.</summary>
    public string ContractHash { get; }
    /// <summary>Gets the session-owned resource collection.</summary>
    public NeoRpcResourceCollection Resources { get; }
    /// <summary>Gets the completed framework authorization decision, or <see langword="null"/> while authorization is pending.</summary>
    public NeoRpcAuthorizationDecision? Authorization { get; }
    internal INeoRpcDispatcher? Dispatcher { get; }

    internal NeoRpcContext WithAuthorization(NeoRpcAuthorizationDecision decision)
    {
        var copy = this;
        return new NeoRpcContext(copy, decision);
    }

    private NeoRpcContext(NeoRpcContext source, NeoRpcAuthorizationDecision decision)
    {
        ViewLabel = source.ViewLabel; DocumentSessionId = source.DocumentSessionId; DocumentId = source.DocumentId; SourceOrigin = source.SourceOrigin;
        IsMainFrame = source.IsMainFrame; WholeViewTrust = source.WholeViewTrust; Platform = source.Platform; CorrelationId = source.CorrelationId;
        CancellationToken = source.CancellationToken; Services = source.Services; ProtocolMinor = source.ProtocolMinor; Features = source.Features;
        ContractHash = source.ContractHash; Resources = source.Resources; Dispatcher = source.Dispatcher; Authorization = decision;
        _view = source._view; _window = source._window;
    }

    /// <summary>Attempts to obtain the originating view without keeping it alive.</summary>
    /// <param name="view">Receives the live view.</param>
    /// <returns><see langword="true"/> when the view is still alive.</returns>
    public bool TryGetView(out global::NeoAstra.NeoAstra? view)
    {
        view = null;
        return _view?.TryGetTarget(out view) == true;
    }

    /// <summary>Attempts to obtain the owned window without keeping it alive.</summary>
    /// <param name="window">Receives the live window.</param>
    /// <returns><see langword="true"/> when the window is still alive.</returns>
    public bool TryGetWindow(out NeoWindow? window)
    {
        window = null;
        return _window?.TryGetTarget(out window) == true;
    }
}

/// <summary>Represents a generated or direct bounded JSON channel source.</summary>
/// <typeparam name="T">The declared channel item type.</typeparam>
public sealed class NeoRpcChannel<T>
{
    /// <summary>Initializes a channel result.</summary>
    /// <param name="items">The asynchronous item source.</param>
    /// <param name="itemTypeInfo">Source-generated JSON metadata for items.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public NeoRpcChannel(IAsyncEnumerable<T> items, JsonTypeInfo<T> itemTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(itemTypeInfo);
        Items = items;
        ItemTypeInfo = itemTypeInfo;
    }

    /// <summary>Gets the item source.</summary>
    public IAsyncEnumerable<T> Items { get; }
    /// <summary>Gets source-generated item serialization metadata.</summary>
    public JsonTypeInfo<T> ItemTypeInfo { get; }
}

/// <summary>Represents an opaque session-owned resource identifier.</summary>
/// <param name="Id">The opaque resource ID.</param>
public readonly record struct NeoRpcResourceHandle(string Id);
