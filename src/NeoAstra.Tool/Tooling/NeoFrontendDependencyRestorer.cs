// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Security.Cryptography;
using System.Text;

namespace NeoAstra.Tooling;

internal sealed class NeoFrontendDependencyRestorer(INeoProcessFactory processFactory)
{
    private const string StateFileName = ".neoastra-restore.sha256";

    internal async Task<bool> RestoreAsync(NeoResolvedProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.PackageManager == "none") return false;
        if (project.PackageManager != "npm")
        {
            Console.WriteLine($"Automatic frontend dependency restore does not manage {project.PackageManager}; using explicitly restored locked dependencies.");
            return false;
        }

        var packageJson = Path.Combine(project.FrontendRoot, "package.json");
        var expectedLockfile = Path.Combine(project.FrontendRoot, "package-lock.json");
        EnsureDirectoryIsNotLink(project.FrontendRoot);
        if (!File.Exists(packageJson)) throw new NeoToolException("package_manifest_missing", "Automatic npm restore requires frontend.root/package.json.");
        if (project.Lockfile is null || !PathsEqual(project.Lockfile, expectedLockfile) || !File.Exists(expectedLockfile))
            throw new NeoToolException("lockfile_missing", "Automatic npm restore requires a committed frontend.root/package-lock.json configured as frontend.lockfile.");
        EnsureFileIsNotLink(packageJson);
        EnsureFileIsNotLink(expectedLockfile);

        var dependencyDirectory = Path.Combine(project.FrontendRoot, "node_modules");
        EnsureDirectoryIsNotLink(dependencyDirectory, allowMissing: true);
        var statePath = Path.Combine(dependencyDirectory, StateFileName);
        await using var restoreLock = await AcquireLockAsync(project.FrontendRoot, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(packageJson) || !File.Exists(expectedLockfile))
            throw new NeoToolException("lockfile_missing", "The npm package manifest or committed package-lock.json was removed while dependency restore was waiting.");
        EnsureFileIsNotLink(packageJson);
        EnsureFileIsNotLink(expectedLockfile);
        var expectedState = ComputeState(packageJson, expectedLockfile);
        if (StateMatches(statePath, expectedState))
        {
            Console.WriteLine("Frontend npm dependencies are current.");
            return false;
        }

        DeleteState(statePath);
        var command = new NeoCommand(["npm", "ci", "--no-audit", "--no-fund"]);
        await using var process = processFactory.Start(new("restore", command, project.FrontendRoot, project.Environment,
            project.SecretEnvironment, (line, error) => (error ? Console.Error : Console.Out).WriteLine($"[frontend restore] {line}")));
        var exit = await process.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (exit != 0) throw new NeoToolException("package_restore_failed", $"The locked npm dependency restore failed with exit code {exit}.");

        EnsureDirectoryIsNotLink(dependencyDirectory, allowMissing: true);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        WriteState(statePath, expectedState + "\n");
        Console.WriteLine("Restored frontend npm dependencies from package-lock.json.");
        return true;
    }

    private static string ComputeState(string packageJson, string lockfile)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "neoastra-npm-ci-v1");
        AppendFile(hash, packageJson);
        AppendFile(hash, lockfile);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendFile(IncrementalHash hash, string path)
    {
        var snapshot = NeoAssetManifestBuilder.ReadFileSnapshot(path);
        Append(hash, Path.GetFileName(path));
        Append(hash, snapshot.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, snapshot.Sha256);
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static bool StateMatches(string path, string expected)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length <= 128 &&
                   (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
                   File.ReadAllText(path, Encoding.UTF8).Trim().Equals(expected, StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void DeleteState(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { throw new NeoToolException("restore_state", "NeoAstra could not invalidate the previous frontend restore state before running npm ci."); }
        catch (UnauthorizedAccessException) { throw new NeoToolException("restore_state", "NeoAstra could not invalidate the previous frontend restore state before running npm ci."); }
    }

    private static void WriteState(string path, string content)
    {
        var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    private static async Task<FileStream> AcquireLockAsync(string frontendRoot, CancellationToken cancellationToken)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(frontendRoot)))).ToLowerInvariant();
        var directory = Path.Combine(Path.GetTempPath(), "neoastra-restore-locks");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, key + ".lock");
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (DateTime.UtcNow >= deadline) throw new NeoToolException("package_restore_lock", "Timed out waiting for another frontend dependency restore to finish.");
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void EnsureDirectoryIsNotLink(string path, bool allowMissing = false)
    {
        if (!Directory.Exists(path))
        {
            if (allowMissing) return;
            throw new NeoToolException("package_restore_path", "The frontend dependency restore directory does not exist.");
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new NeoToolException("package_restore_path", "Frontend dependency restore directories must not be symbolic links, junctions, or reparse points.");
    }

    private static void EnsureFileIsNotLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new NeoToolException("package_restore_path", "Frontend package manifests and lockfiles must not be symbolic links or reparse points.");
    }

    private static bool PathsEqual(string left, string right) => Path.GetFullPath(left).Equals(Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
