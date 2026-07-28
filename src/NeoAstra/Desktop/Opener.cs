// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Diagnostics;

namespace NeoAstra.Desktop.Opener;

/// <summary>Identifies a non-executable content intent accepted by the external opener.</summary>
public enum NeoOpenFileIntent
{
    /// <summary>Plain-text or structured-text document.</summary>
    TextDocument,
    /// <summary>PDF document.</summary>
    PdfDocument,
    /// <summary>Common raster image.</summary>
    Image,
    /// <summary>Common audio content.</summary>
    Audio,
    /// <summary>Common video content.</summary>
    Video,
}

/// <summary>Provides a narrow, typed allow policy for non-executable file content.</summary>
public sealed class NeoOpenFilePolicy
{
    private static readonly IReadOnlyDictionary<NeoOpenFileIntent, string[]> Extensions = new Dictionary<NeoOpenFileIntent, string[]>
    {
        [NeoOpenFileIntent.TextDocument] = [".txt", ".md", ".csv", ".json", ".xml"],
        [NeoOpenFileIntent.PdfDocument] = [".pdf"],
        [NeoOpenFileIntent.Image] = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"],
        [NeoOpenFileIntent.Audio] = [".mp3", ".wav", ".ogg", ".m4a", ".flac"],
        [NeoOpenFileIntent.Video] = [".mp4", ".mov", ".mkv", ".webm"],
    };
    private readonly HashSet<NeoOpenFileIntent> _intents;

    /// <summary>Initializes an explicit set of non-executable content intents.</summary>
    /// <param name="intents">The content intents the application chooses to open.</param>
    public NeoOpenFilePolicy(IEnumerable<NeoOpenFileIntent> intents)
    {
        ArgumentNullException.ThrowIfNull(intents);
        var values = intents.Take(Extensions.Count + 1).ToArray();
        if (values.Length is < 1 || values.Length > Extensions.Count || values.Any(static value => !Enum.IsDefined(value)) || values.Distinct().Count() != values.Length)
            throw new ArgumentException("An open-file policy requires unique supported content intents.", nameof(intents));
        _intents = values.ToHashSet();
    }

    /// <summary>Checks that a path has the fixed non-executable extension for the declared intent.</summary>
    /// <param name="path">Canonical existing path.</param>
    /// <param name="intent">Declared content intent.</param>
    /// <returns><see langword="true"/> when the exact typed intent is enabled and matches.</returns>
    public bool Allows(string path, NeoOpenFileIntent intent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Enum.IsDefined(intent) || !_intents.Contains(intent)) return false;
        var extension = Path.GetExtension(path);
        return Extensions[intent].Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Restricts external URLs by exact scheme, IDN host, and effective port.</summary>
public sealed class NeoUrlScope
{
    private readonly HashSet<string> _origins;

    /// <summary>Initializes an allowlist from absolute origins such as <c>https://example.com</c>.</summary>
    /// <param name="origins">Exact allowed origins.</param>
    public NeoUrlScope(IEnumerable<string> origins)
    {
        ArgumentNullException.ThrowIfNull(origins);
        var values = origins.Take(129).Select(CanonicalOrigin).ToArray();
        if (values.Length is < 1 or > 128 || values.Distinct(StringComparer.Ordinal).Count() != values.Length) throw new ArgumentException("A URL scope requires 1 to 128 unique exact origins.", nameof(origins));
        _origins = new(values, StringComparer.Ordinal);
    }

    /// <summary>Validates an absolute URL without credentials or controls.</summary>
    /// <param name="url">The candidate URL.</param>
    /// <param name="authorized">Receives the normalized URL when allowed.</param>
    /// <returns><see langword="true"/> when the exact origin is allowed.</returns>
    public bool TryAuthorize(Uri url, out Uri? authorized)
    {
        ArgumentNullException.ThrowIfNull(url);
        authorized = null;
        if (!url.IsAbsoluteUri || url.IsFile || url.OriginalString.Length > 4096 || url.OriginalString.Any(char.IsControl) || url.UserInfo.Length != 0 || url.Scheme is not ("http" or "https" or "mailto")) return false;
        try
        {
            if (!_origins.Contains(CanonicalOrigin(url.GetLeftPart(UriPartial.Authority)))) return false;
            authorized = new Uri(url.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped), UriKind.Absolute);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException) { return false; }
    }

    private static string CanonicalOrigin(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 2048 || value.Any(char.IsControl) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.IsFile || uri.UserInfo.Length != 0 || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.Scheme is not ("http" or "https" or "mailto")) throw new ArgumentException("URL origins must be exact HTTP(S)/mailto scheme-host-port values without credentials, paths, queries, or controls.", nameof(value));
        if (uri.Scheme == "mailto")
        {
            // Mailto has no authority and is allowed only as an explicit scheme scope.
            if (!string.Equals(value, "mailto:", StringComparison.Ordinal)) throw new ArgumentException("Mailto scope must be exactly 'mailto:'.", nameof(value));
            return value;
        }
        if (uri.AbsolutePath != "/") throw new ArgumentException("A URL origin cannot contain a path.", nameof(value));
        var host = uri.IdnHost.ToLowerInvariant();
        if (string.IsNullOrEmpty(host)) throw new ArgumentException("A URL origin requires a host.", nameof(value));
        if (host.Contains(':', StringComparison.Ordinal) && host[0] != '[') host = $"[{host}]";
        var port = uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port;
        return $"{uri.Scheme.ToLowerInvariant()}://{host}:{port}";
    }
}

/// <summary>Provides intent-specific external URL/file operations without arbitrary commands or verbs.</summary>
public sealed class NeoExternalOpener
{
    private readonly NeoUrlScope _urls;
    private readonly NeoFileScope _openFiles;
    private readonly NeoFileScope _revealFiles;
    private readonly NeoOpenFilePolicy _openPolicy;

    /// <summary>Initializes explicit URL, open-file, and reveal-file policy.</summary>
    public NeoExternalOpener(NeoUrlScope urls, NeoFileScope openFiles, NeoFileScope revealFiles, NeoOpenFilePolicy openPolicy)
    {
        ArgumentNullException.ThrowIfNull(urls); ArgumentNullException.ThrowIfNull(openFiles); ArgumentNullException.ThrowIfNull(revealFiles); ArgumentNullException.ThrowIfNull(openPolicy);
        _urls = urls; _openFiles = openFiles; _revealFiles = revealFiles; _openPolicy = openPolicy;
        Support = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() ? new(NeoSupportLevel.Native, 1, 1, "Uses only exact URL origins, fixed open/reveal intents, and a typed non-executable content policy; no command, executable content type, shell interpolation, or caller-supplied verb is accepted.") : new(NeoSupportLevel.None, 1, 1, "No supported desktop opener.");
    }

    /// <summary>Gets platform support details.</summary>
    public NeoCapabilityInfo Support { get; }

    /// <summary>Opens one allowed URL in its default handler.</summary>
    public async ValueTask<NeoDesktopStatus> OpenUrlAsync(Uri url, CancellationToken cancellationToken = default)
    {
        if (!_urls.TryAuthorize(url, out var authorized)) return NeoDesktopStatus.Denied;
        return await OpenIntentAsync(authorized!.AbsoluteUri, reveal: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens one allowed existing non-executable file.</summary>
    public async ValueTask<NeoDesktopStatus> OpenFileAsync(string path, NeoOpenFileIntent intent, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(intent)) throw new ArgumentOutOfRangeException(nameof(intent));
        if (!_openFiles.TryAuthorize(path, requireExisting: true, out var canonical)) return _openFiles.IsLexicallyWithin(path) ? NeoDesktopStatus.NotFound : NeoDesktopStatus.Denied;
        if (!File.Exists(canonical) || !_openPolicy.Allows(canonical!, intent)) return NeoDesktopStatus.Denied;
        return await OpenIntentAsync(canonical!, reveal: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reveals one allowed existing file or folder in the file manager.</summary>
    public async ValueTask<NeoDesktopStatus> RevealAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!_revealFiles.TryAuthorize(path, requireExisting: true, out var canonical)) return _revealFiles.IsLexicallyWithin(path) ? NeoDesktopStatus.NotFound : NeoDesktopStatus.Denied;
        return await OpenIntentAsync(canonical!, reveal: true, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<NeoDesktopStatus> OpenIntentAsync(string target, bool reveal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var start = reveal
                    ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false, CreateNoWindow = true }
                    : new ProcessStartInfo(target) { UseShellExecute = true };
                if (reveal) { start.ArgumentList.Add(File.Exists(target) ? "/select," : string.Empty); if (start.ArgumentList[0].Length == 0) start.ArgumentList.Clear(); start.ArgumentList.Add(target); }
                using var process = Process.Start(start);
                return process is null ? NeoDesktopStatus.NoHandler : NeoDesktopStatus.Success;
            }
            var executable = OperatingSystem.IsMacOS() ? "/usr/bin/open" : DesktopProcess.FindTrustedExecutable("/usr/bin/xdg-open", "/usr/local/bin/xdg-open");
            if (string.IsNullOrEmpty(executable)) return NeoDesktopStatus.NoHandler;
            var arguments = OperatingSystem.IsMacOS() && reveal ? new[] { "-R", target } : new[] { reveal && File.Exists(target) ? Path.GetDirectoryName(target)! : target };
            var result = await DesktopProcess.RunAsync(executable, arguments, default, TimeSpan.FromSeconds(30), false, cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 ? NeoDesktopStatus.Success : NeoDesktopStatus.NoHandler;
        }
        catch (OperationCanceledException) { throw; }
        catch (System.ComponentModel.Win32Exception) { return NeoDesktopStatus.NoHandler; }
        catch { return NeoDesktopStatus.Failed; }
    }

}
