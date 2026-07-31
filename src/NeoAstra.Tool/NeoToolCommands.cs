// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NeoAstra.Tooling;

namespace NeoAstra.Tool;

internal sealed record NeoInitOptions(
    string? ConfigurationPath,
    string FrontendRoot,
    IReadOnlyList<string> DevCommand,
    string DevUrl,
    IReadOnlyList<string> BuildCommand,
    string DistDirectory,
    string Identifier,
    string DisplayName,
    string PackageManager,
    IReadOnlyList<string> PackageManagerCommand,
    bool DryRun,
    bool Force);
internal static class NeoToolCommands
{
    internal static string Required(string? value, string name)
    {
        return value ?? throw new NeoToolException("argument_missing", $"Required argument {name} is missing.");
    }

    internal static int Inspect(string? configurationPath)
    {
        var project = Load(configurationPath);
        Console.WriteLine(project.ToInspectJson(redactSecrets: true));
        return 0;
    }

    internal static int Doctor(string? configurationPath, bool json)
    {
        var project = Load(configurationPath);
        var findings = new List<(string Id, string Status, string Detail)>();
        CheckCommand("dotnet", new NeoCommand(["dotnet", "--version"]), project.ProjectDirectory, findings);
        CheckCommand("dev-command", NeoPackageManagerCommandResolver.Apply(project, project.DevCommand), project.FrontendRoot, findings);
        if (project.PackageManager != "none")
        {
            CheckCommand("package-manager", new NeoCommand([.. project.PackageManagerCommand.Arguments, "--version"]), project.FrontendRoot, findings);
        }

        findings.Add(("dev-origin", project.AllowRemoteDevServer ? "warning" : "ok", project.AllowRemoteDevServer ? "Remote/LAN development opt-in is enabled; use only on a trusted network." : "Exact IP-literal loopback origin is enforced."));
        var fallback = Path.Combine(project.DistDirectory, project.SpaFallback.Replace('/', Path.DirectorySeparatorChar));
        findings.Add(("dist", Directory.Exists(project.DistDirectory) && File.Exists(fallback) ? "ok" : "warning", "Run the configured frontend build or select an explicit validated prebuilt directory."));
        findings.Add(("lockfile", project.PackageManager == "none" || project.Lockfile is not null && File.Exists(project.Lockfile) ? "ok" : "error", "Configure and commit the package-manager lockfile; npm projects are restored incrementally with npm ci."));
        findings.Add(("csp", project.ContentSecurityPolicy.Contains("http:", StringComparison.OrdinalIgnoreCase) || project.ContentSecurityPolicy.Contains("https:", StringComparison.OrdinalIgnoreCase) ? "warning" : "ok", "Production CSP should avoid remote scripts."));
        findings.Add(("service-workers", "info", "Custom-scheme service workers are not portable and are unsupported by WebKitGTK; templates do not register one."));
        AddBundleFindings(project, findings);
        AddPlatformFindings(findings);
        if (json)
        {
            WriteFindings(findings);
        }
        else
        {
            foreach (var finding in findings)
            {
                Console.WriteLine($"{finding.Status,-7} {finding.Id}: {finding.Detail}");
            }
        }

        return findings.Any(static finding => finding.Status == "error") ? 2 : 0;
    }

    internal static async Task<int> DevAsync(string? configurationPath, string? devUrl)
    {
        var project = Load(configurationPath, devUrl);
        using var source = new CancellationTokenSource();
        Console.CancelKeyPress += Cancel;
        try
        {
            var processFactory = new NeoProcessFactory();
            await new NeoFrontendDependencyRestorer(processFactory).RestoreAsync(project, source.Token).ConfigureAwait(false);
            return await new NeoDevelopmentOrchestrator(processFactory, new NeoReadinessProbe()).RunAsync(project, source.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= Cancel;
        }

        void Cancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            source.Cancel();
        }
    }

    internal static async Task<int> AssetsAsync(string? configurationPath, string? devUrl, string manifest, string? copy, string? prebuilt, bool allowDevelopmentSettings)
    {
        var project = Load(configurationPath, devUrl);
        if (project.AllowRemoteDevServer && !allowDevelopmentSettings)
        {
            throw new NeoToolException("release_development_setting", "Release asset preparation rejects allowRemoteDevServer unless --allow-development-settings is explicitly supplied.");
        }

        ValidatePrebuiltDirectory(project, prebuilt);
        ValidateLockfile(project);
        if (prebuilt is null)
        {
            NeoCommandPolicy.EnsureProductionBuildDoesNotInstall(project);
            var buildCommand = NeoPackageManagerCommandResolver.Apply(project, project.BuildCommand);
            await using var process = new NeoProcessFactory().Start(new("build", buildCommand, project.FrontendRoot, project.Environment, project.SecretEnvironment, (line, error) => (error ? Console.Error : Console.Out).WriteLine($"[frontend] {line}")));
            var exit = await process.Completion.ConfigureAwait(false);
            if (exit != 0)
            {
                throw new NeoToolException("frontend_build_failed", $"The frontend production build failed with exit code {exit}.");
            }
        }

        var hash = NeoAssetManifestBuilder.Build(project, manifest);
        if (copy is not null)
        {
            NeoAssetManifestBuilder.CopyManifestAssets(manifest, project.DistDirectory, copy);
        }

        Console.WriteLine($"Validated {project.DistDirectory}; asset manifest SHA-256 {hash}.");
        return 0;
    }

    internal static async Task<int> FrontendRestoreAsync(string? configurationPath)
    {
        var project = Load(configurationPath);
        await new NeoFrontendDependencyRestorer(new NeoProcessFactory()).RestoreAsync(project).ConfigureAwait(false);
        return 0;
    }

    internal static int FrontendFingerprint(string? configurationPath, string output, string? invalidate, string? configuration, string? prebuilt, IReadOnlyList<string> inputs)
    {
        var project = Load(configurationPath);
        ValidatePrebuiltDirectory(project, prebuilt);
        ValidateLockfile(project);
        var previous = File.Exists(output) ? File.ReadAllText(output, Encoding.UTF8).Trim() : null;
        var fingerprint = NeoFrontendFingerprint.Write(project, output, prebuilt is not null, configuration ?? string.Empty, inputs);
        if (previous != fingerprint && invalidate is not null)
        {
            File.Delete(invalidate);
        }

        Console.WriteLine($"Frontend input fingerprint {fingerprint} is current.");
        return 0;
    }

    internal static int ContractCheck(string typescript, string manifest)
    {
        if (!File.Exists(typescript) || !File.Exists(manifest))
        {
            throw new NeoToolException("contract_missing", "Generated TypeScript and manifest outputs must exist before frontend build.");
        }

        var first = File.ReadLines(typescript).FirstOrDefault() ?? string.Empty;
        const string prefix = "// <auto-generated by NeoAstra.Generator; contract ";
        if (!first.StartsWith(prefix, StringComparison.Ordinal) || !first.EndsWith('>'))
        {
            throw new NeoToolException("contract_stale", "Generated TypeScript is missing its contract hash.");
        }

        var expected = first[prefix.Length..^1];
        var manifestBytes = File.ReadAllBytes(manifest);
        using var document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions { MaxDepth = 16 });
        var actual = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        if (actual != expected)
        {
            throw new NeoToolException("contract_stale", "Generated TypeScript and backend manifest contract hashes differ.");
        }

        Console.WriteLine($"Generated RPC contract {expected} is current.");
        return 0;
    }

    internal static int Bundle(string? configurationPath, string runtimeIdentifier, string publishDirectory, string assetManifest, string outputDirectory, bool dryRun, bool sign, string? signingIdentityEnvironment, bool executeInstaller)
    {
        var project = Load(configurationPath);
        var request = new NeoBundleRequest(runtimeIdentifier, publishDirectory, assetManifest, outputDirectory, dryRun, sign, signingIdentityEnvironment, executeInstaller);
        var result = NeoBundleOrchestrator.Run(project, request);
        Console.WriteLine($"{(request.DryRun ? "Planned" : "Created")} {result.Artifact}");
        Console.WriteLine($"Staging manifest: {result.StagingManifest}");
        Console.WriteLine("Artifact is not target-host qualified; qualification requires separate recorded install/upgrade/repair/uninstall/launch smoke evidence.");
        return 0;
    }

    internal static int Init(NeoInitOptions options)
    {
        if (options.DevCommand.Count == 0 || options.BuildCommand.Count == 0)
        {
            throw new NeoToolException("init_command", "Explicit --dev-command and --build-command arrays are required (repeat the option for each argument).");
        }

        NeoOriginPolicy.ValidateDevelopmentUrl(options.DevUrl, allowRemote: false);
        var configPath = Path.GetFullPath(options.ConfigurationPath ?? "neoastra.json");
        var exists = File.Exists(configPath);
        if (exists && !options.Force)
        {
            throw new NeoToolException("init_conflict", "neoastra.json already exists; no files were changed. Use --force only after reviewing/backup.");
        }

        var json = CreateConfiguration(options);
        _ = NeoProjectConfiguration.ValidateGenerated(configPath, json);
        Console.WriteLine($"{(exists ? "replace" : "create")} {configPath}");
        Console.WriteLine($"review then run: {PackageCommand(options.PackageManager, options.PackageManagerCommand)}");
        if (options.DryRun)
        {
            Console.WriteLine(json);
            return 0;
        }

        WriteConfigurationAtomically(configPath, json + "\n", exists);
        return 0;
    }

    private static NeoResolvedProject Load(string? configurationPath, string? devUrl = null)
    {
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        if (devUrl is not null)
        {
            overrides["NeoAstraDevUrl"] = devUrl;
        }

        return NeoProjectConfiguration.Load(configurationPath ?? Path.Combine(Environment.CurrentDirectory, "neoastra.json"), overrides);
    }

    private static void ValidatePrebuiltDirectory(NeoResolvedProject project, string? prebuilt)
    {
        if (prebuilt is not null && !Path.GetFullPath(prebuilt, project.ProjectDirectory).Equals(project.DistDirectory, PathComparison()))
        {
            throw new NeoToolException("prebuilt_directory", "Prebuilt mode requires the explicit configured dist directory.");
        }
    }

    private static void ValidateLockfile(NeoResolvedProject project)
    {
        if (project.PackageManager != "none" && (project.Lockfile is null || !File.Exists(project.Lockfile)))
        {
            throw new NeoToolException("lockfile_missing", "The configured package manager requires an existing committed lockfile; unlocked dependency installation is not allowed.");
        }
    }

    private static StringComparison PathComparison()
    {
        return OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    private static void AddBundleFindings(NeoResolvedProject project, List<(string Id, string Status, string Detail)> findings)
    {
        if (project.Bundle is not { } bundle)
        {
            return;
        }

        findings.Add(("bundle-identity", bundle.Identifier == project.Identifier && bundle.NotificationIdentity == project.Identifier ? "ok" : "error", "Bundle, single-instance, notification, data-directory, and update identity must remain identical."));
        var noLaunchDeclarations = bundle.FileAssociations.Count == 0 && bundle.UrlSchemes.Count == 0;
        findings.Add(("launch-routing", noLaunchDeclarations ? "info" : "warning", noLaunchDeclarations ? "No external launch declarations are configured." : "File/protocol declarations require matching OpenFiles/OpenUrls handlers and clean-host launch evidence."));
        var updatesDisabled = bundle.Update?.Mode is null or "disabled";
        findings.Add(("update-mode", updatesDisabled ? "ok" : "warning", updatesDisabled ? "Self-update is unavailable." : "Updater is experimental/store-managed; it is not release-qualified until target-artifact negative, interruption, health, and rollback CI passes."));
        foreach (var icon in bundle.Icons)
        {
            findings.Add(("icon:" + Path.GetFileName(icon), File.Exists(icon) ? "ok" : "error", "Declared source icons are retained and platform conversion is offline and explicit."));
        }
    }

    private static void AddPlatformFindings(List<(string Id, string Status, string Detail)> findings)
    {
        if (OperatingSystem.IsWindows())
        {
            var programDirectories = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            };
            var webView2 = programDirectories.Where(static path => path.Length != 0).Any(path => Directory.Exists(Path.Combine(path, "Microsoft", "EdgeWebView", "Application")));
            findings.Add(("webview2-runtime", webView2 ? "ok" : "warning", webView2 ? "Evergreen WebView2 runtime installation detected." : "WebView2 runtime was not found in standard machine locations; install the Evergreen runtime or validate a fixed runtime explicitly."));
            CheckOptionalCommand("signtool", ["/?"], "windows-packaging", findings);
        }
        else if (OperatingSystem.IsMacOS())
        {
            findings.Add(("wkwebview-runtime", "ok", "WKWebView is supplied by the operating system."));
            CheckOptionalCommand("xcrun", ["--version"], "macos-packaging", findings);
        }
        else
        {
            CheckOptionalCommand("pkg-config", ["--exists", "webkit2gtk-4.1"], "webkitgtk-native-dependencies", findings);
            findings.Add(("webkitgtk-runtime", "info", "Runtime launch remains the authoritative WebKitGTK/native dependency check for the target distribution."));
        }
    }

    private static void CheckCommand(string id, NeoCommand command, string workingDirectory, List<(string Id, string Status, string Detail)> findings)
    {
        try
        {
            var executable = NeoChildProcess.ResolveExecutable(command.Executable, workingDirectory);
            var info = new ProcessStartInfo
            {
                FileName = executable.Path,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var prefix in executable.PrefixArguments)
            {
                info.ArgumentList.Add(prefix);
            }

            foreach (var argument in command.Arguments.Skip(1))
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
                findings.Add((id, "warning", "Executable did not complete the bounded version probe."));
            }
            else
            {
                findings.Add((id, process.ExitCode == 0 ? "ok" : "warning", process.ExitCode == 0 ? "Executable found." : "Executable returned a failure."));
            }

            Task.WaitAll([stdout, stderr], 3000);
        }
        catch
        {
            findings.Add((id, "error", "Configured executable not found; install it explicitly or configure frontend.packageManagerCommand."));
        }
    }

    private static void CheckOptionalCommand(string command, IReadOnlyList<string> arguments, string id, List<(string Id, string Status, string Detail)> findings)
    {
        var temporary = new List<(string Id, string Status, string Detail)>();
        CheckCommand(command, new NeoCommand([command, .. arguments]), Environment.CurrentDirectory, temporary);
        var result = temporary[0];
        findings.Add((id, result.Status == "error" ? "warning" : result.Status, result.Status == "error" ? $"Optional tool '{command}' is unavailable; install it only for the corresponding platform packaging/runtime workflow." : result.Detail));
    }

    private static void WriteFindings(List<(string Id, string Status, string Detail)> findings)
    {
        using var writer = new Utf8JsonWriter(Console.OpenStandardOutput(), new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("version", 1);
        writer.WriteStartArray("findings");
        foreach (var finding in findings)
        {
            writer.WriteStartObject();
            writer.WriteString("id", finding.Id);
            writer.WriteString("status", finding.Status);
            writer.WriteString("detail", finding.Detail);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static string PackageCommand(string manager, IReadOnlyList<string> commandPrefix)
    {
        var prefix = commandPrefix.Count == 0 ? manager : string.Join(' ', commandPrefix.Select(QuoteForDisplay));
        return manager switch
        {
            "npm" => $"{prefix} install @neoastra/client --save",
            "pnpm" => $"{prefix} add @neoastra/client",
            "yarn" => $"{prefix} add @neoastra/client",
            "bun" => $"{prefix} add @neoastra/client",
            "none" => "add @neoastra/client using your existing dependency workflow",
            _ => throw new NeoToolException("package_manager", "Unknown package manager."),
        };
    }

    private static string QuoteForDisplay(string value)
    {
        return value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value;
    }

    private static string CreateConfiguration(NeoInitOptions options)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("$schema", "neoastra-project-v1.schema.json");
            writer.WriteNumber("version", 1);
            writer.WriteStartObject("app");
            writer.WriteString("identifier", options.Identifier);
            writer.WriteString("displayName", options.DisplayName);
            writer.WriteEndObject();
            writer.WriteStartObject("frontend");
            writer.WriteString("root", options.FrontendRoot);
            WriteArray(writer, "devCommand", options.DevCommand);
            writer.WriteString("devUrl", options.DevUrl);
            WriteArray(writer, "buildCommand", options.BuildCommand);
            writer.WriteString("dist", options.DistDirectory);
            writer.WriteString("spaFallback", "index.html");
            writer.WriteString("packageManager", options.PackageManager);
            if (options.PackageManagerCommand.Count != 0)
            {
                WriteArray(writer, "packageManagerCommand", options.PackageManagerCommand);
            }

            if (options.PackageManager != "none")
            {
                writer.WriteString("lockfile", Path.Combine(options.FrontendRoot, LockfileName(options.PackageManager)));
            }

            writer.WriteEndObject();
            writer.WriteStartObject("assets");
            writer.WriteString("origin", "app://neoastra");
            writer.WriteBoolean("cacheHashedAssets", true);
            writer.WriteString("csp", "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'");
            writer.WriteEndObject();
            writer.WriteStartArray("capabilities");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string LockfileName(string manager)
    {
        return manager switch
        {
            "npm" => "package-lock.json",
            "pnpm" => "pnpm-lock.yaml",
            "yarn" => "yarn.lock",
            "bun" => "bun.lock",
            _ => throw new NeoToolException("package_manager", "Unknown package manager."),
        };
    }

    private static void WriteArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteConfigurationAtomically(string path, string content, bool replace)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + ".write." + Guid.NewGuid().ToString("N") + ".tmp";
        string? backupTemporary = null;
        try
        {
            WriteNewFile(temporary, content);
            if (replace)
            {
                if (!File.Exists(path))
                {
                    throw new NeoToolException("init_conflict", "neoastra.json changed before replacement; no destination changes were made.");
                }

                var backup = path + ".bak";
                if (File.Exists(backup))
                {
                    throw new NeoToolException("init_backup_conflict", "The non-overwriting neoastra.json.bak backup already exists; no destination changes were made.");
                }

                backupTemporary = backup + ".write." + Guid.NewGuid().ToString("N") + ".tmp";
                CopyFileDurably(path, backupTemporary);
                File.Move(backupTemporary, backup);
                backupTemporary = null;
                try
                {
                    File.Move(temporary, path, overwrite: true);
                }
                catch
                {
                    try
                    {
                        File.Delete(backup);
                    }
                    catch
                    {
                    }

                    throw;
                }
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        finally
        {
            TryDelete(temporary);
            if (backupTemporary is not null)
            {
                TryDelete(backupTemporary);
            }
        }
    }

    private static void CopyFileDurably(string sourcePath, string destinationPath)
    {
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        source.CopyTo(destination);
        destination.Flush(flushToDisk: true);
    }

    private static void WriteNewFile(string path, string content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}