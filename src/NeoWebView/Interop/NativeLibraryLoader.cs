// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Reflection;
using System.Runtime.InteropServices;
using NeoWebView.Interop.Generated;

namespace NeoWebView.Interop;

internal static class NativeLibraryLoader
{
    private const uint ExpectedAbiMajor = 1;
    private const uint ExpectedAbiMinor = 6;
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
                var major = NativeMethods.neo_webview_get_abi_version_major();
                var minor = NativeMethods.neo_webview_get_abi_version_minor();
                if (major != ExpectedAbiMajor || minor != ExpectedAbiMinor)
                {
                    throw new NeoWebViewNativeLibraryException(
                        $"The loaded NeoWebView native ABI is {major}.{minor}; managed NeoWebView requires the paired ABI {ExpectedAbiMajor}.{ExpectedAbiMinor}.");
                }

                _validated = true;
            }
            catch (NeoWebViewNativeLibraryException)
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

    private static NeoWebViewNativeLibraryException CreateLoadException(Exception innerException)
    {
        var attempts = AttemptedPaths.Count == 0
            ? "No development probe paths were available."
            : $"Probed:{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", AttemptedPaths)}";
        return new NeoWebViewNativeLibraryException(
            $"Could not load a compatible '{NativeMethods.LibraryName}' native library for {RuntimeInformation.RuntimeIdentifier} ({RuntimeInformation.ProcessArchitecture}). " +
            $"Package its RID-specific native asset, place it beside the application, or set NEOWEBVIEW_NATIVE_LIBRARY to its full path. {attempts}", innerException);
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
        var explicitPath = Environment.GetEnvironmentVariable("NEOWEBVIEW_NATIVE_LIBRARY");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return Path.GetFullPath(explicitPath);
        }

        var fileName = OperatingSystem.IsWindows()
            ? "neowebview_native.dll"
            : OperatingSystem.IsMacOS() ? "libneowebview_native.dylib" : "libneowebview_native.so";
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
            yield return Path.Combine(current.FullName, "src", "NeoWebView", "runtimes", portableRid, "native", fileName);
            yield return Path.Combine(artifacts, portableRid, fileName);
            yield return Path.Combine(artifacts, $"{platform}-{architecture}-release", fileName);
            yield return Path.Combine(artifacts, $"{platform}-{architecture}-debug", fileName);
        }
    }
}
