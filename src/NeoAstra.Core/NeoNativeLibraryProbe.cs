// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;

namespace NeoAstra;

/// <summary>Tells what happened when NeoAstra tried to load its native library from one path.</summary>
public enum NeoNativeLibraryProbeResult
{
    /// <summary>The library was loaded from the path.</summary>
    Loaded,

    /// <summary>No file exists at the path.</summary>
    NotFound,

    /// <summary>The file is not a native library of the current operating system, for example an empty, truncated, or text file, or a library of another operating system.</summary>
    InvalidImage,

    /// <summary>The file is a native library built for another processor architecture than the process.</summary>
    ArchitectureMismatch,

    /// <summary>The file was found but a library it depends on could not be found or loaded.</summary>
    MissingDependency,

    /// <summary>The file was found but could not be loaded for another reason, given by <see cref="NeoNativeLibraryProbe.Error"/>.</summary>
    Failed,
}

/// <summary>Describes one path NeoAstra tried when it loaded its native library, and why it failed.</summary>
public sealed class NeoNativeLibraryProbe
{
    internal NeoNativeLibraryProbe(
        string path,
        NeoNativeLibraryProbeResult result,
        string? error = null,
        int nativeErrorCode = 0,
        IReadOnlyList<Architecture>? imageArchitectures = null,
        IReadOnlyList<string>? missingDependencies = null)
    {
        Path = path;
        Result = result;
        Error = error;
        NativeErrorCode = nativeErrorCode;
        ImageArchitectures = imageArchitectures ?? [];
        MissingDependencies = missingDependencies ?? [];
    }

    /// <summary>Gets the path that was tried.</summary>
    public string Path { get; }

    /// <summary>Gets the outcome of the attempt.</summary>
    public NeoNativeLibraryProbeResult Result { get; }

    /// <summary>Gets the error text of the platform loader, which includes the text of <c>dlerror</c> on Linux and macOS; <see langword="null"/> when the library was loaded or the file was not found.</summary>
    public string? Error { get; }

    /// <summary>Gets the Win32 error code of the failed load on Windows, such as 126 for a missing module or 11 and 193 for a bad image; 0 when there is none.</summary>
    public int NativeErrorCode { get; }

    /// <summary>Gets the processor architectures the file is built for, read from its header; empty when the file was not found or its header is not recognized.</summary>
    public IReadOnlyList<Architecture> ImageArchitectures { get; }

    /// <summary>Gets the libraries the file depends on that could not be found, when the platform names them; empty otherwise.</summary>
    /// <remarks>On Linux and macOS the names come from the loader error. On Windows they are the direct imports of the file that are in none of the directories Windows searches by default; a missing dependency of a dependency is not listed.</remarks>
    public IReadOnlyList<string> MissingDependencies { get; }

    /// <summary>Returns the path and the outcome of the attempt.</summary>
    /// <returns>A one-line description of the attempt.</returns>
    public override string ToString() => $"{Path}: {Result}";
}
