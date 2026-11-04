// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NeoAstra.Tooling;

/// <summary>The relay of a running development session, as its discovery file names it.</summary>
internal sealed record NeoMcpRelaySession(Uri Url, string Token, int ProcessId);

/// <summary>
/// The discovery files through which <c>dotnet neoastra mcp</c> finds the relay of the development session of the same project.
/// The files live in a folder of the user, never in the project, so that the token stays out of the repository; on macOS and
/// Linux only the user can read them. Each session writes a file of its own when its relay starts and removes it when it ends,
/// so that two sessions of a project that overlap never remove the file of the other; the server follows the newest session
/// that still runs, and a session that starts removes the files of the sessions of the project that ended without doing so.
/// </summary>
internal static class NeoMcpRelayDiscovery
{
    /// <summary>Gets the folder of the discovery files of the user.</summary>
    internal static string DefaultDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "NeoAstra", "dev-sessions");

    /// <summary>Gets the discovery file of a session of a project: a hash of the full path of the project, so that two projects
    /// never share one, and a hash of the token of the session, so that two sessions never share one either.</summary>
    internal static string PathFor(string directory, string projectDirectory, string token)
        => Path.Combine(directory, $"{Prefix(projectDirectory)}{Hash(token)[..16]}.json");

    private static string Prefix(string projectDirectory)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));
        if (!OperatingSystem.IsLinux()) key = key.ToUpperInvariant();
        return $"mcp-{Hash(key)[..24]}-";
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static IEnumerable<string> FilesOf(string directory, string projectDirectory)
        => Directory.Exists(directory) ? Directory.EnumerateFiles(directory, Prefix(projectDirectory) + "*.json") : [];

    internal static void Write(string directory, string projectDirectory, NeoMcpRelaySession session)
    {
        Directory.CreateDirectory(directory);
        foreach (var file in FilesOf(directory, projectDirectory))
        {
            if (Read(file) is { } other && !IsRunning(other.ProcessId))
            {
                try { File.Delete(file); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* it stays ignored */ }
            }
        }
        var path = PathFor(directory, projectDirectory, session.Token);
        var temporary = path + "." + Environment.ProcessId + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, options))
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("url", session.Url.AbsoluteUri);
            writer.WriteString("token", session.Token);
            writer.WriteNumber("processId", session.ProcessId);
            writer.WriteString("projectDirectory", Path.GetFullPath(projectDirectory));
            writer.WriteEndObject();
        }
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Reads the newest session of a project that still runs, or <see langword="null"/> when none runs.</summary>
    internal static NeoMcpRelaySession? TryRead(string directory, string projectDirectory)
    {
        NeoMcpRelaySession? newest = null;
        var newestTime = DateTime.MinValue;
        foreach (var file in FilesOf(directory, projectDirectory))
        {
            if (Read(file) is not { } session || !IsRunning(session.ProcessId)) continue;
            DateTime written;
            try { written = File.GetLastWriteTimeUtc(file); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            if (newest is null || written > newestTime) (newest, newestTime) = (session, written);
        }
        return newest;
    }

    private static NeoMcpRelaySession? Read(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (!Uri.TryCreate(root.GetProperty("url").GetString(), UriKind.Absolute, out var url) || !url.IsLoopback) return null;
            var token = root.GetProperty("token").GetString();
            var processId = root.GetProperty("processId").GetInt32();
            return string.IsNullOrEmpty(token) ? null : new(url, token, processId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Removes the discovery file of a session, and only that one.</summary>
    internal static void Delete(string directory, string projectDirectory, string token)
    {
        try { File.Delete(PathFor(directory, projectDirectory, token)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The file is gone already, or cannot be removed: it is ignored once the session no longer runs.
        }
    }

    private static bool IsRunning(int processId)
    {
        if (processId == Environment.ProcessId) return true;
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
