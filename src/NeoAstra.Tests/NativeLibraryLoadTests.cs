// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;
using System.Text;
using NeoAstra.Interop;

namespace NeoAstra.Tests;

[TestClass]
public sealed class NativeLibraryLoadTests
{
    private const string LinuxPath = "/app/libneoastra_native.so";
    private const string DotNetLinuxPrefix = "Unable to load shared library '/app/libneoastra_native.so' or one of its dependencies. In order to help diagnose loading problems, consider using a tool like strace. If you're using glibc, consider setting the LD_DEBUG environment variable: ";

    [TestMethod]
    public void Linux_MissingWebKitGtk_NamesTheLibraryAndThePackagesToInstall()
    {
        var error = DotNetLinuxPrefix + "libwebkitgtk-6.0.so.4: cannot open shared object file: No such file or directory";
        var probe = NativeLibraryDiagnostics.Classify(LinuxPath, error, 0, NativePlatform.Linux, Architecture.X64, Elf(Architecture.X64), []);

        Assert.AreEqual(NeoNativeLibraryProbeResult.MissingDependency, probe.Result);
        CollectionAssert.AreEqual(new[] { "libwebkitgtk-6.0.so.4" }, probe.MissingDependencies.ToArray());

        var exception = Create([probe, new NeoNativeLibraryProbe("/app/runtimes/linux-x64/native/libneoastra_native.so", NeoNativeLibraryProbeResult.NotFound)], NativePlatform.Linux, Architecture.X64, "linux-x64");
        StringAssert.Contains(exception.Message, "linux-x64 (X64) on Ubuntu 24.04");
        StringAssert.Contains(exception.Message, $"{LinuxPath}: missing dependency libwebkitgtk-6.0.so.4");
        StringAssert.Contains(exception.Message, "libwebkitgtk-6.0.so.4: cannot open shared object file: No such file or directory");
        StringAssert.Contains(exception.Message, "/app/runtimes/linux-x64/native/libneoastra_native.so");
        StringAssert.Contains(exception.Message, "sudo apt-get install libgtk-4-1 libwebkitgtk-6.0-4");
        StringAssert.Contains(exception.Message, "sudo dnf install gtk4 webkitgtk6.0");
        StringAssert.Contains(exception.Message, "NEOASTRA_NATIVE_LIBRARY");
        StringAssert.Contains(exception.Message, NativeLibraryDiagnostics.TroubleshootingUrl);
        Assert.AreEqual(2, exception.Probes.Count);
        Assert.AreEqual("linux-x64", exception.RuntimeIdentifier);
        Assert.AreEqual(Architecture.X64, exception.ProcessArchitecture);
        Assert.AreEqual("Ubuntu 24.04", exception.OperatingSystemDescription);
        Assert.IsTrue(exception.Guidance.Any(static line => line.Contains("libwebkitgtk-6.0-4", StringComparison.Ordinal)));
        Assert.IsFalse(exception.Guidance.Any(static line => line.StartsWith("No usable", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Linux_OldGlibc_AsksForANewerDistribution()
    {
        var error = DotNetLinuxPrefix + "/lib/x86_64-linux-gnu/libc.so.6: version `GLIBC_2.38' not found (required by /app/libneoastra_native.so)";
        var probe = NativeLibraryDiagnostics.Classify(LinuxPath, error, 0, NativePlatform.Linux, Architecture.X64, Elf(Architecture.X64), []);

        Assert.AreEqual(NeoNativeLibraryProbeResult.Failed, probe.Result);
        var exception = Create([probe], NativePlatform.Linux, Architecture.X64, "linux-x64");
        StringAssert.Contains(exception.Message, "GLIBC_2.38");
        StringAssert.Contains(exception.Message, "Ubuntu 24.04 or later");
    }

    [TestMethod]
    public void Linux_Musl_IsReportedAsUnsupported()
    {
        var exception = Create([new NeoNativeLibraryProbe(LinuxPath, NeoNativeLibraryProbeResult.NotFound)], NativePlatform.Linux, Architecture.X64, "linux-musl-x64");
        StringAssert.Contains(exception.Message, "this process runs as linux-musl-x64 (X64)");
        StringAssert.Contains(exception.Message, "No usable libneoastra_native.so was found");
    }

    [TestMethod]
    public void MacOS_LibraryBuiltForNewerSystem_GivesTheMinimumRelease()
    {
        const string path = "/app/libneoastra_native.dylib";
        const string error = "Unable to load shared library '/app/libneoastra_native.dylib' or one of its dependencies: dlopen(/app/libneoastra_native.dylib, 0x0001): tried: '/app/libneoastra_native.dylib' (mach-o file (/app/libneoastra_native.dylib), but incompatible platform (have 'macOS 26.0', need 'macOS 15.5'), built for macOS 26.0 which is newer than running OS)";
        var probe = NativeLibraryDiagnostics.Classify(path, error, 0, NativePlatform.MacOS, Architecture.Arm64, new NativeImage(NativeImageFormat.MachO, [Architecture.Arm64]), []);

        Assert.AreEqual(NeoNativeLibraryProbeResult.Failed, probe.Result);
        var exception = Create([probe], NativePlatform.MacOS, Architecture.Arm64, "osx-arm64");
        StringAssert.Contains(exception.Message, "newer than running OS");
        StringAssert.Contains(exception.Message, "the osx-arm64 library macOS 26 or later");
    }

    [TestMethod]
    public void MacOS_MissingFramework_IsNamed()
    {
        const string path = "/app/libneoastra_native.dylib";
        const string error = "dlopen(/app/libneoastra_native.dylib, 0x0001): Library not loaded: /System/Library/Frameworks/WebKit.framework/Versions/A/WebKit\n  Referenced from: <UUID> /app/libneoastra_native.dylib\n  Reason: tried: '/System/Library/Frameworks/WebKit.framework/Versions/A/WebKit' (no such file)";
        var probe = NativeLibraryDiagnostics.Classify(path, error, 0, NativePlatform.MacOS, Architecture.X64, new NativeImage(NativeImageFormat.MachO, [Architecture.X64, Architecture.Arm64]), []);

        Assert.AreEqual(NeoNativeLibraryProbeResult.MissingDependency, probe.Result);
        CollectionAssert.AreEqual(new[] { "/System/Library/Frameworks/WebKit.framework/Versions/A/WebKit" }, probe.MissingDependencies.ToArray());
        var exception = Create([probe], NativePlatform.MacOS, Architecture.X64, "osx-x64");
        StringAssert.Contains(exception.Message, "the osx-x64 library needs macOS 15 or later");
        // Every line of a multi-line dlerror stays under its path.
        StringAssert.Contains(exception.Message, Environment.NewLine + "        Referenced from:");
    }

    [TestMethod]
    public void Windows_MissingVisualCppRuntime_PointsToTheRedistributable()
    {
        const string path = @"C:\app\neoastra_native.dll";
        var error = new DllNotFoundException(@"Unable to load DLL 'C:\app\neoastra_native.dll' or one of its dependencies: The specified module could not be found. (0x8007007E)");
        var code = NativeLibraryDiagnostics.GetWin32ErrorCode(error);
        Assert.AreEqual(126, code);

        var probe = NativeLibraryDiagnostics.Classify(path, error.Message, code, NativePlatform.Windows, Architecture.X64, Pe(Architecture.X64), ["MSVCP140.dll", "VCRUNTIME140.dll"]);
        Assert.AreEqual(NeoNativeLibraryProbeResult.MissingDependency, probe.Result);
        Assert.AreEqual(126, probe.NativeErrorCode);

        var exception = Create([probe], NativePlatform.Windows, Architecture.X64, "win-x64");
        StringAssert.Contains(exception.Message, @"C:\app\neoastra_native.dll: missing dependency MSVCP140.dll, VCRUNTIME140.dll (Win32 error 126). Unable to load DLL");
        StringAssert.Contains(exception.Message, "Microsoft Visual C++ Redistributable for x64");
        StringAssert.Contains(exception.Message, "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist");
    }

    [TestMethod]
    public void Windows_UnknownMissingDependency_StillSuggestsTheVisualCppRuntime()
    {
        var probe = NativeLibraryDiagnostics.Classify(@"C:\app\neoastra_native.dll", "The specified module could not be found. (0x8007007E)", 126, NativePlatform.Windows, Architecture.Arm64, Pe(Architecture.Arm64), []);
        Assert.AreEqual(NeoNativeLibraryProbeResult.MissingDependency, probe.Result);
        var exception = Create([probe], NativePlatform.Windows, Architecture.Arm64, "win-arm64");
        StringAssert.Contains(exception.Message, "a library it depends on could not be loaded");
        StringAssert.Contains(exception.Message, "Redistributable for arm64");
    }

    [TestMethod]
    public void Windows_LibraryOfAnotherArchitecture_IsAnArchitectureMismatch()
    {
        var error = new BadImageFormatException("An attempt was made to load a program with an incorrect format. (0x8007000B)") { HResult = unchecked((int)0x8007000B) };
        var code = NativeLibraryDiagnostics.GetWin32ErrorCode(error);
        Assert.AreEqual(11, code);

        var probe = NativeLibraryDiagnostics.Classify(@"C:\app\neoastra_native.dll", error.Message, code, NativePlatform.Windows, Architecture.Arm64, Pe(Architecture.X64), []);
        Assert.AreEqual(NeoNativeLibraryProbeResult.ArchitectureMismatch, probe.Result);
        var exception = Create([probe], NativePlatform.Windows, Architecture.Arm64, "win-arm64");
        StringAssert.Contains(exception.Message, @"C:\app\neoastra_native.dll: built for X64, not Arm64");
        StringAssert.Contains(exception.Message, "use runtimes/win-arm64/native/neoastra_native.dll");
        StringAssert.Contains(exception.Message, "dotnet publish -r win-arm64");
    }

    [TestMethod]
    public void LibraryOfAnotherOperatingSystem_IsAnInvalidImage()
    {
        var probe = NativeLibraryDiagnostics.Classify(LinuxPath, "invalid ELF header", 0, NativePlatform.Linux, Architecture.X64, Pe(Architecture.X64), []);
        Assert.AreEqual(NeoNativeLibraryProbeResult.InvalidImage, probe.Result);
        StringAssert.Contains(Create([probe], NativePlatform.Linux, Architecture.X64, "linux-x64").Message, "not a valid Linux native library");
    }

    [TestMethod]
    public void NothingFound_ListsEveryPathAndHowToProvideTheLibrary()
    {
        var exception = Create(
            [new(@"C:\app\neoastra_native.dll", NeoNativeLibraryProbeResult.NotFound), new(@"C:\app\runtimes\win-x64\native\neoastra_native.dll", NeoNativeLibraryProbeResult.NotFound)],
            NativePlatform.Windows,
            Architecture.X64,
            "win-x64",
            new DllNotFoundException("Unable to load DLL 'neoastra_native' or one of its dependencies: The specified module could not be found. (0x8007007E)"));

        StringAssert.Contains(exception.Message, "Could not load the 'neoastra_native' native library (neoastra_native.dll) for win-x64 (X64)");
        StringAssert.Contains(exception.Message, @"C:\app\neoastra_native.dll");
        StringAssert.Contains(exception.Message, @"C:\app\runtimes\win-x64\native\neoastra_native.dll");
        StringAssert.Contains(exception.Message, "The default .NET probing also failed: Unable to load DLL 'neoastra_native'");
        StringAssert.Contains(exception.Message, "copies runtimes/win-x64/native/neoastra_native.dll beside the application");
        Assert.IsInstanceOfType<DllNotFoundException>(exception.InnerException);
    }

    [TestMethod]
    public void ReadImage_RecognizesTheHeadersOfEachPlatform()
    {
        var elf = new byte[64];
        "\u007FELF"u8.CopyTo(elf);
        elf[5] = 1;
        elf[18] = 183;
        Assert.AreEqual(NativeImageFormat.Elf, NativeLibraryDiagnostics.ReadImage(elf).Format);
        CollectionAssert.AreEqual(new[] { Architecture.Arm64 }, NativeLibraryDiagnostics.ReadImage(elf).Architectures.ToArray());

        byte[] fat = [0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 2, 0x01, 0, 0, 0x07, .. new byte[16], 0x01, 0, 0, 0x0C, .. new byte[16]];
        var universal = NativeLibraryDiagnostics.ReadImage(fat);
        Assert.AreEqual(NativeImageFormat.MachO, universal.Format);
        CollectionAssert.AreEqual(new[] { Architecture.X64, Architecture.Arm64 }, universal.Architectures.ToArray());

        Assert.AreEqual(NativeImageFormat.Unrecognized, NativeLibraryDiagnostics.ReadImage([]).Format);
        Assert.AreEqual(NativeImageFormat.Unrecognized, NativeLibraryDiagnostics.ReadImage("not a library"u8).Format);
    }

    [TestMethod]
    public void ReadPeImports_ListsTheDllsTheShippedLibraryNeeds()
    {
        foreach (var rid in new[] { "win-x64", "win-arm64" })
        {
            var bytes = File.ReadAllBytes(FindRepositoryFile("src", "NeoAstra.Core", "runtimes", rid, "native", "neoastra_native.dll"));
            var imports = NativeLibraryDiagnostics.ReadPeImports(bytes);
            CollectionAssert.Contains(imports.ToArray(), "KERNEL32.dll", rid);
            Assert.AreEqual(rid == "win-x64" ? Architecture.X64 : Architecture.Arm64, NativeLibraryDiagnostics.ReadImage(bytes).Architectures.Single(), rid);
        }

        Assert.AreEqual(0, NativeLibraryDiagnostics.ReadPeImports("MZ"u8).Count);
    }

    [TestMethod]
    public void LoadFrom_DirectoryWithoutTheLibrary_ReportsEveryPathAsNotFound()
    {
        using var directory = new TempDirectory();
        var fileName = NativeLibraryDiagnostics.GetFileName(NativeLibraryDiagnostics.CurrentPlatform);
        var first = Path.Combine(directory.Path, fileName);
        var second = Path.Combine(directory.Path, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", fileName);

        var exception = Assert.ThrowsExactly<NeoAstraNativeLibraryException>(() => NativeLibraryLoader.LoadFrom([first, second, first]));

        Assert.AreEqual(2, exception.Probes.Count, "a repeated path is tried once");
        Assert.IsTrue(exception.Probes.All(static probe => probe.Result == NeoNativeLibraryProbeResult.NotFound));
        StringAssert.Contains(exception.Message, first);
        StringAssert.Contains(exception.Message, second);
        StringAssert.Contains(exception.Message, RuntimeInformation.RuntimeIdentifier);
        StringAssert.Contains(exception.Message, $"No usable {fileName} was found");
        StringAssert.Contains(exception.Message, "NEOASTRA_NATIVE_LIBRARY");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public void LoadFrom_FileThatIsNotALibrary_ReportsAnInvalidImageWithThePlatformError(int kind)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, NativeLibraryDiagnostics.GetFileName(NativeLibraryDiagnostics.CurrentPlatform));
        File.WriteAllBytes(path, kind == 0 ? [] : "this is not a native library"u8.ToArray());

        var exception = Assert.ThrowsExactly<NeoAstraNativeLibraryException>(() => NativeLibraryLoader.LoadFrom([path]));

        var probe = exception.Probes.Single();
        Assert.AreEqual(NeoNativeLibraryProbeResult.InvalidImage, probe.Result);
        Assert.IsFalse(string.IsNullOrWhiteSpace(probe.Error));
        StringAssert.Contains(exception.Message, probe.Error!.Trim().Split('\n')[0].Trim());
        StringAssert.Contains(exception.Message, "is not a valid");
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual(11, probe.NativeErrorCode);
        }
    }

    [TestMethod]
    public void LoadFrom_LibraryOfTheOtherArchitecture_ReportsAnArchitectureMismatch()
    {
        var platform = NativeLibraryDiagnostics.CurrentPlatform;
        var other = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? Architecture.X64 : Architecture.Arm64;
        var source = FindRepositoryFile("src", "NeoAstra.Core", "runtimes", NativeLibraryDiagnostics.GetPortableRuntimeIdentifier(platform, other), "native", NativeLibraryDiagnostics.GetFileName(platform));
        if (NativeLibraryDiagnostics.ReadImage(source).Architectures.Contains(RuntimeInformation.ProcessArchitecture))
        {
            Assert.Inconclusive("The library of the other architecture also contains the architecture of the process.");
        }

        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, Path.GetFileName(source));
        File.Copy(source, path);

        var exception = Assert.ThrowsExactly<NeoAstraNativeLibraryException>(() => NativeLibraryLoader.LoadFrom([path]));

        var probe = exception.Probes.Single();
        Assert.AreEqual(NeoNativeLibraryProbeResult.ArchitectureMismatch, probe.Result, probe.Error);
        CollectionAssert.Contains(probe.ImageArchitectures.ToArray(), other);
        StringAssert.Contains(exception.Message, $"built for {other}, not {RuntimeInformation.ProcessArchitecture}");
        StringAssert.Contains(exception.Message, $"dotnet publish -r {NativeLibraryDiagnostics.GetPortableRuntimeIdentifier(platform, RuntimeInformation.ProcessArchitecture)}");
    }

    [TestMethod]
    public void LoadFrom_LibraryWithAMissingDependency_NamesTheDependency()
    {
        var platform = NativeLibraryDiagnostics.CurrentPlatform;
        if (platform == NativePlatform.MacOS)
        {
            // Editing a load command of a signed Mach-O breaks its signature, which dyld reports instead.
            Assert.Inconclusive("A missing dependency cannot be simulated by editing a macOS library.");
        }

        var (original, renamed) = platform == NativePlatform.Windows ? ("dwmapi.dll", "dwmapX.dll") : ("libgtk-4.so.1", "libgtk-X.so.1");
        var source = FindRepositoryFile("src", "NeoAstra.Core", "runtimes", NativeLibraryDiagnostics.GetPortableRuntimeIdentifier(platform, RuntimeInformation.ProcessArchitecture), "native", NativeLibraryDiagnostics.GetFileName(platform));
        var bytes = File.ReadAllBytes(source);
        if (Replace(bytes, Encoding.ASCII.GetBytes(original), Encoding.ASCII.GetBytes(renamed)) == 0)
        {
            Assert.Inconclusive($"The native library does not import {original}.");
        }

        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, Path.GetFileName(source));
        File.WriteAllBytes(path, bytes);

        var exception = Assert.ThrowsExactly<NeoAstraNativeLibraryException>(() => NativeLibraryLoader.LoadFrom([path]));

        var probe = exception.Probes.Single();
        Assert.AreEqual(NeoNativeLibraryProbeResult.MissingDependency, probe.Result, probe.Error);
        Assert.AreNotEqual(0, probe.MissingDependencies.Count, probe.Error);
        if (platform == NativePlatform.Windows)
        {
            Assert.AreEqual(126, probe.NativeErrorCode);
            CollectionAssert.Contains(probe.MissingDependencies.ToArray(), renamed);
            StringAssert.Contains(exception.Message, $"Install or restore {renamed}");
        }
        else
        {
            // The loader names the first library it cannot open, which may be another one GTK or WebKitGTK needs.
            StringAssert.Contains(exception.Message, "sudo apt-get install libgtk-4-1 libwebkitgtk-6.0-4");
        }

        StringAssert.Contains(exception.Message, $"{path}: missing dependency {probe.MissingDependencies[0]}");
    }

    private static NeoAstraNativeLibraryException Create(NeoNativeLibraryProbe[] probes, NativePlatform platform, Architecture architecture, string rid, Exception? defaultProbingError = null) =>
        NativeLibraryDiagnostics.CreateException(probes, defaultProbingError, platform, architecture, rid, platform switch
        {
            NativePlatform.Windows => "Microsoft Windows 10.0.26100",
            NativePlatform.MacOS => "Darwin 25.0.0",
            _ => "Ubuntu 24.04",
        });

    private static NativeImage Elf(Architecture architecture) => new(NativeImageFormat.Elf, [architecture]);

    private static NativeImage Pe(Architecture architecture) => new(NativeImageFormat.Pe, [architecture]);

    private static int Replace(byte[] bytes, byte[] value, byte[] replacement)
    {
        var count = 0;
        var start = 0;
        int found;
        while ((found = bytes.AsSpan(start).IndexOf(value)) >= 0)
        {
            replacement.CopyTo(bytes, start + found);
            start += found + value.Length;
            count++;
        }

        return count;
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(path)) return path;
        }

        throw new FileNotFoundException($"Could not locate repository file '{Path.Combine(parts)}' from '{AppContext.BaseDirectory}'.");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"neoastra-load-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
