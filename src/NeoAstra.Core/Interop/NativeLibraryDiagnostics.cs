// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using NeoAstra.Interop.Generated;

namespace NeoAstra.Interop;

internal enum NativePlatform
{
    Windows,
    MacOS,
    Linux,
}

internal enum NativeImageFormat
{
    /// <summary>The header could not be read.</summary>
    Unreadable,

    /// <summary>The header was read but is not a known native library format.</summary>
    Unrecognized,
    Pe,
    Elf,
    MachO,
}

internal readonly record struct NativeImage(NativeImageFormat Format, IReadOnlyList<Architecture> Architectures)
{
    public static NativeImage Unreadable { get; } = new(NativeImageFormat.Unreadable, []);
}

/// <summary>Explains why the native library could not be loaded, from the loader errors and the files themselves.</summary>
/// <remarks>Only runs after a load failed, so the inspection of files costs nothing when the library loads.</remarks>
internal static partial class NativeLibraryDiagnostics
{
    internal const string TroubleshootingUrl = "https://github.com/NeoAstra/NeoAstra/blob/main/doc/platform-support.md#troubleshooting";
    private const string VcRedistUrl = "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist";
    private const int ErrorBadFormat = 11;
    private const int ErrorModNotFound = 126;
    private const int ErrorBadExeFormat = 193;

    internal static NativePlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? NativePlatform.Windows : OperatingSystem.IsMacOS() ? NativePlatform.MacOS : NativePlatform.Linux;

    internal static string GetFileName(NativePlatform platform) => platform switch
    {
        NativePlatform.Windows => "neoastra_native.dll",
        NativePlatform.MacOS => "libneoastra_native.dylib",
        _ => "libneoastra_native.so",
    };

    internal static string GetPortableRuntimeIdentifier(NativePlatform platform, Architecture architecture) =>
        $"{platform switch { NativePlatform.Windows => "win", NativePlatform.MacOS => "osx", _ => "linux" }}-{architecture.ToString().ToLowerInvariant()}";

    /// <summary>Describes a failed load of an existing file from the exception of <see cref="NativeLibrary.Load(string)"/> and the file.</summary>
    internal static NeoNativeLibraryProbe Inspect(string path, Exception error)
    {
        var platform = CurrentPlatform;
        var code = platform == NativePlatform.Windows ? GetWin32ErrorCode(error) : 0;
        var image = ReadImage(path);
        var missing = platform == NativePlatform.Windows && code == ErrorModNotFound ? FindMissingWindowsImports(path) : [];
        return Classify(path, error.Message, code, platform, RuntimeInformation.ProcessArchitecture, image, missing);
    }

    /// <summary>Classifies a failed load from already gathered facts.</summary>
    internal static NeoNativeLibraryProbe Classify(
        string path,
        string error,
        int nativeErrorCode,
        NativePlatform platform,
        Architecture processArchitecture,
        NativeImage image,
        IReadOnlyList<string> windowsMissingImports)
    {
        var expected = platform switch
        {
            NativePlatform.Windows => NativeImageFormat.Pe,
            NativePlatform.MacOS => NativeImageFormat.MachO,
            _ => NativeImageFormat.Elf,
        };
        if (image.Format == NativeImageFormat.Unrecognized || (image.Format != NativeImageFormat.Unreadable && image.Format != expected))
        {
            return new(path, NeoNativeLibraryProbeResult.InvalidImage, error, nativeErrorCode, image.Architectures);
        }

        if (image.Architectures.Count > 0 && !image.Architectures.Contains(processArchitecture))
        {
            return new(path, NeoNativeLibraryProbeResult.ArchitectureMismatch, error, nativeErrorCode, image.Architectures);
        }

        IReadOnlyList<string> missing = platform switch
        {
            NativePlatform.Windows => nativeErrorCode == ErrorModNotFound ? windowsMissingImports : [],
            NativePlatform.MacOS => MatchAll(MacOSMissingLibrary(), error, path),
            _ => MatchAll(LinuxMissingLibrary(), error, path),
        };
        if (missing.Count > 0 || (platform == NativePlatform.Windows && nativeErrorCode == ErrorModNotFound))
        {
            return new(path, NeoNativeLibraryProbeResult.MissingDependency, error, nativeErrorCode, image.Architectures, missing);
        }

        return new(path, NeoNativeLibraryProbeResult.Failed, error, nativeErrorCode, image.Architectures);
    }

    /// <summary>Builds the exception thrown when no path gave a usable library.</summary>
    /// <param name="probes">The paths tried by NeoAstra, in order.</param>
    /// <param name="defaultProbingError">The error of the .NET default probing that ran after NeoAstra's, or of the first call into the library.</param>
    /// <param name="platform">The operating system of the process.</param>
    /// <param name="processArchitecture">The architecture of the process.</param>
    /// <param name="runtimeIdentifier">The runtime identifier of the process.</param>
    /// <param name="operatingSystemDescription">The description of the operating system.</param>
    internal static NeoAstraNativeLibraryException CreateException(
        IReadOnlyList<NeoNativeLibraryProbe> probes,
        Exception? defaultProbingError,
        NativePlatform platform,
        Architecture processArchitecture,
        string runtimeIdentifier,
        string operatingSystemDescription)
    {
        var fileName = GetFileName(platform);
        var guidance = GetGuidance(probes, defaultProbingError, platform, processArchitecture, runtimeIdentifier);
        var message = new StringBuilder();
        var loaded = probes.FirstOrDefault(static probe => probe.Result == NeoNativeLibraryProbeResult.Loaded);
        message.Append(loaded is not null && defaultProbingError is EntryPointNotFoundException
            ? $"The '{NativeMethods.LibraryName}' native library loaded from '{loaded.Path}' does not match this version of NeoAstra.Core"
            : $"Could not load the '{NativeMethods.LibraryName}' native library ({fileName})");
        message.Append(CultureInfo.InvariantCulture, $" for {runtimeIdentifier} ({processArchitecture}) on {operatingSystemDescription}.");
        AppendProbes(message, probes, platform, processArchitecture);
        if (defaultProbingError is not null)
        {
            message.AppendLine().Append(defaultProbingError is EntryPointNotFoundException ? "Error: " : "The default .NET probing also failed: ");
            AppendIndented(message, defaultProbingError.Message);
        }

        message.AppendLine().Append("To fix it:");
        foreach (var line in guidance)
        {
            message.AppendLine().Append("  - ").Append(line);
        }

        return new(message.ToString(), probes, guidance, runtimeIdentifier, processArchitecture, operatingSystemDescription, defaultProbingError);
    }

    internal static IReadOnlyList<string> GetGuidance(
        IReadOnlyList<NeoNativeLibraryProbe> probes,
        Exception? defaultProbingError,
        NativePlatform platform,
        Architecture processArchitecture,
        string runtimeIdentifier)
    {
        var fileName = GetFileName(platform);
        var portableRid = GetPortableRuntimeIdentifier(platform, processArchitecture);
        var asset = $"runtimes/{portableRid}/native/{fileName}";
        var guidance = new List<string>();
        bool Has(NeoNativeLibraryProbeResult result) => probes.Any(probe => probe.Result == result);

        if (processArchitecture is not (Architecture.X64 or Architecture.Arm64) ||
            (platform == NativePlatform.Linux && runtimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase)))
        {
            guidance.Add($"NeoAstra ships native libraries for win-x64, win-arm64, osx-x64, osx-arm64, linux-x64, and linux-arm64 (glibc) only; this process runs as {runtimeIdentifier} ({processArchitecture}). Run the application as one of them.");
        }

        if (Has(NeoNativeLibraryProbeResult.Loaded) && defaultProbingError is EntryPointNotFoundException)
        {
            guidance.Add($"Replace the loaded library with the {asset} of the same NeoAstra.Core package version as the managed assemblies.");
        }

        if (Has(NeoNativeLibraryProbeResult.MissingDependency))
        {
            var missing = probes.SelectMany(static probe => probe.MissingDependencies).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var named = missing.Count > 0 ? $" ({string.Join(", ", missing)})" : string.Empty;
            switch (platform)
            {
                case NativePlatform.Windows:
                    var others = missing.Where(static name => !IsVisualCppRuntime(name)).ToList();
                    if (missing.Count == 0 || others.Count < missing.Count)
                    {
                        guidance.Add($"{fileName} needs the Microsoft Visual C++ runtime (MSVCP140.dll, VCRUNTIME140.dll): install the latest Microsoft Visual C++ Redistributable for {processArchitecture.ToString().ToLowerInvariant()} from {VcRedistUrl}, or copy those DLLs beside the application.");
                    }

                    if (others.Count > 0)
                    {
                        guidance.Add($"Install or restore {string.Join(", ", others)}: Windows looks for it beside {fileName}, in the system directory, and on PATH.");
                    }

                    break;
                case NativePlatform.MacOS:
                    guidance.Add($"A library {fileName} depends on is missing{named}. Cocoa and WebKit are part of macOS, so this usually means the macOS release is too old: the osx-x64 library needs macOS 15 or later, the osx-arm64 library macOS 26 or later.");
                    break;
                default:
                    guidance.Add($"Install GTK 4 and WebKitGTK 6.0, which {fileName} depends on{named}. On Debian and Ubuntu 24.04 or later: sudo apt-get install libgtk-4-1 libwebkitgtk-6.0-4. On Fedora (not checked by NeoAstra): sudo dnf install gtk4 webkitgtk6.0. 'ldd <path to {fileName}> | grep \"not found\"' lists every missing library.");
                    break;
            }
        }

        if (Has(NeoNativeLibraryProbeResult.ArchitectureMismatch))
        {
            var found = probes.Where(static probe => probe.Result == NeoNativeLibraryProbeResult.ArchitectureMismatch)
                .SelectMany(static probe => probe.ImageArchitectures).Distinct().Select(static architecture => architecture.ToString());
            guidance.Add($"A library built for {string.Join("/", found)} was found but this process runs as {processArchitecture}: use {asset}, publish for the architecture of the process (dotnet publish -r {portableRid}), or run the application with the architecture of the library.");
        }

        if (Has(NeoNativeLibraryProbeResult.InvalidImage))
        {
            guidance.Add($"A file named like the library is not a valid {Describe(platform)} native library (empty, truncated, a text file, or a library of another operating system): replace it with {asset} from the NeoAstra.Core package.");
        }

        foreach (var probe in probes)
        {
            if (probe.Result != NeoNativeLibraryProbeResult.Failed || probe.Error is not { } error)
            {
                continue;
            }

            if (platform == NativePlatform.MacOS && error.Contains("newer than", StringComparison.OrdinalIgnoreCase))
            {
                guidance.Add("The library is built for a newer macOS than this one: the osx-x64 library needs macOS 15 or later, the osx-arm64 library macOS 26 or later.");
                break;
            }

            if (platform == NativePlatform.Linux && error.Contains("GLIBC_", StringComparison.Ordinal))
            {
                guidance.Add("The glibc of this system is older than the one the library was built with: NeoAstra needs Ubuntu 24.04 or later, or another glibc distribution of the same age.");
                break;
            }

            if (platform == NativePlatform.Windows && probe.NativeErrorCode is ErrorBadFormat or ErrorBadExeFormat)
            {
                guidance.Add($"A DLL that {fileName} depends on is built for another architecture: install the Microsoft Visual C++ Redistributable for {processArchitecture.ToString().ToLowerInvariant()} from {VcRedistUrl}.");
                break;
            }
        }

        if (probes.Count == 0 || probes.All(static probe => probe.Result == NeoNativeLibraryProbeResult.NotFound))
        {
            guidance.Add($"No usable {fileName} was found. Reference the NeoAstra.Core package (NeoAstra references it) so that the build copies {asset} beside the application, publish for a supported RID (dotnet publish -r {portableRid}), or keep the file beside the application.");
        }

        guidance.Add($"Set the NEOASTRA_NATIVE_LIBRARY environment variable to the full path of a working {fileName} to load it from there first. More help: {TroubleshootingUrl}");
        return guidance;
    }

    /// <summary>Reads the format and architectures of a native library from its header.</summary>
    internal static NativeImage ReadImage(string path)
    {
        Span<byte> header = stackalloc byte[4096];
        int length;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            length = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return NativeImage.Unreadable;
        }

        return ReadImage(header[..length]);
    }

    internal static NativeImage ReadImage(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 0x40 && header[0] == 'M' && header[1] == 'Z')
        {
            var peOffset = BinaryPrimitives.ReadInt32LittleEndian(header[0x3C..]);
            if (peOffset > 0 && peOffset <= header.Length - 6 && header.Slice(peOffset, 4).SequenceEqual("PE\0\0"u8))
            {
                var machine = BinaryPrimitives.ReadUInt16LittleEndian(header[(peOffset + 4)..]);
                Architecture? architecture = machine switch
                {
                    0x014C => Architecture.X86,
                    0x8664 => Architecture.X64,
                    0x01C4 => Architecture.Arm,
                    0xAA64 => Architecture.Arm64,
                    _ => null,
                };
                return new(NativeImageFormat.Pe, architecture is { } known ? [known] : []);
            }

            return new(NativeImageFormat.Unrecognized, []);
        }

        if (header.Length >= 20 && header[..4].SequenceEqual("\u007FELF"u8))
        {
            var machine = header[5] == 2 ? BinaryPrimitives.ReadUInt16BigEndian(header[18..]) : BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);
            Architecture? architecture = machine switch
            {
                3 => Architecture.X86,
                40 => Architecture.Arm,
                62 => Architecture.X64,
                183 => Architecture.Arm64,
                21 => Architecture.Ppc64le,
                22 => Architecture.S390x,
                243 => Architecture.RiscV64,
                258 => Architecture.LoongArch64,
                _ => null,
            };
            return new(NativeImageFormat.Elf, architecture is { } known ? [known] : []);
        }

        if (header.Length >= 8)
        {
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (magic is 0xFEEDFACF or 0xFEEDFACE)
            {
                return new(NativeImageFormat.MachO, MachOArchitecture(BinaryPrimitives.ReadUInt32LittleEndian(header[4..])) is { } known ? [known] : []);
            }

            var fatMagic = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (fatMagic is 0xCAFEBABE or 0xCAFEBABF)
            {
                var entrySize = fatMagic == 0xCAFEBABE ? 20 : 32;
                var count = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
                var architectures = new List<Architecture>();
                for (var index = 0; index < count && 8 + ((index + 1) * entrySize) <= header.Length; index++)
                {
                    if (MachOArchitecture(BinaryPrimitives.ReadUInt32BigEndian(header[(8 + (index * entrySize))..])) is { } known)
                    {
                        architectures.Add(known);
                    }
                }

                return new(NativeImageFormat.MachO, architectures);
            }
        }

        return new(NativeImageFormat.Unrecognized, []);
    }

    /// <summary>Lists the direct imports of a PE file that are in none of the directories Windows searches by default.</summary>
    internal static IReadOnlyList<string> FindMissingWindowsImports(string path)
    {
        IReadOnlyList<string> imports;
        try
        {
            imports = ReadPeImports(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var directories = new List<string>();
        void AddDirectory(string? directory)
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                directories.Add(directory.Trim());
            }
        }

        AddDirectory(Path.GetDirectoryName(path));
        AddDirectory(AppContext.BaseDirectory);
        AddDirectory(Environment.SystemDirectory);
        AddDirectory(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            AddDirectory(directory);
        }

        var missing = new List<string>();
        foreach (var name in imports)
        {
            // API sets are resolved by the loader to system DLLs; they have no file of that name.
            if (name.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!directories.Any(directory => ExistsSafe(directory, name)))
            {
                missing.Add(name);
            }
        }

        return missing;
    }

    /// <summary>Reads the names of the DLLs in the import directory of a PE file; empty when the file is malformed.</summary>
    internal static IReadOnlyList<string> ReadPeImports(ReadOnlySpan<byte> file)
    {
        var names = new List<string>();
        if (file.Length < 0x40 || file[0] != 'M' || file[1] != 'Z')
        {
            return names;
        }

        var pe = BinaryPrimitives.ReadInt32LittleEndian(file[0x3C..]);
        if (pe <= 0 || pe > file.Length - 24 || !file.Slice(pe, 4).SequenceEqual("PE\0\0"u8))
        {
            return names;
        }

        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(file[(pe + 6)..]);
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(file[(pe + 20)..]);
        var optional = pe + 24;
        if (optional + 2 > file.Length)
        {
            return names;
        }

        var directories = BinaryPrimitives.ReadUInt16LittleEndian(file[optional..]) switch
        {
            0x10B => optional + 96,
            0x20B => optional + 112,
            _ => -1,
        };
        if (directories < 0 || directories + 16 > file.Length || directories + 16 > optional + optionalSize)
        {
            return names;
        }

        var importRva = BinaryPrimitives.ReadUInt32LittleEndian(file[(directories + 8)..]);
        var sections = optional + optionalSize;
        if (importRva == 0 || sections + (sectionCount * 40) > file.Length)
        {
            return names;
        }

        var sectionTable = file.Slice(sections, sectionCount * 40);
        long ToOffset(uint rva, ReadOnlySpan<byte> table)
        {
            for (var index = 0; index < table.Length; index += 40)
            {
                var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(table[(index + 8)..]);
                var virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(table[(index + 12)..]);
                var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(table[(index + 16)..]);
                var rawPointer = BinaryPrimitives.ReadUInt32LittleEndian(table[(index + 20)..]);
                if (rva >= virtualAddress && rva < virtualAddress + Math.Max(virtualSize, rawSize))
                {
                    return (long)rawPointer + (rva - virtualAddress);
                }
            }

            return -1;
        }

        var descriptor = ToOffset(importRva, sectionTable);
        for (var index = 0; index < 1024 && descriptor >= 0 && descriptor + 20 <= file.Length; index++, descriptor += 20)
        {
            var entry = file.Slice((int)descriptor, 20);
            var nameRva = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (nameRva == 0)
            {
                break;
            }

            var nameOffset = ToOffset(nameRva, sectionTable);
            if (nameOffset < 0 || nameOffset >= file.Length)
            {
                continue;
            }

            var tail = file[(int)nameOffset..];
            var end = tail.IndexOf((byte)0);
            if (end > 0 && end <= 260)
            {
                names.Add(Encoding.ASCII.GetString(tail[..end]));
            }
        }

        return names;
    }

    internal static int GetWin32ErrorCode(Exception error)
    {
        if ((error.HResult & 0xFFFF0000) == 0x80070000)
        {
            return error.HResult & 0xFFFF;
        }

        // DllNotFoundException carries a generic HRESULT; the loader's own one ends its message.
        var match = Win32HResult().Match(error.Message);
        return match.Success ? int.Parse(match.Groups["code"].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : 0;
    }

    private static void AppendProbes(StringBuilder message, IReadOnlyList<NeoNativeLibraryProbe> probes, NativePlatform platform, Architecture processArchitecture)
    {
        if (probes.Count == 0)
        {
            message.AppendLine().Append("NeoAstra did not probe any path: the .NET runtime resolved the library by itself.");
            return;
        }

        message.AppendLine().Append("Paths tried:");
        foreach (var probe in probes.Where(static probe => probe.Result != NeoNativeLibraryProbeResult.NotFound))
        {
            message.AppendLine().Append("  - ").Append(probe.Path).Append(": ").Append(probe.Result switch
            {
                NeoNativeLibraryProbeResult.Loaded => "loaded",
                NeoNativeLibraryProbeResult.InvalidImage => $"not a valid {Describe(platform)} native library",
                NeoNativeLibraryProbeResult.ArchitectureMismatch => $"built for {string.Join("/", probe.ImageArchitectures)}, not {processArchitecture}",
                NeoNativeLibraryProbeResult.MissingDependency => probe.MissingDependencies.Count > 0
                    ? $"missing dependency {string.Join(", ", probe.MissingDependencies)}"
                    : "a library it depends on could not be loaded",
                _ => "could not be loaded",
            });
            if (probe.Error is { } error)
            {
                // The loader text already carries the system message of a Win32 error.
                message.Append(probe.NativeErrorCode != 0
                    ? string.Create(CultureInfo.InvariantCulture, $" (Win32 error {probe.NativeErrorCode}). ")
                    : ". ");
                AppendIndented(message, error);
            }
        }

        var notFound = probes.Where(static probe => probe.Result == NeoNativeLibraryProbeResult.NotFound).ToList();
        if (notFound.Count > 0)
        {
            message.AppendLine().Append("  - not found:");
            foreach (var probe in notFound)
            {
                message.AppendLine().Append("      ").Append(probe.Path);
            }
        }
    }

    private static void AppendIndented(StringBuilder message, string text) =>
        message.Append(text.Trim().ReplaceLineEndings(Environment.NewLine + "      "));

    private static string Describe(NativePlatform platform) => platform switch
    {
        NativePlatform.Windows => "Windows",
        NativePlatform.MacOS => "macOS",
        _ => "Linux",
    };

    private static bool IsVisualCppRuntime(string name) =>
        name.StartsWith("msvcp140", StringComparison.OrdinalIgnoreCase) || name.StartsWith("vcruntime140", StringComparison.OrdinalIgnoreCase);

    private static bool ExistsSafe(string directory, string name)
    {
        try
        {
            return File.Exists(Path.Combine(directory, name));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Architecture? MachOArchitecture(uint cpuType) => cpuType switch
    {
        7 => Architecture.X86,
        0x01000007 => Architecture.X64,
        12 => Architecture.Arm,
        0x0100000C => Architecture.Arm64,
        _ => null,
    };

    private static List<string> MatchAll(Regex regex, string error, string path)
    {
        var self = Path.GetFileName(path);
        var names = new List<string>();
        foreach (Match match in regex.Matches(error))
        {
            var name = match.Groups["name"].Value;
            if (!string.Equals(Path.GetFileName(name), self, StringComparison.Ordinal) && !names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
        }

        return names;
    }

    // glibc: "libgtk-4.so.1: cannot open shared object file: No such file or directory".
    [GeneratedRegex(@"(?<name>[^\s:'""]+\.so(?:\.[0-9]+)*): cannot open shared object file")]
    private static partial Regex LinuxMissingLibrary();

    // dyld: "Library not loaded: @rpath/libfoo.dylib".
    [GeneratedRegex(@"Library not loaded: '?(?<name>[^\s']+)")]
    private static partial Regex MacOSMissingLibrary();

    [GeneratedRegex(@"\(0x8007(?<code>[0-9A-Fa-f]{4})\)")]
    private static partial Regex Win32HResult();
}
