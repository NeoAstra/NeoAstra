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
/// The discovery file through which <c>dotnet neoastra mcp</c> finds the relay of the development session of the same project.
/// The file lives in a folder of the user, never in the project, so that the token stays out of the repository; on macOS and
/// Linux only the user can read it. A session writes it when its relay starts and removes it when it ends; a file whose
/// session no longer runs is ignored.
/// </summary>
internal static class NeoMcpRelayDiscovery
{
    /// <summary>Gets the folder of the discovery files of the user.</summary>
    internal static string DefaultDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "NeoAstra", "dev-sessions");

    /// <summary>Gets the discovery file of a project: a hash of its full path, so that two projects never share one.</summary>
    internal static string PathFor(string directory, string projectDirectory)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));
        if (!OperatingSystem.IsLinux()) key = key.ToUpperInvariant();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        return Path.Combine(directory, $"mcp-{hash}.json");
    }

    internal static void Write(string directory, string projectDirectory, NeoMcpRelaySession session)
    {
        Directory.CreateDirectory(directory);
        var path = PathFor(directory, projectDirectory);
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

    /// <summary>Reads the session of a project, or <see langword="null"/> when none runs.</summary>
    internal static NeoMcpRelaySession? TryRead(string directory, string projectDirectory)
    {
        var path = PathFor(directory, projectDirectory);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (!Uri.TryCreate(root.GetProperty("url").GetString(), UriKind.Absolute, out var url) || !url.IsLoopback) return null;
            var token = root.GetProperty("token").GetString();
            var processId = root.GetProperty("processId").GetInt32();
            if (string.IsNullOrEmpty(token) || !IsRunning(processId)) return null;
            return new(url, token, processId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Removes the discovery file of a project, unless another session wrote it since.</summary>
    internal static void Delete(string directory, string projectDirectory, string token)
    {
        var path = PathFor(directory, projectDirectory);
        try
        {
            using (var document = JsonDocument.Parse(File.ReadAllBytes(path)))
            {
                if (document.RootElement.GetProperty("token").GetString() != token) return;
            }
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Nothing to remove, or another session owns the file now.
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
