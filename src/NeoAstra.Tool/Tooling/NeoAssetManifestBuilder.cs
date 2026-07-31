// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NeoAstra.Tool.Shared;

namespace NeoAstra.Tooling;

internal readonly record struct NeoAssetFileSnapshot(long Length, string Sha256);
internal static class NeoAssetManifestBuilder
{
    private static readonly HashSet<string> ForbiddenSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules",
        ".git",
        ".hg",
        ".svn",
        "src",
        "source"
    };
    private static readonly HashSet<string> ForbiddenNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env",
        ".env.local",
        ".npmrc",
        ".yarnrc",
        "bun.lock",
        "bun.lockb",
        "id_rsa",
        "id_ed25519",
        "package-lock.json",
        "package.json",
        "pnpm-lock.yaml",
        "yarn.lock"
    };
    internal static string Build(NeoResolvedProject project, string outputPath)
    {
        var root = Path.GetFullPath(project.DistDirectory);
        if (!Directory.Exists(root))
            throw new NeoToolException("asset_output_missing", "The configured frontend dist directory does not exist.");
        EnsureSafeRoot(project, root);
        var entries = new List<NeoAssetEntry>();
        var casePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        Enumerate(root, root, entries, casePaths, project, ref total);
        entries.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        if (!entries.Any(entry => entry.Path == project.SpaFallback))
            throw new NeoToolException("asset_entry_missing", "The configured SPA entry document is missing from dist.");
        var manifest = new NeoAssetManifest(1, project.SpaFallback, project.SpaFallback, project.ProductionOrigin.AbsoluteUri.TrimEnd('/'), project.ContentSecurityPolicy, project.ReferrerPolicy, project.SpaRoutePrefixes, project.ExcludedPrefixes, entries);
        var json = manifest.ToJson();
        var fullOutput = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        WriteAtomic(fullOutput, json + "\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    internal static void CopyManifestAssets(string manifestPath, string sourceRoot, string destinationRoot)
    {
        manifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(manifestPath) || IsReparse(manifestPath))
            throw new NeoToolException("asset_copy_invalid", "The asset manifest must be a regular non-link file.");
        var manifest = NeoAssetManifest.Load(manifestPath);
        sourceRoot = Path.GetFullPath(sourceRoot);
        destinationRoot = Path.GetFullPath(destinationRoot);
        EnsureNotLink(sourceRoot);
        if (Directory.Exists(destinationRoot))
        {
            EnsureTreeHasNoLinks(destinationRoot, manifest.Assets.Count * 2 + 1);
            Directory.Delete(destinationRoot, recursive: true);
        }

        var destinationParent = Path.GetDirectoryName(destinationRoot)!;
        if (Directory.Exists(destinationParent))
            EnsureNotLink(destinationParent);
        Directory.CreateDirectory(destinationRoot);
        EnsureNotLink(destinationRoot);
        foreach (var entry in manifest.Assets)
        {
            var source = SafeManifestPath(sourceRoot, entry.Path);
            if (!File.Exists(source) || IsReparse(source))
                throw new NeoToolException("asset_copy_invalid", "A manifest-listed asset is missing or is a link/reparse point.");
            EnsureRelativePathHasNoLinks(sourceRoot, source);
            var sourceSnapshot = ReadFileSnapshot(source, entry.Length, "asset_copy_hash", "A manifest-listed asset changed after validation.");
            if (sourceSnapshot.Length != entry.Length || !sourceSnapshot.Sha256.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new NeoToolException("asset_copy_hash", "A manifest-listed asset changed after validation.");
            var destination = SafeManifestPath(destinationRoot, entry.Path);
            CreateRelativeDirectories(destinationRoot, Path.GetDirectoryName(entry.Path.Replace('/', Path.DirectorySeparatorChar)));
            File.Copy(source, destination);
            EnsureRelativePathHasNoLinks(destinationRoot, destination);
            NeoAssetFileSnapshot destinationSnapshot;
            try
            {
                destinationSnapshot = ReadFileSnapshot(destination, entry.Length, "asset_copy_hash", "A manifest-listed asset changed while it was copied.");
            }
            catch
            {
                File.Delete(destination);
                throw;
            }

            if (destinationSnapshot.Length != entry.Length || !destinationSnapshot.Sha256.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(destination);
                throw new NeoToolException("asset_copy_hash", "A manifest-listed asset changed while it was copied.");
            }
        }

        EnsureNotLink(destinationRoot);
        File.Copy(manifestPath, Path.Combine(destinationRoot, "neoastra-assets.json"), overwrite: true);
    }

    private static void Enumerate(string root, string directory, List<NeoAssetEntry> entries, HashSet<string> casePaths, NeoResolvedProject project, ref long total)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(directory));
        var directoryCount = 1;
        var discoveredFiles = 0;
        while (pending.TryPop(out var current))
        {
            if (IsReparse(current.FullName))
                throw new NeoToolException("asset_link", "Asset output must not contain symbolic links, junctions, or reparse points.");
            var children = new List<FileSystemInfo>();
            foreach (var child in current.EnumerateFileSystemInfos())
            {
                if (IsReparse(child.FullName))
                    throw new NeoToolException("asset_link", "Asset output must not contain symbolic links, junctions, or reparse points.");
                ValidatePortableSegment(child.Name);
                if (child is DirectoryInfo)
                {
                    if (++directoryCount > project.MaximumFiles + 1)
                        throw new NeoToolException("asset_count", "Asset output exceeds its bounded directory count.");
                }
                else if (child is FileInfo)
                {
                    if (++discoveredFiles > project.MaximumFiles)
                        throw new NeoToolException("asset_count", "Asset output exceeds the configured file-count limit.");
                }
                else
                    throw new NeoToolException("asset_type", "Asset output may contain regular files only.");
                children.Add(child);
            }

            children.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            for (var index = children.Count - 1; index >= 0; index--)
                if (children[index] is DirectoryInfo childDirectory)
                    pending.Push(childDirectory);
            foreach (var file in children.OfType<FileInfo>())
            {
                if (IsReparse(file.FullName))
                    throw new NeoToolException("asset_link", "Asset output must not contain symbolic links, junctions, or reparse points.");
                var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/').Normalize(NormalizationForm.FormC);
                ValidateRelativePath(relative);
                if (!casePaths.Add(relative))
                    throw new NeoToolException("asset_case_collision", "Asset output contains paths that collide under case-insensitive lookup.");
                var remaining = project.MaximumTotalBytes - total;
                var snapshot = ReadFileSnapshot(file.FullName, Math.Min(project.MaximumFileBytes, remaining), "asset_size", "Asset output exceeds a configured size limit or changed while it was read.");
                total = checked(total + snapshot.Length);
                if (!project.IncludeSourceMaps && relative.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
                    throw new NeoToolException("asset_source_map", "Source maps are disabled for production assets.");
                var contentType = NeoAssetManifest.GetContentType(relative);
                var cache = project.CacheHashedAssets && HasContentHash(relative) ? "public,max-age=31536000,immutable" : "no-cache";
                if (relative == project.SpaFallback || relative.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                    cache = "no-cache";
                entries.Add(new(relative, snapshot.Length, snapshot.Sha256, contentType, cache));
            }
        }
    }

    internal static void ValidateRelativePath(string relative)
    {
        if (relative.Length is < 1 or > 1024 || relative.StartsWith('/') || relative.Contains('\\') || relative.Contains('\0') || relative.Contains(':'))
            throw new NeoToolException("asset_path", "Asset output contains an invalid relative path.");
        var segments = relative.Split('/');
        foreach (var segment in segments)
            ValidatePortableSegment(segment);
        if (segments.Any(segment => segment is "" or "." or ".." || ForbiddenSegments.Contains(segment)) || ForbiddenNames.Contains(segments[^1]) || segments[^1].StartsWith(".env", StringComparison.OrdinalIgnoreCase))
            throw new NeoToolException("asset_path", "Asset output contains a reserved, secret, source, dependency, or VCS path.");
        if (relative is "neoastra-assets.json" || relative.StartsWith("_neoastra/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
            throw new NeoToolException("asset_reserved_route", "Asset output conflicts with a reserved internal or API route.");
    }

    private static void EnsureSafeRoot(NeoResolvedProject project, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var allowed = new[]
        {
            Path.GetFullPath(project.ProjectDirectory),
            Path.GetFullPath(project.FrontendRoot)
        }.OrderByDescending(static path => path.Length);
        var ancestor = allowed.FirstOrDefault(candidate => root.StartsWith(Path.EndsInDirectorySeparator(candidate) ? candidate : candidate + Path.DirectorySeparatorChar, comparison) && !string.Equals(root, candidate, comparison));
        if (ancestor is null)
            throw new NeoToolException("asset_root", "The dist directory must be a descendant of the explicit project or frontend root.");
        EnsureRelativePathHasNoLinks(ancestor, root);
    }

    private static bool IsReparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static void EnsureNotLink(string path)
    {
        if (IsReparse(path))
            throw new NeoToolException("asset_link", "Asset paths must not traverse symbolic links, junctions, or reparse points.");
    }

    private static void EnsureRelativePathHasNoLinks(string root, string path)
    {
        var current = root;
        EnsureNotLink(current);
        foreach (var segment in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            EnsureNotLink(current);
        }
    }

    private static void EnsureTreeHasNoLinks(string root, int maximumEntries)
    {
        EnsureNotLink(root);
        var count = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.TryPop(out var directory))
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (++count > maximumEntries)
                    throw new NeoToolException("asset_count", "The existing asset destination exceeds its bounded entry count.");
                if (IsReparse(entry.FullName))
                    throw new NeoToolException("asset_link", "Asset destinations must not contain symbolic links, junctions, or reparse points.");
                if (entry is DirectoryInfo child)
                    pending.Push(child);
            }
    }

    private static void CreateRelativeDirectories(string root, string? relative)
    {
        var current = root;
        EnsureNotLink(current);
        if (string.IsNullOrEmpty(relative))
            return;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            ValidatePortableSegment(segment);
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
                Directory.CreateDirectory(current);
            EnsureNotLink(current);
        }
    }

    private static void ValidatePortableSegment(string segment)
    {
        if (segment.Length == 0 || segment.EndsWith(' ') || segment.EndsWith('.'))
            throw new NeoToolException("asset_path", "Asset paths must not contain empty or trailing-dot/space segments.");
        var stem = segment.Split('.')[0].TrimEnd(' ', '.');
        var isReservedName = stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase);
        var isReservedDevice = stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && stem[3] is >= '1' and <= '9';
        if (isReservedName || isReservedDevice)
            throw new NeoToolException("asset_path", "Asset paths must not contain portable device-name segments.");
    }

    private static bool HasContentHash(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var parts = name.Split(['.', '-', '_']);
        for (var index = 1; index < parts.Length; index++)
            if ((parts[index].Length >= 8 && parts[index].All(Uri.IsHexDigit)) || (parts[index].Length == 8 && parts[index].All(static character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')))
                return true;
        for (var index = 0; index < name.Length - 8; index++)
            if (name[index] is '.' or '-' or '_' && name.Length - index - 1 == 8 && name[(index + 1)..].All(static character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
                return true;
        return false;
    }

    private static string SafeManifestPath(string root, string relative)
    {
        ValidateRelativePath(relative);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new NeoToolException("asset_path", "Manifest path escaped its root.");
        return path;
    }

    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    internal static NeoAssetFileSnapshot ReadFileSnapshot(string path) => ReadFileSnapshot(path, long.MaxValue, "asset_changed", "Asset changed while it was read.");
    private static NeoAssetFileSnapshot ReadFileSnapshot(string path, long maximumLength, string changeCode, string changeMessage)
    {
        var beforeAttributes = File.GetAttributes(path);
        var beforeWrite = File.GetLastWriteTimeUtc(path);
        var beforeCreation = File.GetCreationTimeUtc(path);
        if ((beforeAttributes & FileAttributes.ReparsePoint) != 0)
            throw new NeoToolException("asset_link", "Asset files must not be symbolic links or reparse points.");
        long length;
        string hash;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
        {
            length = stream.Length;
            if (length > maximumLength)
                throw new NeoToolException(changeCode, changeMessage);
            hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (stream.Length != length)
                throw new NeoToolException(changeCode, changeMessage);
        }

        var after = new FileInfo(path);
        after.Refresh();
        if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != beforeWrite || after.CreationTimeUtc != beforeCreation || (after.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new NeoToolException(changeCode, changeMessage);
        return new(length, hash);
    }
}
