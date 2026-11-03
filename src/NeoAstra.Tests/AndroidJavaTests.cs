// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Diagnostics;

namespace NeoAstra.Tests;

// The Java side of the Android backend that has no Android type runs on the JDK of the machine, which Android builds need too.
[TestClass]
public sealed class AndroidJavaTests
{
    [TestMethod]
    public async Task DebuggingSwitch_IsOnWhileAnyViewAsksAndSurvivesAFailingSwitch()
    {
        var output = await RunJavaCheckAsync("NeoAstraDebuggingSwitch.java", "NeoAstraDebuggingSwitchCheck");
        StringAssert.Contains(output, "NeoAstraDebuggingSwitch: ok");
    }

    // Compiles a class of the backend with its check from src/NeoAstra.Tests/Java and runs the check, whose failure exits with 1.
    private static async Task<string> RunJavaCheckAsync(string source, string check)
    {
        var jdk = await FindJdkAsync();
        if (jdk is null) { Assert.Inconclusive("A JDK is needed for the checks of the Java side of the Android backend."); return ""; }
        var (javac, java) = jdk.Value;
        var root = RepositoryRoot();
        var output = Path.Combine(Path.GetTempPath(), "neoastra-java-" + Guid.NewGuid().ToString("N"));
        try
        {
            var compiled = await RunAsync(javac, ["-d", output, Path.Combine(root, "native", "src", "android", "java", "com", "neoastra", "backend", source), Path.Combine(root, "src", "NeoAstra.Tests", "Java", check + ".java")]);
            Assert.AreEqual(0, compiled.ExitCode, compiled.Output);
            var ran = await RunAsync(java, ["-cp", output, "com.neoastra.backend." + check]);
            Assert.AreEqual(0, ran.ExitCode, ran.Output);
            return ran.Output;
        }
        finally
        {
            try { Directory.Delete(output, recursive: true); } catch (IOException) { }
        }
    }

    // The javac and the java of one installation that runs: the one of JAVA_HOME, else the first on the path. macOS has
    // launcher stubs for both in /usr/bin without a JDK, which fail when they run, so a candidate counts only once javac answers.
    private static async Task<(string Javac, string Java)?> FindJdkAsync()
    {
        var name = OperatingSystem.IsWindows() ? ".exe" : "";
        var directories = new List<string>();
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(home)) directories.Add(Path.Combine(home, "bin"));
        directories.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var directory in directories)
        {
            string javac = Path.Combine(directory, "javac" + name), java = Path.Combine(directory, "java" + name);
            if (!File.Exists(javac) || !File.Exists(java)) continue;
            try { if ((await RunAsync(javac, ["-version"])).ExitCode == 0) return (javac, java); }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or TimeoutException) { }
        }
        return null;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string program, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException)
        {
            // A compiler or a JVM that hangs does not outlive the test.
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        return (process.ExitCode, await stdout + await stderr);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "native", "src", "android"))) return directory.FullName;
        throw new DirectoryNotFoundException("The repository root was not found.");
    }
}
