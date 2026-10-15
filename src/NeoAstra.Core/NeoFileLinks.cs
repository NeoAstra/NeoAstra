// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra;

/// <summary>Detects links and reparse points without the side effects of <see cref="File.GetAttributes(string)"/>.</summary>
internal static class NeoFileLinks
{
    /// <summary>Reports whether a path is a symbolic link or a reparse point.</summary>
    /// <param name="path">The path to examine; a link is reported even when its target is missing.</param>
    /// <returns><see langword="true"/> for a link or a reparse point; otherwise <see langword="false"/>.</returns>
    /// <exception cref="FileNotFoundException">The path is neither a link nor an existing file or directory.</exception>
    internal static bool IsLink(string path)
    {
        // Windows reports every kind of reparse point (junctions, mount points, cloud placeholders) through the attribute.
        if (OperatingSystem.IsWindows()) return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

        // File.GetAttributes also derives the read-only flag, which asks whether the process belongs to the group of the
        // entry. On iOS that check throws OverflowException for entries that the app does not own, such as "/" and "/var",
        // which a walk from the file-system root meets. A link is exactly what the platform reports as a link target.
        if (new FileInfo(path).LinkTarget is not null) return true;
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("The path does not exist.", path);
        return false;
    }
}
