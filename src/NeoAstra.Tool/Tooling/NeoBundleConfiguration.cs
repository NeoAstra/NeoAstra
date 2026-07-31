// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NeoAstra.Tooling;

internal sealed record NeoBundleAssociation(
    string Extension,
    string MimeType,
    string Role);

internal sealed record NeoBundleUpdateConfiguration(
    string Channel,
    Uri Feed,
    string CurrentKeyId,
    IReadOnlyDictionary<string, string> PublicKeys,
    IReadOnlySet<string> RevokedKeys,
    string Mode);

internal sealed record NeoBundleConfiguration(
    string Identifier,
    string DisplayName,
    string Version,
    string NumericVersion,
    string Publisher,
    string Executable,
    IReadOnlyList<string> Icons,
    IReadOnlyList<string> RuntimeIdentifiers,
    IReadOnlyList<string> Targets,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Notices,
    IReadOnlyList<NeoBundleAssociation> FileAssociations,
    IReadOnlyList<string> UrlSchemes,
    IReadOnlyList<string> Entitlements,
    IReadOnlyList<string> RuntimeDependencies,
    string NotificationIdentity,
    string MinimumOsVersion,
    bool IncludeSymbols,
    NeoBundleUpdateConfiguration? Update)
{
    private static readonly Regex VersionPattern = new("^(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})(?:-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex ExecutablePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly Regex RidPattern = new("^(?:win|osx|linux)-(?:x64|arm64)$", RegexOptions.CultureInvariant);
    private static readonly Regex SchemePattern = new("^[a-z][a-z0-9+.-]{1,63}$", RegexOptions.CultureInvariant);
    internal static NeoBundleConfiguration Parse(JsonElement value, string root, string applicationIdentifier, string applicationName)
    {
        Exact(value, "identifier", "displayName", "version", "numericVersion", "publisher", "executable", "icons", "rids", "targets", "files", "notices", "fileAssociations", "urlSchemes", "entitlements", "runtimeDependencies", "notificationIdentity", "minimumOsVersion", "includeSymbols", "update");
        var identifier = String(value, "identifier", 255);
        var displayName = String(value, "displayName", 128);
        if (!identifier.Equals(applicationIdentifier, StringComparison.Ordinal) || !displayName.Equals(applicationName, StringComparison.Ordinal))
            throw Error("identity", "Bundle identity and display name must exactly match app metadata; identity changes affect data, notifications, launch routing, and updates.");
        var version = String(value, "version", 128);
        if (!VersionPattern.IsMatch(version))
            throw Error("version", "Bundle version must be canonical Semantic Versioning without leading zeroes.");
        var numericVersion = String(value, "numericVersion", 64);
        var numericParts = numericVersion.Split('.');
        if (numericParts.Length is < 3 or > 4 || numericParts.Any(static part => !ushort.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            throw Error("numericVersion", "Platform numeric version must contain three or four dot-separated unsigned 16-bit integers.");
        var executable = String(value, "executable", 128);
        if (!ExecutablePattern.IsMatch(executable))
            throw Error("executable", "Executable identity is invalid.");
        var icons = Paths(value, "icons", root, 32, required: true);
        var rids = Strings(value, "rids", 16, true);
        if (rids.Any(rid => !RidPattern.IsMatch(rid)))
            throw Error("rids", "Only explicit win/osx/linux x64/arm64 RIDs are supported.");
        var targets = Strings(value, "targets", 8, true);
        if (targets.Any(static target => target is not ("portable" or "installer")))
            throw Error("targets", "Bundle targets are portable and installer.");
        var files = RelativePaths(value, "files", 50_000, true);
        var notices = Paths(value, "notices", root, 128, required: true);
        var associations = Associations(value);
        var schemes = Strings(value, "urlSchemes", 32, false);
        if (schemes.Any(scheme => !SchemePattern.IsMatch(scheme)))
            throw Error("urlSchemes", "URL schemes must be canonical lowercase schemes.");
        var entitlements = Strings(value, "entitlements", 128, false);
        if (entitlements.Any(static item => item.Length > 256 || item.Any(char.IsControl)))
            throw Error("entitlements", "Entitlements are invalid.");
        var dependencies = Strings(value, "runtimeDependencies", 128, true);
        var notification = OptionalString(value, "notificationIdentity", 255) ?? identifier;
        if (!notification.Equals(identifier, StringComparison.Ordinal))
            throw Error("notificationIdentity", "Initial notification identity must equal the stable application identifier.");
        var update = value.TryGetProperty("update", out var updateValue) ? ParseUpdate(updateValue) : null;
        return new(identifier, displayName, version, numericVersion, String(value, "publisher", 256), executable, icons, rids, targets, files, notices, associations, schemes, entitlements, dependencies, notification, String(value, "minimumOsVersion", 64), Boolean(value, "includeSymbols", false), update);
    }

    internal void WriteInspect(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("bundle");
        writer.WriteString("identifier", Identifier);
        writer.WriteString("displayName", DisplayName);
        writer.WriteString("version", Version);
        writer.WriteString("numericVersion", NumericVersion);
        writer.WriteString("publisher", Publisher);
        writer.WriteString("executable", Executable);
        WriteArray(writer, "icons", Icons);
        WriteArray(writer, "rids", RuntimeIdentifiers);
        WriteArray(writer, "targets", Targets);
        WriteArray(writer, "files", Files);
        WriteArray(writer, "notices", Notices);
        WriteArray(writer, "urlSchemes", UrlSchemes);
        WriteArray(writer, "entitlements", Entitlements);
        WriteArray(writer, "runtimeDependencies", RuntimeDependencies);
        writer.WriteString("notificationIdentity", NotificationIdentity);
        writer.WriteString("minimumOsVersion", MinimumOsVersion);
        writer.WriteBoolean("includeSymbols", IncludeSymbols);
        writer.WriteStartArray("fileAssociations");
        foreach (var item in FileAssociations)
        {
            writer.WriteStartObject();
            writer.WriteString("extension", item.Extension);
            writer.WriteString("mimeType", item.MimeType);
            writer.WriteString("role", item.Role);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        if (Update is not null)
        {
            writer.WriteStartObject("update");
            writer.WriteString("channel", Update.Channel);
            writer.WriteString("feed", Update.Feed.AbsoluteUri);
            writer.WriteString("currentKeyId", Update.CurrentKeyId);
            writer.WriteString("mode", Update.Mode);
            writer.WriteStartArray("keyIds");
            foreach (var key in Update.PublicKeys.Keys.Order(StringComparer.Ordinal))
                writer.WriteStringValue(key);
            writer.WriteEndArray();
            writer.WriteStartArray("revokedKeyIds");
            foreach (var key in Update.RevokedKeys.Order(StringComparer.Ordinal))
                writer.WriteStringValue(key);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static NeoBundleUpdateConfiguration ParseUpdate(JsonElement value)
    {
        Exact(value, "channel", "feed", "currentKeyId", "publicKeys", "revokedKeyIds", "mode");
        var channel = String(value, "channel", 64);
        if (!Regex.IsMatch(channel, "^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant))
            throw Error("update.channel", "Update channel is invalid.");
        var feedText = String(value, "feed", 2048);
        if (!Uri.TryCreate(feedText, UriKind.Absolute, out var feed) || feed.Scheme != Uri.UriSchemeHttps || feed.UserInfo.Length != 0 || feed.Fragment.Length != 0)
            throw Error("update.feed", "Update feed must be an absolute HTTPS URL without credentials or fragment.");
        var keys = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!value.TryGetProperty("publicKeys", out var keyObject) || keyObject.ValueKind != JsonValueKind.Object)
            throw Error("update.publicKeys", "Pinned update public keys are required.");
        foreach (var property in keyObject.EnumerateObject())
        {
            if (!Regex.IsMatch(property.Name, "^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant) || property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not { } encoded)
                throw Error("update.publicKeys", "Pinned key map is invalid.");
            try
            {
                var key = Convert.FromBase64String(encoded);
                using var algorithm = System.Security.Cryptography.ECDsa.Create();
                algorithm.ImportSubjectPublicKeyInfo(key, out var read);
                if (read != key.Length || algorithm.KeySize != 256)
                    throw new FormatException();
            }
            catch (Exception exception) when (exception is FormatException or System.Security.Cryptography.CryptographicException)
            {
                throw Error("update.publicKeys", "Pinned keys must be base64-encoded ECDSA P-256 SubjectPublicKeyInfo values.");
            }

            if (keys.Count == 16 || !keys.TryAdd(property.Name, encoded))
                throw Error("update.publicKeys", "Pinned key map exceeds its bound or contains duplicates.");
        }

        var revoked = Strings(value, "revokedKeyIds", 16, false).ToHashSet(StringComparer.Ordinal);
        if (revoked.Any(key => !keys.ContainsKey(key)))
            throw Error("update.revokedKeyIds", "Every revoked key must be pinned.");
        var current = String(value, "currentKeyId", 64);
        if (!keys.ContainsKey(current) || revoked.Contains(current))
            throw Error("update.currentKeyId", "Current key must be pinned and not revoked.");
        var mode = OptionalString(value, "mode", 32) ?? "experimental";
        if (mode is not ("disabled" or "experimental" or "store"))
            throw Error("update.mode", "Update mode must be disabled, experimental, or store; self-update cannot yet be advertised as available.");
        return new(channel, feed, current, keys, revoked, mode);
    }

    private static IReadOnlyList<NeoBundleAssociation> Associations(JsonElement value)
    {
        if (!value.TryGetProperty("fileAssociations", out var array))
            return [];
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 128)
            throw Error("fileAssociations", "File associations exceed their bound.");
        var result = new List<NeoBundleAssociation>();
        var extensions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            Exact(item, "extension", "mimeType", "role");
            var extension = String(item, "extension", 32);
            var mime = String(item, "mimeType", 128);
            var role = String(item, "role", 16);
            if (!Regex.IsMatch(extension, "^\\.[a-z0-9][a-z0-9._-]{0,30}$", RegexOptions.CultureInvariant) || !Regex.IsMatch(mime, "^[a-z0-9][a-z0-9!#$&^_.+-]{0,63}/[a-z0-9][a-z0-9!#$&^_.+-]{0,63}$", RegexOptions.CultureInvariant) || role is not ("viewer" or "editor") || !extensions.Add(extension))
                throw Error("fileAssociations", "File association is invalid or duplicated.");
            result.Add(new(extension, mime, role));
        }

        return result;
    }

    private static IReadOnlyList<string> Paths(JsonElement value, string name, string root, int maximum, bool required) => Strings(value, name, maximum, required).Select(path => Path.GetFullPath(path, root)).ToArray();
    private static IReadOnlyList<string> RelativePaths(JsonElement value, string name, int maximum, bool required)
    {
        var result = Strings(value, name, maximum, required).Select(NormalizeRelative).ToArray();
        if (result.Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw Error(name, "Paths collide after canonical normalization.");
        return result;
    }

    internal static string NormalizeRelative(string path)
    {
        var result = path.Replace('\\', '/').Normalize(NormalizationForm.FormC);
        if (result.StartsWith('/') || result.Contains(':') || result.Contains('\0') || result.Split('/').Any(static segment => segment is "" or "." or ".."))
            throw Error("path", "A bundle path is not a canonical relative path.");
        return result;
    }

    private static IReadOnlyList<string> Strings(JsonElement value, string name, int maximum, bool required)
    {
        if (!value.TryGetProperty(name, out var array))
        {
            if (required)
                throw Error(name, "Required array is missing.");
            return [];
        }

        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is 0 && required || array.GetArrayLength() > maximum)
            throw Error(name, "Array is invalid or exceeds its bound.");
        var result = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= 2048 } text || !unique.Add(text))
                throw Error(name, "Array entries must be unique bounded strings.");
            result.Add(text);
        }

        return result;
    }

    private static void Exact(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Error("object", "Bundle section must be an object.");
        var allowed = names.ToHashSet(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name))
                throw Error(property.Name, "Unknown bundle field.");
    }

    private static string String(JsonElement value, string name, int maximum) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text && text.Length <= maximum && !text.Any(char.IsControl) ? text : throw Error(name, "Required bounded string is invalid.");
    private static string? OptionalString(JsonElement value, string name, int maximum) => value.TryGetProperty(name, out var item) ? item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text && text.Length <= maximum && !text.Any(char.IsControl) ? text : throw Error(name, "Optional bounded string is invalid.") : null;
    private static bool Boolean(JsonElement value, string name, bool fallback) => value.TryGetProperty(name, out var item) ? item.ValueKind is JsonValueKind.True or JsonValueKind.False ? item.GetBoolean() : throw Error(name, "Boolean is invalid.") : fallback;
    private static NeoToolException Error(string field, string message) => new("bundle_configuration_" + field.Replace('.', '_'), message);
    private static void WriteArray(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
            writer.WriteStringValue(value);
        writer.WriteEndArray();
    }
}