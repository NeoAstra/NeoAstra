// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.Json;

namespace NeoAstra.Tooling;

/// <summary>The platform whose application a development session runs against the development server.</summary>
internal enum NeoDevelopmentPlatform
{
    Desktop,
    IOS,
    Android,
}

/// <summary>An iOS simulator or an Android device or emulator, with its identifier when there is more than one.</summary>
internal sealed record NeoMobileTarget(NeoDevelopmentPlatform Platform, string? Device)
{
    internal static NeoDevelopmentPlatform ParsePlatform(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "desktop" => NeoDevelopmentPlatform.Desktop,
        "ios" => NeoDevelopmentPlatform.IOS,
        "android" => NeoDevelopmentPlatform.Android,
        _ => throw new NeoToolException("development_platform", "The platform must be desktop, ios, or android."),
    };
}

/// <summary>
/// Builds a mobile application, installs it, and starts it against the loopback development server. The iOS simulator shares
/// the network of the machine and takes the address in its launch environment; an Android device or emulator reaches the
/// server through adb reverse and takes the address in a file of the private files of the debug build, which only adb run-as
/// writes. The returned process follows the application until the session ends, and the Android forwarding and address go
/// with it.
/// </summary>
internal sealed class NeoMobileLauncher(INeoProcessFactory processFactory)
{
    internal const string AndroidAddressFile = "files/neoastra-development-url";

    /// <summary>Finds the target framework of the platform, such as net11.0-android in a project for iOS and Android.</summary>
    internal async Task<string> ResolveFrameworkAsync(NeoResolvedProject project, NeoMobileTarget target, IReadOnlyDictionary<string, string> environment, Action<string, string, bool> log, CancellationToken cancellationToken)
    {
        var platform = target.Platform == NeoDevelopmentPlatform.IOS ? "ios" : "android";
        var properties = await PropertiesAsync(project, environment, log, cancellationToken, "TargetFramework", "TargetFrameworks").ConfigureAwait(false);
        return SelectFramework(properties.GetValueOrDefault("TargetFrameworks"), properties.GetValueOrDefault("TargetFramework"), platform);
    }

    /// <summary>Builds the contract for the platform only: the default contract command builds every target framework.</summary>
    /// <remarks>The builds of a mobile application restore for the framework and runtime they build, so the contract restores too.</remarks>
    internal static NeoCommand ContractCommand(NeoCommand command, string framework)
        => command.Arguments.Count >= 2 && Path.GetFileNameWithoutExtension(command.Executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && command.Arguments[1] == "build" && !command.Arguments.Contains("-f") && !command.Arguments.Contains("--framework")
            ? new([.. command.Arguments.Where(static argument => argument != "--no-restore"), "-f", framework])
            : command;

    internal async Task<INeoChildProcess> StartAsync(NeoResolvedProject project, NeoMobileTarget target, string framework, IReadOnlyDictionary<string, string> environment, Action<string, string, bool> log, CancellationToken cancellationToken)
    {
        return target.Platform == NeoDevelopmentPlatform.IOS
            ? await StartIOSAsync(project, target, framework, environment, log, cancellationToken).ConfigureAwait(false)
            : await StartAndroidAsync(project, target, framework, environment, log, cancellationToken).ConfigureAwait(false);
    }

    internal static string SelectFramework(string? frameworks, string? framework, string platform)
    {
        var candidates = (string.IsNullOrEmpty(frameworks) ? framework ?? string.Empty : frameworks).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // net11.0-android or net11.0-ios26.0: the platform, then nothing or its version.
        return candidates.FirstOrDefault(value => System.Text.RegularExpressions.Regex.IsMatch(value, "-" + platform + @"(\d|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            ?? throw new NeoToolException("development_platform", $"The project has no {platform} target framework.");
    }

    private async Task<INeoChildProcess> StartIOSAsync(NeoResolvedProject project, NeoMobileTarget target, string framework, IReadOnlyDictionary<string, string> environment, Action<string, string, bool> log, CancellationToken cancellationToken)
    {
        string[] scope = ["-f", framework, "-c", "Debug", "-r", "iossimulator-arm64"];
        await RunAsync("build", ["dotnet", "build", .. scope], project, environment, log, cancellationToken).ConfigureAwait(false);
        var properties = await PropertiesAsync(project, environment, log, cancellationToken, [$"-p:TargetFramework={framework}", "-p:Configuration=Debug", "-p:RuntimeIdentifier=iossimulator-arm64"], "TargetDir", "ApplicationId").ConfigureAwait(false);
        // The build names the app bundle in a target, so it is found in the output directory instead.
        var output = Path.GetFullPath(Required(properties, "TargetDir"), project.ProjectDirectory);
        var bundle = Directory.Exists(output) ? Directory.GetDirectories(output, "*.app").SingleOrDefault() : null;
        if (bundle is null) throw new NeoToolException("mobile_build", "The build produced no single app bundle for the iOS simulator.");
        var identifier = Required(properties, "ApplicationId");
        var device = target.Device ?? "booted";
        await RunAsync("install", ["xcrun", "simctl", "install", device, bundle], project, environment, log, cancellationToken).ConfigureAwait(false);
        // The simulator passes the variables with this prefix to the application, without the prefix.
        var launch = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase) { ["SIMCTL_CHILD_NEOASTRA_DEV_URL"] = project.DevUrl.AbsoluteUri };
        return processFactory.Start(new("app", new(["xcrun", "simctl", "launch", "--console-pty", "--terminate-running-process", device, identifier]), project.ProjectDirectory, launch, project.SecretEnvironment, (line, error) => log("app", line, error)));
    }

    private async Task<INeoChildProcess> StartAndroidAsync(NeoResolvedProject project, NeoMobileTarget target, string framework, IReadOnlyDictionary<string, string> environment, Action<string, string, bool> log, CancellationToken cancellationToken)
    {
        var adb = AdbPath();
        string[] device = target.Device is null ? [] : ["-s", target.Device];
        string[] scope = ["-f", framework, "-c", "Debug"];
        // An installed build carries its assemblies, so that it starts the same way however it is launched.
        string[] install = ["dotnet", "build", .. scope, "-t:Install", "-p:EmbedAssembliesIntoApk=true", .. target.Device is null ? Array.Empty<string>() : [$"-p:AdbTarget=-s {target.Device}"]];
        await RunAsync("build", install, project, environment, log, cancellationToken).ConfigureAwait(false);
        var identifier = Required(await PropertiesAsync(project, environment, log, cancellationToken, [$"-p:TargetFramework={framework}", "-p:Configuration=Debug"], "ApplicationId").ConfigureAwait(false), "ApplicationId");
        var port = $"tcp:{project.DevUrl.Port}";
        await RunAsync("reverse", [adb, .. device, "reverse", port, port], project, environment, log, cancellationToken).ConfigureAwait(false);
        await RunAsync("address", [adb, .. device, "shell", $"run-as {identifier} sh -c 'mkdir -p files && echo {project.DevUrl.AbsoluteUri} > {AndroidAddressFile}'"], project, environment, log, cancellationToken).ConfigureAwait(false);
        await RunAsync("launch", [adb, .. device, "shell", "am", "force-stop", identifier], project, environment, log, cancellationToken).ConfigureAwait(false);
        await RunAsync("launch", [adb, .. device, "shell", "monkey", "-p", identifier, "-c", "android.intent.category.LAUNCHER", "1"], project, environment, log, cancellationToken).ConfigureAwait(false);
        var logcat = processFactory.Start(new("app", new([adb, .. device, "logcat", "-v", "brief", "-s", "NeoAstra:*", "DOTNET:*", "AndroidRuntime:E", "chromium:*"]), project.ProjectDirectory, environment, project.SecretEnvironment, (line, error) => log("app", line, error)));
        return new AndroidSession(logcat, async () =>
        {
            // The address goes with the session: a later launch of the debug build loads its packaged assets again.
            await RunQuietlyAsync([adb, .. device, "shell", $"run-as {identifier} rm -f {AndroidAddressFile}"], project, environment).ConfigureAwait(false);
            await RunQuietlyAsync([adb, .. device, "reverse", "--remove", port], project, environment).ConfigureAwait(false);
        });
    }

    private async Task RunAsync(string label, IReadOnlyList<string> arguments, NeoResolvedProject project, IReadOnlyDictionary<string, string> environment, Action<string, string, bool> log, CancellationToken cancellationToken)
    {
        await using var process = processFactory.Start(new(label, new(arguments), project.ProjectDirectory, environment, project.SecretEnvironment, (line, error) => log(label, line, error)));
        var exit = await process.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (exit != 0) throw new NeoToolException("mobile_" + label, $"The {label} step of the mobile application failed with exit code {exit}.");
    }

    private async Task RunQuietlyAsync(IReadOnlyList<string> arguments, NeoResolvedProject project, IReadOnlyDictionary<string, string> environment)
    {
        try
        {
            await using var process = processFactory.Start(new("cleanup", new(arguments), project.ProjectDirectory, environment, project.SecretEnvironment, static (_, _) => { }));
            await process.Completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is NeoToolException or TimeoutException) { }
    }

    private Task<Dictionary<string, string>> PropertiesAsync(NeoResolvedProject project, IReadOnlyDictionary<string, string> environment, Action<string, string, bool> log, CancellationToken cancellationToken, params string[] names)
        => PropertiesAsync(project, environment, log, cancellationToken, [], names);

    // MSBuild prints one value for one property and a JSON object for several.
    private async Task<Dictionary<string, string>> PropertiesAsync(NeoResolvedProject project, IReadOnlyDictionary<string, string> environment, Action<string, string, bool> log, CancellationToken cancellationToken, string[] scope, params string[] names)
    {
        var output = new List<string>();
        string[] arguments = ["dotnet", "msbuild", .. scope, .. names.Select(static name => "-getProperty:" + name)];
        await using (var process = processFactory.Start(new("properties", new(arguments), project.ProjectDirectory, environment, project.SecretEnvironment, (line, error) => { if (error) log("properties", line, true); else lock (output) output.Add(line); })))
        {
            var exit = await process.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (exit != 0) throw new NeoToolException("mobile_properties", $"MSBuild could not evaluate the project (exit code {exit}).");
        }
        return ParseProperties(string.Join('\n', output), names);
    }

    internal static Dictionary<string, string> ParseProperties(string output, IReadOnlyList<string> names)
    {
        var text = output.Trim();
        if (names.Count == 1) return new(StringComparer.Ordinal) { [names[0]] = text };
        using var document = JsonDocument.Parse(text);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.GetProperty("Properties").EnumerateObject()) result[property.Name] = property.Value.GetString() ?? string.Empty;
        return result;
    }

    private static string Required(Dictionary<string, string> properties, string name)
        => properties.TryGetValue(name, out var value) && value.Length != 0 ? value : throw new NeoToolException("mobile_properties", $"The project does not define {name}.");

    // adb comes with the Android SDK, which the Android workload finds the same way.
    internal static string AdbPath()
    {
        foreach (var variable in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } root && File.Exists(Path.Combine(root, "platform-tools", OperatingSystem.IsWindows() ? "adb.exe" : "adb")))
                return Path.Combine(root, "platform-tools", OperatingSystem.IsWindows() ? "adb.exe" : "adb");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var standard = OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Android", "sdk", "platform-tools", "adb")
            : OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe")
            : Path.Combine(home, "Android", "Sdk", "platform-tools", "adb");
        return File.Exists(standard) ? standard : "adb";
    }

    private sealed class AndroidSession(INeoChildProcess logcat, Func<Task> cleanup) : INeoChildProcess
    {
        private int _cleaned;

        public int? ExitCode => logcat.ExitCode;
        public Task<int> Completion => logcat.Completion;

        public async Task StopAsync(TimeSpan timeout)
        {
            try { await logcat.StopAsync(timeout).ConfigureAwait(false); }
            finally { await CleanAsync().ConfigureAwait(false); }
        }

        public async ValueTask DisposeAsync()
        {
            try { await logcat.DisposeAsync().ConfigureAwait(false); }
            finally { await CleanAsync().ConfigureAwait(false); }
        }

        private Task CleanAsync() => Interlocked.Exchange(ref _cleaned, 1) == 0 ? cleanup() : Task.CompletedTask;
    }
}
