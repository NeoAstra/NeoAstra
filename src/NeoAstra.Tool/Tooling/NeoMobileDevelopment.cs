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

/// <summary>An iOS simulator or iPhone, or an Android device or emulator, with its identifier when there is more than one.</summary>
internal sealed record NeoMobileTarget(NeoDevelopmentPlatform Platform, string? Device)
{
    /// <summary>Gets whether the target is an iPhone or iPad rather than a simulator, as the launcher found it.</summary>
    internal bool IsPhysical { get; init; }

    /// <summary>Gets whether the target is an iPhone or iPad rather than a simulator.</summary>
    internal bool IsPhysicalIOSDevice => Platform == NeoDevelopmentPlatform.IOS && Device is not null && IsPhysical;

    /// <summary>Whether an identifier has the form of a device rather than of a simulator, which has a UUID.</summary>
    internal static bool LooksLikeDevice(string device) => !Guid.TryParse(device, out _);

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

    /// <summary>The file of the private files of the debug build with the address of the relay and the token of the session.</summary>
    internal const string AndroidRelayFile = "files/neoastra-development-mcp";

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
        if (target.IsPhysicalIOSDevice) return await StartIOSDeviceAsync(project, target.Device!, framework, environment, log, cancellationToken).ConfigureAwait(false);
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
        // The simulator shares the loopback interface of the machine, so the application reaches the relay at its own address.
        foreach (var name in new[] { NeoDevelopmentOrchestrator.McpUrlVariable, NeoDevelopmentOrchestrator.McpTokenVariable })
            if (environment.TryGetValue(name, out var value)) launch["SIMCTL_CHILD_" + name] = value;
        return processFactory.Start(new("app", new(["xcrun", "simctl", "launch", "--console-pty", "--terminate-running-process", device, identifier]), project.ProjectDirectory, launch, project.SecretEnvironment, (line, error) => log("app", line, error)));
    }

    // An iPhone does not reach the loopback interface of the machine, which is the only place a development server may be. With
    // the MCP plugin, the application tunnels the server through the relay on the address of the machine in the local network,
    // so its view loads the page from 127.0.0.1 as in a simulator; without it, the application loads the assets of its build.
    private async Task<INeoChildProcess> StartIOSDeviceAsync(NeoResolvedProject project, string device, string framework, IReadOnlyDictionary<string, string> environment, Action<string, string, bool> log, CancellationToken cancellationToken)
    {
        string[] scope = ["-f", framework, "-c", "Debug", "-r", "ios-arm64"];
        string[] properties = [$"-p:TargetFramework={framework}", "-p:Configuration=Debug", "-p:RuntimeIdentifier=ios-arm64"];
        // The iPhone reaches the relay only on the address of the machine in the local network, not on its loopback interface.
        var reachable = environment.TryGetValue(NeoDevelopmentOrchestrator.McpUrlVariable, out var relayUrl) && Uri.TryCreate(relayUrl, UriKind.Absolute, out var relay) && !relay.IsLoopback;
        var tunnel = reachable && await ReferencesMcpAsync(project, environment, log, cancellationToken, properties).ConfigureAwait(false);
        var build = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase) { ["NeoAstraBuildFrontend"] = tunnel ? "false" : "true" };
        // A project without a signing identity of its own is signed with the Apple Development identity and the profile of its team
        // that Xcode manages.
        var identity = (await PropertiesAsync(project, build, log, cancellationToken, properties, "CodesignKey").ConfigureAwait(false)).GetValueOrDefault("CodesignKey");
        string[] signing = string.IsNullOrWhiteSpace(identity) ? ["-p:CodesignKey=Apple Development"] : [];
        await RunAsync("build", ["dotnet", "build", .. scope, .. signing], project, build, log, cancellationToken).ConfigureAwait(false);
        var output = await PropertiesAsync(project, build, log, cancellationToken, properties, "TargetDir", "ApplicationId").ConfigureAwait(false);
        var directory = Path.GetFullPath(Required(output, "TargetDir"), project.ProjectDirectory);
        var bundle = Directory.Exists(directory) ? Directory.GetDirectories(directory, "*.app").SingleOrDefault() : null;
        if (bundle is null) throw new NeoToolException("mobile_build", "The build produced no single app bundle for the device.");
        var identifier = Required(output, "ApplicationId");
        await RunAsync("install", ["xcrun", "devicectl", "device", "install", "app", "--device", device, bundle], project, environment, log, cancellationToken).ConfigureAwait(false);
        // devicectl passes the variables with this prefix to the application, without the prefix.
        var launch = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { NeoDevelopmentOrchestrator.McpUrlVariable, NeoDevelopmentOrchestrator.McpTokenVariable })
            if (environment.TryGetValue(name, out var value)) launch["DEVICECTL_CHILD_" + name] = value;
        if (tunnel)
        {
            launch["DEVICECTL_CHILD_NEOASTRA_DEV_URL"] = project.DevUrl.AbsoluteUri;
            launch["DEVICECTL_CHILD_" + NeoDevelopmentOrchestrator.McpTunnelPortVariable] = project.DevUrl.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            log("app", reachable
                ? "The application has no NeoAstra.Mcp plugin to tunnel the development server: it loads the assets of its build, and a change of the frontend needs a new session."
                : "The device cannot reach the relay to tunnel the development server: the application loads the assets of its build, and a change of the frontend needs a new session.", false);
        }
        return processFactory.Start(new("app", new(["xcrun", "devicectl", "device", "process", "launch", "--device", device, "--terminate-existing", "--console", identifier]), project.ProjectDirectory, launch, project.SecretEnvironment, (line, error) => log("app", line, error)));
    }

    // Whether the application references the MCP plugin, as a package or as a project, in the build for the device.
    private async Task<bool> ReferencesMcpAsync(NeoResolvedProject project, IReadOnlyDictionary<string, string> environment, Action<string, string, bool> log, CancellationToken cancellationToken, string[] scope)
    {
        var output = new List<string>();
        string[] arguments = ["dotnet", "msbuild", .. scope, "-getItem:PackageReference", "-getItem:ProjectReference"];
        await using (var process = processFactory.Start(new("references", new(arguments), project.ProjectDirectory, environment, project.SecretEnvironment, (line, error) => { if (error) log("references", line, true); else lock (output) output.Add(line); })))
        {
            var exit = await process.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (exit != 0) throw new NeoToolException("mobile_properties", $"MSBuild could not evaluate the project (exit code {exit}).");
        }
        return ReferencesMcp(string.Join('\n', output));
    }

    /// <summary>Finds whether the device of an iOS target is an iPhone or iPad: one that the simulators do not know, by its identifier or its name.</summary>
    /// <remarks>Without the list of the simulators, an identifier that is not a UUID is taken as a device.</remarks>
    internal async Task<NeoMobileTarget> ResolveIOSDeviceAsync(NeoResolvedProject project, NeoMobileTarget target, IReadOnlyDictionary<string, string> environment, Action<string, string, bool> log, CancellationToken cancellationToken)
    {
        if (target.Platform != NeoDevelopmentPlatform.IOS || target.Device is not { } device) return target;
        var output = new List<string>();
        int exit;
        await using (var process = processFactory.Start(new("simulators", new(["xcrun", "simctl", "list", "devices", "-j"]), project.ProjectDirectory, environment, project.SecretEnvironment, (line, error) => { if (!error) lock (output) output.Add(line); })))
        {
            exit = await process.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        var simulator = exit == 0 ? IsSimulator(string.Join('\n', output), device) : null;
        return target with { IsPhysical = !(simulator ?? !NeoMobileTarget.LooksLikeDevice(device)) };
    }

    // Whether the list of the simulators has the device, by its identifier or its name; null when the list cannot be read.
    internal static bool? IsSimulator(string output, string device)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("devices", out var runtimes)) return null;
            foreach (var runtime in runtimes.EnumerateObject())
                foreach (var simulator in runtime.Value.EnumerateArray())
                    if (Matches(simulator, "udid") || Matches(simulator, "name")) return true;
            return false;
        }
        catch (JsonException)
        {
            return null;
        }

        bool Matches(JsonElement simulator, string property)
            => simulator.TryGetProperty(property, out var value) && string.Equals(value.GetString(), device, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ReferencesMcp(string output)
    {
        using var document = JsonDocument.Parse(output);
        if (!document.RootElement.TryGetProperty("Items", out var items)) return false;
        static bool Has(JsonElement items, string kind, string property)
            => items.TryGetProperty(kind, out var list) && list.EnumerateArray().Any(item => item.TryGetProperty(property, out var value) && string.Equals(value.GetString(), "NeoAstra.Mcp", StringComparison.OrdinalIgnoreCase));
        return Has(items, "PackageReference", "Identity") || Has(items, "ProjectReference", "Filename");
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
        // The relay of the session reaches the device the same way, through a forwarding of its port, and its address and token
        // go in a file beside the address of the server, which only adb run-as writes. The token goes through the standard input
        // of adb, never in the arguments of a process that others on the machine or on the device may list.
        var relay = environment.TryGetValue(NeoDevelopmentOrchestrator.McpUrlVariable, out var relayUrl) && environment.TryGetValue(NeoDevelopmentOrchestrator.McpTokenVariable, out var relayToken)
            && Uri.TryCreate(relayUrl, UriKind.Absolute, out var relayAddress) && IsShellSafe(relayToken)
            ? (Port: $"tcp:{relayAddress.Port}", Line: $"{relayAddress.AbsoluteUri} {relayToken}")
            : ((string Port, string Line)?)null;
        // The address goes with the session: a later launch of the debug build loads its packaged assets again. A launch that
        // fails or is canceled once the forwarding is in place removes it too.
        async Task CleanupAsync()
        {
            await RunQuietlyAsync([adb, .. device, "shell", $"run-as {identifier} rm -f {AndroidAddressFile} {AndroidRelayFile}"], project, environment).ConfigureAwait(false);
            await RunQuietlyAsync([adb, .. device, "reverse", "--remove", port], project, environment).ConfigureAwait(false);
            if (relay is { } forwarded) await RunQuietlyAsync([adb, .. device, "reverse", "--remove", forwarded.Port], project, environment).ConfigureAwait(false);
        }
        try
        {
            await RunAsync("reverse", [adb, .. device, "reverse", port, port], project, environment, log, cancellationToken).ConfigureAwait(false);
            if (relay is { } forwarding) await RunAsync("reverse", [adb, .. device, "reverse", forwarding.Port, forwarding.Port], project, environment, log, cancellationToken).ConfigureAwait(false);
            var relayFile = relay is not null ? $" && cat > {AndroidRelayFile}" : $" && rm -f {AndroidRelayFile}";
            await RunAsync("address", [adb, .. device, "shell", $"run-as {identifier} sh -c 'mkdir -p files && echo {project.DevUrl.AbsoluteUri} > {AndroidAddressFile}{relayFile}'"], project, environment, log, cancellationToken, relay is { } line ? line.Line + "\n" : null).ConfigureAwait(false);
            await RunAsync("launch", [adb, .. device, "shell", "am", "force-stop", identifier], project, environment, log, cancellationToken).ConfigureAwait(false);
            // monkey starts the launcher activity without knowing its name; an emulator has no physical keys, and monkey refuses
            // to run there with its default share of system keys, so the one event it sends is the launch alone.
            await RunAsync("launch", [adb, .. device, "shell", "monkey", "--pct-syskeys", "0", "-p", identifier, "-c", "android.intent.category.LAUNCHER", "1"], project, environment, log, cancellationToken).ConfigureAwait(false);
            var logcat = processFactory.Start(new("app", new([adb, .. device, "logcat", "-v", "brief", "-s", "NeoAstra:*", "DOTNET:*", "AndroidRuntime:E", "chromium:*"]), project.ProjectDirectory, environment, project.SecretEnvironment, (line, error) => log("app", line, error)));
            return new AndroidSession(logcat, CleanupAsync);
        }
        catch
        {
            await CleanupAsync().ConfigureAwait(false);
            throw;
        }
    }

    // The token of the relay is one word of the file that the application reads: letters, digits, hyphens, and underscores only.
    private static bool IsShellSafe(string value)
        => value.Length != 0 && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private Task RunAsync(string label, IReadOnlyList<string> arguments, NeoResolvedProject project, IReadOnlyDictionary<string, string> environment, Action<string, string, bool> log, CancellationToken cancellationToken)
        => RunAsync(label, arguments, project, environment, log, cancellationToken, null);

    private async Task RunAsync(string label, IReadOnlyList<string> arguments, NeoResolvedProject project, IReadOnlyDictionary<string, string> environment, Action<string, string, bool> log, CancellationToken cancellationToken, string? standardInput)
    {
        await using var process = processFactory.Start(new(label, new(arguments), project.ProjectDirectory, environment, project.SecretEnvironment, (line, error) => log(label, line, error)) { StandardInput = standardInput });
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
