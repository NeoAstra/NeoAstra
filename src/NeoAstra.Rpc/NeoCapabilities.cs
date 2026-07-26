// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeoAstra.Rpc;

/// <summary>Identifies a supported NeoAstra host platform without consulting renderer state.</summary>
public enum NeoCapabilityPlatform
{
    /// <summary>Microsoft Windows with WebView2.</summary>
    Windows,
    /// <summary>Apple macOS with WKWebView.</summary>
    MacOS,
    /// <summary>Linux with WebKitGTK.</summary>
    Linux,
}

/// <summary>Classifies the security impact of a permission.</summary>
public enum NeoPermissionRisk
{
    /// <summary>The operation exposes little ambient authority.</summary>
    Low,
    /// <summary>The operation can expose private user or application data.</summary>
    Sensitive,
    /// <summary>The operation can materially change the host or execute code.</summary>
    High,
}

/// <summary>Identifies a built-in validated scope family.</summary>
public enum NeoScopeFamily
{
    /// <summary>No argument scope is used.</summary>
    None,
    /// <summary>Filesystem roots and operations.</summary>
    Filesystem,
    /// <summary>External URL opener destinations.</summary>
    Url,
    /// <summary>Predeclared executable invocation.</summary>
    Process,
    /// <summary>Clipboard direction and formats.</summary>
    Clipboard,
    /// <summary>Notification categories and payload bounds.</summary>
    Notifications,
    /// <summary>Dialog kinds, locations, and filters.</summary>
    Dialogs,
    /// <summary>Network destinations, methods, redirects, and byte bounds.</summary>
    Network,
    /// <summary>Remembered grant identity and duration.</summary>
    Persistence,
}

/// <summary>Controls how exact scope values appear in support diagnostics.</summary>
public enum NeoAuditRedaction
{
    /// <summary>Only the scope family and item counts are reported.</summary>
    Full,
    /// <summary>Non-sensitive identifiers are reported but paths, URLs, and payloads remain hidden.</summary>
    SensitiveValues,
}

/// <summary>Declares one renderer permission and its static security policy.</summary>
public sealed class NeoPermissionDeclaration
{
    /// <summary>Initializes a permission declaration.</summary>
    /// <param name="id">Stable colon-separated permission ID.</param>
    /// <param name="version">Positive scope/declaration schema version.</param>
    /// <param name="commands">Exact commands covered by the permission.</param>
    /// <param name="risk">Security risk classification.</param>
    /// <param name="scopeFamily">Validated scope family.</param>
    /// <exception cref="ArgumentException">A declaration value is malformed or unsafe.</exception>
    public NeoPermissionDeclaration(string id, int version, IEnumerable<string> commands, NeoPermissionRisk risk, NeoScopeFamily scopeFamily)
    {
        if (!NeoRpcValidation.IsPermission(id)) throw new ArgumentException("A permission ID must be a bounded colon-separated ASCII identifier.", nameof(id));
        if (version is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(version));
        ArgumentNullException.ThrowIfNull(commands);
        var commandArray = commands.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (commandArray.Length is < 1 or > 256 || commandArray.Any(static value => !NeoRpcValidation.IsWireName(value)))
            throw new ArgumentException("Permission commands must be a non-empty bounded set of exact wire names.", nameof(commands));
        if (!Enum.IsDefined(risk)) throw new ArgumentOutOfRangeException(nameof(risk));
        if (!Enum.IsDefined(scopeFamily)) throw new ArgumentOutOfRangeException(nameof(scopeFamily));
        Id = id;
        Version = version;
        Commands = Array.AsReadOnly(commandArray);
        Risk = risk;
        ScopeFamily = scopeFamily;
    }

    /// <summary>Gets the stable permission ID.</summary>
    public string Id { get; }
    /// <summary>Gets the declaration and scope schema version.</summary>
    public int Version { get; }
    /// <summary>Gets the exact commands covered by the permission.</summary>
    public IReadOnlyList<string> Commands { get; }
    /// <summary>Gets the risk classification.</summary>
    public NeoPermissionRisk Risk { get; }
    /// <summary>Gets the validated scope family.</summary>
    public NeoScopeFamily ScopeFamily { get; }
    /// <summary>Gets whether every grant requires scope data.</summary>
    public bool ScopeRequired { get; init; }
    /// <summary>Gets whether multiple grants may safely union their validated scopes.</summary>
    public bool UnionSafe { get; init; }
    /// <summary>Gets supported platforms. An empty set means every supported platform.</summary>
    public IReadOnlyList<NeoCapabilityPlatform> Platforms { get; init; } = Array.Empty<NeoCapabilityPlatform>();
    /// <summary>Gets the safe default command timeout.</summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Gets the safe command-specific concurrency limit.</summary>
    public int MaximumConcurrency { get; init; } = 8;
    /// <summary>Gets the audit redaction policy.</summary>
    public NeoAuditRedaction Redaction { get; init; } = NeoAuditRedaction.Full;
    /// <summary>Gets bounded documentation for tooling.</summary>
    public string Documentation { get; init; } = string.Empty;

    internal NeoPermissionDeclaration Validate()
    {
        if (ScopeRequired && ScopeFamily == NeoScopeFamily.None) throw new ArgumentException($"Permission '{Id}' requires a scope family.");
        if (!ScopeRequired && ScopeFamily != NeoScopeFamily.None) throw new ArgumentException($"Permission '{Id}' declares a scope family but does not require scope.");
        if (DefaultTimeout <= TimeSpan.Zero || DefaultTimeout > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(DefaultTimeout));
        if (MaximumConcurrency is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(MaximumConcurrency));
        if (Documentation.Length > 4096 || Documentation.Any(char.IsControl)) throw new ArgumentException("Permission documentation must be bounded and free of controls.", nameof(Documentation));
        if (!Enum.IsDefined(Redaction)) throw new ArgumentOutOfRangeException(nameof(Redaction));
        ArgumentNullException.ThrowIfNull(Platforms);
        if (Platforms.Count > 3 || Platforms.Any(static value => !Enum.IsDefined(value)) || Platforms.Distinct().Count() != Platforms.Count)
            throw new ArgumentException("Permission platforms must be a unique supported set.", nameof(Platforms));
        return this;
    }
}

/// <summary>Describes one statically composed plugin permission catalog.</summary>
public sealed class NeoPluginPermissionCatalog
{
    private const int MaximumPermissions = 512;
    private const int MaximumPermissionSets = 128;
    /// <summary>Initializes a plugin catalog. Referencing or registering it grants no renderer authority.</summary>
    /// <param name="id">Stable plugin ID.</param>
    /// <param name="version">Plugin version.</param>
    /// <param name="minimumNeoAstraVersion">Minimum compatible NeoAstra version.</param>
    /// <param name="permissions">Static permission declarations.</param>
    /// <param name="permissionSets">Optional named sets expanded into exact permission IDs.</param>
    /// <exception cref="ArgumentException">Catalog metadata is malformed.</exception>
    public NeoPluginPermissionCatalog(string id, string version, string minimumNeoAstraVersion, IEnumerable<NeoPermissionDeclaration> permissions, IReadOnlyDictionary<string, IReadOnlyList<string>>? permissionSets = null)
    {
        if (!NeoRpcValidation.IsWireName(id, 128)) throw new ArgumentException("The plugin ID is malformed.", nameof(id));
        if (!IsVersion(version) || !IsVersion(minimumNeoAstraVersion)) throw new ArgumentException("Plugin versions must be bounded numeric semantic versions.");
        ArgumentNullException.ThrowIfNull(permissions);
        Id = id;
        Version = version;
        MinimumNeoAstraVersion = minimumNeoAstraVersion;
        var permissionArray = permissions.Take(MaximumPermissions + 1).Select(static value => value.Validate()).ToArray();
        if (permissionArray.Length is < 1 or > MaximumPermissions) throw new ArgumentException($"A plugin catalog must contain 1 to {MaximumPermissions} permissions.", nameof(permissions));
        Permissions = Array.AsReadOnly(permissionArray);
        var declared = Permissions.ToDictionary(static value => value.Id, StringComparer.Ordinal);
        var sets = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var suppliedSets = permissionSets ?? new Dictionary<string, IReadOnlyList<string>>();
        if (suppliedSets.Count > MaximumPermissionSets) throw new ArgumentException($"A plugin catalog may contain at most {MaximumPermissionSets} permission sets.", nameof(permissionSets));
        if (suppliedSets.Values.Sum(static value => value?.Count ?? 0) > 4096) throw new ArgumentException("A plugin catalog may contain at most 4096 permission-set entries.", nameof(permissionSets));
        foreach (var pair in suppliedSets)
        {
            if (!NeoRpcValidation.IsWireName(pair.Key, 128) || pair.Value is null || pair.Value.Count is < 1 or > 256 || pair.Value.Any(static value => !NeoRpcValidation.IsPermission(value)))
                throw new ArgumentException("A plugin permission set is malformed.", nameof(permissionSets));
            if (pair.Value.Distinct(StringComparer.Ordinal).Count() != pair.Value.Count) throw new ArgumentException("A plugin permission set contains duplicate permission IDs.", nameof(permissionSets));
            if (pair.Value.Any(value => !declared.TryGetValue(value, out var declaration) || declaration.Risk == NeoPermissionRisk.High))
                throw new ArgumentException("Plugin permission sets may contain only this plugin's low or sensitive permissions; high-risk permissions require individual grants.", nameof(permissionSets));
            sets.Add(pair.Key, Array.AsReadOnly(pair.Value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
        }
        PermissionSets = new ReadOnlyDictionary<string, IReadOnlyList<string>>(sets);
    }

    /// <summary>Gets the stable plugin ID.</summary>
    public string Id { get; }
    /// <summary>Gets the plugin version.</summary>
    public string Version { get; }
    /// <summary>Gets the minimum NeoAstra version.</summary>
    public string MinimumNeoAstraVersion { get; }
    /// <summary>Gets static permission declarations.</summary>
    public IReadOnlyList<NeoPermissionDeclaration> Permissions { get; }
    /// <summary>Gets statically expanded permission sets.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> PermissionSets { get; }

    private static bool IsVersion(string value) => !string.IsNullOrEmpty(value) && value.Length <= 64 && value.All(static c => c is >= '0' and <= '9' or '.');
}

/// <summary>Builds an immutable application and plugin permission catalog.</summary>
public sealed class NeoPermissionCatalogBuilder
{
    private const int MaximumApplicationPermissions = 4096;
    private const int MaximumPlugins = 256;
    private readonly List<NeoPermissionDeclaration> _application = [];
    private readonly List<NeoPluginPermissionCatalog> _plugins = [];

    /// <summary>Adds one application permission declaration. This grants nothing.</summary>
    /// <param name="declaration">The static declaration.</param>
    /// <returns>This builder.</returns>
    public NeoPermissionCatalogBuilder Add(NeoPermissionDeclaration declaration) { ArgumentNullException.ThrowIfNull(declaration); if (_application.Count >= MaximumApplicationPermissions) throw new InvalidOperationException($"An application catalog may contain at most {MaximumApplicationPermissions} permissions."); _application.Add(declaration.Validate()); return this; }
    /// <summary>Adds one plugin catalog. This grants nothing.</summary>
    /// <param name="plugin">The static plugin catalog.</param>
    /// <returns>This builder.</returns>
    public NeoPermissionCatalogBuilder AddPlugin(NeoPluginPermissionCatalog plugin) { ArgumentNullException.ThrowIfNull(plugin); if (_plugins.Count >= MaximumPlugins) throw new InvalidOperationException($"An application catalog may contain at most {MaximumPlugins} plugins."); _plugins.Add(plugin); return this; }
    /// <summary>Builds a validated immutable catalog.</summary>
    /// <returns>The catalog.</returns>
    /// <exception cref="InvalidOperationException">IDs, commands, plugins, or permission sets conflict.</exception>
    public NeoPermissionCatalog Build() => new(_application, _plugins);
}

/// <summary>Contains all statically declared permissions and plugin metadata. It contains no grants.</summary>
public sealed class NeoPermissionCatalog
{
    private readonly IReadOnlyDictionary<string, NeoPermissionDeclaration> _permissions;
    private readonly IReadOnlyDictionary<string, string> _permissionSets;

    internal NeoPermissionCatalog(IEnumerable<NeoPermissionDeclaration> application, IEnumerable<NeoPluginPermissionCatalog> plugins)
    {
        var applicationArray = application.Take(4097).ToArray();
        var pluginArray = plugins.Take(257).OrderBy(static value => value.Id, StringComparer.Ordinal).ToArray();
        if (applicationArray.Length > 4096 || pluginArray.Length > 256 || applicationArray.Length + pluginArray.Sum(static value => value.Permissions.Count) > 4096 || pluginArray.Sum(static value => value.PermissionSets.Values.Sum(static set => set.Count)) > 65_536) throw new InvalidOperationException("The combined permission catalog exceeds its bounded declaration, plugin, or permission-set count.");
        var declarations = new SortedDictionary<string, NeoPermissionDeclaration>(StringComparer.Ordinal);
        var commands = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var declaration in applicationArray.Concat(pluginArray.SelectMany(static value => value.Permissions)))
        {
            declaration.Validate();
            if (!declarations.TryAdd(declaration.Id, declaration)) throw new InvalidOperationException($"Permission '{declaration.Id}' is declared more than once.");
            foreach (var command in declaration.Commands)
                if (!commands.TryAdd(command, declaration.Id) && !string.Equals(commands[command], declaration.Id, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Command '{command}' is covered by conflicting permissions.");
        }
        if (pluginArray.Select(static value => value.Id).Distinct(StringComparer.Ordinal).Count() != pluginArray.Length) throw new InvalidOperationException("A plugin ID is duplicated.");
        var sets = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var plugin in pluginArray)
        foreach (var set in plugin.PermissionSets)
        foreach (var permission in set.Value)
        {
            if (!declarations.ContainsKey(permission)) throw new InvalidOperationException($"Permission set '{plugin.Id}:{set.Key}' references unknown permission '{permission}'.");
            sets.Add($"{plugin.Id}:{set.Key}:{permission}", permission);
        }
        _permissions = new ReadOnlyDictionary<string, NeoPermissionDeclaration>(declarations);
        _permissionSets = new ReadOnlyDictionary<string, string>(sets);
        Plugins = Array.AsReadOnly(pluginArray);
    }

    /// <summary>Gets declarations keyed by exact permission ID.</summary>
    public IReadOnlyDictionary<string, NeoPermissionDeclaration> Permissions => _permissions;
    /// <summary>Gets registered plugin metadata.</summary>
    public IReadOnlyList<NeoPluginPermissionCatalog> Plugins { get; }
    internal bool TryGet(string id, out NeoPermissionDeclaration? declaration) => _permissions.TryGetValue(id, out declaration);
    internal IEnumerable<string> Expand(string value)
    {
        if (_permissions.ContainsKey(value)) { yield return value; yield break; }
        var prefix = value + ":";
        var matched = false;
        foreach (var pair in _permissionSets)
        {
            if (!pair.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            matched = true;
            yield return pair.Value;
        }
        if (!matched) throw new NeoCapabilityValidationException("unknown_permission", $"Unknown permission or permission set '{value}'.");
    }
}

/// <summary>Configures deterministic capability resolution.</summary>
public sealed class NeoCapabilityResolutionOptions
{
    /// <summary>Gets the trusted target platform.</summary>
    public NeoCapabilityPlatform Platform { get; init; } = NeoCapabilityPlatform.Windows;
    /// <summary>Gets whether release validation is active.</summary>
    public bool Release { get; init; } = true;
    /// <summary>Gets the named resolved security profile.</summary>
    public NeoSecurityProfile Profile { get; init; } = NeoSecurityProfile.ProductionLocalApp;
    /// <summary>Gets whether an explicit reviewed view pattern is accepted. Release defaults to exact labels only.</summary>
    public bool AllowReviewedViewPatterns { get; init; }
}

/// <summary>Reports deterministic capability validation failures.</summary>
public sealed class NeoCapabilityValidationException : Exception
{
    /// <summary>Initializes a validation exception.</summary>
    /// <param name="code">Stable validation code.</param>
    /// <param name="message">Safe configuration diagnostic.</param>
    public NeoCapabilityValidationException(string code, string message) : base(message) { Code = code; }
    /// <summary>Gets the stable validation code.</summary>
    public string Code { get; }
}

/// <summary>Contains stable capability authorization decision codes.</summary>
public static class NeoCapabilityDecisionCodes
{
    /// <summary>Authorization succeeded.</summary>
    public const string Allowed = "allowed";
    /// <summary>No view selector matched.</summary>
    public const string NoMatchingCapability = "no_matching_capability";
    /// <summary>The matching capability omitted the permission.</summary>
    public const string PermissionMissing = "permission_missing";
    /// <summary>The backend supplied no authenticated origin.</summary>
    public const string OriginUnavailable = "origin_unavailable";
    /// <summary>The authenticated origin did not match.</summary>
    public const string OriginMismatch = "origin_mismatch";
    /// <summary>The target platform did not match.</summary>
    public const string PlatformMismatch = "platform_mismatch";
    /// <summary>The scope configuration was invalid.</summary>
    public const string ScopeInvalid = "scope_invalid";
    /// <summary>The command arguments were outside scope.</summary>
    public const string ScopeDenied = "scope_denied";
    /// <summary>The document session was stale.</summary>
    public const string SessionStale = "session_stale";
    /// <summary>A bounded runtime limit was exceeded.</summary>
    public const string LimitExceeded = "limit_exceeded";
}

/// <summary>An immutable resolved capability manifest suitable for embedding and runtime matching.</summary>
public sealed class NeoCapabilityManifest
{
    private readonly IReadOnlyList<ResolvedCapability> _capabilities;

    private NeoCapabilityManifest(NeoCapabilityPlatform platform, NeoSecurityProfile profile, IReadOnlyList<ResolvedCapability> capabilities, string json, string hash)
    {
        Platform = platform; Profile = profile; _capabilities = capabilities; Json = json; Hash = hash;
    }

    /// <summary>Gets the capability schema version.</summary>
    public int Version => 1;
    /// <summary>Gets the resolved target platform.</summary>
    public NeoCapabilityPlatform Platform { get; }
    /// <summary>Gets the resolved named security profile.</summary>
    public NeoSecurityProfile Profile { get; }
    /// <summary>Gets canonical deterministic JSON without machine paths or timestamps.</summary>
    public string Json { get; }
    /// <summary>Gets the SHA-256 hash of the canonical manifest JSON.</summary>
    public string Hash { get; }
    /// <summary>Gets redacted grant summaries.</summary>
    public IReadOnlyList<string> GrantSummaries => Array.AsReadOnly(_capabilities.Select(value => value.Summary(Platform)).ToArray());

    /// <summary>Validates and resolves a versioned capability document.</summary>
    /// <param name="utf8Json">UTF-8 capability JSON.</param>
    /// <param name="catalog">Static application/plugin permission catalog.</param>
    /// <param name="options">Release and target policy.</param>
    /// <returns>An immutable resolved manifest.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="NeoCapabilityValidationException">The document is unknown, malformed, overlapping, or unsafe.</exception>
    public static NeoCapabilityManifest Resolve(ReadOnlySpan<byte> utf8Json, NeoPermissionCatalog catalog, NeoCapabilityResolutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Profile);
        if (!Enum.IsDefined(options.Platform)) throw new ArgumentOutOfRangeException(nameof(options), "The target platform is unsupported.");
        options.Profile.Validate(options.Release, null, false);
        if (options.Release && options.AllowReviewedViewPatterns) throw new NeoCapabilityValidationException("view_pattern", "Reviewed view patterns cannot be enabled for release resolution.");
        if (utf8Json.Length is 0 or > 1024 * 1024) throw new NeoCapabilityValidationException("document_size", "A capability document must be between 1 byte and 1 MiB.");
        CapabilityDocumentDto document;
        try
        {
            using var parsed = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
            ValidateUniqueProperties(parsed.RootElement);
            document = JsonSerializer.Deserialize(utf8Json, CapabilityJsonContext.Default.CapabilityDocumentDto) ?? throw new JsonException();
        }
        catch (JsonException exception) { throw new NeoCapabilityValidationException("invalid_schema", $"The capability document does not satisfy schema v1: {SafeJsonMessage(exception)}"); }
        if (document.Version != 1) throw new NeoCapabilityValidationException("unknown_version", "Only capability schema version 1 is supported.");
        if (!string.Equals(document.Schema, "neoastra-capabilities-v1.schema.json", StringComparison.Ordinal)) throw new NeoCapabilityValidationException("unknown_schema", "The capability schema identifier is missing or unknown.");
        if (document.Capabilities is null || document.Capabilities.Length is < 1 or > 1024) throw new NeoCapabilityValidationException("capability_count", "A capability document requires 1 to 1024 records.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new List<ResolvedCapability>();
        foreach (var item in document.Capabilities)
        {
            ValidateIdentifier(item.Id, "capability ID");
            if (!ids.Add(item.Id!)) throw new NeoCapabilityValidationException("duplicate_id", $"Capability ID '{item.Id}' is duplicated.");
            if (item.DevelopmentOnly && (options.Release || !options.Profile.Development)) throw new NeoCapabilityValidationException("development_grant", $"Development-only capability '{item.Id}' requires a non-release development profile.");
            if (item.Views is null || item.Views.Length is < 1 or > 128) throw new NeoCapabilityValidationException("view_selector", $"Capability '{item.Id}' requires bounded view selectors.");
            var selectors = item.Views.Select(value => ViewSelector.Parse(value, options.AllowReviewedViewPatterns)).OrderBy(static value => value.Text, StringComparer.Ordinal).ToArray();
            if (selectors.Select(static value => value.Text).Distinct(StringComparer.Ordinal).Count() != selectors.Length) throw new NeoCapabilityValidationException("view_selector", $"Capability '{item.Id}' contains duplicate view selectors.");
            if (item.Platforms is { Length: > 3 }) throw new NeoCapabilityValidationException("platform_selector", $"Capability '{item.Id}' has too many platform selectors.");
            var platforms = (item.Platforms ?? Array.Empty<string>()).Select(ParsePlatform).ToArray();
            if (platforms.Distinct().Count() != platforms.Length) throw new NeoCapabilityValidationException("platform_selector", $"Capability '{item.Id}' contains duplicate platform selectors.");
            var platformMatch = platforms.Length == 0 || platforms.Contains(options.Platform);
            var origins = (item.Origins ?? Array.Empty<string>()).Select(CanonicalOrigin.Parse).OrderBy(static value => value.Value, StringComparer.Ordinal).ToArray();
            if (origins.Length > 128) throw new NeoCapabilityValidationException("origin_count", $"Capability '{item.Id}' has too many origins.");
            if (origins.Distinct().Count() != origins.Length) throw new NeoCapabilityValidationException("invalid_origin", $"Capability '{item.Id}' contains duplicate canonical origins.");
            if (platformMatch && options.Platform == NeoCapabilityPlatform.Linux && origins.Length != 0)
                throw new NeoCapabilityValidationException("origin_unavailable", $"Capability '{item.Id}' requires authenticated origins that WebKitGTK cannot prove.");
            if (!platformMatch) continue;
            if (item.Permissions is null || item.Permissions.Length is < 1 or > 512) throw new NeoCapabilityValidationException("permission_count", $"Capability '{item.Id}' requires bounded permission grants.");
            var grants = new List<ResolvedGrant>();
            foreach (var grantDto in item.Permissions)
            {
                var value = grantDto.StringValue ?? grantDto.Id ?? throw new NeoCapabilityValidationException("permission_missing", $"Capability '{item.Id}' contains a permission without an ID.");
                foreach (var permissionId in catalog.Expand(value))
                {
                    if (!catalog.TryGet(permissionId, out var declaration) || declaration is null) throw new NeoCapabilityValidationException("unknown_permission", $"Unknown permission '{permissionId}'.");
                    if (grantDto.Version is { } grantVersion && grantVersion != declaration.Version) throw new NeoCapabilityValidationException("permission_version", $"Permission '{permissionId}' uses unsupported version {grantVersion}.");
                    if (declaration.Platforms.Count != 0 && !declaration.Platforms.Contains(options.Platform) && platformMatch)
                        throw new NeoCapabilityValidationException("permission_platform", $"Permission '{permissionId}' is unavailable on {options.Platform}.");
                    NeoCapabilityScope? scope = null;
                    if (declaration.ScopeRequired)
                    {
                        if (grantDto.Scope.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) throw new NeoCapabilityValidationException("scope_required", $"Permission '{permissionId}' requires a scope.");
                        scope = NeoScopeParser.Parse(declaration.ScopeFamily, grantDto.Scope, options.Platform);
                    }
                    else if (grantDto.Scope.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                    {
                        throw new NeoCapabilityValidationException("scope_unexpected", $"Permission '{permissionId}' does not accept scope data.");
                    }
                    grants.Add(new(permissionId, declaration, scope));
                }
            }
            var duplicateGroups = grants.GroupBy(static value => value.Permission, StringComparer.Ordinal);
            foreach (var duplicate in duplicateGroups.Where(static value => value.Count() > 1))
                if (!duplicate.First().Declaration.UnionSafe) throw new NeoCapabilityValidationException("capability_overlap", $"Permission '{duplicate.Key}' has overlapping grants but is not union-safe.");
            resolved.Add(new(item.Id!, selectors, platforms, origins, grants.OrderBy(static value => value.Permission, StringComparer.Ordinal).ThenBy(static value => value.Scope?.Summary, StringComparer.Ordinal).ToArray(), item.DevelopmentOnly, platformMatch));
        }
        ValidateOverlaps(resolved);
        var ordered = resolved.OrderBy(static value => value.Id, StringComparer.Ordinal).ToArray();
        if (!options.Profile.BridgeEnabled && ordered.Length != 0) throw new NeoCapabilityValidationException("profile_conflict", "The remote-content profile cannot resolve renderer capability grants.");
        var json = WriteCanonical(options.Platform, options.Profile, ordered, catalog);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        return new(options.Platform, options.Profile, Array.AsReadOnly(ordered), json, hash);
    }

    internal NeoCapabilityMatch Match(NeoRpcAuthorizationRequest request)
    {
        if (request.Context.Platform != Platform) return NeoCapabilityMatch.Deny(NeoCapabilityDecisionCodes.PlatformMismatch);
        if (!request.Context.IsMainFrame) return NeoCapabilityMatch.Deny(NeoCapabilityDecisionCodes.OriginUnavailable);
        var view = _capabilities.Where(item => item.Selectors.Any(selector => selector.IsMatch(request.Context.ViewLabel))).ToArray();
        if (view.Length == 0) return NeoCapabilityMatch.Deny(NeoCapabilityDecisionCodes.NoMatchingCapability);
        var platform = view.Where(static item => item.PlatformMatch).ToArray();
        if (platform.Length == 0) return NeoCapabilityMatch.Deny(NeoCapabilityDecisionCodes.PlatformMismatch);
        if (Platform == NeoCapabilityPlatform.Linux && !request.Context.WholeViewTrust) return NeoCapabilityMatch.Deny(NeoCapabilityDecisionCodes.OriginUnavailable);
        var originUnavailable = false; var originMismatch = false;
        var origin = platform.Where(item =>
        {
            if (item.Origins.Count == 0) return true;
            if (Platform == NeoCapabilityPlatform.Linux || request.Context.SourceOrigin is null) { originUnavailable = true; return false; }
            CanonicalOrigin supplied;
            try { supplied = CanonicalOrigin.Parse(request.Context.SourceOrigin.GetLeftPart(UriPartial.Authority)); }
            catch { originMismatch = true; return false; }
            var matches = item.Origins.Contains(supplied);
            if (!matches) originMismatch = true;
            return matches;
        }).ToArray();
        if (origin.Length == 0) return NeoCapabilityMatch.Deny(originUnavailable ? NeoCapabilityDecisionCodes.OriginUnavailable : originMismatch ? NeoCapabilityDecisionCodes.OriginMismatch : NeoCapabilityDecisionCodes.NoMatchingCapability);
        if (request.Permission is null) return NeoCapabilityMatch.Deny(NeoCapabilityDecisionCodes.PermissionMissing);
        var grants = origin.SelectMany(static item => item.Grants).Where(item => string.Equals(item.Permission, request.Permission, StringComparison.Ordinal) && item.Declaration.Commands.Contains(request.Operation, StringComparer.Ordinal)).ToArray();
        if (grants.Length == 0) return NeoCapabilityMatch.Deny(NeoCapabilityDecisionCodes.PermissionMissing);
        if (grants.Any(static value => value.Scope is null)) return NeoCapabilityMatch.Allow(request.Permission, Array.Empty<NeoCapabilityScope>());
        foreach (var grant in grants)
        {
            try { if (grant.Scope!.Allows(request.Arguments, out _)) return NeoCapabilityMatch.Allow(request.Permission, grants.Select(static value => value.Scope!).ToArray()); }
            catch { return NeoCapabilityMatch.Deny(NeoCapabilityDecisionCodes.ScopeInvalid); }
        }
        return NeoCapabilityMatch.Deny(NeoCapabilityDecisionCodes.ScopeDenied);
    }

    internal void ValidateRegistration(string operation, string permission, int? maximumConcurrency, TimeSpan? timeout, TimeSpan hostTimeout)
    {
        var declaration = _capabilities.Where(static capability => capability.PlatformMatch).SelectMany(static capability => capability.Grants).Where(grant => string.Equals(grant.Permission, permission, StringComparison.Ordinal)).Select(static grant => grant.Declaration).FirstOrDefault();
        if (declaration is null) return; // Ungranted operations remain registered but the manifest authorizer always denies renderer dispatch.
        if (!declaration.Commands.Contains(operation, StringComparer.Ordinal)) throw new InvalidOperationException($"Granted permission '{permission}' does not declare registered operation '{operation}'.");
        if (maximumConcurrency is { } concurrency && concurrency > declaration.MaximumConcurrency) throw new InvalidOperationException($"Registered operation '{operation}' exceeds permission '{permission}' concurrency policy.");
        if (maximumConcurrency is not null && (timeout ?? hostTimeout) > declaration.DefaultTimeout) throw new InvalidOperationException($"Registered operation '{operation}' exceeds permission '{permission}' timeout policy.");
    }

    private static void ValidateOverlaps(IReadOnlyList<ResolvedCapability> capabilities)
    {
        for (var i = 0; i < capabilities.Count; i++)
        for (var j = i + 1; j < capabilities.Count; j++)
        {
            var left = capabilities[i]; var right = capabilities[j];
            if (!left.PlatformMatch || !right.PlatformMatch || !left.Selectors.Any(a => right.Selectors.Any(b => a.Overlaps(b)))) continue;
            foreach (var permission in left.Grants.Select(static value => value.Permission).Intersect(right.Grants.Select(static value => value.Permission), StringComparer.Ordinal))
            {
                var declaration = left.Grants.First(value => value.Permission == permission).Declaration;
                if (!declaration.UnionSafe) throw new NeoCapabilityValidationException("capability_overlap", $"Capabilities '{left.Id}' and '{right.Id}' broaden non-union-safe permission '{permission}'.");
            }
        }
    }

    private static string WriteCanonical(NeoCapabilityPlatform platform, NeoSecurityProfile profile, IReadOnlyList<ResolvedCapability> capabilities, NeoPermissionCatalog catalog)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject(); writer.WriteNumber("version", 1); writer.WriteString("platform", PlatformText(platform)); writer.WriteString("profile", profile.Name);
            writer.WritePropertyName("plugins"); writer.WriteStartArray();
            foreach (var plugin in catalog.Plugins)
            {
                writer.WriteStartObject(); writer.WriteString("id", plugin.Id); writer.WriteString("version", plugin.Version); writer.WriteString("minimumNeoAstraVersion", plugin.MinimumNeoAstraVersion);
                writer.WritePropertyName("permissions"); writer.WriteStartArray(); foreach (var declaration in plugin.Permissions.OrderBy(static value => value.Id, StringComparer.Ordinal)) WriteDeclaration(writer, declaration); writer.WriteEndArray();
                writer.WritePropertyName("permissionSets"); writer.WriteStartObject(); foreach (var set in plugin.PermissionSets) { writer.WritePropertyName(set.Key); writer.WriteStartArray(); foreach (var permission in set.Value) writer.WriteStringValue(permission); writer.WriteEndArray(); } writer.WriteEndObject(); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WritePropertyName("capabilities"); writer.WriteStartArray();
            foreach (var capability in capabilities)
            {
                writer.WriteStartObject(); writer.WriteString("id", capability.Id); writer.WriteBoolean("developmentOnly", capability.DevelopmentOnly);
                writer.WritePropertyName("views"); writer.WriteStartArray(); foreach (var selector in capability.Selectors) writer.WriteStringValue(selector.Text); writer.WriteEndArray();
                writer.WritePropertyName("platforms"); writer.WriteStartArray(); foreach (var value in capability.Platforms.Order()) writer.WriteStringValue(PlatformText(value)); writer.WriteEndArray();
                writer.WritePropertyName("origins"); writer.WriteStartArray(); foreach (var value in capability.Origins) writer.WriteStringValue(value.Value); writer.WriteEndArray();
                writer.WritePropertyName("permissions"); writer.WriteStartArray(); foreach (var grant in capability.Grants) { writer.WriteStartObject(); WriteDeclarationProperties(writer, grant.Declaration); writer.WritePropertyName("scope"); if (grant.Scope is null) writer.WriteNullValue(); else grant.Scope.WriteCanonical(writer); writer.WriteEndObject(); } writer.WriteEndArray(); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteDeclaration(Utf8JsonWriter writer, NeoPermissionDeclaration declaration)
    {
        writer.WriteStartObject(); WriteDeclarationProperties(writer, declaration); writer.WriteEndObject();
    }

    private static void WriteDeclarationProperties(Utf8JsonWriter writer, NeoPermissionDeclaration declaration)
    {
        writer.WriteString("id", declaration.Id); writer.WriteNumber("version", declaration.Version); writer.WriteString("risk", declaration.Risk.ToString().ToLowerInvariant()); writer.WriteString("scopeFamily", declaration.ScopeFamily.ToString().ToLowerInvariant()); writer.WriteBoolean("scopeRequired", declaration.ScopeRequired); writer.WriteBoolean("unionSafe", declaration.UnionSafe);
        writer.WritePropertyName("commands"); writer.WriteStartArray(); foreach (var command in declaration.Commands) writer.WriteStringValue(command); writer.WriteEndArray(); writer.WritePropertyName("platforms"); writer.WriteStartArray(); foreach (var platform in declaration.Platforms.Order()) writer.WriteStringValue(PlatformText(platform)); writer.WriteEndArray(); writer.WriteNumber("timeoutMilliseconds", (long)declaration.DefaultTimeout.TotalMilliseconds); writer.WriteNumber("maximumConcurrency", declaration.MaximumConcurrency); writer.WriteString("redaction", declaration.Redaction.ToString().ToLowerInvariant()); writer.WriteString("documentation", declaration.Documentation);
    }

    private static void ValidateIdentifier(string? value, string name) { if (!NeoRpcValidation.IsWireName(value, 128)) throw new NeoCapabilityValidationException("invalid_id", $"The {name} is malformed."); }
    private static NeoCapabilityPlatform ParsePlatform(string? value) => value switch { "windows" => NeoCapabilityPlatform.Windows, "macos" => NeoCapabilityPlatform.MacOS, "linux" => NeoCapabilityPlatform.Linux, _ => throw new NeoCapabilityValidationException("unknown_platform", $"Unknown platform '{value}'.") };
    internal static string PlatformText(NeoCapabilityPlatform value) => value switch { NeoCapabilityPlatform.Windows => "windows", NeoCapabilityPlatform.MacOS => "macos", NeoCapabilityPlatform.Linux => "linux", _ => throw new ArgumentOutOfRangeException(nameof(value)) };
    private static string SafeJsonMessage(JsonException value) => $"JSON byte {value.BytePositionInLine?.ToString() ?? "unknown"}.";
    private static void ValidateUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new NeoCapabilityValidationException("duplicate_property", "Capability JSON object properties must be unique.");
                ValidateUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) ValidateUniqueProperties(item);
        }
    }

    private sealed record ResolvedCapability(string Id, IReadOnlyList<ViewSelector> Selectors, IReadOnlyList<NeoCapabilityPlatform> Platforms, IReadOnlyList<CanonicalOrigin> Origins, IReadOnlyList<ResolvedGrant> Grants, bool DevelopmentOnly, bool PlatformMatch)
    {
        internal string Summary(NeoCapabilityPlatform platform) => $"{Id}: views={Selectors.Count}, permissions={Grants.Count}, wholeViewTrust={(PlatformMatch && platform == NeoCapabilityPlatform.Linux ? "required" : "false")}, originAuthenticated={(Origins.Count != 0 ? "required" : "false")}";
    }
    private sealed record ResolvedGrant(string Permission, NeoPermissionDeclaration Declaration, NeoCapabilityScope? Scope);

    private readonly record struct ViewSelector(string Text, string Prefix, bool Pattern)
    {
        internal static ViewSelector Parse(string? value, bool patterns)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl)) throw new NeoCapabilityValidationException("view_selector", "A view selector is malformed.");
            var star = value.IndexOf('*');
            if (star < 0)
            {
                if (!NeoRpcValidation.IsWireName(value, 128)) throw new NeoCapabilityValidationException("view_selector", "A view selector must be a bounded ASCII identifier.");
                return new(value, value, false);
            }
            if (!patterns || star != value.Length - 1 || value.LastIndexOf('*') != star || star < 3 || !NeoRpcValidation.IsWireName(value[..^1], 127)) throw new NeoCapabilityValidationException("view_pattern", $"View pattern '{value}' is not an explicitly allowed trailing-prefix pattern.");
            return new(value, value[..^1], true);
        }
        internal bool IsMatch(string value) => Pattern ? value.StartsWith(Prefix, StringComparison.Ordinal) : string.Equals(Prefix, value, StringComparison.Ordinal);
        internal bool Overlaps(ViewSelector other) => IsMatch(other.Prefix) || other.IsMatch(Prefix);
    }

    private readonly record struct CanonicalOrigin(string Value)
    {
        internal static CanonicalOrigin Parse(string? value)
        {
            if (value is null || value.Length > 2048 || value.Any(static c => c > 0x7f || char.IsControl(c)) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new NeoCapabilityValidationException("invalid_origin", "Origins must be absolute ASCII scheme/host/effective-port values without credentials or paths.");
            var scheme = uri.Scheme.ToLowerInvariant(); var host = uri.IdnHost.ToLowerInvariant();
            if (host.Contains(':', StringComparison.Ordinal) && host[0] != '[') host = $"[{host}]";
            var port = uri.IsDefaultPort ? scheme switch { "http" => 80, "https" => 443, _ => -1 } : uri.Port;
            var canonical = port < 0 ? $"{scheme}://{host}" : $"{scheme}://{host}:{port}";
            return new(canonical);
        }
    }
}

internal readonly record struct NeoCapabilityMatch(bool Allowed, string Code, string? Permission, IReadOnlyList<NeoCapabilityScope>? Scopes)
{
    internal static NeoCapabilityMatch Allow(string permission, IReadOnlyList<NeoCapabilityScope> scopes) => new(true, NeoCapabilityDecisionCodes.Allowed, permission, scopes);
    internal static NeoCapabilityMatch Deny(string code) => new(false, code, null, null);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class CapabilityDocumentDto
{
    [JsonPropertyName("$schema")] public string? Schema { get; set; }
    public int Version { get; set; }
    public CapabilityDto[]? Capabilities { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class CapabilityDto
{
    public string? Id { get; set; }
    public string[]? Views { get; set; }
    public string[]? Platforms { get; set; }
    public string[]? Origins { get; set; }
    public CapabilityPermissionDto[]? Permissions { get; set; }
    public bool DevelopmentOnly { get; set; }
}

[JsonConverter(typeof(CapabilityPermissionConverter))]
internal sealed class CapabilityPermissionDto
{
    public string? StringValue { get; set; }
    public string? Id { get; set; }
    public int? Version { get; set; }
    public JsonElement Scope { get; set; }
}

internal sealed class CapabilityPermissionConverter : JsonConverter<CapabilityPermissionDto>
{
    public override CapabilityPermissionDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return new() { StringValue = reader.GetString() };
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
        using var document = JsonDocument.ParseValue(ref reader); var root = document.RootElement;
        foreach (var property in root.EnumerateObject()) if (property.Name is not ("id" or "version" or "scope")) throw new JsonException($"Unknown permission field '{property.Name}'.");
        if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) throw new JsonException("A permission object requires a string ID.");
        int? parsedVersion = null;
        if (root.TryGetProperty("version", out var version))
        {
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number)) throw new JsonException("A permission version must be an integer.");
            parsedVersion = number;
        }
        return new()
        {
            Id = id.GetString(),
            Version = parsedVersion,
            Scope = root.TryGetProperty("scope", out var scope) ? scope.Clone() : default,
        };
    }
    public override void Write(Utf8JsonWriter writer, CapabilityPermissionDto value, JsonSerializerOptions options) => throw new NotSupportedException();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false, AllowTrailingCommas = false, ReadCommentHandling = JsonCommentHandling.Disallow)]
[JsonSerializable(typeof(CapabilityDocumentDto))]
internal sealed partial class CapabilityJsonContext : JsonSerializerContext;
