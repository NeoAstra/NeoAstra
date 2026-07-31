// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Buffers;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NeoAstra.Tooling;

internal sealed record NeoUpdateArtifact(
    Uri Url,
    long Length,
    string Sha256,
    string Format,
    string Signature,
    string RuntimeIdentifier);

internal sealed record NeoVerifiedUpdateManifest(
    int SchemaVersion,
    string ApplicationId,
    string Channel,
    Version Version,
    long Build,
    DateTimeOffset ReleasedAt,
    Version MinimumUpdaterVersion,
    Version MinimumAppVersion,
    Version MaximumAppVersion,
    string SigningKeyId,
    NeoUpdateArtifact Artifact,
    int RolloutPercent,
    string? ReleaseNotesUrl,
    byte[] CanonicalBytes);

internal sealed record NeoUpdateClientPolicy(
    string ApplicationId,
    string Channel,
    string RuntimeIdentifier,
    Version CurrentVersion,
    long CurrentBuild,
    Version UpdaterVersion,
    Uri Feed,
    IReadOnlyDictionary<string, byte[]> PublicKeys,
    IReadOnlySet<string> RevokedKeys,
    bool AllowDevelopmentDowngrade = false,
    long MaximumManifestBytes = 1024 * 1024,
    long MaximumArtifactBytes = 2L * 1024 * 1024 * 1024,
    bool IsStoreManaged = false,
    string? RolloutIdentity = null);

internal sealed record NeoUpdateHandoff(
    string ApplicationId,
    string Version,
    string ArtifactPath,
    string ArtifactSha256,
    int Attempt,
    string State,
    string PreviousPath);
internal static class NeoUpdateManifestVerifier
{
    internal static NeoVerifiedUpdateManifest Verify(ReadOnlySpan<byte> bytes, NeoUpdateClientPolicy policy)
    {
        if (policy.IsStoreManaged)
            throw Error("store_managed", "Direct updates are disabled for store-managed installations.");
        if (bytes.Length is < 2 || bytes.Length > policy.MaximumManifestBytes)
            throw Error("manifest_size", "Update manifest exceeds its authenticated parsing bound.");
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            ValidateComplexity(document.RootElement);
            var root = document.RootElement;
            Exact(root, "schemaVersion", "applicationId", "channel", "version", "build", "releasedAt", "minimumUpdaterVersion", "minimumAppVersion", "maximumAppVersion", "signingKeyId", "artifacts", "rolloutPercent", "releaseNotesUrl", "signature");
            var schema = Integer(root, "schemaVersion");
            if (schema != 1)
                throw Error("schema", "Unknown update schema or critical fields fail closed.");
            var application = String(root, "applicationId", 255);
            var channel = String(root, "channel", 64);
            var version = ParseVersion(String(root, "version", 64), "version");
            var build = Long(root, "build", 0, long.MaxValue);
            var released = Date(root, "releasedAt");
            var minimumUpdater = ParseVersion(String(root, "minimumUpdaterVersion", 64), "minimumUpdaterVersion");
            var minimumApp = ParseVersion(String(root, "minimumAppVersion", 64), "minimumAppVersion");
            var maximumApp = ParseVersion(String(root, "maximumAppVersion", 64), "maximumAppVersion");
            var keyId = String(root, "signingKeyId", 64);
            var rollout = root.TryGetProperty("rolloutPercent", out var rolloutValue) && rolloutValue.TryGetInt32(out var percent) && percent is >= 0 and <= 100 ? percent : throw Error("rollout", "Rollout percent is invalid.");
            var notes = OptionalString(root, "releaseNotesUrl", 2048);
            if (notes is not null)
                ValidateHttps(new Uri(notes, UriKind.Absolute), policy.Feed, allowPathChange: true);
            var artifacts = RequiredArray(root, "artifacts", 32);
            NeoUpdateArtifact? selected = null;
            foreach (var item in artifacts.EnumerateArray())
            {
                var artifact = ParseArtifact(item, policy);
                if (artifact.RuntimeIdentifier == policy.RuntimeIdentifier)
                {
                    if (selected is not null)
                        throw Error("artifact_duplicate", "Manifest has duplicate RID artifacts.");
                    selected = artifact;
                }
            }

            if (selected is null)
                throw Error("wrong_rid", "Manifest has no artifact for the exact runtime identifier.");
            if (!policy.PublicKeys.TryGetValue(keyId, out var key) || policy.RevokedKeys.Contains(keyId))
                throw Error("key", "Manifest signing key is unknown or revoked.");
            var canonical = Canonical(root, includeSignature: false);
            var signature = DecodeRange(String(root, "signature", 256), 64, 80, "signature");
            if (!VerifySignature(signature, canonical, key))
                throw Error("signature", "Manifest signature verification failed.");
            ValidatePolicy(application, channel, version, build, minimumUpdater, minimumApp, maximumApp, policy);
            if (rollout < 100)
            {
                if (string.IsNullOrWhiteSpace(policy.RolloutIdentity) || policy.RolloutIdentity.Length > 255 || policy.RolloutIdentity.Any(char.IsControl))
                    throw Error("rollout_identity", "A bounded backend-owned stable rollout identity is required.");
                var bucketBytes = SHA256.HashData(Encoding.UTF8.GetBytes(application + "\n" + channel + "\n" + build.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + policy.RolloutIdentity));
                var bucket = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bucketBytes) % 100;
                if (bucket >= rollout)
                    throw Error("rollout", "This installation is outside the deterministic staged rollout cohort.");
            }

            return new(schema, application, channel, version, build, released, minimumUpdater, minimumApp, maximumApp, keyId, selected, rollout, notes, canonical);
        }
        catch (NeoToolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or OverflowException)
        {
            throw Error("invalid", "Update manifest is not valid bounded canonical data.");
        }
    }

    internal static byte[] CanonicalForSigning(ReadOnlySpan<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        ValidateComplexity(document.RootElement);
        return Canonical(document.RootElement, includeSignature: false);
    }

    private static NeoUpdateArtifact ParseArtifact(JsonElement value, NeoUpdateClientPolicy policy)
    {
        Exact(value, "rid", "url", "length", "sha256", "format", "signature");
        var rid = String(value, "rid", 32);
        var url = new Uri(String(value, "url", 2048), UriKind.Absolute);
        ValidateHttps(url, policy.Feed, allowPathChange: true);
        var length = Long(value, "length", 1, policy.MaximumArtifactBytes);
        var hash = String(value, "sha256", 64);
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || hash.Any(char.IsUpper))
            throw Error("artifact_hash", "Artifact hash must be lowercase SHA-256.");
        var format = String(value, "format", 32);
        var allowed = rid.StartsWith("win-", StringComparison.Ordinal) ? new[]
        {
            "zip",
            "msix"
        }

        : rid.StartsWith("osx-", StringComparison.Ordinal) ? new[]
        {
            "zip",
            "dmg",
            "pkg"
        }

        : new[]
        {
            "tar.gz",
            "deb"
        };
        if (!allowed.Contains(format, StringComparer.Ordinal))
            throw Error("artifact_format", "Artifact format is incompatible with its RID.");
        var signature = String(value, "signature", 256);
        _ = DecodeRange(signature, 64, 80, "artifact.signature");
        return new(url, length, hash, format, signature, rid);
    }

    private static void ValidatePolicy(string application, string channel, Version version, long build, Version minimumUpdater, Version minimumApp, Version maximumApp, NeoUpdateClientPolicy policy)
    {
        if (application != policy.ApplicationId)
            throw Error("wrong_app", "Manifest application identity does not match.");
        if (channel != policy.Channel)
            throw Error("wrong_channel", "Manifest channel does not match.");
        if (minimumUpdater > policy.UpdaterVersion)
            throw Error("updater_version", "Updater is below the authenticated minimum.");
        if (policy.CurrentVersion < minimumApp || policy.CurrentVersion > maximumApp)
            throw Error("upgrade_range", "Installed version is outside the authenticated upgrade range.");
        var comparison = version.CompareTo(policy.CurrentVersion);
        if (comparison < 0 || comparison == 0 && build <= policy.CurrentBuild)
        {
            if (!policy.AllowDevelopmentDowngrade || policy.Channel is "stable" or "beta")
                throw Error("replay_downgrade", "Replay, same-build, and downgrade manifests are denied.");
        }
    }

    internal static void ValidateHttps(Uri uri, Uri feed, bool allowPathChange)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !uri.IdnHost.Equals(feed.IdnHost, StringComparison.OrdinalIgnoreCase) || uri.Port != feed.Port)
            throw Error("url_policy", "Update URLs and redirects require HTTPS and the exact pinned feed host/port without credentials or fragments.");
        if (!allowPathChange && uri.AbsolutePath != feed.AbsolutePath)
            throw Error("redirect_policy", "Manifest redirects cannot change the canonical feed path.");
    }

    private static byte[] Canonical(JsonElement root, bool includeSignature)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, Encoder = JavaScriptEncoder.Default }))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject().Where(property => includeSignature || property.Name != "signature").OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var number))
                    writer.WriteNumberValue(number);
                else
                    throw Error("number", "Floating-point manifest values are forbidden.");
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw Error("value", "Unsupported manifest value.");
        }
    }

    private static void ValidateComplexity(JsonElement root)
    {
        var nodes = 0;
        Visit(root);
        void Visit(JsonElement value)
        {
            if (++nodes > 4096)
                throw Error("complexity", "Manifest exceeds structural bounds.");
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw Error("duplicate", "Duplicate manifest properties are forbidden.");
                    Visit(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray())
                    Visit(item);
        }
    }

    private static JsonElement RequiredArray(JsonElement root, string name, int maximum) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() is > 0 && value.GetArrayLength() <= maximum ? value : throw Error(name, "Required bounded array is invalid.");
    private static string String(JsonElement root, string name, int maximum) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text && text.Length <= maximum && !text.Any(char.IsControl) ? text : throw Error(name, "Required bounded string is invalid.");
    private static string? OptionalString(JsonElement root, string name, int maximum) => root.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text && text.Length <= maximum && !text.Any(char.IsControl) ? text : throw Error(name, "Optional bounded string is invalid.") : null;
    private static int Integer(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : throw Error(name, "Required integer is invalid.");
    private static long Long(JsonElement root, string name, long minimum, long maximum) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) && result >= minimum && result <= maximum ? result : throw Error(name, "Integer is outside its bound.");
    private static DateTimeOffset Date(JsonElement root, string name) => DateTimeOffset.TryParseExact(String(root, name, 64), "yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var value) ? value : throw Error(name, "Timestamp must be canonical UTC seconds.");
    private static Version ParseVersion(string value, string name) => Version.TryParse(value, out var version) && version.Revision >= 0 ? version : throw Error(name, "Update versions must be four-part numeric versions.");
    private static byte[] Decode(string value, int length, string name)
    {
        try
        {
            var result = Convert.FromBase64String(value);
            return result.Length == length ? result : throw Error(name, "Encoded value has the wrong length.");
        }
        catch (FormatException)
        {
            throw Error(name, "Encoded value is not canonical base64.");
        }
    }

    private static byte[] DecodeRange(string value, int minimum, int maximum, string name)
    {
        try
        {
            var result = Convert.FromBase64String(value);
            return result.Length is >= 64 && result.Length <= maximum ? result : throw Error(name, "Encoded value has the wrong length.");
        }
        catch (FormatException)
        {
            throw Error(name, "Encoded value is not canonical base64.");
        }
    }

    private static bool VerifySignature(ReadOnlySpan<byte> signature, ReadOnlySpan<byte> data, ReadOnlySpan<byte> publicKey)
    {
        try
        {
            using var algorithm = ECDsa.Create();
            algorithm.ImportSubjectPublicKeyInfo(publicKey, out var read);
            return read == publicKey.Length && algorithm.KeySize == 256 && algorithm.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static void Exact(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Error("object", "Manifest object is invalid.");
        var allowed = names.ToHashSet(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name))
                throw Error("critical_field", "Unknown manifest fields fail closed.");
    }

    private static NeoToolException Error(string code, string message) => new("update_" + code, message);
}

internal sealed class NeoAuthenticatedDownloader
{
    internal async Task<byte[]> DownloadManifestAsync(NeoUpdateClientPolicy policy, CancellationToken cancellationToken)
    {
        if (policy.IsStoreManaged)
            throw new NeoToolException("update_store_managed", "Direct updates are disabled for store-managed installations.");
        NeoUpdateManifestVerifier.ValidateHttps(policy.Feed, policy.Feed, allowPathChange: false);
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 1
        };
        var uri = policy.Feed;
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (redirects == 3 || response.Headers.Location is not { } location)
                    throw new NeoToolException("update_redirect", "Manifest redirect limit or location is invalid.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                NeoUpdateManifestVerifier.ValidateHttps(uri, policy.Feed, allowPathChange: false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw new NeoToolException("update_http", "Update manifest download failed.");
            if (response.Content.Headers.ContentLength is { } length && length > policy.MaximumManifestBytes)
                throw new NeoToolException("update_manifest_size", "Update manifest exceeds its download bound.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) != 0)
                {
                    if (output.Length + read > policy.MaximumManifestBytes)
                        throw new NeoToolException("update_manifest_size", "Update manifest exceeds its download bound.");
                    output.Write(buffer, 0, read);
                }

                return output.ToArray();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        throw new NeoToolException("update_redirect", "Manifest redirect limit exceeded.");
    }

    internal async Task<string> DownloadAsync(NeoVerifiedUpdateManifest manifest, NeoUpdateClientPolicy policy, string ownedDirectory, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(ownedDirectory);
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new NeoToolException("update_download_root", "Update download root cannot be a link/reparse point.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10),
            MaxResponseContentBufferSize = 1
        };
        var temporary = Path.Combine(root, ".download-" + Guid.NewGuid().ToString("N") + ".tmp");
        var destination = Path.Combine(root, manifest.Artifact.Sha256 + "." + manifest.Artifact.Format.Replace('.', '-'));
        try
        {
            var uri = manifest.Artifact.Url;
            HttpResponseMessage? response = null;
            for (var redirects = 0; redirects <= 3; redirects++)
            {
                response?.Dispose();
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var location = response.Headers.Location;
                    if (location is null)
                        throw new NeoToolException("update_redirect", "Update redirect lacks a location.");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    NeoUpdateManifestVerifier.ValidateHttps(uri, policy.Feed, allowPathChange: true);
                    continue;
                }

                break;
            }

            using (response)
            {
                if (response is null || !response.IsSuccessStatusCode)
                    throw new NeoToolException("update_http", "Authenticated update download failed.");
                if (response.Content.Headers.ContentLength is { } length && length != manifest.Artifact.Length)
                    throw new NeoToolException("update_length", "Artifact Content-Length differs from the signed length.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                long total = 0;
                try
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) != 0)
                    {
                        total += read;
                        if (total > manifest.Artifact.Length || total > policy.MaximumArtifactBytes)
                            throw new NeoToolException("update_oversize", "Artifact exceeded its signed byte bound.");
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }

                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (total != manifest.Artifact.Length || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != manifest.Artifact.Sha256)
                        throw new NeoToolException("update_hash", "Artifact is truncated or its SHA-256 differs from authenticated metadata.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(buffer);
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            var artifactSignature = Convert.FromBase64String(manifest.Artifact.Signature);
            var key = policy.PublicKeys[manifest.SigningKeyId];
            var digest = Convert.FromHexString(manifest.Artifact.Sha256);
            using var algorithm = ECDsa.Create();
            algorithm.ImportSubjectPublicKeyInfo(key, out var readKey);
            if (readKey != key.Length || !algorithm.VerifyHash(digest, artifactSignature))
                throw new NeoToolException("update_artifact_signature", "Independent artifact signature verification failed.");
            File.Move(temporary, destination, true);
            return destination;
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
            }

            throw;
        }
    }
}

internal static class NeoAuthenticatedPackageExtractor
{
    private const int MaximumFiles = 50_000;
    private const long MaximumExpandedBytes = 4L * 1024 * 1024 * 1024;
    internal static string ExtractPortable(NeoVerifiedUpdateManifest manifest, string authenticatedArtifact, string ownedDirectory)
    {
        if (manifest.Artifact.Format is not ("zip" or "tar.gz"))
            throw new NeoToolException("update_package_manager", "Native installer updates require the reviewed platform package manager/helper and are never treated as portable archives.");
        var artifact = Path.GetFullPath(authenticatedArtifact);
        if (!File.Exists(artifact) || Hash(artifact) != manifest.Artifact.Sha256)
            throw new NeoToolException("update_package_hash", "Package extraction requires the already authenticated artifact digest.");
        var root = Path.GetFullPath(ownedDirectory);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new NeoToolException("update_extract_root", "Extraction root must be an empty backend-owned directory.");
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new NeoToolException("update_extract_root", "Extraction root cannot be a link/reparse point.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        long expanded = 0;
        if (manifest.Artifact.Format == "zip")
        {
            using var archive = ZipFile.OpenRead(artifact);
            foreach (var entry in archive.Entries)
            {
                var relative = PathValue(entry.FullName, paths, ref count);
                if (entry.ExternalAttributes >> 16 is var mode && (mode & 0xf000) == 0xa000)
                    throw new NeoToolException("update_package_link", "Portable package links are forbidden.");
                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(Destination(root, relative));
                    continue;
                }

                expanded = checked(expanded + entry.Length);
                if (expanded > MaximumExpandedBytes)
                    throw new NeoToolException("update_package_size", "Expanded package exceeds its bound.");
                var destination = Destination(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using (var input = entry.Open())
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
                ApplyUnixMode(destination, mode);
            }
        }
        else
        {
            using var compressed = new GZipStream(File.OpenRead(artifact), CompressionMode.Decompress);
            using var reader = new TarReader(compressed);
            TarEntry? entry;
            while ((entry = reader.GetNextEntry(copyData: false)) is not null)
            {
                var relative = PathValue(entry.Name, paths, ref count);
                if (entry.EntryType is TarEntryType.Directory)
                {
                    Directory.CreateDirectory(Destination(root, relative));
                    continue;
                }

                if (entry.EntryType is not TarEntryType.RegularFile || entry.DataStream is null)
                    throw new NeoToolException("update_package_link", "Portable package links and special files are forbidden.");
                var destination = Destination(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[64 * 1024];
                    int read;
                    while ((read = entry.DataStream.Read(buffer)) != 0)
                    {
                        expanded = checked(expanded + read);
                        if (expanded > MaximumExpandedBytes)
                            throw new NeoToolException("update_package_size", "Expanded package exceeds its bound.");
                        output.Write(buffer, 0, read);
                    }
                }
                ApplyUnixMode(destination, (int)entry.Mode);
            }
        }

        var identities = Directory.EnumerateFiles(root, "neoastra-package.json", SearchOption.AllDirectories).ToArray();
        if (identities.Length != 1)
            throw new NeoToolException("update_package_identity", "Portable package must contain exactly one identity root.");
        var packageRoot = Path.GetDirectoryName(identities[0])!;
        if (Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(path => !path.StartsWith(packageRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            throw new NeoToolException("update_package_identity", "Portable package contains files outside its single identity root.");
        VerifyIdentity(packageRoot, manifest);
        return packageRoot;
    }

    private static void ApplyUnixMode(string path, int mode)
    {
        var permissions = mode & 0x1ff;
        if (!OperatingSystem.IsWindows() && permissions != 0)
            File.SetUnixFileMode(path, (UnixFileMode)permissions);
    }

    private static string PathValue(string value, HashSet<string> paths, ref int count)
    {
        if (++count > MaximumFiles)
            throw new NeoToolException("update_package_count", "Package file count exceeds its bound.");
        string relative;
        try
        {
            relative = NeoBundleConfiguration.NormalizeRelative(value.TrimEnd('/'));
        }
        catch (NeoToolException)
        {
            throw new NeoToolException("update_package_path", "Package contains a non-canonical or traversing path.");
        }

        if (!paths.Add(relative))
            throw new NeoToolException("update_package_collision", "Package paths collide under portable normalized comparison.");
        return relative;
    }

    private static string Destination(string root, string relative)
    {
        var destination = Path.GetFullPath(relative.Replace('/', Path.DirectorySeparatorChar), root);
        if (!destination.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new NeoToolException("update_package_path", "Package destination escaped its owned root.");
        return destination;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void VerifyIdentity(string root, NeoVerifiedUpdateManifest manifest)
    {
        var path = Path.Combine(root, "neoastra-package.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024)
            throw new NeoToolException("update_package_identity", "Portable package identity metadata is missing or oversized.");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 4 });
            var value = document.RootElement;
            var names = value.EnumerateObject().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);
            var hasExpectedProperties = names.SetEquals(
                ["schemaVersion", "applicationId", "version", "rid", "executable"]);
            var executable = value.GetProperty("executable").GetString();
            var hasMatchingIdentity = value.GetProperty("schemaVersion").GetInt32() == 1
                && value.GetProperty("applicationId").GetString() == manifest.ApplicationId
                && value.GetProperty("version").GetString() == manifest.Version.ToString()
                && value.GetProperty("rid").GetString() == manifest.Artifact.RuntimeIdentifier;
            var hasValidExecutable = executable is { Length: > 0 and <= 255 }
                && !executable.Any(char.IsControl)
                && Path.GetFileName(executable) == executable;
            if (!hasExpectedProperties || !hasMatchingIdentity || !hasValidExecutable)
                throw new NeoToolException("update_package_identity", "Portable package identity does not match authenticated update metadata.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new NeoToolException("update_package_identity", "Portable package identity metadata is invalid.");
        }
    }
}

internal static class NeoAtomicUpdateInstaller
{
    internal static NeoUpdateHandoff Prepare(string applicationId, string version, string artifact, string installDirectory, string stateDirectory)
    {
        ValidateOwned(applicationId, artifact, installDirectory, stateDirectory);
        Directory.CreateDirectory(stateDirectory);
        var stateFile = Path.Combine(stateDirectory, "handoff.json");
        var previousAttempt = Read(stateFile);
        var previousFailedAttempt = previousAttempt is not null && previousAttempt.State != "healthy" ? previousAttempt.Attempt : 0;
        if (previousFailedAttempt >= 2)
            throw new NeoToolException("update_rollback_loop", "Repeated failed update loop was stopped after two authenticated attempts.");
        var hash = Hash(artifact);
        var handoff = new NeoUpdateHandoff(applicationId, version, Path.GetFullPath(artifact), hash, previousFailedAttempt + 1, "pending", Path.GetFullPath(installDirectory) + ".previous");
        WriteAtomic(stateFile, handoff);
        return handoff;
    }

    internal static void InstallAuthenticatedPayload(NeoUpdateHandoff handoff, string extractedPayload, string installDirectory, string stateDirectory, int? applicationProcessId = null)
    {
        if (applicationProcessId is { } processId)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(processId);
                if (!process.HasExited)
                    throw new NeoToolException("update_quit_handoff", "Update helper must wait for the normal application quit handoff.");
            }
            catch (ArgumentException)
            {
            }
        }

        var install = Path.GetFullPath(installDirectory);
        var payload = Path.GetFullPath(extractedPayload);
        var state = Path.GetFullPath(stateDirectory);
        ValidateOwned(handoff.ApplicationId, handoff.ArtifactPath, install, state);
        if (Overlaps(payload, install) || Overlaps(payload, state))
            throw new NeoToolException("update_path_overlap", "Payload, install, and state roots must be disjoint.");
        if (!Directory.Exists(payload) || ContainsLink(payload))
            throw new NeoToolException("update_payload", "Authenticated extracted payload must be a real link-free directory.");
        if (Hash(handoff.ArtifactPath) != handoff.ArtifactSha256)
            throw new NeoToolException("update_artifact_changed", "Authenticated artifact changed before helper installation.");
        var previous = Path.GetFullPath(handoff.PreviousPath);
        if (previous != install + ".previous")
            throw new NeoToolException("update_state_path", "Handoff previous path is not the fixed application backup path.");
        if (Directory.Exists(previous))
            Directory.Delete(previous, true);
        WriteAtomic(Path.Combine(state, "handoff.json"), handoff with { State = "switching" });
        if (Directory.Exists(install))
            Directory.Move(install, previous);
        try
        {
            Directory.Move(payload, install);
            WriteAtomic(Path.Combine(state, "handoff.json"), handoff with { State = "installed" });
        }
        catch
        {
            if (!Directory.Exists(install) && Directory.Exists(previous))
                Directory.Move(previous, install);
            WriteAtomic(Path.Combine(state, "handoff.json"), handoff with { State = "rolledBack" });
            throw;
        }
    }

    internal static void MarkHealthy(string installDirectory, string stateDirectory)
    {
        var path = Path.Combine(Path.GetFullPath(stateDirectory), "handoff.json");
        var state = Read(path) ?? throw new NeoToolException("update_state", "Update handoff is missing.");
        var install = Path.GetFullPath(installDirectory);
        if (Path.GetFullPath(state.PreviousPath) != install + ".previous")
            throw new NeoToolException("update_state_path", "Health cleanup backup path is not the fixed application backup path.");
        if (state.State != "installed")
            throw new NeoToolException("update_state", "Only an installed update can become healthy.");
        WriteAtomic(path, state with { State = "healthy", Attempt = 0 });
        if (Directory.Exists(state.PreviousPath))
            Directory.Delete(state.PreviousPath, true);
        Cleanup(stateDirectory);
    }

    internal static void Rollback(string installDirectory, string stateDirectory)
    {
        var path = Path.Combine(Path.GetFullPath(stateDirectory), "handoff.json");
        var state = Read(path) ?? throw new NeoToolException("update_state", "Update handoff is missing.");
        var install = Path.GetFullPath(installDirectory);
        if (Path.GetFullPath(state.PreviousPath) != install + ".previous")
            throw new NeoToolException("update_state_path", "Rollback backup path is not the fixed application backup path.");
        if (state.State is not ("installed" or "pending") || !Directory.Exists(state.PreviousPath))
            throw new NeoToolException("update_rollback", "No previously authenticated installation is available for rollback.");
        var failed = install + ".failed";
        if (Directory.Exists(failed))
            Directory.Delete(failed, true);
        if (Directory.Exists(install))
            Directory.Move(install, failed);
        Directory.Move(state.PreviousPath, install);
        WriteAtomic(path, state with { State = "rolledBack" });
        if (Directory.Exists(failed))
            Directory.Delete(failed, true);
    }

    internal static bool RequiresRollback(string stateDirectory, TimeSpan healthTimeout, DateTimeOffset now)
    {
        var path = Path.Combine(Path.GetFullPath(stateDirectory), "handoff.json");
        var state = Read(path);
        return state?.State == "installed" && now - File.GetLastWriteTimeUtc(path) > healthTimeout;
    }

    internal static void RecoverInterrupted(string installDirectory, string stateDirectory)
    {
        var path = Path.Combine(Path.GetFullPath(stateDirectory), "handoff.json");
        var state = Read(path);
        if (state?.State != "switching")
            return;
        var install = Path.GetFullPath(installDirectory);
        if (Path.GetFullPath(state.PreviousPath) != install + ".previous")
            throw new NeoToolException("update_state_path", "Interrupted backup path is not the fixed application backup path.");
        if (Directory.Exists(state.PreviousPath))
        {
            var failed = install + ".interrupted";
            if (Directory.Exists(failed))
                Directory.Delete(failed, true);
            if (Directory.Exists(install))
                Directory.Move(install, failed);
            Directory.Move(state.PreviousPath, install);
            if (Directory.Exists(failed))
                Directory.Delete(failed, true);
        }

        WriteAtomic(path, state with { State = "rolledBack" });
    }

    private static void Cleanup(string stateDirectory)
    {
        foreach (var file in Directory.EnumerateFiles(stateDirectory, ".download-*.tmp"))
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(1))
                    File.Delete(file);
            }
            catch
            {
            }
    }

    private static void ValidateOwned(string applicationId, string artifact, string install, string state)
    {
        if (string.IsNullOrEmpty(applicationId) || applicationId.Length > 255 || applicationId.Any(char.IsControl))
            throw new NeoToolException("update_app", "Application identity is invalid.");
        if (!Path.IsPathFullyQualified(artifact) || !Path.IsPathFullyQualified(install) || !Path.IsPathFullyQualified(state) || Path.GetFullPath(install) == Path.GetPathRoot(install) || Path.GetFullPath(state) == Path.GetPathRoot(state) || Overlaps(Path.GetFullPath(install), Path.GetFullPath(state)) || IsBelow(Path.GetFullPath(artifact), Path.GetFullPath(install)))
            throw new NeoToolException("update_path", "Updater accepts only explicit disjoint non-root canonical backend-owned paths.");
    }

    private static bool ContainsLink(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            return true;
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return true;
        return false;
    }

    private static bool Overlaps(string left, string right) => IsBelow(left, right) || IsBelow(right, left);
    private static bool IsBelow(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return fullPath.Equals(fullRoot, comparison) || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static NeoUpdateHandoff? Read(string path)
    {
        if (!File.Exists(path))
            return null;
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 64 * 1024)
            throw new NeoToolException("update_state", "Update handoff exceeds its bound.");
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            var names = root.EnumerateObject().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);
            if (!names.SetEquals(["schemaVersion", "applicationId", "version", "artifactPath", "artifactSha256", "attempt", "state", "previousPath"]) || root.GetProperty("schemaVersion").GetInt32() != 1)
                throw new JsonException();
            var applicationId = root.GetProperty("applicationId").GetString();
            var version = root.GetProperty("version").GetString();
            var artifactPath = root.GetProperty("artifactPath").GetString();
            var artifactSha256 = root.GetProperty("artifactSha256").GetString();
            var attempt = root.GetProperty("attempt").GetInt32();
            var state = root.GetProperty("state").GetString();
            var previousPath = root.GetProperty("previousPath").GetString();
            if (applicationId is not { Length: > 0 and <= 255 }
                || applicationId.Any(char.IsControl))
                throw new JsonException();
            if (version is not { Length: > 0 and <= 64 }
                || version.Any(char.IsControl))
                throw new JsonException();
            if (artifactPath is not { Length: > 0 and <= 32_768 }
                || !Path.IsPathFullyQualified(artifactPath))
                throw new JsonException();
            if (artifactSha256 is not { Length: 64 }
                || !artifactSha256.All(Uri.IsHexDigit)
                || artifactSha256.Any(char.IsUpper))
                throw new JsonException();
            if (attempt is < 0 or > 2
                || state is not ("pending" or "switching" or "installed" or "healthy" or "rolledBack"))
                throw new JsonException();
            if (previousPath is not { Length: > 0 and <= 32_768 }
                || !Path.IsPathFullyQualified(previousPath))
                throw new JsonException();

            return new(applicationId, version, artifactPath, artifactSha256, attempt, state, previousPath);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new NeoToolException("update_state", "Update handoff is corrupt.");
        }
    }

    private static void WriteAtomic(string path, NeoUpdateHandoff state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".write-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("applicationId", state.ApplicationId);
            writer.WriteString("version", state.Version);
            writer.WriteString("artifactPath", state.ArtifactPath);
            writer.WriteString("artifactSha256", state.ArtifactSha256);
            writer.WriteNumber("attempt", state.Attempt);
            writer.WriteString("state", state.State);
            writer.WriteString("previousPath", state.PreviousPath);
            writer.WriteEndObject();
            writer.Flush();
            stream.Flush(true);
        }

        File.Move(temporary, path, true);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
