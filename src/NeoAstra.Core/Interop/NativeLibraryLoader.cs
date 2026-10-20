// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Reflection;
using System.Runtime.InteropServices;
using NeoAstra.Interop.Generated;

namespace NeoAstra.Interop;

internal static class NativeLibraryLoader
{
    internal const uint ExpectedAbiMajor = NeoNativeAbi.Major;
    private static readonly object Sync = new();
    private static readonly List<NeoNativeLibraryProbe> Probes = [];
    private static nint _loadedHandle;
    private static bool _validated;

    static NativeLibraryLoader()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryLoader).Assembly, Resolve);
    }

    internal static void EnsureLoaded()
    {
        lock (Sync)
        {
            if (_validated)
            {
                return;
            }

            try
            {
                var major = NativeMethods.neoastra_get_abi_version_major();
                var minor = NativeMethods.neoastra_get_abi_version_minor();
                if (!NeoNativeAbi.IsCompatible(major, minor))
                {
                    throw CreateAbiException(major, minor);
                }

                _validated = true;
            }
            catch (NeoAstraNativeLibraryException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Usually DllNotFoundException or BadImageFormatException from the .NET default probing that runs
                // after the resolver, or EntryPointNotFoundException from a library of another version.
                throw CreateLoadException([.. Probes], ex);
            }
        }
    }

    /// <summary>Loads the library from the first of <paramref name="candidates"/> that loads, the way the resolver does.</summary>
    /// <param name="candidates">The paths to try, in order.</param>
    /// <returns>The handle of the loaded library.</returns>
    /// <exception cref="NeoAstraNativeLibraryException">No candidate could be loaded; the exception describes each attempt.</exception>
    internal static nint LoadFrom(IEnumerable<string> candidates)
    {
        var probes = new List<NeoNativeLibraryProbe>();
        var handle = Probe(candidates, probes);
        return handle != 0 ? handle : throw CreateLoadException(probes, null);
    }

    private static NeoAstraNativeLibraryException CreateLoadException(IReadOnlyList<NeoNativeLibraryProbe> probes, Exception? innerException) =>
        NativeLibraryDiagnostics.CreateException(
            probes,
            innerException,
            NativeLibraryDiagnostics.CurrentPlatform,
            RuntimeInformation.ProcessArchitecture,
            RuntimeInformation.RuntimeIdentifier,
            RuntimeInformation.OSDescription);

    private static NeoAstraNativeLibraryException CreateAbiException(uint major, uint minor)
    {
        var platform = NativeLibraryDiagnostics.CurrentPlatform;
        var asset = $"runtimes/{NativeLibraryDiagnostics.GetPortableRuntimeIdentifier(platform, RuntimeInformation.ProcessArchitecture)}/native/{NativeLibraryDiagnostics.GetFileName(platform)}";
        var loaded = Probes.LastOrDefault(static probe => probe.Result == NeoNativeLibraryProbeResult.Loaded);
        return new NeoAstraNativeLibraryException(
            $"The loaded {NativeMethods.LibraryName} native ABI is {major}.{minor}; managed NeoAstra requires ABI major {ExpectedAbiMajor}. " +
            (loaded is null ? string.Empty : $"It was loaded from '{loaded.Path}'. ") +
            $"Install the paired RID asset ({asset} of the same NeoAstra.Core package) or set NEOASTRA_NATIVE_LIBRARY to its full path.",
            [.. Probes],
            [$"Replace the library with the {asset} of the same NeoAstra.Core package version as the managed assemblies, or set NEOASTRA_NATIVE_LIBRARY to its full path."],
            RuntimeInformation.RuntimeIdentifier,
            RuntimeInformation.ProcessArchitecture,
            RuntimeInformation.OSDescription,
            null);
    }

    private static nint Probe(IEnumerable<string> candidates, List<NeoNativeLibraryProbe> probes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in candidates)
        {
            if (!seen.Add(path))
            {
                continue;
            }

            // Loading an absolute path that does not exist fails anyway; skipping it avoids an exception per probe.
            if (Path.IsPathFullyQualified(path) && !File.Exists(path))
            {
                probes.Add(new NeoNativeLibraryProbe(path, NeoNativeLibraryProbeResult.NotFound));
                continue;
            }

            try
            {
                var handle = NativeLibrary.Load(path);
                probes.Add(new NeoNativeLibraryProbe(path, NeoNativeLibraryProbeResult.Loaded));
                return handle;
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                // NativeLibrary.TryLoad would drop this message, which carries dlerror() or the Win32 error.
                probes.Add(NativeLibraryDiagnostics.Inspect(path, ex));
            }
        }

        return 0;
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, NativeMethods.LibraryName, StringComparison.Ordinal))
        {
            return 0;
        }

        lock (Sync)
        {
            if (_loadedHandle != 0)
            {
                return _loadedHandle;
            }

            // Returning 0 lets the .NET default probing run next; EnsureLoaded reports both.
            Probes.Clear();
            _loadedHandle = Probe(GetCandidatePaths(), Probes);
            return _loadedHandle;
        }
    }

    private static IEnumerable<string> GetCandidatePaths()
    {
        var explicitPath = Environment.GetEnvironmentVariable("NEOASTRA_NATIVE_LIBRARY");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return Path.GetFullPath(explicitPath);
        }

        var fileName = NativeLibraryDiagnostics.GetFileName(NativeLibraryDiagnostics.CurrentPlatform);
        var baseDirectory = AppContext.BaseDirectory;
        var portableRid = NativeLibraryDiagnostics.GetPortableRuntimeIdentifier(NativeLibraryDiagnostics.CurrentPlatform, RuntimeInformation.ProcessArchitecture);
        yield return Path.Combine(baseDirectory, fileName);
        yield return Path.Combine(baseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", fileName);
        yield return Path.Combine(baseDirectory, "runtimes", portableRid, "native", fileName);

        var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var current = new DirectoryInfo(baseDirectory);
        for (var index = 0; index < 8 && current is not null; index++, current = current.Parent)
        {
            var artifacts = Path.Combine(current.FullName, "artifacts", "native");
            yield return Path.Combine(current.FullName, "src", "NeoAstra", "runtimes", portableRid, "native", fileName);
            yield return Path.Combine(artifacts, portableRid, fileName);
            yield return Path.Combine(artifacts, $"{platform}-{architecture}-release", fileName);
            yield return Path.Combine(artifacts, $"{platform}-{architecture}-debug", fileName);
        }
    }
}
