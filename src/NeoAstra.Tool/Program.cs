// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NeoAstra.Tool;
using NeoAstra.Tooling;

return await MainAsync(args).ConfigureAwait(false);

static async Task<int> MainAsync(string[] args)
{
    try
    {
        if (args.Length == 0 || args[0] is "-h" or "--help") { Usage(); return 0; }
        return args[0] switch
        {
            "inspect" => Inspect(args),
            "doctor" => Doctor(args),
            "init" => Init(args),
            "dev" => await DevAsync(args).ConfigureAwait(false),
            "assets" => await AssetsAsync(args).ConfigureAwait(false),
            "frontend" => await FrontendAsync(args).ConfigureAwait(false),
            "contract" => Contract(args),
            "bundle" => Bundle(args),
            "capabilities" => CapabilityCommand.Run(args[1..]),
            _ => throw new NeoToolException("unknown_command", "Unknown command. Run 'dotnet neoastra --help'."),
        };
    }
    catch (NeoToolException exception) { Console.Error.WriteLine($"{exception.Code}: {exception.Message}"); return 2; }
    catch (OperationCanceledException) { Console.Error.WriteLine("cancelled: Operation cancelled; child process trees were stopped."); return 130; }
    catch (Exception) { Console.Error.WriteLine("tool_failure: NeoAstra tooling failed without exposing sensitive details."); return 1; }
}

static int Inspect(string[] args)
{
    var project = Load(args, 1); Console.WriteLine(project.ToInspectJson(redactSecrets: true)); return 0;
}

static int Doctor(string[] args)
{
    var project = Load(args, 1); var findings = new List<(string Id, string Status, string Detail)>();
    CheckCommand("dotnet", ["--version"], findings);
    CheckCommand(project.DevCommand.Executable, ["--version"], findings);
    if (project.PackageManager != "none") CheckCommand(project.PackageManager, ["--version"], findings);
    findings.Add(("dev-origin", project.AllowRemoteDevServer ? "warning" : "ok", project.AllowRemoteDevServer ? "Remote/LAN development opt-in is enabled; use only on a trusted network." : "Exact IP-literal loopback origin is enforced."));
    findings.Add(("dist", Directory.Exists(project.DistDirectory) && File.Exists(Path.Combine(project.DistDirectory, project.SpaFallback.Replace('/', Path.DirectorySeparatorChar))) ? "ok" : "warning", "Run the configured frontend build or select an explicit validated prebuilt directory."));
    findings.Add(("lockfile", project.PackageManager == "none" || project.Lockfile is not null && File.Exists(project.Lockfile) ? "ok" : "error", "Configure and commit the package-manager lockfile; npm projects are restored incrementally with npm ci."));
    findings.Add(("csp", project.ContentSecurityPolicy.Contains("http:", StringComparison.OrdinalIgnoreCase) || project.ContentSecurityPolicy.Contains("https:", StringComparison.OrdinalIgnoreCase) ? "warning" : "ok", "Production CSP should avoid remote scripts."));
    findings.Add(("service-workers", "info", "Custom-scheme service workers are not portable and are unsupported by WebKitGTK; templates do not register one."));
    if (project.Bundle is { } bundle)
    {
        findings.Add(("bundle-identity", bundle.Identifier == project.Identifier && bundle.NotificationIdentity == project.Identifier ? "ok" : "error", "Bundle, single-instance, notification, data-directory, and update identity must remain identical."));
        findings.Add(("launch-routing", bundle.FileAssociations.Count == 0 && bundle.UrlSchemes.Count == 0 ? "info" : "warning", bundle.FileAssociations.Count == 0 && bundle.UrlSchemes.Count == 0 ? "No external launch declarations are configured." : "File/protocol declarations require matching OpenFiles/OpenUrls handlers and clean-host launch evidence."));
        findings.Add(("update-mode", bundle.Update?.Mode is null or "disabled" ? "ok" : "warning", bundle.Update?.Mode is null or "disabled" ? "Self-update is unavailable." : "Updater is experimental/store-managed; it is not release-qualified until target-artifact negative, interruption, health, and rollback CI passes."));
        foreach (var icon in bundle.Icons) findings.Add(("icon:" + Path.GetFileName(icon), File.Exists(icon) ? "ok" : "error", "Declared source icons are retained and platform conversion is offline and explicit."));
    }
    if (OperatingSystem.IsWindows())
    {
        var webView2 = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(static path => path.Length != 0).Any(path => Directory.Exists(Path.Combine(path, "Microsoft", "EdgeWebView", "Application")));
        findings.Add(("webview2-runtime", webView2 ? "ok" : "warning", webView2 ? "Evergreen WebView2 runtime installation detected." : "WebView2 runtime was not found in standard machine locations; install the Evergreen runtime or validate a fixed runtime explicitly."));
        CheckOptionalCommand("signtool", ["/?"], "windows-packaging", findings);
    }
    else if (OperatingSystem.IsMacOS())
    {
        findings.Add(("wkwebview-runtime", "ok", "WKWebView is supplied by the operating system.")); CheckOptionalCommand("xcrun", ["--version"], "macos-packaging", findings);
    }
    else
    {
        CheckOptionalCommand("pkg-config", ["--exists", "webkit2gtk-4.1"], "webkitgtk-native-dependencies", findings);
        findings.Add(("webkitgtk-runtime", "info", "Runtime launch remains the authoritative WebKitGTK/native dependency check for the target distribution."));
    }
    var json = args.Contains("--json", StringComparer.Ordinal);
    if (json) WriteFindings(findings); else foreach (var finding in findings) Console.WriteLine($"{finding.Status,-7} {finding.Id}: {finding.Detail}");
    return findings.Any(static finding => finding.Status == "error") ? 2 : 0;
}

static async Task<int> DevAsync(string[] args)
{
    var project = Load(args, 1); using var source = new CancellationTokenSource();
    Console.CancelKeyPress += Cancel;
    try
    {
        var processFactory = new NeoProcessFactory();
        await new NeoFrontendDependencyRestorer(processFactory).RestoreAsync(project, source.Token).ConfigureAwait(false);
        return await new NeoDevelopmentOrchestrator(processFactory, new NeoReadinessProbe()).RunAsync(project, source.Token).ConfigureAwait(false);
    }
    finally { Console.CancelKeyPress -= Cancel; }
    void Cancel(object? sender, ConsoleCancelEventArgs eventArgs) { eventArgs.Cancel = true; source.Cancel(); }
}

static async Task<int> AssetsAsync(string[] args)
{
    var project = Load(args, 1); var manifest = Required(args, "--manifest"); var copy = Optional(args, "--copy"); var prebuilt = Optional(args, "--prebuilt");
    if (project.AllowRemoteDevServer && !args.Contains("--allow-development-settings", StringComparer.Ordinal)) throw new NeoToolException("release_development_setting", "Release asset preparation rejects allowRemoteDevServer unless --allow-development-settings is explicitly supplied.");
    if (prebuilt is not null && !Path.GetFullPath(prebuilt, project.ProjectDirectory).Equals(project.DistDirectory, PathComparison())) throw new NeoToolException("prebuilt_directory", "Prebuilt mode requires the explicit configured dist directory.");
    ValidateLockfile(project);
    if (prebuilt is null)
    {
        NeoCommandPolicy.EnsureProductionBuildDoesNotInstall(project);
        await using var process = new NeoProcessFactory().Start(new("build", project.BuildCommand, project.FrontendRoot, project.Environment, project.SecretEnvironment, (line, error) => (error ? Console.Error : Console.Out).WriteLine($"[frontend] {line}")));
        var exit = await process.Completion.ConfigureAwait(false); if (exit != 0) throw new NeoToolException("frontend_build_failed", $"The frontend production build failed with exit code {exit}.");
    }
    var hash = NeoAssetManifestBuilder.Build(project, manifest); if (copy is not null) NeoAssetManifestBuilder.CopyManifestAssets(manifest, project.DistDirectory, copy);
    Console.WriteLine($"Validated {project.DistDirectory}; asset manifest SHA-256 {hash}."); return 0;
}

static async Task<int> FrontendAsync(string[] args)
{
    if (args.Length < 2 || args[1] is not ("fingerprint" or "restore")) throw new NeoToolException("frontend_usage", "Usage: dotnet neoastra frontend <restore|fingerprint> --config <file> ...");
    var project = Load(args, 2);
    if (args[1] == "restore")
    {
        await new NeoFrontendDependencyRestorer(new NeoProcessFactory()).RestoreAsync(project).ConfigureAwait(false);
        return 0;
    }
    var prebuilt = Optional(args, "--prebuilt");
    if (prebuilt is not null && !Path.GetFullPath(prebuilt, project.ProjectDirectory).Equals(project.DistDirectory, PathComparison()))
        throw new NeoToolException("prebuilt_directory", "Prebuilt mode requires the explicit configured dist directory.");
    ValidateLockfile(project);
    var output = Required(args, "--output");
    var previous = File.Exists(output) ? File.ReadAllText(output, Encoding.UTF8).Trim() : null;
    var fingerprint = NeoFrontendFingerprint.Write(project, output, prebuilt is not null,
        Optional(args, "--configuration") ?? string.Empty, Values(args, "--input"));
    if (previous != fingerprint && Optional(args, "--invalidate") is { } invalidated) File.Delete(invalidated);
    Console.WriteLine($"Frontend input fingerprint {fingerprint} is current.");
    return 0;
}

static int Contract(string[] args)
{
    if (args.Length < 2 || args[1] != "check") throw new NeoToolException("contract_usage", "Usage: dotnet neoastra contract check --typescript <file> --manifest <file>");
    var typescript = Required(args, "--typescript"); var manifest = Required(args, "--manifest");
    if (!File.Exists(typescript) || !File.Exists(manifest)) throw new NeoToolException("contract_missing", "Generated TypeScript and manifest outputs must exist before frontend build.");
    var first = File.ReadLines(typescript).FirstOrDefault() ?? string.Empty; const string prefix = "// <auto-generated by NeoAstra.Generator; contract ";
    if (!first.StartsWith(prefix, StringComparison.Ordinal) || !first.EndsWith('>')) throw new NeoToolException("contract_stale", "Generated TypeScript is missing its contract hash.");
    var hash = first[prefix.Length..^1]; var manifestBytes = File.ReadAllBytes(manifest); using var document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions { MaxDepth = 16 });
    var actual = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
    if (actual != hash) throw new NeoToolException("contract_stale", "Generated TypeScript and backend manifest contract hashes differ.");
    Console.WriteLine($"Generated RPC contract {hash} is current."); return 0;
}

static int Bundle(string[] args)
{
    var project = Load(args, 1);
    var request = new NeoBundleRequest(Required(args, "--rid"), Required(args, "--publish"), Required(args, "--assets-manifest"), Required(args, "--output"),
        args.Contains("--dry-run", StringComparer.Ordinal), args.Contains("--sign", StringComparer.Ordinal), Optional(args, "--signing-identity-env"), args.Contains("--execute-installer", StringComparer.Ordinal));
    var result = NeoBundleOrchestrator.Run(project, request);
    Console.WriteLine($"{(request.DryRun ? "Planned" : "Created")} {result.Artifact}");
    Console.WriteLine($"Staging manifest: {result.StagingManifest}");
    Console.WriteLine("Artifact is not target-host qualified; qualification requires separate recorded install/upgrade/repair/uninstall/launch smoke evidence.");
    return 0;
}

static int Init(string[] args)
{
    var configPath = Path.GetFullPath(Optional(args, "--config") ?? "neoastra.json"); var dryRun = args.Contains("--dry-run", StringComparer.Ordinal); var force = args.Contains("--force", StringComparer.Ordinal);
    var frontend = Required(args, "--frontend-root"); var devUrl = Required(args, "--dev-url"); var dist = Required(args, "--dist"); var identifier = Required(args, "--identifier"); var name = Required(args, "--display-name"); var packageManager = Required(args, "--package-manager");
    var dev = Values(args, "--dev-command"); var build = Values(args, "--build-command"); if (dev.Count == 0 || build.Count == 0) throw new NeoToolException("init_command", "Explicit --dev-command and --build-command arrays are required (repeat the option for each argument).");
    NeoOriginPolicy.ValidateDevelopmentUrl(devUrl, allowRemote: false);
    var exists = File.Exists(configPath); if (exists && !force) throw new NeoToolException("init_conflict", "neoastra.json already exists; no files were changed. Use --force only after reviewing/backup.");
    var json = CreateConfiguration(identifier, name, frontend, dev, devUrl, build, dist, packageManager);
    _ = NeoProjectConfiguration.ValidateGenerated(configPath, json);
    Console.WriteLine($"{(exists ? "replace" : "create")} {configPath}"); Console.WriteLine($"review then run: {PackageCommand(packageManager)}");
    if (dryRun) { Console.WriteLine(json); return 0; }
    WriteConfigurationAtomically(configPath, json + "\n", exists); return 0;
}

static void WriteConfigurationAtomically(string path, string content, bool replace)
{
    var directory = Path.GetDirectoryName(path)!; Directory.CreateDirectory(directory);
    var temporary = path + ".write." + Guid.NewGuid().ToString("N") + ".tmp"; string? backupTemporary = null;
    try
    {
        WriteNewFile(temporary, content);
        if (replace)
        {
            if (!File.Exists(path)) throw new NeoToolException("init_conflict", "neoastra.json changed before replacement; no destination changes were made.");
            var backup = path + ".bak"; if (File.Exists(backup)) throw new NeoToolException("init_backup_conflict", "The non-overwriting neoastra.json.bak backup already exists; no destination changes were made.");
            backupTemporary = backup + ".write." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
            using (var destination = new FileStream(backupTemporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough)) { source.CopyTo(destination); destination.Flush(flushToDisk: true); }
            File.Move(backupTemporary, backup); backupTemporary = null;
            try { File.Move(temporary, path, overwrite: true); }
            catch { try { File.Delete(backup); } catch { } throw; }
        }
        else File.Move(temporary, path);
    }
    finally
    {
        try { File.Delete(temporary); } catch { }
        if (backupTemporary is not null) try { File.Delete(backupTemporary); } catch { }
    }
}

static void WriteNewFile(string path, string content)
{
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
    using var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true); writer.Write(content); writer.Flush(); stream.Flush(flushToDisk: true);
}

static NeoResolvedProject Load(string[] args, int start)
{
    var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
    if (Optional(args, "--dev-url") is { } devUrl) overrides["NeoAstraDevUrl"] = devUrl;
    return NeoProjectConfiguration.Load(Optional(args, "--config") ?? Path.Combine(Environment.CurrentDirectory, "neoastra.json"), overrides);
}
static string Required(string[] args, string name) => Optional(args, name) ?? throw new NeoToolException("argument_missing", $"Required argument {name} is missing.");
static string? Optional(string[] args, string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
static List<string> Values(string[] args, string name) { var result = new List<string>(); for (var index = 0; index < args.Length - 1; index++) if (args[index] == name) result.Add(args[++index]); return result; }
static StringComparison PathComparison() => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
static void ValidateLockfile(NeoResolvedProject project) { if (project.PackageManager != "none" && (project.Lockfile is null || !File.Exists(project.Lockfile))) throw new NeoToolException("lockfile_missing", "The configured package manager requires an existing committed lockfile; unlocked dependency installation is not allowed."); }
static void CheckCommand(string command, IReadOnlyList<string> arguments, List<(string, string, string)> findings)
{
    try
    {
        var executable = NeoChildProcess.ResolveExecutable(command, Environment.CurrentDirectory);
        var info = new ProcessStartInfo { FileName = executable.Path, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var prefix in executable.PrefixArguments) info.ArgumentList.Add(prefix);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        if (!process.WaitForExit(3000))
        {
            process.Kill(entireProcessTree: true); process.WaitForExit(3000);
            findings.Add((command, "warning", "Executable did not complete the bounded version probe."));
        }
        else findings.Add((command, process.ExitCode == 0 ? "ok" : "warning", process.ExitCode == 0 ? "Executable found." : "Executable returned a failure."));
        Task.WaitAll([stdout, stderr], 3000);
    }
    catch { findings.Add((command, "error", "Configured executable not found; install it explicitly and restore dependencies using its documented workflow.")); }
}
static void CheckOptionalCommand(string command, IReadOnlyList<string> arguments, string id, List<(string, string, string)> findings) { var temporary = new List<(string, string, string)>(); CheckCommand(command, arguments, temporary); var result = temporary[0]; findings.Add((id, result.Item2 == "error" ? "warning" : result.Item2, result.Item2 == "error" ? $"Optional tool '{command}' is unavailable; install it only for the corresponding platform packaging/runtime workflow." : result.Item3)); }
static void WriteFindings(List<(string Id, string Status, string Detail)> findings) { using var writer = new Utf8JsonWriter(Console.OpenStandardOutput(), new JsonWriterOptions { Indented = true }); writer.WriteStartObject(); writer.WriteNumber("version", 1); writer.WriteStartArray("findings"); foreach (var finding in findings) { writer.WriteStartObject(); writer.WriteString("id", finding.Id); writer.WriteString("status", finding.Status); writer.WriteString("detail", finding.Detail); writer.WriteEndObject(); } writer.WriteEndArray(); writer.WriteEndObject(); }
static string PackageCommand(string manager) => manager switch { "npm" => "npm install @neoastra/client --save", "pnpm" => "pnpm add @neoastra/client", "yarn" => "yarn add @neoastra/client", "bun" => "bun add @neoastra/client", "none" => "add @neoastra/client using your existing dependency workflow", _ => throw new NeoToolException("package_manager", "Unknown package manager.") };
static string CreateConfiguration(string identifier, string name, string root, IReadOnlyList<string> dev, string devUrl, IReadOnlyList<string> build, string dist, string packageManager) { using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true })) { writer.WriteStartObject(); writer.WriteString("$schema", "neoastra-project-v1.schema.json"); writer.WriteNumber("version", 1); writer.WriteStartObject("app"); writer.WriteString("identifier", identifier); writer.WriteString("displayName", name); writer.WriteEndObject(); writer.WriteStartObject("frontend"); writer.WriteString("root", root); WriteArray(writer, "devCommand", dev); writer.WriteString("devUrl", devUrl); WriteArray(writer, "buildCommand", build); writer.WriteString("dist", dist); writer.WriteString("spaFallback", "index.html"); writer.WriteString("packageManager", packageManager); if (packageManager != "none") writer.WriteString("lockfile", Path.Combine(root, packageManager == "npm" ? "package-lock.json" : packageManager == "pnpm" ? "pnpm-lock.yaml" : packageManager == "yarn" ? "yarn.lock" : "bun.lock")); writer.WriteEndObject(); writer.WriteStartObject("assets"); writer.WriteString("origin", "app://neoastra"); writer.WriteBoolean("cacheHashedAssets", true); writer.WriteString("csp", "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'"); writer.WriteEndObject(); writer.WriteStartArray("capabilities"); writer.WriteEndArray(); writer.WriteEndObject(); } return Encoding.UTF8.GetString(stream.ToArray()); }
static void WriteArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> values) { writer.WriteStartArray(name); foreach (var value in values) writer.WriteStringValue(value); writer.WriteEndArray(); }
static void Usage() => Console.WriteLine("NeoAstra tooling (no telemetry)\n  inspect|doctor|dev [--config neoastra.json] [--json]\n  frontend restore --config <file>\n  frontend fingerprint --config <file> --output <file> [--invalidate <stamp>] [--configuration <name>] [--prebuilt <explicit-dist>] [--input <path>]...\n  assets --config <file> --manifest <file> [--copy <dir>] [--prebuilt <explicit-dist>]\n  bundle --config <file> --rid <rid> --publish <dir> --assets-manifest <file> --output <dir> [--dry-run] [--execute-installer] [--sign --signing-identity-env <name>]\n  capabilities resolve --capabilities <file> --catalog <file> --platform <windows|macos|linux> --configuration <Release|Debug> <output>\n  contract check --typescript <file> --manifest <file>\n  init --dry-run --frontend-root <dir> --dev-command <arg>... --dev-url <url> --build-command <arg>... --dist <dir> --identifier <id> --display-name <name> --package-manager <npm|pnpm|yarn|bun|none>");
