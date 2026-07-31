// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using NeoAstra.Tool.Shared.Interop;

namespace NeoAstra.Tooling;

internal sealed record NeoBundleRequest(
    string RuntimeIdentifier,
    string PublishDirectory,
    string AssetManifest,
    string OutputDirectory,
    bool DryRun,
    bool Sign,
    string? SigningIdentityEnvironment,
    bool ExecuteInstaller);

internal sealed record NeoBundleResult(
    string Artifact,
    string StagingManifest,
    IReadOnlyList<NeoBundleCommandPlan> Commands,
    bool HostQualified);

internal sealed record NeoBundleCommandPlan(
    string Stage,
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<int> RedactedArguments,
    bool Executed);

internal sealed record NeoStagingEntry(
    string Path,
    long Length,
    string Sha256,
    string Mode,
    string Component);
internal static class NeoBundleOrchestrator
{
    private const long MaximumBundleBytes = 4L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> ForbiddenNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env",
        ".git",
        ".gitignore",
        "neoastra.json",
        "appsettings.Development.json",
        "launchSettings.json",
        "secrets.json"
    };
    private static readonly HashSet<string> ForbiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".csproj",
        ".sln",
        ".slnx",
        ".user",
        ".tmp",
        ".cache"
    };
    private static readonly DateTimeOffset ArchiveTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    internal static NeoBundleResult Run(NeoResolvedProject project, NeoBundleRequest request)
    {
        var bundle = project.Bundle ?? throw new NeoToolException("bundle_missing", "The schema-validated bundle section is required.");
        ValidateRequest(bundle, request);
        var plans = new List<NeoBundleCommandPlan>();
        AddPrerequisitePlans(request.RuntimeIdentifier, plans);
        var entries = Collect(bundle, request);
        VerifyAbi(bundle, request, entries);
        VerifyFrontend(request.AssetManifest);
        var output = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(output);
        var inspect = Path.Combine(output, "inspect");
        Directory.CreateDirectory(inspect);
        var stagingManifest = Path.Combine(inspect, "staging-manifest.v1.json");
        WriteStagingManifest(stagingManifest, bundle, request.RuntimeIdentifier, entries);
        WritePlatformInputs(inspect, bundle, request.RuntimeIdentifier, request.PublishDirectory, plans);
        AddSigningPlans(bundle, request, plans);
        var artifactName = $"{bundle.Executable}-{bundle.Version}-{request.RuntimeIdentifier}";
        var artifact = Path.Combine(output, artifactName + (request.RuntimeIdentifier.StartsWith("win-", StringComparison.Ordinal) || request.RuntimeIdentifier.StartsWith("osx-", StringComparison.Ordinal) ? ".zip" : ".tar.gz"));
        if (!request.DryRun)
        {
            var temporary = CreatePrivateTemporary(output);
            try
            {
                var payload = Path.Combine(temporary, request.RuntimeIdentifier.StartsWith("osx-", StringComparison.Ordinal) ? bundle.Executable + ".app" : bundle.Executable);
                Stage(entries, request.PublishDirectory, payload, request.RuntimeIdentifier);
                PreparePlatformPayload(bundle, request.RuntimeIdentifier, inspect, payload);
                if (request.Sign && !request.RuntimeIdentifier.StartsWith("linux-", StringComparison.Ordinal))
                    ExecutePayloadSigning(request, payload, plans);
                entries = SnapshotPayload(payload, bundle);
                WriteStagingManifest(stagingManifest, bundle, request.RuntimeIdentifier, entries);
                if (artifact.EndsWith(".zip", StringComparison.Ordinal))
                    CreateZip(payload, artifact);
                else
                    CreateTar(payload, artifact);
                VerifyArchive(artifact, entries);
                if (request.Sign && request.RuntimeIdentifier.StartsWith("osx-", StringComparison.Ordinal))
                {
                    ExecuteMacNotarization(artifact, payload, plans);
                    entries = SnapshotPayload(payload, bundle);
                    WriteStagingManifest(stagingManifest, bundle, request.RuntimeIdentifier, entries);
                    CreateZip(payload, artifact);
                    VerifyArchive(artifact, entries);
                }

                if (request.Sign && request.RuntimeIdentifier.StartsWith("linux-", StringComparison.Ordinal))
                    ExecuteArtifactSigning(request, artifact, plans);
                if (request.ExecuteInstaller)
                    ExecuteInstaller(bundle, request, inspect, payload, plans);
            }
            finally
            {
                DeletePrivateTemporary(temporary, output);
            }
        }

        WriteReleaseMetadata(output, inspect, bundle, request, entries, artifact, plans);
        return new(artifact, stagingManifest, plans, false);
    }

    private static void ValidateRequest(NeoBundleConfiguration bundle, NeoBundleRequest request)
    {
        if (!bundle.RuntimeIdentifiers.Contains(request.RuntimeIdentifier, StringComparer.Ordinal))
            throw new NeoToolException("bundle_rid", "The requested RID is not declared by bundle metadata.");
        var publish = Path.GetFullPath(request.PublishDirectory);
        if (!Directory.Exists(publish))
            throw new NeoToolException("bundle_publish", "The explicit publish directory does not exist.");
        _ = NeoChildProcess.ResolveExecutable("dotnet", Environment.CurrentDirectory);
        if (!HostMatches(request.RuntimeIdentifier) && (request.Sign || request.ExecuteInstaller))
            throw new NeoToolException("bundle_target_host", "Signing and installer execution require the matching target host; cross-host output is never qualified.");
        if (request.Sign && string.IsNullOrWhiteSpace(request.SigningIdentityEnvironment))
            throw new NeoToolException("bundle_signing_reference", "Signing requires an explicit environment-variable name containing the platform signing identity; values never enter configuration or command plans.");
        if (request.SigningIdentityEnvironment is { } name && (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]{0,127}$") || Environment.GetEnvironmentVariable(name) is not { Length: > 0 }))
            throw new NeoToolException("bundle_signing_reference", "The signing identity environment reference is invalid or unavailable.");
        if (request.ExecuteInstaller)
            _ = NeoChildProcess.ResolveExecutable(request.RuntimeIdentifier.StartsWith("win-", StringComparison.Ordinal) ? "makeappx" : request.RuntimeIdentifier.StartsWith("osx-", StringComparison.Ordinal) ? "pkgbuild" : "dpkg-deb", Environment.CurrentDirectory);
        if (request.Sign)
            _ = NeoChildProcess.ResolveExecutable(request.RuntimeIdentifier.StartsWith("win-", StringComparison.Ordinal) ? "signtool" : request.RuntimeIdentifier.StartsWith("osx-", StringComparison.Ordinal) ? "codesign" : "gpg", Environment.CurrentDirectory);
        if (bundle.Notices.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != bundle.Notices.Count)
            throw new NeoToolException("bundle_notice_collision", "Notice output file names collide under portable comparison.");
        foreach (var icon in bundle.Icons)
        {
            var info = new FileInfo(icon);
            if (!info.Exists || info.Length is < 1 or > 16 * 1024 * 1024 || Path.GetExtension(icon).ToLowerInvariant() is not (".ico" or ".icns" or ".png" or ".svg"))
                throw new NeoToolException("bundle_icon", "Each declared source icon must be an existing bounded ICO, ICNS, PNG, or SVG file; platform-required conversion is explicit and offline.");
        }

        foreach (var notice in bundle.Notices)
            if (!File.Exists(notice) || new FileInfo(notice).Length > 4 * 1024 * 1024)
                throw new NeoToolException("bundle_notice", "Every declared notice/license must be an existing bounded file.");
    }

    private static List<NeoStagingEntry> Collect(NeoBundleConfiguration bundle, NeoBundleRequest request)
    {
        var root = Path.GetFullPath(request.PublishDirectory);
        var comparer = StringComparer.OrdinalIgnoreCase;
        var collisions = new HashSet<string>(comparer);
        var result = new List<NeoStagingEntry>();
        long total = 0;
        foreach (var relative in bundle.Files.Order(StringComparer.Ordinal))
        {
            var normalized = NeoBundleConfiguration.NormalizeRelative(relative);
            var full = Path.GetFullPath(normalized.Replace('/', Path.DirectorySeparatorChar), root);
            if (!IsBelow(full, root) || !File.Exists(full))
                throw new NeoToolException("bundle_declared_file", $"Declared publish file '{normalized}' is missing or escapes the publish root.");
            var attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new NeoToolException("bundle_link", "Symlinks/reparse points are forbidden in the staged payload.");
            if (!collisions.Add(normalized.Normalize(NormalizationForm.FormC)))
                throw new NeoToolException("bundle_collision", "Bundle paths collide under portable case-insensitive normalized comparison.");
            if (normalized.Split('/').Any(ForbiddenNames.Contains) || ForbiddenExtensions.Contains(Path.GetExtension(normalized)))
                throw new NeoToolException("bundle_forbidden_file", $"Development/forbidden file '{normalized}' cannot enter a release bundle.");
            var info = new FileInfo(full);
            if (info.Length > 1024L * 1024 * 1024 || (total += info.Length) > MaximumBundleBytes)
                throw new NeoToolException("bundle_size", "Declared payload exceeds per-file or total release bounds.");
            var mode = IsExecutable(normalized, bundle.Executable) ? "0755" : "0644";
            result.Add(new(normalized, info.Length, HashFile(full), mode, Component(normalized)));
        }

        return result;
    }

    private static void VerifyAbi(NeoBundleConfiguration bundle, NeoBundleRequest request, List<NeoStagingEntry> entries)
    {
        var native = request.RuntimeIdentifier.StartsWith("win-", StringComparison.Ordinal) ? "neoastra_native.dll" : request.RuntimeIdentifier.StartsWith("osx-", StringComparison.Ordinal) ? "libneoastra_native.dylib" : "libneoastra_native.so";
        var nativeEntries = entries.Where(entry => Path.GetFileName(entry.Path).Equals(native, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (nativeEntries.Length != 1)
            throw new NeoToolException("bundle_native_abi", $"Publish payload must contain exactly one native ABI asset '{native}' required by {request.RuntimeIdentifier}.");
        var nativeEntry = nativeEntries[0];
        var nativePath = Path.Combine(request.PublishDirectory, nativeEntry.Path.Replace('/', Path.DirectorySeparatorChar));
        if (!ArchitectureMatches(File.ReadAllBytes(nativePath), request.RuntimeIdentifier))
            throw new NeoToolException("bundle_native_architecture", "Configured RID and native ABI asset machine architecture do not match.");
        var identityEntries = entries.Where(entry => Path.GetFileName(entry.Path).Equals("neoastra-native.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (identityEntries.Length != 1)
            throw new NeoToolException("bundle_native_abi", "Publish payload must contain exactly one generated native ABI identity.");
        try
        {
            var identityPath = Path.Combine(request.PublishDirectory, identityEntries[0].Path.Replace('/', Path.DirectorySeparatorChar));
            using var document = JsonDocument.Parse(File.ReadAllBytes(identityPath), new JsonDocumentOptions { MaxDepth = 4 });
            var identity = document.RootElement;
            var names = identity.EnumerateObject().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);
            if (!names.SetEquals(["schemaVersion", "rid", "file", "sha256", "abiMajor", "abiMinor"]) || identity.GetProperty("schemaVersion").GetInt32() != 1 || identity.GetProperty("rid").GetString() != request.RuntimeIdentifier || identity.GetProperty("file").GetString() != native || identity.GetProperty("sha256").GetString() != nativeEntry.Sha256)
                throw new NeoToolException("bundle_native_abi", "Native ABI identity does not match the selected RID binary.");
            ValidateAbiVersion(identity.GetProperty("abiMajor").GetUInt32(), identity.GetProperty("abiMinor").GetUInt32());
        }
        catch (NeoToolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new NeoToolException("bundle_native_abi", "Native ABI identity is invalid.");
        }

        var executableName = request.RuntimeIdentifier.StartsWith("win-", StringComparison.Ordinal) ? bundle.Executable + ".exe" : bundle.Executable;
        var executable = entries.FirstOrDefault(entry => entry.Path.Equals(executableName, StringComparison.OrdinalIgnoreCase));
        if (executable is null)
            throw new NeoToolException("bundle_executable", "Declared payload does not contain the configured executable identity.");
        var bytes = File.ReadAllBytes(Path.Combine(request.PublishDirectory, executable.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!ArchitectureMatches(bytes, request.RuntimeIdentifier))
            throw new NeoToolException("bundle_architecture", "Configured RID and executable machine architecture do not match.");
    }

    internal static void ValidateAbiVersion(uint major, uint minor)
    {
        if (major != NeoNativeAbi.Major || minor != NeoNativeAbi.Minor)
            throw new NeoToolException("bundle_native_abi", $"Native ABI {major}.{minor} does not match managed NeoAstra ABI {NeoNativeAbi.Major}.{NeoNativeAbi.Minor}.");
    }

    private static bool ArchitectureMatches(ReadOnlySpan<byte> bytes, string rid)
    {
        if (bytes.Length < 64)
            return false;
        if (bytes[0] == 'M' && bytes[1] == 'Z')
        {
            var offset = BitConverter.ToInt32(bytes.Slice(0x3c, 4));
            if (offset < 0 || offset + 6 > bytes.Length)
                return false;
            var machine = BitConverter.ToUInt16(bytes.Slice(offset + 4, 2));
            return rid == "win-x64" && machine == 0x8664 || rid == "win-arm64" && machine == 0xaa64;
        }

        if (bytes[0] == 0x7f && bytes[1] == 'E' && bytes[2] == 'L' && bytes[3] == 'F')
        {
            var machine = BitConverter.ToUInt16(bytes.Slice(18, 2));
            return rid == "linux-x64" && machine == 62 || rid == "linux-arm64" && machine == 183;
        }

        var magic = BitConverter.ToUInt32(bytes[..4]);
        if (magic is 0xfeedfacf or 0xcffaedfe)
        {
            var little = magic == 0xfeedfacf;
            var cpu = little ? BitConverter.ToUInt32(bytes.Slice(4, 4)) : System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(4, 4));
            return rid == "osx-x64" && cpu == 0x01000007 || rid == "osx-arm64" && cpu == 0x0100000c;
        }

        return false;
    }

    private static void VerifyFrontend(string path)
    {
        var info = new FileInfo(Path.GetFullPath(path));
        if (!info.Exists || info.Length is < 2 or > 16 * 1024 * 1024)
            throw new NeoToolException("bundle_frontend_manifest", "A bounded generated frontend asset manifest is required.");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(info.FullName), new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException();
        }
        catch (JsonException)
        {
            throw new NeoToolException("bundle_frontend_manifest", "Frontend asset manifest is invalid.");
        }
    }

    private static void WriteStagingManifest(string path, NeoBundleConfiguration bundle, string rid, IReadOnlyList<NeoStagingEntry> entries)
    {
        WriteJson(path, writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("applicationId", bundle.Identifier);
            writer.WriteString("version", bundle.Version);
            writer.WriteString("rid", rid);
            writer.WriteStartArray("files");
            foreach (var entry in entries.OrderBy(static entry => entry.Path, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("path", entry.Path);
                writer.WriteNumber("length", entry.Length);
                writer.WriteString("sha256", entry.Sha256);
                writer.WriteString("mode", entry.Mode);
                writer.WriteString("component", entry.Component);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static void WritePlatformInputs(string inspect, NeoBundleConfiguration bundle, string rid, string publish, List<NeoBundleCommandPlan> plans)
    {
        var directory = Path.Combine(inspect, rid);
        Directory.CreateDirectory(directory);
        File.Copy(bundle.Icons[0], Path.Combine(directory, "source-icon" + Path.GetExtension(bundle.Icons[0]).ToLowerInvariant()), overwrite: true);
        if (rid.StartsWith("win-", StringComparison.Ordinal))
        {
            var manifest = Path.Combine(directory, "AppxManifest.xml");
            using var writer = XmlWriter.Create(manifest, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true });
            writer.WriteStartElement("Package", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");
            writer.WriteAttributeString("xmlns", "uap", null, "http://schemas.microsoft.com/appx/manifest/uap/windows10");
            writer.WriteAttributeString("xmlns", "rescap", null, "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities");
            writer.WriteAttributeString("IgnorableNamespaces", "uap rescap");
            writer.WriteStartElement("Identity");
            writer.WriteAttributeString("Name", bundle.Identifier);
            writer.WriteAttributeString("Publisher", bundle.Publisher);
            writer.WriteAttributeString("Version", FourPart(bundle.NumericVersion));
            writer.WriteAttributeString("ProcessorArchitecture", rid.EndsWith("arm64", StringComparison.Ordinal) ? "arm64" : "x64");
            writer.WriteEndElement();
            writer.WriteStartElement("Properties");
            writer.WriteElementString("DisplayName", bundle.DisplayName);
            writer.WriteElementString("PublisherDisplayName", bundle.Publisher);
            writer.WriteElementString("Logo", "Assets\\icon.png");
            writer.WriteEndElement();
            writer.WriteStartElement("Resources");
            writer.WriteStartElement("Resource");
            writer.WriteAttributeString("Language", "en-us");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteStartElement("Dependencies");
            writer.WriteStartElement("TargetDeviceFamily");
            writer.WriteAttributeString("Name", "Windows.Desktop");
            writer.WriteAttributeString("MinVersion", FourPart(bundle.MinimumOsVersion));
            writer.WriteAttributeString("MaxVersionTested", "10.0.26100.0");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteStartElement("Applications");
            writer.WriteStartElement("Application");
            writer.WriteAttributeString("Id", "App");
            writer.WriteAttributeString("Executable", bundle.Executable + ".exe");
            writer.WriteAttributeString("EntryPoint", "Windows.FullTrustApplication");
            writer.WriteStartElement("uap", "VisualElements", null);
            writer.WriteAttributeString("DisplayName", bundle.DisplayName);
            writer.WriteAttributeString("Description", bundle.DisplayName);
            writer.WriteAttributeString("BackgroundColor", "transparent");
            writer.WriteAttributeString("Square150x150Logo", "Assets\\icon.png");
            writer.WriteAttributeString("Square44x44Logo", "Assets\\icon.png");
            writer.WriteEndElement();
            WriteWindowsExtensions(writer, bundle);
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteStartElement("Capabilities");
            writer.WriteStartElement("rescap", "Capability", null);
            writer.WriteAttributeString("Name", "runFullTrust");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.Flush();
            plans.Add(new("installer", "makeappx", ["pack", "/d", "<package-root>", "/p", "<artifact>.msix", "/o"], [], false));
        }
        else if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            WritePlist(Path.Combine(directory, "Info.plist"), bundle);
            WritePlist(Path.Combine(directory, "Entitlements.plist"), bundle, entitlements: true);
            plans.Add(new("portable-dmg", "hdiutil", ["create", "-srcfolder", "<app>", "-format", "UDZO", "<artifact>.dmg"], [], false));
            plans.Add(new("installer", "pkgbuild", ["--component", "<app>", "--install-location", "/Applications", "<artifact>.pkg"], [], false));
        }
        else
        {
            var desktop = new StringBuilder("[Desktop Entry]\nType=Application\nVersion=1.0\n").Append("Name=").AppendLine(EscapeDesktop(bundle.DisplayName)).Append("Exec=").Append(bundle.Executable).Append(" %U\nIcon=").Append(bundle.Identifier).Append("\nTerminal=false\nCategories=Utility;\n");
            if (bundle.FileAssociations.Count != 0)
                desktop.Append("MimeType=").Append(string.Join(';', bundle.FileAssociations.Select(static item => item.MimeType))).Append(";\n");
            File.WriteAllText(Path.Combine(directory, bundle.Identifier + ".desktop"), desktop.ToString(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, bundle.Identifier + ".xml"), LinuxMime(bundle), new UTF8Encoding(false));
            var control = $"Package: {LinuxPackage(bundle.Identifier)}\nVersion: {DebVersion(bundle.Version)}\nArchitecture: {(rid.EndsWith("arm64", StringComparison.Ordinal) ? "arm64" : "amd64")}\nMaintainer: {bundle.Publisher}\nDepends: {string.Join(", ", bundle.RuntimeDependencies)}\nDescription: {bundle.DisplayName}\n";
            File.WriteAllText(Path.Combine(directory, "control"), control, new UTF8Encoding(false));
            plans.Add(new("installer", "dpkg-deb", ["--root-owner-group", "--build", "<package-root>", "<artifact>.deb"], [], false));
        }
    }

    private static void AddPrerequisitePlans(string rid, List<NeoBundleCommandPlan> plans)
    {
        plans.Add(new("publish", "dotnet", ["publish", "--configuration", "Release", "--runtime", rid, "--self-contained", "true", "-p:PublishAot=true", "--no-restore"], [], false));
        plans.Add(new("frontend", "dotnet", ["neoastra", "assets", "--prebuilt", "<configured-dist>", "--manifest", "<asset-manifest>"], [], false));
    }

    private static void AddSigningPlans(NeoBundleConfiguration bundle, NeoBundleRequest request, List<NeoBundleCommandPlan> plans)
    {
        if (!request.Sign)
            return;
        var identity = "$" + request.SigningIdentityEnvironment;
        if (request.RuntimeIdentifier.StartsWith("win-", StringComparison.Ordinal))
        {
            plans.Add(new("sign-inside-out", "signtool", ["sign", "/fd", "SHA256", "/sha1", identity, "/tr", "$NEOASTRA_TIMESTAMP_URL", "/td", "SHA256", "<PE-or-MSIX>"], [4], false));
            plans.Add(new("post-sign-verify", "signtool", ["verify", "/pa", "/all", "<PE-or-MSIX>"], [], false));
        }
        else if (request.RuntimeIdentifier.StartsWith("osx-", StringComparison.Ordinal))
        {
            plans.Add(new("sign-inside-out", "codesign", ["--force", "--options", "runtime", "--entitlements", "<entitlements>", "--sign", identity, "<nested-and-app>"], [6], false));
            plans.Add(new("notarize", "xcrun", ["notarytool", "submit", "<artifact>", "--keychain-profile", "$NEOASTRA_NOTARY_PROFILE", "--wait"], [4], false));
            plans.Add(new("staple", "xcrun", ["stapler", "staple", "<app>"], [], false));
            plans.Add(new("post-sign-verify", "codesign", ["--verify", "--deep", "--strict", "--verbose=2", "<app>"], [], false));
            plans.Add(new("gatekeeper", "spctl", ["--assess", "--type", "execute", "--verbose=2", "<app>"], [], false));
        }
        else
        {
            plans.Add(new("sign", "gpg", ["--batch", "--local-user", identity, "--detach-sign", "--armor", "<artifact>"], [2], false));
            plans.Add(new("post-sign-verify", "gpg", ["--verify", "<artifact>.asc", "<artifact>"], [], false));
        }
    }

    private static void Stage(IEnumerable<NeoStagingEntry> entries, string publish, string payload, string rid)
    {
        var root = rid.StartsWith("osx-", StringComparison.Ordinal) ? Path.Combine(payload, "Contents", "MacOS") : payload;
        Directory.CreateDirectory(root);
        foreach (var entry in entries)
        {
            var destination = Path.GetFullPath(entry.Path.Replace('/', Path.DirectorySeparatorChar), root);
            if (!IsBelow(destination, root))
                throw new NeoToolException("bundle_stage_path", "A staging destination escaped its private root.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(publish, entry.Path.Replace('/', Path.DirectorySeparatorChar)), destination, overwrite: false);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(destination, entry.Mode == "0755" ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private static void PreparePlatformPayload(NeoBundleConfiguration bundle, string rid, string inspect, string payload)
    {
        var generated = Path.Combine(inspect, rid);
        WriteJson(Path.Combine(payload, "neoastra-package.json"), writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("applicationId", bundle.Identifier);
            writer.WriteString("version", bundle.NumericVersion.ToString());
            writer.WriteString("rid", rid);
            writer.WriteString("executable", bundle.Executable);
            writer.WriteEndObject();
        });
        if (rid.StartsWith("win-", StringComparison.Ordinal))
        {
            File.Copy(Path.Combine(generated, "AppxManifest.xml"), Path.Combine(payload, "AppxManifest.xml"), true);
            var assets = Path.Combine(payload, "Assets");
            Directory.CreateDirectory(assets);
            File.Copy(bundle.Icons[0], Path.Combine(assets, "icon" + Path.GetExtension(bundle.Icons[0]).ToLowerInvariant()), true);
        }
        else if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            File.Copy(Path.Combine(generated, "Info.plist"), Path.Combine(payload, "Contents", "Info.plist"), true);
            var resources = Path.Combine(payload, "Contents", "Resources");
            Directory.CreateDirectory(resources);
            File.Copy(bundle.Icons[0], Path.Combine(resources, "icon" + Path.GetExtension(bundle.Icons[0]).ToLowerInvariant()), true);
        }
        else
        {
            var share = Path.Combine(payload, "share");
            Directory.CreateDirectory(Path.Combine(share, "applications"));
            Directory.CreateDirectory(Path.Combine(share, "mime", "packages"));
            Directory.CreateDirectory(Path.Combine(share, "icons", "hicolor", "256x256", "apps"));
            File.Copy(Path.Combine(generated, bundle.Identifier + ".desktop"), Path.Combine(share, "applications", bundle.Identifier + ".desktop"), true);
            File.Copy(Path.Combine(generated, bundle.Identifier + ".xml"), Path.Combine(share, "mime", "packages", bundle.Identifier + ".xml"), true);
            File.Copy(bundle.Icons[0], Path.Combine(share, "icons", "hicolor", "256x256", "apps", bundle.Identifier + Path.GetExtension(bundle.Icons[0]).ToLowerInvariant()), true);
        }
    }

    private static List<NeoStagingEntry> SnapshotPayload(string payload, NeoBundleConfiguration bundle)
    {
        var result = new List<NeoStagingEntry>();
        foreach (var file in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(payload, file).Replace('\\', '/').Normalize(NormalizationForm.FormC);
            var info = new FileInfo(file);
            result.Add(new(relative, info.Length, HashFile(file), IsExecutable(relative, bundle.Executable) ? "0755" : "0644", Component(relative)));
        }

        return result;
    }

    private static void CreateZip(string source, string destination)
    {
        using var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(Path.GetDirectoryName(source)!, file).Replace('\\', '/');
            var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
            entry.LastWriteTime = ArchiveTimestamp;
            using var input = File.OpenRead(file);
            using var output = entry.Open();
            input.CopyTo(output);
        }
    }

    private static void CreateTar(string source, string destination)
    {
        using var output = new GZipStream(new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None), CompressionLevel.SmallestSize);
        using var tar = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: false);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var mode = OperatingSystem.IsWindows() ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead : File.GetUnixFileMode(file);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, Path.GetRelativePath(Path.GetDirectoryName(source)!, file).Replace('\\', '/'))
            {
                DataStream = File.OpenRead(file),
                ModificationTime = DateTimeOffset.UnixEpoch,
                Uid = 0,
                Gid = 0,
                UserName = "root",
                GroupName = "root",
                Mode = mode
            };
            tar.WriteEntry(entry);
            entry.DataStream.Dispose();
        }
    }

    private static void VerifyArchive(string artifact, IReadOnlyList<NeoStagingEntry> expected)
    {
        if (!File.Exists(artifact) || new FileInfo(artifact).Length == 0)
            throw new NeoToolException("bundle_archive", "Portable archive was not created.");
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        if (artifact.EndsWith(".zip", StringComparison.Ordinal))
        {
            using var archive = ZipFile.OpenRead(artifact);
            foreach (var entry in archive.Entries.Where(static entry => !entry.FullName.EndsWith('/')))
            {
                using var stream = entry.Open();
                actual.Add(StripArchiveRoot(entry.FullName), Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
            }
        }
        else
        {
            using var compressed = new GZipStream(File.OpenRead(artifact), CompressionMode.Decompress);
            using var reader = new TarReader(compressed);
            TarEntry? entry;
            while ((entry = reader.GetNextEntry(copyData: false)) is not null)
            {
                if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream is null)
                    throw new NeoToolException("bundle_archive", "Portable archive contains an unexpected special entry.");
                actual.Add(StripArchiveRoot(entry.Name), Convert.ToHexString(SHA256.HashData(entry.DataStream)).ToLowerInvariant());
            }
        }

        if (actual.Count != expected.Count || expected.Any(entry => !actual.TryGetValue(entry.Path, out var hash) || hash != entry.Sha256))
            throw new NeoToolException("bundle_archive", "Portable archive content differs from the deterministic staging manifest.");
    }

    private static string StripArchiveRoot(string path)
    {
        var normalized = path.Replace('\\', '/');
        var separator = normalized.IndexOf('/');
        return separator < 0 ? normalized : normalized[(separator + 1)..];
    }

    private static void ExecutePayloadSigning(NeoBundleRequest request, string payload, List<NeoBundleCommandPlan> plans)
    {
        var identity = Environment.GetEnvironmentVariable(request.SigningIdentityEnvironment!)!;
        if (request.RuntimeIdentifier.StartsWith("win-", StringComparison.Ordinal))
        {
            var timestamp = Environment.GetEnvironmentVariable("NEOASTRA_TIMESTAMP_URL");
            if (!Uri.TryCreate(timestamp, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
                throw new NeoToolException("bundle_timestamp", "Windows signing requires NEOASTRA_TIMESTAMP_URL with a reviewed HTTPS timestamp service.");
            var targets = Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories).Where(path => Path.GetExtension(path) is ".exe" or ".dll").Order(StringComparer.Ordinal).ToArray();
            if (targets.Length == 0)
                throw new NeoToolException("bundle_sign_target", "No PE signing targets were found.");
            foreach (var target in targets)
            {
                RunTool("signtool", ["sign", "/fd", "SHA256", "/sha1", identity, "/tr", timestamp, "/td", "SHA256", target]);
                RunTool("signtool", ["verify", "/pa", "/all", target]);
            }
        }
        else
        {
            var entitlements = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(payload)!)!, "inspect", request.RuntimeIdentifier, "Entitlements.plist");
            foreach (var target in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories).Where(path => Path.GetExtension(path) is ".dylib" or ".framework").OrderByDescending(static path => path.Length))
                RunTool("codesign", ["--force", "--options", "runtime", "--sign", identity, target]);
            RunTool("codesign", ["--force", "--options", "runtime", "--entitlements", entitlements, "--sign", identity, payload]);
            RunTool("codesign", ["--verify", "--deep", "--strict", "--verbose=2", payload]);
        }

        foreach (var plan in plans.Where(static plan => plan.Stage is "sign-inside-out" or "post-sign-verify").ToArray())
            ReplaceExecuted(plans, plan);
    }

    private static void ExecuteArtifactSigning(NeoBundleRequest request, string artifact, List<NeoBundleCommandPlan> plans)
    {
        var identity = Environment.GetEnvironmentVariable(request.SigningIdentityEnvironment!)!;
        RunTool("gpg", ["--batch", "--local-user", identity, "--detach-sign", "--armor", artifact]);
        RunTool("gpg", ["--verify", artifact + ".asc", artifact]);
        foreach (var plan in plans.Where(static plan => plan.Stage is "sign" or "post-sign-verify").ToArray())
            ReplaceExecuted(plans, plan);
    }

    private static void ExecuteMacNotarization(string artifact, string payload, List<NeoBundleCommandPlan> plans)
    {
        var profile = Environment.GetEnvironmentVariable("NEOASTRA_NOTARY_PROFILE");
        if (string.IsNullOrWhiteSpace(profile))
            throw new NeoToolException("bundle_notary_reference", "macOS notarization requires the NEOASTRA_NOTARY_PROFILE keychain-profile reference.");
        RunTool("xcrun", ["notarytool", "submit", artifact, "--keychain-profile", profile, "--wait"]);
        RunTool("xcrun", ["stapler", "staple", payload]);
        RunTool("codesign", ["--verify", "--deep", "--strict", "--verbose=2", payload]);
        RunTool("spctl", ["--assess", "--type", "execute", "--verbose=2", payload]);
        foreach (var plan in plans.Where(static plan => plan.Stage is "notarize" or "staple" or "gatekeeper").ToArray())
            ReplaceExecuted(plans, plan);
    }

    private static void ExecuteInstaller(NeoBundleConfiguration bundle, NeoBundleRequest request, string inspect, string payload, List<NeoBundleCommandPlan> plans)
    {
        if (OperatingSystem.IsMacOS())
        {
            var dmgPlan = plans.Single(value => value.Stage == "portable-dmg");
            var dmg = Path.Combine(request.OutputDirectory, $"{bundle.Executable}-{bundle.Version}-{request.RuntimeIdentifier}.dmg");
            RunTool(dmgPlan.Executable, dmgPlan.Arguments.Select(argument => argument switch
            {
                "<app>" => payload,
                "<artifact>.dmg" => dmg,
                _ => argument
            }).ToArray());
            ReplaceExecuted(plans, dmgPlan);
            if (!File.Exists(dmg) || new FileInfo(dmg).Length == 0)
                throw new NeoToolException("bundle_dmg", "hdiutil did not create the expected DMG.");
        }

        var plan = plans.Single(value => value.Stage == "installer");
        var destination = Path.Combine(request.OutputDirectory, $"{bundle.Executable}-{bundle.Version}-{request.RuntimeIdentifier}" + (OperatingSystem.IsWindows() ? ".msix" : OperatingSystem.IsMacOS() ? ".pkg" : ".deb"));
        var packageRoot = payload;
        if (OperatingSystem.IsWindows())
        {
            packageRoot = Path.Combine(inspect, request.RuntimeIdentifier, "package-root");
            CopyDirectory(payload, packageRoot);
            File.Copy(Path.Combine(inspect, request.RuntimeIdentifier, "AppxManifest.xml"), Path.Combine(packageRoot, "AppxManifest.xml"), true);
            var assets = Path.Combine(packageRoot, "Assets");
            Directory.CreateDirectory(assets);
            if (bundle.Icons.Count != 0)
                foreach (var name in new[]
                {
                    "icon.png",
                    "icon44.png",
                    "icon150.png",
                    "icon310.png"
                }

                )
                    File.Copy(bundle.Icons[0], Path.Combine(assets, name), true);
        }
        else if (OperatingSystem.IsLinux())
        {
            packageRoot = Path.Combine(Path.GetDirectoryName(payload)!, "deb-root");
            var control = Path.Combine(packageRoot, "DEBIAN");
            var app = Path.Combine(packageRoot, "opt", bundle.Identifier);
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(Path.GetDirectoryName(app)!);
            CopyDirectory(payload, app);
            File.Copy(Path.Combine(inspect, request.RuntimeIdentifier, "control"), Path.Combine(control, "control"));
            var share = Path.Combine(app, "share");
            if (Directory.Exists(share))
                CopyDirectory(share, Path.Combine(packageRoot, "usr", "share"));
            var bin = Path.Combine(packageRoot, "usr", "bin");
            Directory.CreateDirectory(bin);
            var launcher = Path.Combine(bin, bundle.Executable);
            File.WriteAllText(launcher, $"#!/bin/sh\nexec /opt/{bundle.Identifier}/{bundle.Executable} \"$@\"\n", new UTF8Encoding(false));
            File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var arguments = plan.Arguments.Select(argument => argument switch
        {
            "<staging>" => payload,
            "<package-root>" => packageRoot,
            "<app>" => payload,
            "<artifact>.msix" or "<artifact>.pkg" or "<artifact>.deb" => destination,
            _ => argument
        }).ToList();
        if (request.Sign && OperatingSystem.IsMacOS())
            arguments.InsertRange(arguments.Count - 1, ["--sign", Environment.GetEnvironmentVariable(request.SigningIdentityEnvironment!)!]);
        RunTool(plan.Executable, arguments);
        ReplaceExecuted(plans, plan);
        if (!File.Exists(destination) || new FileInfo(destination).Length == 0)
            throw new NeoToolException("bundle_installer", "Platform installer tool did not create the expected package.");
        if (request.Sign && OperatingSystem.IsWindows())
        {
            var identity = Environment.GetEnvironmentVariable(request.SigningIdentityEnvironment!)!;
            var timestamp = Environment.GetEnvironmentVariable("NEOASTRA_TIMESTAMP_URL")!;
            RunTool("signtool", ["sign", "/fd", "SHA256", "/sha1", identity, "/tr", timestamp, "/td", "SHA256", destination]);
            RunTool("signtool", ["verify", "/pa", "/all", destination]);
        }
        else if (request.Sign && OperatingSystem.IsMacOS())
            RunTool("pkgutil", ["--check-signature", destination]);
        else if (request.Sign)
        {
            var identity = Environment.GetEnvironmentVariable(request.SigningIdentityEnvironment!)!;
            RunTool("gpg", ["--batch", "--local-user", identity, "--detach-sign", "--armor", destination]);
            RunTool("gpg", ["--verify", destination + ".asc", destination]);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static void RunTool(string executable, IReadOnlyList<string> arguments)
    {
        var resolved = NeoChildProcess.ResolveExecutable(executable, Environment.CurrentDirectory);
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = resolved.Path,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in resolved.PrefixArguments.Concat(arguments))
            start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new NeoToolException("bundle_tool", "Platform tool failed to start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5 * 60 * 1000))
        {
            process.Kill(true);
            throw new NeoToolException("bundle_tool_timeout", "Platform tool exceeded its five-minute bound.");
        }

        Task.WaitAll([stdout, stderr]);
        if (process.ExitCode != 0)
            throw new NeoToolException("bundle_tool", $"Platform tool '{Path.GetFileName(executable)}' failed with exit code {process.ExitCode}; output is intentionally not echoed because tools may expose credential metadata.");
    }

    private static void ReplaceExecuted(List<NeoBundleCommandPlan> plans, NeoBundleCommandPlan plan)
    {
        plans[plans.IndexOf(plan)] = plan with
        {
            Executed = true
        };
    }

    private static void WriteReleaseMetadata(string output, string inspect, NeoBundleConfiguration bundle, NeoBundleRequest request, IReadOnlyList<NeoStagingEntry> entries, string artifact, IReadOnlyList<NeoBundleCommandPlan> plans)
    {
        foreach (var notice in bundle.Notices.Order(StringComparer.Ordinal))
            File.Copy(notice, Path.Combine(inspect, "notice-" + Path.GetFileName(notice)), true);
        WriteJson(Path.Combine(output, "sbom.spdx.json"), writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("spdxVersion", "SPDX-2.3");
            writer.WriteString("dataLicense", "CC0-1.0");
            writer.WriteString("SPDXID", "SPDXRef-DOCUMENT");
            writer.WriteString("name", bundle.DisplayName);
            writer.WriteStartArray("files");
            foreach (var entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("SPDXID", "SPDXRef-File-" + entry.Sha256[..16]);
                writer.WriteString("fileName", entry.Path);
                writer.WriteStartArray("checksums");
                writer.WriteStartObject();
                writer.WriteString("algorithm", "SHA256");
                writer.WriteString("checksumValue", entry.Sha256);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
        WriteJson(Path.Combine(output, "sbom.cyclonedx.json"), writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("bomFormat", "CycloneDX");
            writer.WriteString("specVersion", "1.6");
            writer.WriteNumber("version", 1);
            writer.WriteStartArray("components");
            foreach (var group in entries.GroupBy(static entry => entry.Component).OrderBy(static group => group.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "library");
                writer.WriteString("name", group.Key);
                writer.WriteString("version", bundle.Version);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
        WriteJson(Path.Combine(output, "provenance.json"), writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("applicationId", bundle.Identifier);
            writer.WriteString("version", bundle.Version);
            writer.WriteString("rid", request.RuntimeIdentifier);
            writer.WriteString("configuration", "Release");
            writer.WriteBoolean("nativeAot", true);
            writer.WriteBoolean("signed", request.Sign);
            writer.WriteBoolean("hostQualified", false);
            writer.WriteString("qualification", "requires-recorded-target-host-install-smoke");
            writer.WriteString("stagingManifestSha256", HashFile(Path.Combine(inspect, "staging-manifest.v1.json")));
            if (File.Exists(artifact))
                writer.WriteString("artifactSha256", HashFile(artifact));
            writer.WriteString("sourceCommit", Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "unknown");
            writer.WriteEndObject();
        });
        WriteJson(Path.Combine(output, "support.json"), writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("applicationId", bundle.Identifier);
            writer.WriteString("version", bundle.Version);
            writer.WriteString("rid", request.RuntimeIdentifier);
            writer.WriteString("webViewPolicy", request.RuntimeIdentifier.StartsWith("win-") ? "evergreen-detect" : "system-runtime");
            writer.WriteString("updateMode", bundle.Update?.Mode ?? "disabled");
            writer.WriteStartArray("runtimeDependencies");
            foreach (var dependency in bundle.RuntimeDependencies)
                writer.WriteStringValue(dependency);
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
        if (bundle.IncludeSymbols)
        {
            var symbols = entries.Where(static entry => Path.GetExtension(entry.Path) is ".pdb" or ".dbg" or ".dSYM").ToArray();
            WriteJson(Path.Combine(output, "symbols.json"), writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteStartArray("files");
                foreach (var entry in symbols)
                    writer.WriteStringValue(entry.Path);
                writer.WriteEndArray();
                writer.WriteEndObject();
            });
        }

        WriteJson(Path.Combine(inspect, "command-plan.json"), writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteBoolean("dryRun", request.DryRun);
            writer.WriteStartArray("commands");
            foreach (var plan in plans)
            {
                writer.WriteStartObject();
                writer.WriteString("stage", plan.Stage);
                writer.WriteString("executable", plan.Executable);
                writer.WriteStartArray("arguments");
                for (var index = 0; index < plan.Arguments.Count; index++)
                    writer.WriteStringValue(plan.RedactedArguments.Contains(index) ? "[REDACTED_REFERENCE]" : plan.Arguments[index]);
                writer.WriteEndArray();
                writer.WriteBoolean("executed", plan.Executed);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
        var checksumFiles = Directory.EnumerateFiles(output, "*", SearchOption.TopDirectoryOnly).Where(path => Path.GetFileName(path) != "SHA256SUMS").OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal).ToArray();
        File.WriteAllLines(Path.Combine(output, "SHA256SUMS"), checksumFiles.Select(path => $"{HashFile(path)}  {Path.GetFileName(path)}"), new UTF8Encoding(false));
    }

    private static string CreatePrivateTemporary(string output)
    {
        var path = Path.Combine(output, ".stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static void DeletePrivateTemporary(string path, string output)
    {
        var full = Path.GetFullPath(path);
        if (!IsBelow(full, Path.GetFullPath(output)) || !Path.GetFileName(full).StartsWith(".stage-", StringComparison.Ordinal))
            throw new NeoToolException("bundle_cleanup", "Refused to clean a staging path outside the owned output directory.");
        if (Directory.Exists(full))
            Directory.Delete(full, true);
    }

    private static bool IsBelow(string path, string root) => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool HostMatches(string rid) => rid.StartsWith("win-", StringComparison.Ordinal) && OperatingSystem.IsWindows() || rid.StartsWith("osx-", StringComparison.Ordinal) && OperatingSystem.IsMacOS() || rid.StartsWith("linux-", StringComparison.Ordinal) && OperatingSystem.IsLinux();
    private static bool IsExecutable(string path, string executable) => Path.GetFileNameWithoutExtension(path).Equals(executable, StringComparison.OrdinalIgnoreCase) || path.EndsWith(".so", StringComparison.Ordinal) || path.EndsWith(".dylib", StringComparison.Ordinal);
    private static string Component(string path) => path.Contains("neoastra", StringComparison.OrdinalIgnoreCase) ? "NeoAstra" : path.EndsWith(".deps.json", StringComparison.Ordinal) || path.EndsWith(".runtimeconfig.json", StringComparison.Ordinal) ? ".NET runtime policy" : "application";
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void WriteJson(string path, Action<Utf8JsonWriter> write)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.Default });
        write(writer);
        writer.Flush();
        stream.WriteByte((byte)'\n');
    }

    private static string FourPart(string version) => string.Join('.', version.Split('.').Concat(["0", "0", "0", "0"]).Take(4));
    private static string DebVersion(string version) => version.Replace('+', '.');
    private static string LinuxPackage(string identifier) => identifier.ToLowerInvariant().Replace('.', '-');
    private static string EscapeDesktop(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
    private static string LinuxMime(NeoBundleConfiguration bundle)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        builder.Append("<mime-info xmlns=\"http://www.freedesktop.org/standards/shared-mime-info\">\n");
        foreach (var association in bundle.FileAssociations)
        {
            builder.Append("  <mime-type type=\"").Append(XmlEscape(association.MimeType)).Append("\"><comment>");
            builder.Append(XmlEscape(bundle.DisplayName)).Append("</comment><glob pattern=\"*");
            builder.Append(XmlEscape(association.Extension)).Append("\"/></mime-type>\n");
        }

        builder.Append("</mime-info>\n");
        return builder.ToString();
    }
    private static string XmlEscape(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;
    private static void WriteWindowsExtensions(XmlWriter writer, NeoBundleConfiguration bundle)
    {
        if (bundle.FileAssociations.Count == 0 && bundle.UrlSchemes.Count == 0)
            return;
        writer.WriteStartElement("Extensions");
        foreach (var item in bundle.FileAssociations)
        {
            writer.WriteStartElement("uap", "Extension", null);
            writer.WriteAttributeString("Category", "windows.fileTypeAssociation");
            writer.WriteStartElement("uap", "FileTypeAssociation", null);
            writer.WriteAttributeString("Name", item.Extension[1..]);
            writer.WriteStartElement("uap", "SupportedFileTypes", null);
            writer.WriteElementString("uap", "FileType", null, item.Extension);
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        foreach (var scheme in bundle.UrlSchemes)
        {
            writer.WriteStartElement("uap", "Extension", null);
            writer.WriteAttributeString("Category", "windows.protocol");
            writer.WriteStartElement("uap", "Protocol", null);
            writer.WriteAttributeString("Name", scheme);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WritePlist(string path, NeoBundleConfiguration bundle, bool entitlements = false)
    {
        using var xml = XmlWriter.Create(path, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, DoNotEscapeUriAttributes = false });
        xml.WriteDocType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null);
        xml.WriteStartElement("plist");
        xml.WriteAttributeString("version", "1.0");
        xml.WriteStartElement("dict");
        if (entitlements)
        {
            foreach (var item in bundle.Entitlements.Order(StringComparer.Ordinal))
            {
                xml.WriteElementString("key", item);
                xml.WriteStartElement("true");
                xml.WriteEndElement();
            }
        }
        else
        {
            Key(xml, "CFBundleIdentifier", bundle.Identifier);
            Key(xml, "CFBundleDisplayName", bundle.DisplayName);
            Key(xml, "CFBundleExecutable", bundle.Executable);
            Key(xml, "CFBundleShortVersionString", bundle.Version.Split('+')[0]);
            Key(xml, "CFBundleVersion", bundle.NumericVersion);
            Key(xml, "LSMinimumSystemVersion", bundle.MinimumOsVersion);
            if (bundle.UrlSchemes.Count != 0)
            {
                xml.WriteElementString("key", "CFBundleURLTypes");
                xml.WriteStartElement("array");
                foreach (var scheme in bundle.UrlSchemes)
                {
                    xml.WriteStartElement("dict");
                    xml.WriteElementString("key", "CFBundleURLSchemes");
                    xml.WriteStartElement("array");
                    xml.WriteElementString("string", scheme);
                    xml.WriteEndElement();
                    xml.WriteEndElement();
                }

                xml.WriteEndElement();
            }
        }

        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    private static void Key(XmlWriter writer, string key, string value)
    {
        writer.WriteElementString("key", key);
        writer.WriteElementString("string", value);
    }
}
