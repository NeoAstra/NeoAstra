// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

#if NEOASTRA_TOOL
namespace NeoAstra.Tool.Shared;
#else
namespace NeoAstra;
#endif

/// <summary>Describes one immutable file in a version 1 production asset manifest.</summary>
/// <param name="Path">The normalized, slash-separated relative asset path.</param>
/// <param name="Length">The exact file length.</param>
/// <param name="Sha256">The lowercase SHA-256 digest.</param>
/// <param name="ContentType">The response content type.</param>
/// <param name="CacheControl">The response cache policy.</param>
public sealed record NeoAssetEntry(string Path, long Length, string Sha256, string ContentType, string CacheControl);

/// <summary>Represents the strict, deterministic production asset manifest consumed by the static host.</summary>
public sealed class NeoAssetManifest
{
    private const int MaximumManifestBytes = 16 * 1024 * 1024;
    private static readonly HashSet<string> ForbiddenSegments = new(StringComparer.OrdinalIgnoreCase) { "node_modules", ".git", ".hg", ".svn", "src", "source" };
    private static readonly HashSet<string> ForbiddenNames = new(StringComparer.OrdinalIgnoreCase) { ".env", ".env.local", ".npmrc", ".yarnrc", "bun.lock", "bun.lockb", "id_rsa", "id_ed25519", "package-lock.json", "package.json", "pnpm-lock.yaml", "yarn.lock" };
    private readonly IReadOnlyDictionary<string, NeoAssetEntry> _byPath;

    /// <summary>Creates a validated version 1 asset manifest.</summary>
    /// <param name="version">The manifest version; only version 1 is supported.</param>
    /// <param name="entryDocument">The entry document.</param>
    /// <param name="spaFallback">The SPA fallback document.</param>
    /// <param name="origin">The production custom-scheme origin.</param>
    /// <param name="contentSecurityPolicy">The production Content Security Policy.</param>
    /// <param name="referrerPolicy">The production referrer policy.</param>
    /// <param name="spaRoutePrefixes">Optional route prefixes eligible for SPA fallback.</param>
    /// <param name="excludedPrefixes">Internal/API prefixes never eligible for SPA fallback.</param>
    /// <param name="assets">Sorted asset entries.</param>
    /// <exception cref="ArgumentException">A manifest field or asset entry is invalid, duplicated, unsorted, or ambiguous.</exception>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is unsupported or an asset bound is exceeded.</exception>
    public NeoAssetManifest(int version, string entryDocument, string spaFallback, string origin,
        string contentSecurityPolicy, string referrerPolicy, IReadOnlyList<string> spaRoutePrefixes,
        IReadOnlyList<string> excludedPrefixes, IReadOnlyList<NeoAssetEntry> assets)
    {
        ArgumentNullException.ThrowIfNull(entryDocument); ArgumentNullException.ThrowIfNull(spaFallback);
        ArgumentNullException.ThrowIfNull(origin); ArgumentNullException.ThrowIfNull(contentSecurityPolicy);
        ArgumentNullException.ThrowIfNull(referrerPolicy); ArgumentNullException.ThrowIfNull(spaRoutePrefixes);
        ArgumentNullException.ThrowIfNull(excludedPrefixes); ArgumentNullException.ThrowIfNull(assets);
        if (version != 1) throw new ArgumentOutOfRangeException(nameof(version), "Only asset manifest version 1 is supported.");
        ValidateAssetPath(entryDocument); ValidateAssetPath(spaFallback);
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || originUri.Scheme is "about" or "blob" or "data" or "file" or "ftp" or "http" or "https" or "javascript" or "ws" or "wss" || string.IsNullOrEmpty(originUri.Host) || originUri.AbsolutePath != "/" || originUri.UserInfo.Length != 0 || originUri.Query.Length != 0 || originUri.Fragment.Length != 0) throw new ArgumentException("Asset origin must be an authority-based non-standard custom-scheme origin.", nameof(origin));
        if (contentSecurityPolicy.Length is < 1 or > 16384 || contentSecurityPolicy.Contains("unsafe-eval", StringComparison.OrdinalIgnoreCase) || contentSecurityPolicy.Contains('*')) throw new ArgumentException("The production CSP is missing, oversized, or permissive.", nameof(contentSecurityPolicy));
        if (referrerPolicy.Length is < 1 or > 128) throw new ArgumentException("The referrer policy is invalid.", nameof(referrerPolicy));
        if (assets.Count is < 1 or > 50_000) throw new ArgumentOutOfRangeException(nameof(assets));
        var paths = new Dictionary<string, NeoAssetEntry>(StringComparer.Ordinal);
        var casePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previous = null; long total = 0;
        foreach (var entry in assets)
        {
            ArgumentNullException.ThrowIfNull(entry); ArgumentNullException.ThrowIfNull(entry.Path); ArgumentNullException.ThrowIfNull(entry.Sha256); ArgumentNullException.ThrowIfNull(entry.ContentType); ArgumentNullException.ThrowIfNull(entry.CacheControl); ValidateAssetPath(entry.Path);
            if (previous is not null && StringComparer.Ordinal.Compare(previous, entry.Path) >= 0) throw new ArgumentException("Manifest assets must be strictly ordinal-sorted.", nameof(assets));
            if (!casePaths.Add(entry.Path)) throw new ArgumentException("Manifest assets contain a case-colliding path.", nameof(assets));
            if (entry.Length is < 0 or > 1024L * 1024 * 1024 || checked(total += entry.Length) > 4L * 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(assets), "Asset sizes exceed hard limits.");
            if (entry.Sha256.Length != 64 || !entry.Sha256.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f')) throw new ArgumentException("Asset SHA-256 must be lowercase hexadecimal.", nameof(assets));
            ValidateHeader(entry.ContentType, nameof(assets)); ValidateHeader(entry.CacheControl, nameof(assets));
            if (entry.ContentType != GetContentType(entry.Path) || entry.CacheControl is not ("no-cache" or "public,max-age=31536000,immutable")) throw new ArgumentException("Manifest content type or cache policy is not canonical.", nameof(assets));
            paths.Add(entry.Path, entry); previous = entry.Path;
        }
        if (!paths.ContainsKey(entryDocument) || !paths.ContainsKey(spaFallback)) throw new ArgumentException("Entry and SPA fallback documents must be listed assets.", nameof(assets));
        ValidateRoutes(spaRoutePrefixes, nameof(spaRoutePrefixes)); ValidateRoutes(excludedPrefixes, nameof(excludedPrefixes));
        if (!excludedPrefixes.Contains("/api", StringComparer.Ordinal) || !excludedPrefixes.Contains("/_neoastra", StringComparer.Ordinal)) throw new ArgumentException("Manifest exclusions must reserve /api and /_neoastra.", nameof(excludedPrefixes));
        Version = version; EntryDocument = entryDocument; SpaFallback = spaFallback; Origin = origin.TrimEnd('/');
        ContentSecurityPolicy = contentSecurityPolicy; ReferrerPolicy = referrerPolicy;
        SpaRoutePrefixes = spaRoutePrefixes.ToArray(); ExcludedPrefixes = excludedPrefixes.ToArray(); Assets = assets.ToArray(); _byPath = paths;
        TotalBytes = total;
    }

    /// <summary>Gets the manifest version.</summary>
    public int Version { get; }
    /// <summary>Gets the entry document path.</summary>
    public string EntryDocument { get; }
    /// <summary>Gets the SPA fallback document path.</summary>
    public string SpaFallback { get; }
    /// <summary>Gets the exact production origin.</summary>
    public string Origin { get; }
    /// <summary>Gets the production Content Security Policy.</summary>
    public string ContentSecurityPolicy { get; }
    /// <summary>Gets the production referrer policy.</summary>
    public string ReferrerPolicy { get; }
    /// <summary>Gets optional SPA route prefixes.</summary>
    public IReadOnlyList<string> SpaRoutePrefixes { get; }
    /// <summary>Gets prefixes excluded from SPA fallback.</summary>
    public IReadOnlyList<string> ExcludedPrefixes { get; }
    /// <summary>Gets strictly sorted asset entries.</summary>
    public IReadOnlyList<NeoAssetEntry> Assets { get; }
    /// <summary>Gets total manifest-listed bytes.</summary>
    public long TotalBytes { get; }

    /// <summary>Creates a content-free diagnostic summary suitable for application snapshots.</summary>
    /// <param name="developmentOrigin">The configured development origin, when active.</param>
    /// <returns>Version, hashes, counts, origins, fallback mode, and source-map presence without local paths or asset contents.</returns>
    public NeoAssetDiagnostics CreateDiagnostics(Uri? developmentOrigin = null)
    {
        var json = Encoding.UTF8.GetBytes(ToJson());
        return new NeoAssetDiagnostics(Version, Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant(), EntryDocument,
            Assets.Count, TotalBytes, Origin, developmentOrigin?.GetLeftPart(UriPartial.Authority),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ContentSecurityPolicy))).ToLowerInvariant(),
            SpaRoutePrefixes.Count == 0 ? "extension-and-accept" : "configured-routes", Assets.Any(static asset => asset.Path.EndsWith(".map", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Loads and strictly validates a version 1 manifest.</summary>
    /// <param name="path">The manifest path.</param>
    /// <returns>The validated manifest.</returns>
    /// <exception cref="ArgumentException">The manifest is malformed, contains duplicate/unknown fields, or violates a security bound.</exception>
    /// <exception cref="FileNotFoundException">The manifest does not exist.</exception>
    public static NeoAssetManifest Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Asset manifest was not found.", path);
        if (info.Length is < 1 or > MaximumManifestBytes) throw new ArgumentException("Asset manifest exceeds its size bound.", nameof(path));
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(info.FullName), new JsonDocumentOptions { MaxDepth = 16 });
            ValidateJson(document.RootElement); var root = document.RootElement;
            Exact(root, "version", "entryDocument", "spaFallback", "origin", "csp", "referrerPolicy", "spaRoutePrefixes", "excludedPrefixes", "assets");
            var entries = root.GetProperty("assets").EnumerateArray().Select(item => { Exact(item, "path", "length", "sha256", "contentType", "cacheControl"); return new NeoAssetEntry(item.GetProperty("path").GetString()!, item.GetProperty("length").GetInt64(), item.GetProperty("sha256").GetString()!, item.GetProperty("contentType").GetString()!, item.GetProperty("cacheControl").GetString()!); }).ToArray();
            return new(root.GetProperty("version").GetInt32(), root.GetProperty("entryDocument").GetString()!, root.GetProperty("spaFallback").GetString()!, root.GetProperty("origin").GetString()!, root.GetProperty("csp").GetString()!, root.GetProperty("referrerPolicy").GetString()!, ReadStringArray(root, "spaRoutePrefixes"), ReadStringArray(root, "excludedPrefixes"), entries);
        }
        catch (JsonException exception) { throw new ArgumentException("Asset manifest is invalid JSON.", nameof(path), exception); }
        catch (InvalidOperationException exception) { throw new ArgumentException("Asset manifest has an invalid field type.", nameof(path), exception); }
        catch (KeyNotFoundException exception) { throw new ArgumentException("Asset manifest is missing a required field.", nameof(path), exception); }
        catch (Exception exception) when (exception is NullReferenceException or FormatException or OverflowException) { throw new ArgumentException("Asset manifest has an invalid field value.", nameof(path), exception); }
    }

    /// <summary>Writes deterministic compact JSON suitable for hashing and packaging.</summary>
    /// <returns>The manifest JSON without a trailing newline.</returns>
    public string ToJson()
    {
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteNumber("version", Version); writer.WriteString("entryDocument", EntryDocument); writer.WriteString("spaFallback", SpaFallback); writer.WriteString("origin", Origin); writer.WriteString("csp", ContentSecurityPolicy); writer.WriteString("referrerPolicy", ReferrerPolicy);
            WriteArray(writer, "spaRoutePrefixes", SpaRoutePrefixes); WriteArray(writer, "excludedPrefixes", ExcludedPrefixes); writer.WriteStartArray("assets");
            foreach (var entry in Assets) { writer.WriteStartObject(); writer.WriteString("path", entry.Path); writer.WriteNumber("length", entry.Length); writer.WriteString("sha256", entry.Sha256); writer.WriteString("contentType", entry.ContentType); writer.WriteString("cacheControl", entry.CacheControl); writer.WriteEndObject(); }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Returns the portable content type used by the production manifest builder.</summary>
    /// <param name="path">An asset path.</param>
    /// <returns>A known content type or <c>application/octet-stream</c>.</returns>
    public static string GetContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".css" => "text/css; charset=utf-8", ".gif" => "image/gif", ".html" => "text/html; charset=utf-8", ".ico" => "image/x-icon", ".jpeg" or ".jpg" => "image/jpeg", ".js" or ".mjs" => "text/javascript; charset=utf-8", ".json" => "application/json; charset=utf-8", ".png" => "image/png", ".svg" => "image/svg+xml", ".txt" => "text/plain; charset=utf-8", ".wasm" => "application/wasm", ".webp" => "image/webp", ".ttf" => "font/ttf", ".woff" => "font/woff", ".woff2" => "font/woff2", ".xml" => "application/xml; charset=utf-8", _ => "application/octet-stream",
    };

    internal bool TryGetAsset(string path, out NeoAssetEntry entry) => _byPath.TryGetValue(path, out entry!);
    private static void ValidateAssetPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segments = path.Split('/');
        if (path.Length is < 1 or > 1024 || path.StartsWith('/') || path.Contains('\\') || path.Contains('\0') || path.Contains(':') || !path.IsNormalized(NormalizationForm.FormC) || segments.Any(static segment => segment is "" or "." or ".." || IsPortableAmbiguousSegment(segment)) || segments.Any(ForbiddenSegments.Contains) || ForbiddenNames.Contains(segments[^1]) || segments[^1].StartsWith(".env", StringComparison.OrdinalIgnoreCase) || path.Equals("neoastra-assets.json", StringComparison.OrdinalIgnoreCase) || path.StartsWith("_neoastra/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("api/", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Manifest contains an invalid, ambiguous, reserved, secret, source, dependency, or VCS asset path.");
    }
    private static bool IsPortableAmbiguousSegment(string segment)
    {
        if (segment.EndsWith(' ') || segment.EndsWith('.')) return true;
        var stem = segment.Split('.')[0].TrimEnd(' ', '.');
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9';
    }
    private static void ValidateHeader(string value, string name) { if (string.IsNullOrWhiteSpace(value) || value.Length > 16384 || value.Any(static c => c is '\r' or '\n' || c < ' ')) throw new ArgumentException("Manifest contains invalid header metadata.", name); }
    private static void ValidateRoutes(IReadOnlyList<string> routes, string name) { if (routes.Count > 128 || routes.Distinct(StringComparer.Ordinal).Count() != routes.Count || routes.Any(static route => route.Length is < 1 or > 1024 || !route.StartsWith('/') || route != "/" && route.EndsWith('/') || route.Contains('\\') || route.Contains('?') || route.Contains('#') || route.Contains('\0') || route.Contains(':') || route.Contains("%2f", StringComparison.OrdinalIgnoreCase) || route.Contains("%5c", StringComparison.OrdinalIgnoreCase) || route.Contains("%2e", StringComparison.OrdinalIgnoreCase) || route != "/" && route[1..].Split('/').Any(static segment => segment is "" or "." or ".."))) throw new ArgumentException("Manifest contains an invalid route prefix.", name); }
    private static void Exact(JsonElement item, params string[] fields) { if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException("Manifest member must be an object."); var set = fields.ToHashSet(StringComparer.Ordinal); foreach (var property in item.EnumerateObject()) if (!set.Contains(property.Name)) throw new ArgumentException("Manifest contains an unknown field."); }
    private static string[] ReadStringArray(JsonElement root, string name) => root.GetProperty(name).EnumerateArray().Select(static item => item.GetString()!).ToArray();
    private static void WriteArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> values) { writer.WriteStartArray(name); foreach (var value in values) writer.WriteStringValue(value); writer.WriteEndArray(); }
    private static void ValidateJson(JsonElement root) { var count = 0; Visit(root); void Visit(JsonElement value) { if (++count > 500_000) throw new ArgumentException("Manifest exceeds its complexity bound."); if (value.ValueKind == JsonValueKind.Object) { var names = new HashSet<string>(StringComparer.Ordinal); foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw new ArgumentException("Manifest contains a duplicate property."); Visit(property.Value); } } else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Visit(item); } }
}

#if !NEOASTRA_TOOL
/// <summary>Securely hosts exactly the regular files listed by a production asset manifest.</summary>
public sealed class NeoManifestResourceProvider : INeoResourceProvider
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _root;
    private readonly string _prefix;
    private readonly StringComparison _comparison;
    private readonly NeoAssetManifest _manifest;
    private readonly Uri _origin;

    /// <summary>Creates a fail-closed production provider for a validated asset root and manifest.</summary>
    /// <param name="rootDirectory">The packaged asset root.</param>
    /// <param name="manifest">The validated manifest.</param>
    /// <exception cref="ArgumentException">The root or an existing ancestor is a link/reparse point.</exception>
    /// <exception cref="DirectoryNotFoundException">The root does not exist.</exception>
    public NeoManifestResourceProvider(string rootDirectory, NeoAssetManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory); ArgumentNullException.ThrowIfNull(manifest);
        _root = Path.GetFullPath(rootDirectory); if (!Directory.Exists(_root)) throw new DirectoryNotFoundException("The packaged asset root does not exist.");
        EnsureRootHasNoLinks(_root, nameof(rootDirectory));
        _prefix = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar; _comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal; _manifest = manifest; _origin = new Uri(manifest.Origin);
    }

    /// <inheritdoc />
    public NeoResourceResponse? GetResponse(NeoResourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); var head = request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
        if (!request.Uri.IsAbsoluteUri || !request.Uri.Scheme.Equals(_origin.Scheme, StringComparison.OrdinalIgnoreCase) || !request.Uri.Host.Equals(_origin.Host, StringComparison.OrdinalIgnoreCase) || request.Uri.Port != _origin.Port || request.Uri.UserInfo.Length != 0) return NeoResourceResponse.Empty(403, "Forbidden");
        if (!head && !request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)) return NeoResourceResponse.Empty(405, "Method Not Allowed");
        if (!TryDecodePath(request.Uri, out var path)) return NeoResourceResponse.Empty(400, "Bad Request");
        if (path.Length == 0) path = _manifest.EntryDocument;
        if (!_manifest.TryGetAsset(path, out var asset))
        {
            if (!ShouldFallback(request, path) || !_manifest.TryGetAsset(_manifest.SpaFallback, out asset!)) return null;
        }
        var fullPath = Path.GetFullPath(Path.Combine(_root, asset.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(_prefix, _comparison) || !File.Exists(fullPath) || ContainsReparse(fullPath)) return NeoResourceResponse.Empty(403, "Forbidden");
        var info = new FileInfo(fullPath); using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan); if (info.Length != asset.Length || !Convert.ToHexString(SHA256.HashData(stream)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)) return NeoResourceResponse.Empty(500, "Asset Integrity Failure");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Cache-Control"] = asset.CacheControl, ["Content-Length"] = asset.Length.ToString(CultureInfo.InvariantCulture), ["Content-Security-Policy"] = _manifest.ContentSecurityPolicy, ["Referrer-Policy"] = _manifest.ReferrerPolicy, ["X-Content-Type-Options"] = "nosniff" };
        return head ? NeoResourceResponse.CreateEmpty(200, "OK", asset.Length, headers, asset.ContentType) : NeoResourceResponse.CreateFile(fullPath, asset.ContentType, asset.Length, headers);
    }

    private bool ShouldFallback(NeoResourceRequest request, string path)
    {
        if (request.Kind != NeoResourceKind.Document || !request.IsMainFrame) return false;
        var absolute = "/" + path;
        if (_manifest.ExcludedPrefixes.Any(prefix => absolute.Equals(prefix, StringComparison.Ordinal) || absolute.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.Ordinal))) return false;
        if (!string.IsNullOrEmpty(Path.GetExtension(path))) return false;
        if (request.Headers.TryGetValue("Accept", out var accept) && !accept.Contains("text/html", StringComparison.OrdinalIgnoreCase) && !accept.Contains("*/*", StringComparison.Ordinal)) return false;
        return _manifest.SpaRoutePrefixes.Count == 0 || _manifest.SpaRoutePrefixes.Any(prefix => absolute.Equals(prefix, StringComparison.Ordinal) || absolute.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.Ordinal));
    }

    private static bool TryDecodePath(Uri uri, out string path)
    {
        path = string.Empty;
        if (uri.OriginalString.Contains("%2e", StringComparison.OrdinalIgnoreCase)) return false;
        var escaped = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        if (uri.AbsolutePath.StartsWith("//", StringComparison.Ordinal) || escaped.Contains("%2f", StringComparison.OrdinalIgnoreCase) || escaped.Contains("%5c", StringComparison.OrdinalIgnoreCase) || escaped.Contains("%00", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var stream = new MemoryStream(escaped.Length); Span<byte> bytes = stackalloc byte[4]; for (var index = 0; index < escaped.Length; index++) { if (escaped[index] == '%') { if (index + 2 >= escaped.Length || !byte.TryParse(escaped.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) return false; stream.WriteByte(b); index += 2; } else { var count = Encoding.UTF8.GetBytes(escaped.AsSpan(index, 1), bytes); stream.Write(bytes[..count]); } }
            path = StrictUtf8.GetString(stream.ToArray()).TrimStart('/').Normalize(NormalizationForm.FormC);
        }
        catch (DecoderFallbackException) { return false; }
        if (path.Contains('\\') || path.Contains('\0') || path.Contains(':') || path.Split('/').Any(static segment => segment is "." or ".." or "")) return path.Length == 0;
        return true;
    }
    private bool ContainsReparse(string path) { var current = _root; foreach (var segment in Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) { current = Path.Combine(current, segment); if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true; } return false; }
    private static void EnsureRootHasNoLinks(string path, string parameterName)
    {
        var root = Path.GetPathRoot(path); if (string.IsNullOrEmpty(root)) throw new ArgumentException("The packaged asset root is invalid.", parameterName);
        var current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("The packaged asset root must not traverse a link or reparse point.", parameterName);
        foreach (var segment in path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("The packaged asset root must not traverse a link or reparse point.", parameterName);
        }
    }
}
#endif

/// <summary>Provides a bounded, content-free production asset diagnostic snapshot.</summary>
/// <param name="ManifestVersion">The asset manifest version.</param>
/// <param name="ManifestSha256">The deterministic manifest SHA-256.</param>
/// <param name="EntryDocument">The entry document name without a local source path.</param>
/// <param name="AssetCount">The number of manifest-listed assets.</param>
/// <param name="TotalBytes">The sum of manifest-listed asset lengths.</param>
/// <param name="ProductionOrigin">The configured production origin.</param>
/// <param name="DevelopmentOrigin">The active development origin, if any.</param>
/// <param name="ContentSecurityPolicySha256">The CSP SHA-256 rather than policy contents.</param>
/// <param name="SpaFallbackMode">The configured SPA fallback mode.</param>
/// <param name="ContainsSourceMaps">Whether source maps are listed.</param>
public sealed record NeoAssetDiagnostics(int ManifestVersion, string ManifestSha256, string EntryDocument,
    int AssetCount, long TotalBytes, string ProductionOrigin, string? DevelopmentOrigin,
    string ContentSecurityPolicySha256, string SpaFallbackMode, bool ContainsSourceMaps);
