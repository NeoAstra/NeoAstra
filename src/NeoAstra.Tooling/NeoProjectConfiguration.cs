// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NeoAstra.Tooling;

internal sealed record NeoCommand(IReadOnlyList<string> Arguments)
{
    internal string Executable => Arguments[0];
}

internal static class NeoCommandPolicy
{
    private static readonly HashSet<string> InstallerExecutables = new(StringComparer.OrdinalIgnoreCase) { "bunx", "npx", "pnpx" };
    private static readonly HashSet<string> InstallerVerbs = new(StringComparer.OrdinalIgnoreCase) { "add", "ci", "create", "dlx", "exec", "i", "install", "up", "update" };

    internal static void EnsureProductionBuildDoesNotInstall(NeoResolvedProject project)
    {
        var executable = Path.GetFileNameWithoutExtension(project.BuildCommand.Executable);
        var packageManager = Path.GetFileNameWithoutExtension(project.PackageManager);
        if (InstallerExecutables.Contains(executable) || executable.Equals(packageManager, StringComparison.OrdinalIgnoreCase) && project.BuildCommand.Arguments.Skip(1).Any(InstallerVerbs.Contains))
            throw new NeoToolException("implicit_install", "The production build command must not install or update packages; restore explicit locked dependencies before invoking NeoAstra.");
    }
}

internal sealed record NeoResolvedProject(
    string ConfigurationPath,
    string ProjectDirectory,
    string Identifier,
    string DisplayName,
    string FrontendRoot,
    NeoCommand DevCommand,
    NeoCommand BackendCommand,
    NeoCommand ContractCommand,
    Uri DevUrl,
    NeoCommand BuildCommand,
    string DistDirectory,
    string SpaFallback,
    string PackageManager,
    string? Lockfile,
    string? GeneratedContract,
    bool AllowRemoteDevServer,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlySet<string> SecretEnvironment,
    Uri ProductionOrigin,
    bool CacheHashedAssets,
    string ContentSecurityPolicy,
    string ReferrerPolicy,
    bool IncludeSourceMaps,
    int MaximumFiles,
    long MaximumFileBytes,
    long MaximumTotalBytes,
    IReadOnlyList<string> SpaRoutePrefixes,
    IReadOnlyList<string> ExcludedPrefixes,
    IReadOnlyList<string> Capabilities,
    NeoBundleConfiguration? Bundle)
{
    internal string ToInspectJson(bool redactSecrets)
    {
        var environment = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in Environment) environment[pair.Key] = redactSecrets && SecretEnvironment.Contains(pair.Key) ? "[REDACTED]" : pair.Value;
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteNumber("version", 1); writer.WriteString("configurationPath", ConfigurationPath); writer.WriteString("projectDirectory", ProjectDirectory);
            writer.WriteStartObject("app"); writer.WriteString("identifier", Identifier); writer.WriteString("displayName", DisplayName); writer.WriteEndObject();
            writer.WriteStartObject("frontend"); writer.WriteString("root", FrontendRoot); WriteArray(writer, "devCommand", DevCommand.Arguments); WriteArray(writer, "backendCommand", BackendCommand.Arguments); WriteArray(writer, "contractCommand", ContractCommand.Arguments); writer.WriteString("devUrl", DevUrl.AbsoluteUri); WriteArray(writer, "buildCommand", BuildCommand.Arguments); writer.WriteString("dist", DistDirectory); writer.WriteString("spaFallback", SpaFallback); writer.WriteString("packageManager", PackageManager); WriteOptional(writer, "lockfile", Lockfile); WriteOptional(writer, "generatedContract", GeneratedContract); writer.WriteBoolean("allowRemoteDevServer", AllowRemoteDevServer); writer.WriteStartObject("environment"); foreach (var pair in environment) writer.WriteString(pair.Key, pair.Value); writer.WriteEndObject(); writer.WriteEndObject();
            writer.WriteStartObject("assets"); writer.WriteString("origin", ProductionOrigin.AbsoluteUri.TrimEnd('/')); writer.WriteBoolean("cacheHashedAssets", CacheHashedAssets); writer.WriteString("csp", ContentSecurityPolicy); writer.WriteString("referrerPolicy", ReferrerPolicy); writer.WriteBoolean("includeSourceMaps", IncludeSourceMaps); writer.WriteNumber("maximumFiles", MaximumFiles); writer.WriteNumber("maximumFileBytes", MaximumFileBytes); writer.WriteNumber("maximumTotalBytes", MaximumTotalBytes); WriteArray(writer, "spaRoutePrefixes", SpaRoutePrefixes); WriteArray(writer, "excludedPrefixes", ExcludedPrefixes); writer.WriteEndObject(); WriteArray(writer, "capabilities", Capabilities); Bundle?.WriteInspect(writer); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value) { if (value is null) writer.WriteNull(name); else writer.WriteString(name, value); }
    private static void WriteArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> values) { writer.WriteStartArray(name); foreach (var value in values) writer.WriteStringValue(value); writer.WriteEndArray(); }
}

internal static class NeoProjectConfiguration
{
    private const int MaximumConfigurationBytes = 1024 * 1024;
    private static readonly Regex IdentifierPattern = new("^[A-Za-z][A-Za-z0-9]*(\\.[A-Za-z0-9][A-Za-z0-9-]*)+$", RegexOptions.CultureInvariant);
    private static readonly Regex ProductionOriginPattern = new("^[a-z][a-z0-9+.-]*://[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?(?::[0-9]{1,5})?$", RegexOptions.CultureInvariant);

    internal static NeoResolvedProject Load(string path, IReadOnlyDictionary<string, string>? overrides = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length is < 1 or > MaximumConfigurationBytes) throw new NeoToolException("configuration_file", "neoastra.json must exist and be between 1 byte and 1 MiB.");
        var bytes = File.ReadAllBytes(fullPath);
        if (bytes.Length is < 1 or > MaximumConfigurationBytes) throw new NeoToolException("configuration_file", "neoastra.json must be between 1 byte and 1 MiB.");
        return Parse(bytes, fullPath, overrides);
    }

    internal static NeoResolvedProject ValidateGenerated(string path, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); ArgumentNullException.ThrowIfNull(json);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length is < 1 or > MaximumConfigurationBytes) throw new NeoToolException("configuration_file", "Generated neoastra.json must be between 1 byte and 1 MiB.");
        return Parse(bytes, Path.GetFullPath(path), null);
    }

    private static NeoResolvedProject Parse(byte[] bytes, string fullPath, IReadOnlyDictionary<string, string>? overrides)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            ValidateComplexity(document.RootElement);
            var root = document.RootElement;
            Exact(root, "$schema", "version", "app", "frontend", "assets", "capabilities", "bundle");
            if (String(root, "$schema") != "neoastra-project-v1.schema.json" || Integer(root, "version") != 1) throw Error("version", "Only neoastra project schema version 1 is supported.");
            var app = RequiredObject(root, "app"); Exact(app, "identifier", "displayName");
            var identifier = String(app, "identifier", 255); if (!IdentifierPattern.IsMatch(identifier)) throw Error("app.identifier", "The application identifier is invalid.");
            var displayName = String(app, "displayName", 128);
            var frontend = RequiredObject(root, "frontend");
            Exact(frontend, "root", "devCommand", "backendCommand", "contractCommand", "devUrl", "buildCommand", "dist", "spaFallback", "packageManager", "lockfile", "generatedContract", "allowRemoteDevServer", "environment", "secretEnvironment");
            var directory = Path.GetDirectoryName(fullPath)!;
            var frontendRoot = ResolvePath(directory, String(frontend, "root"), "frontend.root");
            var dist = ResolvePath(directory, String(frontend, "dist"), "frontend.dist");
            var allowRemote = Boolean(frontend, "allowRemoteDevServer", false);
            var devUrlText = Override(overrides, "NeoAstraDevUrl") ?? String(frontend, "devUrl", 2048);
            var devUrl = NeoOriginPolicy.ValidateDevelopmentUrl(devUrlText, allowRemote);
            var environment = ReadEnvironment(frontend);
            var secrets = ReadStrings(frontend, "secretEnvironment", 128, false).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (secrets.Any(name => !environment.ContainsKey(name))) throw Error("frontend.secretEnvironment", "Every secret environment name must be declared in frontend.environment.");
            var packageManager = OptionalString(frontend, "packageManager") ?? "none";
            if (packageManager is not ("npm" or "pnpm" or "yarn" or "bun" or "none")) throw Error("frontend.packageManager", "Unknown package manager.");
            var lockfile = OptionalPath(frontend, "lockfile", directory);
            var generated = OptionalPath(frontend, "generatedContract", directory);
            var spaFallback = AssetPath(String(frontend, "spaFallback"), "frontend.spaFallback");
            var assets = RequiredObject(root, "assets");
            Exact(assets, "origin", "cacheHashedAssets", "csp", "referrerPolicy", "includeSourceMaps", "maximumFiles", "maximumFileBytes", "maximumTotalBytes", "spaRoutePrefixes", "excludedPrefixes");
            var origin = ValidateProductionOrigin(String(assets, "origin", 256));
            var csp = String(assets, "csp", 16384); if (csp.Contains("unsafe-eval", StringComparison.OrdinalIgnoreCase) || csp.Contains("*", StringComparison.Ordinal)) throw Error("assets.csp", "Production CSP must not contain unsafe-eval or wildcard sources.");
            var capabilities = ReadStrings(root, "capabilities", 256, true).Select(item => ResolvePath(directory, item, "capabilities")).ToArray();
            return new(fullPath, directory, identifier, displayName, frontendRoot, Command(frontend, "devCommand"),
                OptionalCommand(frontend, "backendCommand") ?? new NeoCommand(["dotnet", "watch", "run"]), OptionalCommand(frontend, "contractCommand") ?? new NeoCommand(["dotnet", "build", "--no-restore"]), devUrl,
                Command(frontend, "buildCommand"), dist, spaFallback, packageManager, lockfile, generated, allowRemote,
                environment, secrets, origin, Boolean(assets, "cacheHashedAssets", true), csp,
                OptionalString(assets, "referrerPolicy", 128) ?? "no-referrer", Boolean(assets, "includeSourceMaps", false),
                BoundedInteger(assets, "maximumFiles", 10_000, 1, 50_000), BoundedLong(assets, "maximumFileBytes", 64L * 1024 * 1024, 1, 1024L * 1024 * 1024),
                BoundedLong(assets, "maximumTotalBytes", 256L * 1024 * 1024, 1, 4L * 1024 * 1024 * 1024),
                ReadRoutes(assets, "spaRoutePrefixes"), ReadRoutes(assets, "excludedPrefixes", ["/api", "/_neoastra"]), capabilities,
                root.TryGetProperty("bundle", out var bundle) ? NeoBundleConfiguration.Parse(bundle, directory, identifier, displayName) : null);
        }
        catch (NeoToolException) { throw; }
        catch (JsonException exception) { throw new NeoToolException("invalid_json", $"neoastra.json is invalid JSON at byte {exception.BytePositionInLine}."); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        { throw new NeoToolException("configuration_invalid", "neoastra.json is invalid."); }
    }

    private static Uri ValidateProductionOrigin(string value)
    {
        if (!ProductionOriginPattern.IsMatch(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" || string.IsNullOrEmpty(uri.Host) || uri.Scheme is "about" or "blob" or "data" or "file" or "ftp" or "http" or "https" or "javascript" or "ws" or "wss" || !value.Equals(uri.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal)) throw Error("assets.origin", "Production origin must be a canonical lowercase authority-based non-standard custom-scheme origin without path, query, fragment, or credentials.");
        return uri;
    }

    private static string ResolvePath(string root, string value, string field)
    {
        if (value.IndexOf('\0') >= 0) throw Error(field, "A path contains NUL.");
        var path = Path.GetFullPath(value, root);
        return path;
    }

    private static string? OptionalPath(JsonElement value, string name, string root) => OptionalString(value, name) is { } path ? ResolvePath(root, path, name) : null;
    private static NeoCommand Command(JsonElement value, string name) => OptionalCommand(value, name) ?? throw Error(name, "A non-empty command array is required.");
    private static NeoCommand? OptionalCommand(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var command)) return null;
        if (command.ValueKind != JsonValueKind.Array || command.GetArrayLength() is < 1 or > 256) throw Error(name, "A command must be an array of 1 through 256 arguments.");
        var arguments = command.EnumerateArray().Select((item, index) => item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 and <= 32768 } text ? text : throw Error($"{name}[{index}]", "Command arguments must be non-empty bounded strings.")).ToArray();
        return new(arguments);
    }

    private static Dictionary<string, string> ReadEnvironment(JsonElement frontend)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!frontend.TryGetProperty("environment", out var value)) return result;
        if (value.ValueKind != JsonValueKind.Object) throw Error("frontend.environment", "Environment must be an object.");
        foreach (var property in value.EnumerateObject())
        {
            if (result.Count == 128 || property.Name.Length > 1024 || !Regex.IsMatch(property.Name, "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant) || property.Value.ValueKind != JsonValueKind.String || property.Value.GetString()!.Length > 32768) throw Error("frontend.environment", "Environment additions are invalid or exceed their bounds.");
            result.Add(property.Name, property.Value.GetString()!);
        }
        return result;
    }

    private static IReadOnlyList<string> ReadRoutes(JsonElement value, string name, IReadOnlyList<string>? defaults = null)
    {
        if (!value.TryGetProperty(name, out var routes)) return defaults ?? [];
        var result = ReadStrings(value, name, 128, false).ToList();
        if (result.Any(route => !ValidRoute(route))) throw Error(name, "Route prefixes must be normalized absolute URL paths.");
        if (defaults is not null) foreach (var required in defaults) if (!result.Contains(required, StringComparer.Ordinal)) result.Add(required);
        if (result.Count > 128) throw Error(name, "Route prefixes exceed their bound after mandatory exclusions are applied.");
        return result;
    }

    private static bool ValidRoute(string route) => route.StartsWith('/') && (route == "/" || !route.EndsWith('/')) && !route.Contains('\\') && !route.Contains('?') && !route.Contains('#') && !route.Contains('\0') && !route.Contains(':') && !route.Contains("%2f", StringComparison.OrdinalIgnoreCase) && !route.Contains("%5c", StringComparison.OrdinalIgnoreCase) && !route.Contains("%2e", StringComparison.OrdinalIgnoreCase) && (route == "/" || route[1..].Split('/').All(static segment => segment is not ("" or "." or "..")));

    private static IReadOnlyList<string> ReadStrings(JsonElement value, string name, int maximum, bool paths)
    {
        if (!value.TryGetProperty(name, out var array)) return [];
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > maximum) throw Error(name, "Array is invalid or exceeds its bound.");
        var result = new List<string>(); var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= 1024 } text || !unique.Add(text) || (paths && text.IndexOf('\0') >= 0)) throw Error(name, "Array entries must be unique bounded strings.");
            result.Add(text);
        }
        return result;
    }

    private static string AssetPath(string value, string field)
    {
        var normalized = value.Replace('\\', '/').Normalize(NormalizationForm.FormC);
        if (normalized.StartsWith('/') || normalized.Contains(':') || normalized.Contains('\0') || normalized.Split('/').Any(segment => segment is "" or "." or "..")) throw Error(field, "Asset path must be a normalized relative path.");
        return normalized;
    }

    private static void Exact(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Error("object", "A configuration section must be an object.");
        var allowed = names.ToHashSet(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!allowed.Contains(property.Name)) throw Error(property.Name, "Unknown configuration field.");
    }
    private static JsonElement RequiredObject(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Object ? item : throw Error(name, "Required object is missing.");
    private static string String(JsonElement value, string name, int maximum = 1024) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text && text.Length <= maximum ? text : throw Error(name, "Required string is missing or exceeds its bound.");
    private static string? OptionalString(JsonElement value, string name, int maximum = 1024) => value.TryGetProperty(name, out var item) ? item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text && text.Length <= maximum ? text : throw Error(name, "Optional string is invalid.") : null;
    private static int Integer(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.TryGetInt32(out var number) ? number : throw Error(name, "Required integer is missing.");
    private static bool Boolean(JsonElement value, string name, bool fallback) => value.TryGetProperty(name, out var item) ? item.ValueKind is JsonValueKind.True or JsonValueKind.False ? item.GetBoolean() : throw Error(name, "Boolean is invalid.") : fallback;
    private static int BoundedInteger(JsonElement value, string name, int fallback, int minimum, int maximum) => checked((int)BoundedLong(value, name, fallback, minimum, maximum));
    private static long BoundedLong(JsonElement value, string name, long fallback, long minimum, long maximum) => value.TryGetProperty(name, out var item) ? item.TryGetInt64(out var number) && number >= minimum && number <= maximum ? number : throw Error(name, "Integer is outside its allowed range.") : fallback;
    private static string? Override(IReadOnlyDictionary<string, string>? values, string name) => values is not null && values.TryGetValue(name, out var value) && value.Length != 0 ? value : null;
    private static NeoToolException Error(string field, string message) => new("configuration_" + field.Replace('.', '_'), message);
    private static void ValidateComplexity(JsonElement root)
    {
        var nodes = 0; Visit(root);
        void Visit(JsonElement element)
        {
            if (++nodes > 50_000) throw Error("complexity", "Configuration exceeds its structural complexity bound.");
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw Error(property.Name, "Duplicate JSON property."); Visit(property.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Visit(item);
        }
    }
}

internal sealed class NeoToolException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}
