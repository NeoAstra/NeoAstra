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
    private static readonly List<string> AttemptedPaths = [];
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
                    throw new NeoAstraNativeLibraryException(
                        $"The loaded {NativeMethods.LibraryName} native ABI is {major}.{minor}; managed NeoAstra requires ABI major {ExpectedAbiMajor}. " +
                        "Install the paired RID asset or set NEOASTRA_NATIVE_LIBRARY to its full path.");
                }

                _validated = true;
            }
            catch (NeoAstraNativeLibraryException)
            {
                throw;
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                throw CreateLoadException(ex);
            }
            catch (Exception ex)
            {
                throw CreateLoadException(ex);
            }
        }
    }

    private static NeoAstraNativeLibraryException CreateLoadException(Exception innerException)
    {
        var attempts = AttemptedPaths.Count == 0
            ? "No development probe paths were available."
            : $"Probed:{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", AttemptedPaths)}";
        return new NeoAstraNativeLibraryException(
            $"Could not load a compatible '{NativeMethods.LibraryName}' native library for {RuntimeInformation.RuntimeIdentifier} ({RuntimeInformation.ProcessArchitecture}). " +
            $"Package its RID-specific native asset, place it beside the application, or set NEOASTRA_NATIVE_LIBRARY to its full path. {attempts}", innerException);
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

            foreach (var path in GetCandidatePaths())
            {
                if (!AttemptedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    AttemptedPaths.Add(path);
                }

                if (NativeLibrary.TryLoad(path, out _loadedHandle))
                {
                    return _loadedHandle;
                }
            }

            return 0;
        }
    }

    private static IEnumerable<string> GetCandidatePaths()
    {
        var explicitPath = Environment.GetEnvironmentVariable("NEOASTRA_NATIVE_LIBRARY");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return Path.GetFullPath(explicitPath);
        }

        var fileName = OperatingSystem.IsWindows()
            ? "neoastra_native.dll"
            : OperatingSystem.IsMacOS() ? "libneoastra_native.dylib" : "libneoastra_native.so";
        var baseDirectory = AppContext.BaseDirectory;
        var portableRid = $"{(OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux")}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
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
