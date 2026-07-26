// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Globalization;
using System.Text.Json;

namespace NeoAstra.Rpc;

/// <summary>Base class for immutable validated renderer argument scopes.</summary>
public abstract class NeoCapabilityScope
{
    /// <summary>Gets the built-in scope family.</summary>
    public abstract NeoScopeFamily Family { get; }
    /// <summary>Gets a redacted deterministic summary that contains no exact paths, URLs, or payload data.</summary>
    public abstract string Summary { get; }
    internal abstract bool Allows(JsonElement arguments, out string safeReason);
    internal abstract void WriteCanonical(Utf8JsonWriter writer);
}

/// <summary>Represents a validated filesystem root capability.</summary>
public sealed class NeoFileSystemScope : NeoCapabilityScope
{
    private readonly IReadOnlyDictionary<string, string> _roots;
    private readonly IReadOnlySet<string> _operations;
    private readonly bool _allowLinks;
    private readonly StringComparison _pathComparison;
    private readonly NeoCapabilityPlatform _platform;

    internal NeoFileSystemScope(IReadOnlyDictionary<string, string> roots, IReadOnlySet<string> operations, bool allowLinks, NeoCapabilityPlatform platform) { _roots = roots; _operations = operations; _allowLinks = allowLinks; _platform = platform; _pathComparison = platform == NeoCapabilityPlatform.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal; }
    /// <inheritdoc />
    public override NeoScopeFamily Family => NeoScopeFamily.Filesystem;
    /// <inheritdoc />
    public override string Summary => $"filesystem:roots={_roots.Count},operations={_operations.Count},links={(_allowLinks ? "reviewed" : "denied")}";

    /// <summary>Validates a root token, canonical relative path, and operation.</summary>
    /// <param name="rootToken">Predeclared root token.</param><param name="relativePath">Relative path that never uses ambient current directory.</param><param name="operation">read, write, create, or delete.</param>
    /// <param name="canonicalPath">Receives a canonical absolute path only after validation.</param><returns>Whether access is in scope.</returns>
    public bool TryResolve(string rootToken, string relativePath, string operation, out string? canonicalPath)
    {
        canonicalPath = null;
        if (!_roots.TryGetValue(rootToken, out var root) || !_operations.Contains(operation) || !ScopeValidation.IsSafeRelativePath(relativePath) || ScopeValidation.HasDeviceOrAlternateStream(relativePath, _platform)) return false;
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, _pathComparison) && !string.Equals(path, root, _pathComparison)) return false;
        if (!_allowLinks && ScopeValidation.HasLinkOrReparsePoint(root, path)) return false;
        canonicalPath = path;
        return true;
    }

    internal override bool Allows(JsonElement arguments, out string safeReason)
    {
        safeReason = "path_out_of_scope";
        return !arguments.TryGetProperty("path", out _) && ScopeValidation.TryString(arguments, "root", out var root) && ScopeValidation.TryString(arguments, "relativePath", out var path) && ScopeValidation.TryString(arguments, "operation", out var operation) && TryResolve(root, path, operation, out _);
    }
    internal override void WriteCanonical(Utf8JsonWriter writer) { writer.WriteStartObject(); writer.WritePropertyName("roots"); writer.WriteStartArray(); foreach (var pair in _roots.OrderBy(static value => value.Key, StringComparer.Ordinal)) { writer.WriteStartObject(); writer.WriteString("token", pair.Key); writer.WriteString("path", pair.Value); writer.WriteEndObject(); } writer.WriteEndArray(); ScopeCanonical.WriteStrings(writer, "operations", _operations); writer.WriteBoolean("allowSymlinks", _allowLinks); writer.WriteEndObject(); }
}

/// <summary>Represents validated exact URL opener destinations.</summary>
public sealed class NeoUrlScope : NeoCapabilityScope
{
    private readonly IReadOnlySet<string> _schemes; private readonly IReadOnlySet<string> _hosts; private readonly IReadOnlySet<int> _ports; private readonly IReadOnlyList<string> _prefixes;
    internal NeoUrlScope(IReadOnlySet<string> schemes, IReadOnlySet<string> hosts, IReadOnlySet<int> ports, IReadOnlyList<string> prefixes) { _schemes = schemes; _hosts = hosts; _ports = ports; _prefixes = prefixes; }
    /// <inheritdoc />
    public override NeoScopeFamily Family => NeoScopeFamily.Url;
    /// <inheritdoc />
    public override string Summary => $"url:schemes={_schemes.Count},hosts={_hosts.Count},ports={_ports.Count},paths={_prefixes.Count}";
    /// <summary>Checks one absolute credential-free URL after host and effective-port normalization.</summary><param name="url">The URL.</param><returns>Whether the URL is in scope.</returns>
    public bool Allows(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || url.UserInfo.Length != 0 || !string.IsNullOrEmpty(url.Fragment) || url.HostNameType == UriHostNameType.Unknown || url.OriginalString.Any(static c => c > 0x7f || char.IsControl(c)) || ScopeValidation.HasAmbiguousUrlEncoding(url.OriginalString)) return false;
        var scheme = url.Scheme.ToLowerInvariant(); var host = url.IdnHost.ToLowerInvariant(); var port = ScopeValidation.EffectivePort(url);
        return _schemes.Contains(scheme) && _hosts.Contains(host) && (_ports.Count == 0 || _ports.Contains(port)) && (_prefixes.Count == 0 || _prefixes.Any(prefix => ScopeValidation.PathPrefix(url.AbsolutePath, prefix)));
    }
    internal override bool Allows(JsonElement arguments, out string safeReason) { safeReason = "url_out_of_scope"; return ScopeValidation.TryString(arguments, "url", out var value) && Uri.TryCreate(value, UriKind.Absolute, out var uri) && Allows(uri); }
    internal override void WriteCanonical(Utf8JsonWriter writer) { writer.WriteStartObject(); WriteCanonicalProperties(writer); writer.WriteEndObject(); }
    internal void WriteCanonicalProperties(Utf8JsonWriter writer) { ScopeCanonical.WriteStrings(writer, "schemes", _schemes); ScopeCanonical.WriteStrings(writer, "hosts", _hosts); writer.WritePropertyName("ports"); writer.WriteStartArray(); foreach (var port in _ports.Order()) writer.WriteNumberValue(port); writer.WriteEndArray(); writer.WritePropertyName("pathPrefixes"); writer.WriteStartArray(); foreach (var prefix in _prefixes) writer.WriteStringValue(prefix); writer.WriteEndArray(); }
}

/// <summary>Represents validated predeclared executable and fixed-argument policy.</summary>
public sealed class NeoProcessScope : NeoCapabilityScope
{
    private readonly IReadOnlyDictionary<string, ProcessEntry> _entries;
    internal NeoProcessScope(IReadOnlyDictionary<string, ProcessEntry> entries) => _entries = entries;
    /// <inheritdoc />
    public override NeoScopeFamily Family => NeoScopeFamily.Process;
    /// <inheritdoc />
    public override string Summary => $"process:executables={_entries.Count},shell=denied";
    /// <summary>Checks a predeclared executable identity and exact fixed argument vector.</summary><param name="executableId">Opaque executable ID, never a renderer path.</param><param name="arguments">Argument vector, never a shell string.</param><returns>Whether invocation is in scope.</returns>
    public bool Allows(string executableId, IReadOnlyList<string> arguments) => TryResolve(executableId, arguments, out _);
    /// <summary>Resolves a predeclared executable ID and exact argument vector without PATH or shell lookup.</summary>
    /// <param name="executableId">Opaque executable ID.</param><param name="arguments">Exact argument vector.</param><param name="invocation">Receives immutable trusted process metadata.</param><returns>Whether the request is in scope.</returns>
    public bool TryResolve(string executableId, IReadOnlyList<string> arguments, out NeoProcessInvocation? invocation)
    {
        ArgumentNullException.ThrowIfNull(executableId); ArgumentNullException.ThrowIfNull(arguments); invocation = null;
        if (!_entries.TryGetValue(executableId, out var entry) || !entry.Arguments.SequenceEqual(arguments, StringComparer.Ordinal)) return false;
        invocation = new(entry.Path, entry.Arguments, entry.WorkingDirectory, entry.Environment); return true;
    }
    internal override bool Allows(JsonElement arguments, out string safeReason)
    {
        safeReason = "process_out_of_scope";
        if (!ScopeValidation.TryString(arguments, "executable", out var id) || !arguments.TryGetProperty("arguments", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 64) return false;
        var values = new List<string>(); foreach (var item in array.EnumerateArray()) { if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } value || value.Length > 4096 || value.Any(char.IsControl)) return false; values.Add(value); }
        if (!TryResolve(id, values, out var invocation)) return false;
        if (arguments.TryGetProperty("path", out _) || arguments.TryGetProperty("shell", out _) || arguments.TryGetProperty("command", out _)) return false;
        if (arguments.TryGetProperty("workingDirectory", out var workingDirectory) && (workingDirectory.ValueKind != JsonValueKind.String || !string.Equals(workingDirectory.GetString(), invocation!.WorkingDirectory, StringComparison.Ordinal))) return false;
        if (arguments.TryGetProperty("environment", out var environment) && (environment.ValueKind != JsonValueKind.Object || environment.EnumerateObject().Count() > 64 || environment.EnumerateObject().Any(pair => !invocation!.EnvironmentNames.Contains(pair.Name, StringComparer.Ordinal) || pair.Value.ValueKind != JsonValueKind.String || pair.Value.GetString()!.Length > 8192 || pair.Value.GetString()!.Any(char.IsControl)))) return false;
        return true;
    }
    internal override void WriteCanonical(Utf8JsonWriter writer) { writer.WriteStartObject(); writer.WritePropertyName("executables"); writer.WriteStartArray(); foreach (var pair in _entries) { writer.WriteStartObject(); writer.WriteString("id", pair.Key); writer.WriteString("path", pair.Value.Path); writer.WritePropertyName("arguments"); writer.WriteStartArray(); foreach (var argument in pair.Value.Arguments) writer.WriteStringValue(argument); writer.WriteEndArray(); if (pair.Value.WorkingDirectory is null) writer.WriteNull("workingDirectory"); else writer.WriteString("workingDirectory", pair.Value.WorkingDirectory); ScopeCanonical.WriteStrings(writer, "environment", pair.Value.Environment); writer.WriteEndObject(); } writer.WriteEndArray(); writer.WriteEndObject(); }
    internal sealed record ProcessEntry(string Path, IReadOnlyList<string> Arguments, string? WorkingDirectory, IReadOnlySet<string> Environment);
}

/// <summary>Contains immutable trusted process launch metadata resolved from a predeclared identity.</summary>
public sealed class NeoProcessInvocation
{
    internal NeoProcessInvocation(string path, IReadOnlyList<string> arguments, string? workingDirectory, IReadOnlySet<string> environmentNames) { Path = path; Arguments = Array.AsReadOnly(arguments.ToArray()); WorkingDirectory = workingDirectory; EnvironmentNames = Array.AsReadOnly(environmentNames.Order(StringComparer.Ordinal).ToArray()); }
    /// <summary>Gets the canonical absolute executable path.</summary>
    public string Path { get; }
    /// <summary>Gets the exact fixed argument vector.</summary>
    public IReadOnlyList<string> Arguments { get; }
    /// <summary>Gets the canonical fixed working directory, or <see langword="null"/>.</summary>
    public string? WorkingDirectory { get; }
    /// <summary>Gets names of environment values the application may explicitly supply; ambient inheritance is not implied.</summary>
    public IReadOnlyList<string> EnvironmentNames { get; }
}

/// <summary>Represents explicit clipboard formats and direction.</summary>
public sealed class NeoClipboardScope : NeoCapabilityScope
{
    private readonly IReadOnlySet<string> _formats; private readonly IReadOnlySet<string> _operations;
    internal NeoClipboardScope(IReadOnlySet<string> formats, IReadOnlySet<string> operations) { _formats = formats; _operations = operations; }
    /// <inheritdoc />
    public override NeoScopeFamily Family => NeoScopeFamily.Clipboard;
    /// <inheritdoc />
    public override string Summary => $"clipboard:formats={_formats.Count},operations={_operations.Count}";
    /// <summary>Checks a format and read/write direction.</summary>
    public bool Allows(string format, string operation) => _formats.Contains(format) && _operations.Contains(operation);
    internal override bool Allows(JsonElement arguments, out string safeReason) { safeReason = "clipboard_out_of_scope"; return ScopeValidation.TryString(arguments, "format", out var format) && ScopeValidation.TryString(arguments, "operation", out var operation) && Allows(format, operation); }
    internal override void WriteCanonical(Utf8JsonWriter writer) { writer.WriteStartObject(); ScopeCanonical.WriteStrings(writer, "formats", _formats); ScopeCanonical.WriteStrings(writer, "operations", _operations); writer.WriteEndObject(); }
}

/// <summary>Represents bounded notification identity and action policy.</summary>
public sealed class NeoNotificationScope : NeoCapabilityScope
{
    private readonly string _appIdentity; private readonly IReadOnlySet<string> _categories; private readonly int _maximumPayloadBytes; private readonly bool _persistent; private readonly IReadOnlySet<string> _urgencies;
    internal NeoNotificationScope(string appIdentity, IReadOnlySet<string> categories, int maximumPayloadBytes, bool persistent, IReadOnlySet<string> urgencies) { _appIdentity = appIdentity; _categories = categories; _maximumPayloadBytes = maximumPayloadBytes; _persistent = persistent; _urgencies = urgencies; }
    /// <inheritdoc />
    public override NeoScopeFamily Family => NeoScopeFamily.Notifications;
    /// <inheritdoc />
    public override string Summary => $"notifications:categories={_categories.Count},payloadBytes={_maximumPayloadBytes},persistent={_persistent}";
    internal override bool Allows(JsonElement arguments, out string safeReason)
    {
        safeReason = "notification_out_of_scope";
        return ScopeValidation.TryString(arguments, "appIdentity", out var app) && string.Equals(app, _appIdentity, StringComparison.Ordinal) && ScopeValidation.TryString(arguments, "category", out var category) && _categories.Contains(category) && ScopeValidation.TryString(arguments, "urgency", out var urgency) && _urgencies.Contains(urgency) && (!arguments.TryGetProperty("persistent", out var persistent) || persistent.ValueKind == JsonValueKind.False || _persistent && persistent.ValueKind == JsonValueKind.True) && ScopeValidation.Utf8PropertyBytes(arguments, "payload") <= _maximumPayloadBytes;
    }
    internal override void WriteCanonical(Utf8JsonWriter writer) { writer.WriteStartObject(); writer.WriteString("appIdentity", _appIdentity); ScopeCanonical.WriteStrings(writer, "categories", _categories); writer.WriteNumber("maximumPayloadBytes", _maximumPayloadBytes); writer.WriteBoolean("persistent", _persistent); ScopeCanonical.WriteStrings(writer, "urgencies", _urgencies); writer.WriteEndObject(); }
}

/// <summary>Represents validated native dialog kinds, location tokens, and extension filters.</summary>
public sealed class NeoDialogScope : NeoCapabilityScope
{
    private readonly IReadOnlySet<string> _kinds; private readonly IReadOnlySet<string> _locations; private readonly IReadOnlySet<string> _extensions;
    internal NeoDialogScope(IReadOnlySet<string> kinds, IReadOnlySet<string> locations, IReadOnlySet<string> extensions) { _kinds = kinds; _locations = locations; _extensions = extensions; }
    /// <inheritdoc />
    public override NeoScopeFamily Family => NeoScopeFamily.Dialogs;
    /// <inheritdoc />
    public override string Summary => $"dialogs:kinds={_kinds.Count},locations={_locations.Count},filters={_extensions.Count}";
    internal override bool Allows(JsonElement arguments, out string safeReason)
    {
        safeReason = "dialog_out_of_scope";
        if (!ScopeValidation.TryString(arguments, "kind", out var kind) || !_kinds.Contains(kind) || !ScopeValidation.TryString(arguments, "initialLocation", out var location) || !_locations.Contains(location)) return false;
        if (!arguments.TryGetProperty("extensions", out var filters)) return true;
        return filters.ValueKind == JsonValueKind.Array && filters.GetArrayLength() <= 32 && filters.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && _extensions.Contains(item.GetString()!));
    }
    internal override void WriteCanonical(Utf8JsonWriter writer) { writer.WriteStartObject(); ScopeCanonical.WriteStrings(writer, "kinds", _kinds); ScopeCanonical.WriteStrings(writer, "initialLocations", _locations); ScopeCanonical.WriteStrings(writer, "extensions", _extensions); writer.WriteEndObject(); }
}

/// <summary>Represents constrained network requests and redirect/body/response policy.</summary>
public sealed class NeoNetworkScope : NeoCapabilityScope
{
    private readonly NeoUrlScope _destinations; private readonly IReadOnlySet<string> _methods; private readonly IReadOnlySet<string> _headers; private readonly bool _redirects; private readonly int _maximumBodyBytes; private readonly int _maximumResponseBytes;
    internal NeoNetworkScope(NeoUrlScope destinations, IReadOnlySet<string> methods, IReadOnlySet<string> headers, bool redirects, int maximumBodyBytes, int maximumResponseBytes) { _destinations = destinations; _methods = methods; _headers = headers; _redirects = redirects; _maximumBodyBytes = maximumBodyBytes; _maximumResponseBytes = maximumResponseBytes; }
    /// <inheritdoc />
    public override NeoScopeFamily Family => NeoScopeFamily.Network;
    /// <inheritdoc />
    public override string Summary => $"network:{_destinations.Summary},methods={_methods.Count},headers={_headers.Count},redirects={_redirects},body={_maximumBodyBytes},response={_maximumResponseBytes}";
    internal override bool Allows(JsonElement arguments, out string safeReason)
    {
        safeReason = "network_out_of_scope";
        if (!ScopeValidation.TryString(arguments, "url", out var url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !_destinations.Allows(uri) || !ScopeValidation.TryString(arguments, "method", out var method) || !_methods.Contains(method.ToUpperInvariant())) return false;
        if (arguments.TryGetProperty("followRedirects", out var redirects) && redirects.ValueKind == JsonValueKind.True && !_redirects) return false;
        if (ScopeValidation.Utf8PropertyBytes(arguments, "body") > _maximumBodyBytes) return false;
        if (arguments.TryGetProperty("maximumResponseBytes", out var max) && (!max.TryGetInt32(out var requested) || requested > _maximumResponseBytes)) return false;
        if (arguments.TryGetProperty("headers", out var headers))
        {
            if (headers.ValueKind != JsonValueKind.Object || headers.EnumerateObject().Count() > 64 || headers.EnumerateObject().Any(header => !_headers.Contains(header.Name.ToLowerInvariant()) || header.Value.ValueKind != JsonValueKind.String || header.Value.GetString()!.Length > 8192)) return false;
        }
        return true;
    }
    internal override void WriteCanonical(Utf8JsonWriter writer) { writer.WriteStartObject(); _destinations.WriteCanonicalProperties(writer); ScopeCanonical.WriteStrings(writer, "methods", _methods); ScopeCanonical.WriteStrings(writer, "headers", _headers); writer.WriteBoolean("allowRedirects", _redirects); writer.WriteNumber("maximumBodyBytes", _maximumBodyBytes); writer.WriteNumber("maximumResponseBytes", _maximumResponseBytes); writer.WriteEndObject(); }
}

/// <summary>Represents remembered grant identity and bounded duration.</summary>
public sealed class NeoPersistenceScope : NeoCapabilityScope
{
    private readonly IReadOnlySet<string> _identities; private readonly IReadOnlySet<string> _kinds; private readonly TimeSpan _maximumDuration;
    internal NeoPersistenceScope(IReadOnlySet<string> identities, IReadOnlySet<string> kinds, TimeSpan maximumDuration) { _identities = identities; _kinds = kinds; _maximumDuration = maximumDuration; }
    /// <inheritdoc />
    public override NeoScopeFamily Family => NeoScopeFamily.Persistence;
    /// <inheritdoc />
    public override string Summary => $"persistence:identities={_identities.Count},kinds={_kinds.Count},maximumSeconds={(long)_maximumDuration.TotalSeconds}";
    internal override bool Allows(JsonElement arguments, out string safeReason)
    {
        safeReason = "persistence_out_of_scope";
        return ScopeValidation.TryString(arguments, "identity", out var identity) && _identities.Contains(identity) && ScopeValidation.TryString(arguments, "kind", out var kind) && _kinds.Contains(kind) && arguments.TryGetProperty("durationSeconds", out var duration) && duration.TryGetInt64(out var seconds) && seconds >= 0 && seconds <= _maximumDuration.TotalSeconds;
    }
    internal override void WriteCanonical(Utf8JsonWriter writer) { writer.WriteStartObject(); ScopeCanonical.WriteStrings(writer, "identities", _identities); ScopeCanonical.WriteStrings(writer, "kinds", _kinds); writer.WriteNumber("maximumDurationSeconds", (long)_maximumDuration.TotalSeconds); writer.WriteEndObject(); }
}

internal static class ScopeCanonical
{
    internal static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WritePropertyName(name); writer.WriteStartArray(); foreach (var value in values.Order(StringComparer.Ordinal)) writer.WriteStringValue(value); writer.WriteEndArray();
    }
}

internal static class NeoScopeParser
{
    internal static NeoCapabilityScope Parse(NeoScopeFamily family, JsonElement scope, NeoCapabilityPlatform platform)
    {
        if (scope.ValueKind != JsonValueKind.Object || scope.GetRawText().Length > 256 * 1024 || scope.GetRawText().Count(static c => c is '[' or '{') > 4096) throw Invalid("A scope must be a bounded object.");
        return family switch
        {
            NeoScopeFamily.Filesystem => FileSystem(scope, platform), NeoScopeFamily.Url => Url(scope), NeoScopeFamily.Process => Process(scope, platform), NeoScopeFamily.Clipboard => Clipboard(scope),
            NeoScopeFamily.Notifications => Notifications(scope), NeoScopeFamily.Dialogs => Dialogs(scope), NeoScopeFamily.Network => Network(scope), NeoScopeFamily.Persistence => Persistence(scope),
            _ => throw Invalid("The scope family is unsupported."),
        };
    }

    private static NeoFileSystemScope FileSystem(JsonElement value, NeoCapabilityPlatform platform)
    {
        Fields(value, "roots", "operations", "allowSymlinks"); var rootsElement = Required(value, "roots", JsonValueKind.Array); if (rootsElement.GetArrayLength() is < 1 or > 64) throw Invalid("Filesystem roots must contain 1 to 64 entries.");
        var roots = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in rootsElement.EnumerateArray())
        {
            Fields(root, "token", "path"); var token = Text(root, "token", 64, identifier: true); var path = Text(root, "path", 4096, identifier: false);
            if (!Path.IsPathFullyQualified(path) || ScopeValidation.HasUnsafeUnicode(path) || ScopeValidation.HasDeviceOrAlternateStream(path, platform)) throw Invalid("A filesystem root is not a safe absolute path.");
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); if (!roots.TryAdd(token, path)) throw Invalid("A filesystem root token is duplicated.");
        }
        var operations = Set(value, "operations", 4, "read", "write", "create", "delete"); var links = OptionalBool(value, "allowSymlinks");
        return new(roots, operations, links, platform);
    }

    private static NeoUrlScope Url(JsonElement value)
    {
        Fields(value, "schemes", "hosts", "ports", "pathPrefixes");
        var schemes = Set(value, "schemes", 16, "https", "http");
        if (schemes.Any(static value => value is not ("http" or "https"))) throw Invalid("Dangerous or custom URL schemes are denied by the built-in opener scope.");
        var hosts = Set(value, "hosts", 128, allowAny: true); if (hosts.Any(static host => host.Length > 253 || host.Any(c => c > 0x7f) || !Uri.CheckHostName(host).Equals(UriHostNameType.Dns))) throw Invalid("URL hosts must be exact bounded ASCII DNS names.");
        var normalizedHosts = hosts.Select(static host => new IdnMapping().GetAscii(host).ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var ports = IntSet(value, "ports", 128, 1, 65535, optional: true); var prefixes = StringList(value, "pathPrefixes", 128, 2048, optional: true);
        if (prefixes.Any(static prefix => !prefix.StartsWith('/') || prefix.Contains("..", StringComparison.Ordinal) || ScopeValidation.HasUnsafeUnicode(prefix))) throw Invalid("URL path prefixes must be canonical absolute paths.");
        if (prefixes.Distinct(StringComparer.Ordinal).Count() != prefixes.Count) throw Invalid("URL path prefixes must be unique.");
        return new(schemes, normalizedHosts, ports, prefixes.Order(StringComparer.Ordinal).ToArray());
    }

    private static NeoProcessScope Process(JsonElement value, NeoCapabilityPlatform platform)
    {
        Fields(value, "executables"); var entriesElement = Required(value, "executables", JsonValueKind.Array); if (entriesElement.GetArrayLength() is < 1 or > 64) throw Invalid("Process scopes require bounded executable entries.");
        var entries = new SortedDictionary<string, NeoProcessScope.ProcessEntry>(StringComparer.Ordinal);
        foreach (var entry in entriesElement.EnumerateArray())
        {
            Fields(entry, "id", "path", "arguments", "workingDirectory", "environment"); var id = Text(entry, "id", 64, true); var path = Text(entry, "path", 4096, false);
            if (!Path.IsPathFullyQualified(path) || ScopeValidation.HasUnsafeUnicode(path) || ScopeValidation.HasDeviceOrAlternateStream(path, platform)) throw Invalid("Executable paths must be safe absolute paths.");
            var args = StringList(entry, "arguments", 64, 4096); if (args.Any(static arg => arg.Any(char.IsControl))) throw Invalid("Fixed process arguments contain controls.");
            var working = OptionalText(entry, "workingDirectory", 4096); if (working is not null && (!Path.IsPathFullyQualified(working) || ScopeValidation.HasUnsafeUnicode(working) || ScopeValidation.HasDeviceOrAlternateStream(working, platform))) throw Invalid("A process working directory must be a safe absolute path.");
            var environment = Set(entry, "environment", 64, allowAny: true, optional: true); if (environment.Any(static name => !ScopeValidation.IsAsciiIdentifier(name))) throw Invalid("Environment names must be exact ASCII identifiers.");
            if (!entries.TryAdd(id, new(Path.GetFullPath(path), Array.AsReadOnly(args.ToArray()), working is null ? null : Path.GetFullPath(working), environment))) throw Invalid("An executable ID is duplicated.");
        }
        return new(entries);
    }

    private static NeoClipboardScope Clipboard(JsonElement value) { Fields(value, "formats", "operations"); return new(Set(value, "formats", 5, "text", "html", "image", "files"), Set(value, "operations", 2, "read", "write")); }
    private static NeoNotificationScope Notifications(JsonElement value)
    {
        Fields(value, "appIdentity", "categories", "maximumPayloadBytes", "persistent", "urgencies"); var app = Text(value, "appIdentity", 128, true); var categories = IdentifierSet(value, "categories", 64); var bytes = Integer(value, "maximumPayloadBytes", 1, 64 * 1024); var persistent = OptionalBool(value, "persistent"); var urgencies = Set(value, "urgencies", 3, "low", "normal", "high"); return new(app, categories, bytes, persistent, urgencies);
    }
    private static NeoDialogScope Dialogs(JsonElement value) { Fields(value, "kinds", "initialLocations", "extensions"); var kinds = Set(value, "kinds", 4, "openFile", "saveFile", "openFolder", "message"); var locations = IdentifierSet(value, "initialLocations", 64); var extensions = Set(value, "extensions", 64, allowAny: true, optional: true); if (extensions.Any(static x => x.Length is < 1 or > 32 || x[0] == '.' || !x.All(char.IsAsciiLetterOrDigit))) throw Invalid("Dialog extensions must be bounded extension tokens."); return new(kinds, locations, extensions); }
    private static NeoNetworkScope Network(JsonElement value)
    {
        Fields(value, "schemes", "hosts", "ports", "pathPrefixes", "methods", "headers", "allowRedirects", "maximumBodyBytes", "maximumResponseBytes"); var url = UrlSubset(value); var methods = Set(value, "methods", 9, "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"); var headers = Set(value, "headers", 64, allowAny: true, optional: true).Select(static x => x.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal); if (headers.Any(static x => x is "authorization" or "cookie" or "proxy-authorization" || !ScopeValidation.IsHttpToken(x))) throw Invalid("Network headers include a credential or invalid header name."); return new(url, methods, headers, OptionalBool(value, "allowRedirects"), Integer(value, "maximumBodyBytes", 0, 16 * 1024 * 1024), Integer(value, "maximumResponseBytes", 1, 64 * 1024 * 1024));
    }
    private static NeoUrlScope UrlSubset(JsonElement source)
    {
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartObject(); foreach (var name in new[] { "schemes", "hosts", "ports", "pathPrefixes" }) if (source.TryGetProperty(name, out var property)) { writer.WritePropertyName(name); property.WriteTo(writer); } writer.WriteEndObject(); }
        using var doc = JsonDocument.Parse(stream.ToArray()); return Url(doc.RootElement);
    }
    private static NeoPersistenceScope Persistence(JsonElement value) { Fields(value, "identities", "kinds", "maximumDurationSeconds"); return new(IdentifierSet(value, "identities", 64), Set(value, "kinds", 8, "browserPermission", "nativeGrant", "applicationGrant"), TimeSpan.FromSeconds(Integer(value, "maximumDurationSeconds", 0, 365 * 24 * 60 * 60))); }

    private static void Fields(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("A scope member must be an object."); var names = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!names.Contains(property.Name)) throw Invalid($"Unsupported scope field '{property.Name}'.");
    }
    private static JsonElement Required(JsonElement value, string name, JsonValueKind kind) { if (!value.TryGetProperty(name, out var result) || result.ValueKind != kind) throw Invalid($"Scope field '{name}' is required and must be {kind}."); return result; }
    private static string Text(JsonElement value, string name, int maximum, bool identifier) { var result = Required(value, name, JsonValueKind.String).GetString()!; if (result.Length is 0 || result.Length > maximum || result.Any(char.IsControl) || identifier && !NeoRpcValidation.IsWireName(result, maximum)) throw Invalid($"Scope field '{name}' is malformed."); return result; }
    private static string? OptionalText(JsonElement value, string name, int maximum) => value.TryGetProperty(name, out var item) ? item.ValueKind == JsonValueKind.Null ? null : item.ValueKind == JsonValueKind.String && item.GetString() is { } text && text.Length <= maximum && !text.Any(char.IsControl) ? text : throw Invalid($"Scope field '{name}' is malformed.") : null;
    private static bool OptionalBool(JsonElement value, string name) => value.TryGetProperty(name, out var result) ? result.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw Invalid($"Scope field '{name}' must be Boolean.") } : false;
    private static int Integer(JsonElement value, string name, int minimum, int maximum) { if (!value.TryGetProperty(name, out var result) || !result.TryGetInt32(out var number) || number < minimum || number > maximum) throw Invalid($"Scope field '{name}' is outside its bound."); return number; }
    private static HashSet<string> Set(JsonElement value, string name, int maximum, params string[] allowed) => Set(value, name, maximum, false, false, allowed);
    private static HashSet<string> IdentifierSet(JsonElement value, string name, int maximum) { var set = Set(value, name, maximum, allowAny: true); if (set.Any(static item => !NeoRpcValidation.IsWireName(item, 128))) throw Invalid($"Scope field '{name}' must contain bounded ASCII identifiers."); return set; }
    private static HashSet<string> Set(JsonElement value, string name, int maximum, bool allowAny, bool optional = false, params string[] allowed)
    {
        if (!value.TryGetProperty(name, out var result)) { if (optional) return new(StringComparer.Ordinal); throw Invalid($"Scope field '{name}' is required."); }
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() > maximum || !optional && result.GetArrayLength() < 1) throw Invalid($"Scope field '{name}' must be a bounded array.");
        var set = new HashSet<string>(StringComparer.Ordinal); foreach (var item in result.EnumerateArray()) { if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= 256 } text || text.Any(char.IsControl) || !set.Add(text) || !allowAny && !allowed.Contains(text, StringComparer.Ordinal)) throw Invalid($"Scope field '{name}' contains an unsupported or duplicate value."); } return set;
    }
    private static HashSet<int> IntSet(JsonElement value, string name, int maximum, int minimum, int maxValue, bool optional)
    {
        if (!value.TryGetProperty(name, out var result)) { if (optional) return []; throw Invalid($"Scope field '{name}' is required."); } if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() > maximum) throw Invalid($"Scope field '{name}' is not a bounded array."); var set = new HashSet<int>(); foreach (var item in result.EnumerateArray()) if (!item.TryGetInt32(out var number) || number < minimum || number > maxValue || !set.Add(number)) throw Invalid($"Scope field '{name}' contains an invalid value."); return set;
    }
    private static IReadOnlyList<string> StringList(JsonElement value, string name, int maximumItems, int maximumLength, bool optional = false)
    {
        if (!value.TryGetProperty(name, out var result)) { if (optional) return Array.Empty<string>(); throw Invalid($"Scope field '{name}' is required."); } if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() > maximumItems) throw Invalid($"Scope field '{name}' is not a bounded array."); var list = new List<string>(); foreach (var item in result.EnumerateArray()) { if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text || text.Length > maximumLength || ScopeValidation.HasUnsafeUnicode(text)) throw Invalid($"Scope field '{name}' contains an invalid string."); list.Add(text); } return list;
    }
    private static NeoCapabilityValidationException Invalid(string message) => new("scope_invalid", message);
}

internal static class ScopeValidation
{
    internal static bool TryString(JsonElement value, string name, out string text) { text = string.Empty; return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String && item.GetString() is { } result && result.Length <= 8192 && !result.Any(char.IsControl) && (text = result) is not null; }
    internal static bool IsSafeRelativePath(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 4096 || Path.IsPathRooted(value) || HasUnsafeUnicode(value) || value.Contains(':')) return false;
        return !value.Replace('\\', '/').Split('/').Any(static segment => segment is "" or "." or "..");
    }
    internal static bool HasUnsafeUnicode(string value) => !value.IsNormalized(System.Text.NormalizationForm.FormC) || value.Any(static c => char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse);
    internal static bool HasDeviceOrAlternateStream(string value, NeoCapabilityPlatform platform)
    {
        if (platform != NeoCapabilityPlatform.Windows) return false; var normalized = value.Replace('/', '\\'); if (normalized.StartsWith("\\\\", StringComparison.Ordinal)) return true;
        foreach (var segment in normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries)) { var stem = segment.Split('.')[0].TrimEnd(' '); if (segment.EndsWith(' ') || segment.EndsWith('.') || segment.Contains(':') && !segment.EndsWith(':') || stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9') return true; } return false;
    }
    internal static bool HasLinkOrReparsePoint(string root, string target)
    {
        try { if ((File.Exists(root) || Directory.Exists(root)) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return true; var relative = Path.GetRelativePath(root, target); var current = root; foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)) { current = Path.Combine(current, segment); if (!File.Exists(current) && !Directory.Exists(current)) break; var attributes = File.GetAttributes(current); if ((attributes & FileAttributes.ReparsePoint) != 0) return true; } } catch { return true; } return false;
    }
    internal static int EffectivePort(Uri uri) => uri.IsDefaultPort ? uri.Scheme.ToLowerInvariant() switch { "http" => 80, "https" => 443, _ => -1 } : uri.Port;
    internal static bool PathPrefix(string path, string prefix) => string.Equals(path, prefix, StringComparison.Ordinal) || path.StartsWith(prefix.EndsWith('/') ? prefix : prefix + "/", StringComparison.Ordinal);
    internal static bool HasAmbiguousUrlEncoding(string value) => value.Contains('\\') || value.Contains("%2e", StringComparison.OrdinalIgnoreCase) || value.Contains("%2f", StringComparison.OrdinalIgnoreCase) || value.Contains("%5c", StringComparison.OrdinalIgnoreCase);
    internal static int Utf8PropertyBytes(JsonElement args, string name) => args.TryGetProperty(name, out var value) ? System.Text.Encoding.UTF8.GetByteCount(value.GetRawText()) : 0;
    internal static bool IsAsciiIdentifier(string value) => value.Length is > 0 and <= 128 && value.All(static c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
    internal static bool IsHttpToken(string value) => value.Length is > 0 and <= 128 && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~');
}
