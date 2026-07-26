// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Rpc;

/// <summary>Named security defaults with fully visible resolved settings.</summary>
public sealed class NeoSecurityProfile
{
    private NeoSecurityProfile(string name, bool bridgeEnabled, bool controlledAssetsOnly, bool restrictNavigation, bool denyPopups, bool devTools, bool detailedErrors, bool allowRemoteContent, bool development)
    {
        Name = name; BridgeEnabled = bridgeEnabled; ControlledAssetsOnly = controlledAssetsOnly; RestrictTopLevelNavigation = restrictNavigation; DenyPopups = denyPopups; DevToolsEnabled = devTools; DetailedErrors = detailedErrors; AllowRemoteContent = allowRemoteContent; Development = development;
    }

    /// <summary>Production local-application defaults: controlled assets, explicit capabilities, denied popups, restrictive navigation, and redacted errors.</summary>
    public static NeoSecurityProfile ProductionLocalApp { get; } = new("production-local-app", true, true, true, true, false, false, false, false);
    /// <summary>Development defaults: exact loopback origin may be configured while command grants remain explicit.</summary>
    public static NeoSecurityProfile DevelopmentLocalApp { get; } = new("development", true, false, true, true, true, true, false, true);
    /// <summary>Remote-content defaults: bridge/RPC disabled and external navigation delegated to the system browser.</summary>
    public static NeoSecurityProfile RemoteContent { get; } = new("remote-content", false, false, true, true, false, false, true, false);

    /// <summary>Gets the stable profile name.</summary>
    public string Name { get; }
    /// <summary>Gets whether a bridge may be enabled after explicit capabilities exist.</summary>
    public bool BridgeEnabled { get; }
    /// <summary>Gets whether only controlled application-scheme assets are accepted.</summary>
    public bool ControlledAssetsOnly { get; }
    /// <summary>Gets whether top-level navigation is restricted to the configured application/development origin.</summary>
    public bool RestrictTopLevelNavigation { get; }
    /// <summary>Gets whether unexpected popup/new-window requests are denied.</summary>
    public bool DenyPopups { get; }
    /// <summary>Gets whether DevTools may be enabled.</summary>
    public bool DevToolsEnabled { get; }
    /// <summary>Gets whether bounded detailed RPC errors may be enabled.</summary>
    public bool DetailedErrors { get; }
    /// <summary>Gets whether remote content is expected.</summary>
    public bool AllowRemoteContent { get; }
    /// <summary>Gets whether development authority is active.</summary>
    public bool Development { get; }

    /// <summary>Validates an exact development origin and release boundary.</summary>
    /// <param name="release">Whether release output is being produced.</param><param name="developmentOrigin">Optional exact development origin.</param><param name="allowRemoteDevelopmentOrigin">Explicit review override for non-loopback development origins.</param>
    /// <exception cref="InvalidOperationException">Development authority could flow into release output.</exception><exception cref="ArgumentException">The origin is not exact or safely approved.</exception>
    public void Validate(bool release, Uri? developmentOrigin, bool allowRemoteDevelopmentOrigin)
    {
        if (release && (Development || developmentOrigin is not null)) throw new InvalidOperationException("Development security authority cannot enter release output.");
        if (!Development && developmentOrigin is not null) throw new ArgumentException("A development origin requires the development profile.", nameof(developmentOrigin));
        if (developmentOrigin is null) return;
        if (!developmentOrigin.IsAbsoluteUri || developmentOrigin.Scheme is not ("http" or "https") || developmentOrigin.UserInfo.Length != 0 || developmentOrigin.AbsolutePath != "/" || !string.IsNullOrEmpty(developmentOrigin.Query) || !string.IsNullOrEmpty(developmentOrigin.Fragment)) throw new ArgumentException("A development origin must be one exact HTTP(S) origin without credentials or path data.", nameof(developmentOrigin));
        var isAddress = System.Net.IPAddress.TryParse(developmentOrigin.Host, out var address);
        if (!allowRemoteDevelopmentOrigin && !isAddress && !string.Equals(developmentOrigin.Host, "localhost", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Development servers bind to loopback unless a remote-network origin is explicitly approved.", nameof(developmentOrigin));
        if (!allowRemoteDevelopmentOrigin && isAddress && !System.Net.IPAddress.IsLoopback(address!)) throw new ArgumentException("Development servers bind to loopback unless a remote-network origin is explicitly approved.", nameof(developmentOrigin));
        if (developmentOrigin.Port <= 0) throw new ArgumentException("A development origin requires one exact configured port.", nameof(developmentOrigin));
    }
}

/// <summary>Contains immutable framework authorization information attached before application dispatch.</summary>
public sealed class NeoRpcAuthorizationDecision
{
    internal NeoRpcAuthorizationDecision(string permission, string code, IReadOnlyList<NeoCapabilityScope> scopes) { Permission = permission; Code = code; Scopes = Array.AsReadOnly(scopes.ToArray()); }
    /// <summary>Gets the exact declared permission authorized for this operation.</summary>
    public string Permission { get; }
    /// <summary>Gets the stable authorization decision code.</summary>
    public string Code { get; }
    /// <summary>Gets validated immutable scopes that applications may inspect for domain authorization.</summary>
    public IReadOnlyList<NeoCapabilityScope> Scopes { get; }
}

/// <summary>Receives policy-redacted authorization audit events that still contain operational identifiers.</summary>
public interface INeoCapabilityDiagnosticSink
{
    /// <summary>Records one policy-redacted authorization decision. Implementations must not throw.</summary><param name="diagnostic">The bounded diagnostic.</param>
    void Write(NeoCapabilityDiagnostic diagnostic);
}

/// <summary>Describes one policy-redacted capability decision without command arguments or sensitive exact scope values.</summary>
public readonly record struct NeoCapabilityDiagnostic
{
    /// <summary>Initializes a redacted decision.</summary>
    public NeoCapabilityDiagnostic(DateTimeOffset timestamp, string decisionCode, string viewLabel, string? permission, string operation, bool originAuthenticated, bool wholeViewTrust, NeoCapabilityPlatform platform, string documentSessionSuffix, string correlationId)
    { Timestamp = timestamp; DecisionCode = decisionCode; ViewLabel = viewLabel; Permission = permission; Operation = operation; OriginAuthenticated = originAuthenticated; WholeViewTrust = wholeViewTrust; Platform = platform; DocumentSessionSuffix = documentSessionSuffix; CorrelationId = correlationId; }
    /// <summary>Gets the timestamp.</summary>
    public DateTimeOffset Timestamp { get; }
    /// <summary>Gets the stable decision code.</summary>
    public string DecisionCode { get; }
    /// <summary>Gets the immutable configured view label, which remains operational identifier data.</summary>
    public string ViewLabel { get; }
    /// <summary>Gets the exact declared permission, if present.</summary>
    public string? Permission { get; }
    /// <summary>Gets the exact declared command/event name.</summary>
    public string Operation { get; }
    /// <summary>Gets whether backend-authenticated origin metadata was present.</summary>
    public bool OriginAuthenticated { get; }
    /// <summary>Gets whether the backend explicitly marked the entire view as trusted.</summary>
    public bool WholeViewTrust { get; }
    /// <summary>Gets the trusted host platform.</summary>
    public NeoCapabilityPlatform Platform { get; }
    /// <summary>Gets only a bounded suffix of the opaque document-session ID.</summary>
    public string DocumentSessionSuffix { get; }
    /// <summary>Gets the host correlation ID.</summary>
    public string CorrelationId { get; }
}

/// <summary>Fail-closed RPC authorization backed by one immutable resolved capability manifest.</summary>
public sealed class NeoCapabilityAuthorizationService : INeoRpcAuthorizationService
{
    private readonly NeoCapabilityManifest _manifest; private readonly INeoCapabilityDiagnosticSink? _diagnostics;
    /// <summary>Initializes authorization. The manifest grants only its explicit records.</summary><param name="manifest">Resolved immutable manifest.</param>
    public NeoCapabilityAuthorizationService(NeoCapabilityManifest manifest) : this(manifest, null) { }
    /// <summary>Initializes authorization and redacted auditing.</summary><param name="manifest">Resolved immutable manifest.</param><param name="diagnostics">Optional redacted diagnostic sink.</param>
    public NeoCapabilityAuthorizationService(NeoCapabilityManifest manifest, INeoCapabilityDiagnosticSink? diagnostics) { ArgumentNullException.ThrowIfNull(manifest); _manifest = manifest; _diagnostics = diagnostics; }
    internal NeoCapabilityManifest Manifest => _manifest;
    /// <inheritdoc />
    public ValueTask<NeoRpcAuthorizationResult> AuthorizeAsync(NeoRpcAuthorizationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var match = _manifest.Match(request); Write(request, match.Code);
        return ValueTask.FromResult(match.Allowed
            ? NeoRpcAuthorizationResult.Allow(new NeoRpcAuthorizationDecision(match.Permission!, match.Code, match.Scopes!))
            : match.Code is NeoCapabilityDecisionCodes.ScopeDenied or NeoCapabilityDecisionCodes.ScopeInvalid ? NeoRpcAuthorizationResult.DenyScope(match.Code) : NeoRpcAuthorizationResult.DenyPermission(match.Code));
    }
    private void Write(NeoRpcAuthorizationRequest request, string code)
    {
        var session = request.Context.DocumentSessionId; var suffix = session.Length <= 8 ? session : session[^8..];
        try { _diagnostics?.Write(new(DateTimeOffset.UtcNow, code, request.Context.ViewLabel, request.Permission, request.Operation, request.Context.SourceOrigin is not null && request.Context.Platform != NeoCapabilityPlatform.Linux, request.Context.WholeViewTrust, request.Context.Platform, suffix, request.Context.CorrelationId)); } catch { }
    }
}

/// <summary>Redacted current limits, usage, and capability posture for support diagnostics.</summary>
public sealed class NeoRpcDiagnosticSnapshot
{
    internal NeoRpcDiagnosticSnapshot(string profile, string? manifestHash, int sessions, int invocations, int resources, long resourceBytes, IReadOnlyDictionary<string, long> limits, IReadOnlyList<string> grants)
    { Profile = profile; ManifestHash = manifestHash; ActiveSessions = sessions; ActiveInvocations = invocations; ActiveResources = resources; ActiveResourceBytes = resourceBytes; Limits = new System.Collections.ObjectModel.ReadOnlyDictionary<string, long>(new SortedDictionary<string, long>(limits.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal)); Grants = Array.AsReadOnly(grants.ToArray()); }
    /// <summary>Gets the named resolved security profile.</summary>
    public string Profile { get; }
    /// <summary>Gets the embedded capability manifest hash, but no scope secrets.</summary>
    public string? ManifestHash { get; }
    /// <summary>Gets active session count.</summary>
    public int ActiveSessions { get; }
    /// <summary>Gets active application invocation count.</summary>
    public int ActiveInvocations { get; }
    /// <summary>Gets active resource count.</summary>
    public int ActiveResources { get; }
    /// <summary>Gets active declared resource bytes.</summary>
    public long ActiveResourceBytes { get; }
    /// <summary>Gets configured safe limits.</summary>
    public IReadOnlyDictionary<string, long> Limits { get; }
    /// <summary>Gets redacted manifest grant summaries.</summary>
    public IReadOnlyList<string> Grants { get; }
}
