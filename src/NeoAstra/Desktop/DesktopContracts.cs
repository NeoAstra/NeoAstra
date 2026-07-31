// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Desktop;

internal interface INeoApplicationBoundDesktopService
{
    void BindApplication(NeoApplication application);
}

/// <summary>Identifies a portable desktop-service outcome without exposing native error text.</summary>
public enum NeoDesktopStatus
{
    /// <summary>The operation succeeded.</summary>
    Success,
    /// <summary>The user canceled normal native UX.</summary>
    Canceled,
    /// <summary>The platform does not support the operation.</summary>
    Unsupported,
    /// <summary>Application or renderer policy denied the operation.</summary>
    Denied,
    /// <summary>The target was not found.</summary>
    NotFound,
    /// <summary>No operating-system handler is registered.</summary>
    NoHandler,
    /// <summary>An existing native registration conflicts.</summary>
    Conflict,
    /// <summary>Secure storage is locked or user interaction was denied.</summary>
    Locked,
    /// <summary>Persisted protected data is corrupt.</summary>
    Corrupt,
    /// <summary>A configured bounded resource limit was reached.</summary>
    LimitExceeded,
    /// <summary>The operating system reported a contained failure.</summary>
    Failed,
}

/// <summary>Contains a typed desktop-service result and safe diagnostic code.</summary>
/// <typeparam name="T">Owned result value type.</typeparam>
/// <param name="Status">Portable outcome.</param>
/// <param name="Value">Owned result value when successful.</param>
/// <param name="Code">Optional stable non-sensitive diagnostic code.</param>
public readonly record struct NeoDesktopResult<T>(NeoDesktopStatus Status, T? Value = default, string? Code = null)
{
    /// <summary>Gets whether the operation succeeded.</summary>
    public bool IsSuccess => Status == NeoDesktopStatus.Success;

    /// <summary>Creates a successful result.</summary>
    /// <param name="value">The owned result.</param>
    /// <returns>A successful result.</returns>
    public static NeoDesktopResult<T> Success(T value) => new(NeoDesktopStatus.Success, value);

    /// <summary>Creates a result without a value.</summary>
    /// <param name="status">The non-success outcome.</param>
    /// <param name="code">Optional stable safe code.</param>
    /// <returns>The result.</returns>
    public static NeoDesktopResult<T> Failure(NeoDesktopStatus status, string? code = null) => new(status, default, code);
}

/// <summary>Contains common hard bounds for official desktop services.</summary>
public static class NeoDesktopLimits
{
    /// <summary>Maximum copied clipboard payload bytes.</summary>
    public const int MaximumClipboardBytes = 16 * 1024 * 1024;
    /// <summary>Maximum notification action count.</summary>
    public const int MaximumNotificationActions = 4;
    /// <summary>Maximum drag/drop item count.</summary>
    public const int MaximumDropItems = 256;
    /// <summary>Maximum safe-storage secret bytes.</summary>
    public const int MaximumSecretBytes = 64 * 1024;
    /// <summary>Maximum bounded external process output bytes.</summary>
    public const int MaximumProcessOutputBytes = 1024 * 1024;
}

/// <summary>Restricts canonical filesystem paths to explicitly configured roots.</summary>
public sealed class NeoFileScope
{
    private readonly string[] _lexicalRoots;
    private readonly string[] _roots;
    private readonly StringComparison _comparison;

    /// <summary>Initializes a scope from absolute roots.</summary>
    /// <param name="roots">Existing or application-controlled absolute roots.</param>
    /// <exception cref="ArgumentException">A root is relative, malformed, duplicated, or unbounded.</exception>
    public NeoFileScope(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var comparer = StringComparerForPlatform();
        var lexicalRoots = roots.Take(129).Select(root => Canonicalize(root, requireExisting: false)).ToArray();
        if (lexicalRoots.Length is < 1 or > 128 || lexicalRoots.Distinct(comparer).Count() != lexicalRoots.Length) throw new ArgumentException("A file scope requires 1 to 128 unique absolute roots.", nameof(roots));
        var values = lexicalRoots
            .Select(root => (Lexical: root, Canonical: Directory.Exists(root) || File.Exists(root) ? Canonicalize(root, requireExisting: true) : root))
            .OrderBy(static value => value.Canonical, comparer)
            .ToArray();
        if (values.Select(static value => value.Canonical).Distinct(comparer).Count() != values.Length) throw new ArgumentException("A file scope requires 1 to 128 unique canonical roots.", nameof(roots));
        _lexicalRoots = values.Select(static value => value.Lexical).ToArray();
        _roots = values.Select(static value => value.Canonical).ToArray();
    }

    /// <summary>Gets canonical roots without exposing mutable storage.</summary>
    public IReadOnlyList<string> Roots => Array.AsReadOnly((string[])_roots.Clone());

    /// <summary>Checks and returns an owned canonical path.</summary>
    /// <param name="path">The candidate path.</param>
    /// <param name="requireExisting">Whether the path must exist and symlinks must resolve.</param>
    /// <param name="canonicalPath">Receives the canonical path only when allowed.</param>
    /// <returns><see langword="true"/> when the path is inside one configured root.</returns>
    public bool TryAuthorize(string path, bool requireExisting, out string? canonicalPath)
    {
        canonicalPath = null;
        try
        {
            var lexical = Canonicalize(path, requireExisting: false);
            if (!IsWithinLexicalRoots(lexical)) return false;
            var candidate = requireExisting ? Canonicalize(lexical, requireExisting: true) : lexical;
            foreach (var root in _roots)
            {
                if (string.Equals(candidate, root, _comparison) || candidate.StartsWith(EnsureSeparator(root), _comparison))
                {
                    canonicalPath = candidate;
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        return false;
    }

    internal bool TryAuthorizeCreatableFile(string path, out string? canonicalPath)
    {
        canonicalPath = null;
        try
        {
            var full = Canonicalize(path, requireExisting: false);
            if (!IsWithinLexicalRoots(full)) return false;
            var parent = Path.GetDirectoryName(full);
            var name = Path.GetFileName(full);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name)) return false;
            var resolvedParent = Canonicalize(parent, requireExisting: true);
            var candidate = Path.Combine(resolvedParent, name);
            foreach (var root in _roots)
            {
                if (string.Equals(candidate, root, _comparison) || candidate.StartsWith(EnsureSeparator(root), _comparison)) { canonicalPath = candidate; return true; }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        return false;
    }

    internal bool IsLexicallyWithin(string path)
    {
        try { return IsWithinLexicalRoots(Canonicalize(path, requireExisting: false)); }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    internal static string Canonicalize(string path, bool requireExisting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > 32_768 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("A path must be a bounded absolute path.", nameof(path));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!requireExisting) return full;
        var root = Path.GetPathRoot(full) ?? throw new ArgumentException("The path has no filesystem root.", nameof(path));
        var current = root;
        foreach (var segment in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : File.Exists(current) ? new FileInfo(current) : throw new FileNotFoundException("The scoped path was not found.");
            current = Path.GetFullPath(info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName);
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    private static string EnsureSeparator(string path) => Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
    private bool IsWithinRoots(string candidate) => _roots.Any(root => string.Equals(candidate, root, _comparison) || candidate.StartsWith(EnsureSeparator(root), _comparison));
    private bool IsWithinLexicalRoots(string candidate) => IsWithinRoots(candidate) || _lexicalRoots.Any(root => string.Equals(candidate, root, _comparison) || candidate.StartsWith(EnsureSeparator(root), _comparison));
    private static StringComparer StringComparerForPlatform() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
