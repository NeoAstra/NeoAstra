// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Security.Cryptography;
using System.Text;

namespace NeoAstra.Tooling;

internal static class NeoFrontendFingerprint
{
    private const int MaximumInputFiles = 100_000;
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules",
    };

    internal static string Write(NeoResolvedProject project, string outputPath, bool prebuilt,
        string configuration, IReadOnlyList<string> additionalInputs)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(additionalInputs);

        var files = new SortedDictionary<string, string>(PathComparer());
        if (prebuilt)
        {
            AddTree(project.DistDirectory, [], files);
        }
        else
        {
            AddTree(project.FrontendRoot,
                [project.DistDirectory, Path.Combine(project.ProjectDirectory, "bin"), Path.Combine(project.ProjectDirectory, "obj"), Path.GetDirectoryName(Path.GetFullPath(outputPath))!], files);
        }

        if (File.Exists(project.ConfigurationPath)) AddFile(project.ConfigurationPath, files);
        if (project.Lockfile is not null) AddRequiredFile(project.Lockfile, files);
        if (project.GeneratedContract is not null) AddRequiredFile(project.GeneratedContract, files);
        foreach (var input in additionalInputs)
        {
            if (string.IsNullOrWhiteSpace(input)) throw new NeoToolException("frontend_input", "An additional frontend input path is empty.");
            var fullPath = Path.GetFullPath(input, project.ProjectDirectory);
            if (File.Exists(fullPath)) AddFile(fullPath, files);
            else if (Directory.Exists(fullPath)) AddTree(fullPath, [], files);
            else throw new NeoToolException("frontend_input", "An additional frontend input path does not exist.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "neoastra-frontend-fingerprint-v1");
        Append(hash, configuration);
        Append(hash, prebuilt ? "prebuilt" : "build");
        Append(hash, project.ToInspectJson(redactSecrets: false));
        var toolAssembly = typeof(NeoFrontendFingerprint).Assembly;
        Append(hash, toolAssembly.ManifestModule.ModuleVersionId.ToString("D"));
        if (toolAssembly.Location.Length != 0) Append(hash, NeoAssetManifestBuilder.ReadFileSnapshot(toolAssembly.Location).Sha256);
        foreach (var pair in files)
        {
            Append(hash, pair.Key);
            Append(hash, pair.Value);
        }

        var fingerprint = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        WriteIfChanged(outputPath, fingerprint + "\n");
        return fingerprint;
    }

    private static void AddTree(string root, IReadOnlyList<string> excludedRoots, SortedDictionary<string, string> files)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new NeoToolException("frontend_input", "The configured frontend input directory does not exist.");
        EnsureNotLink(root);
        var excluded = excludedRoots.Select(Path.GetFullPath).ToArray();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.TryPop(out var directory))
        {
            EnsureNotLink(directory.FullName);
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo childDirectory)
                {
                    if (ExcludedDirectoryNames.Contains(childDirectory.Name)) continue;
                    if (excluded.Any(path => PathsEqual(childDirectory.FullName, path))) continue;
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new NeoToolException("frontend_input", "Frontend inputs must not contain symbolic links, junctions, or reparse points outside excluded dependency directories.");
                    pending.Push(childDirectory);
                }
                else if (entry is FileInfo file)
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new NeoToolException("frontend_input", "Frontend inputs must not contain symbolic links, junctions, or reparse points outside excluded dependency directories.");
                    AddFile(file.FullName, files);
                }
                else
                {
                    throw new NeoToolException("frontend_input", "Frontend inputs must contain regular files and directories only.");
                }
            }
        }
    }

    private static void AddFile(string path, SortedDictionary<string, string> files)
    {
        path = Path.GetFullPath(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new NeoToolException("frontend_input", "Frontend input files must not be symbolic links or reparse points.");
        if (!files.ContainsKey(path) && files.Count >= MaximumInputFiles)
            throw new NeoToolException("frontend_input", "The frontend input graph exceeds 100,000 files.");
        var snapshot = NeoAssetManifestBuilder.ReadFileSnapshot(path);
        files[path] = $"{snapshot.Length}:{snapshot.Sha256}";
    }

    private static void AddRequiredFile(string path, SortedDictionary<string, string> files)
    {
        if (!File.Exists(path)) throw new NeoToolException("frontend_input", "A configured frontend input file does not exist.");
        AddFile(path, files);
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void WriteIfChanged(string path, string content)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath) && File.ReadAllText(fullPath, Encoding.UTF8) == content) return;
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    private static void EnsureNotLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new NeoToolException("frontend_input", "The frontend input root must not be a symbolic link, junction, or reparse point.");
    }

    private static bool PathsEqual(string left, string right) => Path.GetFullPath(left).Equals(Path.GetFullPath(right), PathComparison());
    private static StringComparer PathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison() => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
