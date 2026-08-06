// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using NeoAstra.Tooling;
using XenoAtom.CommandLine;

namespace NeoAstra.Tool;

internal static class NeoToolApplication
{
    private const string Section = "";
    internal static async Task<int> RunAsync(string[] arguments)
    {
        try
        {
            var app = Create();
            var effectiveArguments = arguments.Length == 0 ? ["--help"] : arguments;
            return await app.RunAsync(effectiveArguments).ConfigureAwait(false);
        }
        catch (NeoToolException exception)
        {
            Console.Error.WriteLine($"{exception.Code}: {exception.Message}");
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("cancelled: Operation cancelled; child process trees were stopped.");
            return 130;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("tool_failure: NeoAstra tooling failed without exposing sensitive details.");
            return 1;
        }
    }

    internal static CommandApp Create()
    {
        return new CommandApp("neoastra", "Secure development and delivery tooling for NeoAstra applications.")
        {
            new CommandUsage(),
            "NeoAstra tooling (no telemetry)",
            Section,
            "Options:",
            new HelpOption(),
            Section,
            "Commands:",
            CreateInspectCommand(),
            CreateDoctorCommand(),
            CreateInitCommand(),
            CreateDevCommand(),
            CreateAssetsCommand(),
            CreateFrontendCommand(),
            CreateContractCommand(),
            CreateBundleCommand(),
            CreateCapabilitiesCommand(),
        };
    }

    private static Command CreateInspectCommand()
    {
        string? config = null;
        return new Command("inspect", "Print the resolved project configuration with secrets redacted.")
        {
            new CommandUsage(),
            {
                "config=",
                "Path to {FILE} (default: neoastra.json).",
                value => config = value
            },
            new HelpOption(),
            (_, _) => ValueTask.FromResult(NeoToolCommands.Inspect(config)),
        };
    }

    private static Command CreateDoctorCommand()
    {
        string? config = null;
        var json = false;
        return new Command("doctor", "Check the configured toolchain and project policy.")
        {
            new CommandUsage(),
            {
                "config=",
                "Path to {FILE} (default: neoastra.json).",
                value => config = value
            },
            {
                "json",
                "Write machine-readable JSON findings.",
                value => json = value is not null
            },
            new HelpOption(),
            (_, _) => ValueTask.FromResult(NeoToolCommands.Doctor(config, json)),
        };
    }

    private static Command CreateDevCommand()
    {
        string? config = null;
        string? devUrl = null;
        return new Command("dev", "Restore dependencies and coordinate frontend and backend development.")
        {
            new CommandUsage(),
            {
                "config=",
                "Path to {FILE} (default: neoastra.json).",
                value => config = value
            },
            {
                "dev-url=",
                "Override the development {URL}.",
                value => devUrl = value
            },
            new HelpOption(),
            (_, _) => new ValueTask<int>(NeoToolCommands.DevAsync(config, devUrl)),
        };
    }

    private static Command CreateAssetsCommand()
    {
        string? config = null;
        string? devUrl = null;
        string? manifest = null;
        string? copy = null;
        string? prebuilt = null;
        string? staticRoot = null;
        var allowDevelopmentSettings = false;
        return new Command("assets", "Build or validate production frontend assets.")
        {
            new CommandUsage(),
            {
                "config=",
                "Path to {FILE} (default: neoastra.json).",
                value => config = value
            },
            {
                "dev-url=",
                "Override the development {URL}.",
                value => devUrl = value
            },
            {
                "manifest=",
                "Write the asset manifest to {FILE}.",
                value => manifest = value
            },
            {
                "copy=",
                "Copy validated assets to {DIRECTORY}.",
                value => copy = value
            },
            {
                "prebuilt=",
                "Validate the configured prebuilt {DIRECTORY} without building.",
                value => prebuilt = value
            },
            {
                "static-root=",
                "Use an SDK-materialized static frontend {DIRECTORY}.",
                value => staticRoot = value
            },
            {
                "allow-development-settings",
                "Allow explicitly reviewed development settings.",
                value => allowDevelopmentSettings = value is not null
            },
            new HelpOption(),
            (_, _) => new ValueTask<int>(NeoToolCommands.AssetsAsync(config, devUrl, NeoToolCommands.Required(manifest, "--manifest"), copy, prebuilt, staticRoot, allowDevelopmentSettings)),
        };
    }

    private static Command CreateFrontendCommand()
    {
        return new Command("frontend", "Manage locked frontend dependencies and build fingerprints.")
        {
            new CommandUsage(),
            new HelpOption(),
            CreateFrontendRestoreCommand(),
            CreateFrontendFingerprintCommand(),
        };
    }

    private static Command CreateFrontendRestoreCommand()
    {
        string? config = null;
        return new Command("restore", "Incrementally restore locked frontend dependencies.")
        {
            new CommandUsage(),
            {
                "config=",
                "Path to {FILE} (default: neoastra.json).",
                value => config = value
            },
            new HelpOption(),
            (_, _) => new ValueTask<int>(NeoToolCommands.FrontendRestoreAsync(config)),
        };
    }

    private static Command CreateFrontendFingerprintCommand()
    {
        string? config = null;
        string? output = null;
        string? invalidate = null;
        string? configuration = null;
        string? prebuilt = null;
        var inputs = new List<string>();
        return new Command("fingerprint", "Write a deterministic fingerprint of frontend inputs.")
        {
            new CommandUsage(),
            {
                "config=",
                "Path to {FILE} (default: neoastra.json).",
                value => config = value
            },
            {
                "output=",
                "Write the fingerprint to {FILE}.",
                value => output = value
            },
            {
                "invalidate=",
                "Delete {FILE} when the fingerprint changes.",
                value => invalidate = value
            },
            {
                "configuration=",
                "Include the build {CONFIGURATION}.",
                value => configuration = value
            },
            {
                "prebuilt=",
                "Fingerprint the configured prebuilt {DIRECTORY}.",
                value => prebuilt = value
            },
            {
                "input=",
                "Include an additional input {PATH}; may be repeated.",
                inputs
            },
            new HelpOption(),
            (_, _) => ValueTask.FromResult(NeoToolCommands.FrontendFingerprint(config, NeoToolCommands.Required(output, "--output"), invalidate, configuration, prebuilt, inputs)),
        };
    }

    private static Command CreateContractCommand()
    {
        string? typescript = null;
        string? manifest = null;
        return new Command("contract", "Validate generated frontend/backend RPC contracts.")
        {
            new CommandUsage(),
            new HelpOption(),
            new Command("check", "Check generated TypeScript against its backend manifest.")
            {
                new CommandUsage(),
                {
                    "typescript=",
                    "Generated TypeScript {FILE}.",
                    value => typescript = value
                },
                {
                    "manifest=",
                    "Generated backend manifest {FILE}.",
                    value => manifest = value
                },
                new HelpOption(),
                (_, _) => ValueTask.FromResult(NeoToolCommands.ContractCheck(
                    NeoToolCommands.Required(typescript, "--typescript"),
                    NeoToolCommands.Required(manifest, "--manifest"))),
            },
        };
    }

    private static Command CreateBundleCommand()
    {
        string? config = null;
        string? rid = null;
        string? publish = null;
        string? assetsManifest = null;
        string? output = null;
        string? signingIdentityEnvironment = null;
        var dryRun = false;
        var sign = false;
        var executeInstaller = false;
        return new Command("bundle", "Create an inspectable platform bundle.")
        {
            new CommandUsage(),
            {
                "config=",
                "Path to {FILE} (default: neoastra.json).",
                value => config = value
            },
            {
                "rid=",
                "Target runtime {IDENTIFIER}.",
                value => rid = value
            },
            {
                "publish=",
                "Managed publish {DIRECTORY}.",
                value => publish = value
            },
            {
                "assets-manifest=",
                "Validated asset manifest {FILE}.",
                value => assetsManifest = value
            },
            {
                "output=",
                "Bundle output {DIRECTORY}.",
                value => output = value
            },
            {
                "dry-run",
                "Plan without creating an artifact.",
                value => dryRun = value is not null
            },
            {
                "sign",
                "Sign using the configured platform workflow.",
                value => sign = value is not null
            },
            {
                "signing-identity-env=",
                "Environment variable {NAME} containing the signing identity.",
                value => signingIdentityEnvironment = value
            },
            {
                "execute-installer",
                "Run the target-host installer packaging tool.",
                value => executeInstaller = value is not null
            },
            new HelpOption(),
            (_, _) => ValueTask.FromResult(NeoToolCommands.Bundle(
                config,
                NeoToolCommands.Required(rid, "--rid"),
                NeoToolCommands.Required(publish, "--publish"),
                NeoToolCommands.Required(assetsManifest, "--assets-manifest"),
                NeoToolCommands.Required(output, "--output"),
                dryRun,
                sign,
                signingIdentityEnvironment,
                executeInstaller)),
        };
    }

    private static Command CreateCapabilitiesCommand()
    {
        string? capabilities = null;
        string? catalog = null;
        string? platform = null;
        string? configuration = null;
        string? output = null;
        return new Command("capabilities", "Resolve a platform capability manifest.")
        {
            new CommandUsage(),
            new HelpOption(),
            new Command("resolve", "Resolve and validate configured capabilities.")
            {
                new CommandUsage(),
                {
                    "capabilities=",
                    "Application capability {FILE}.",
                    value => capabilities = value
                },
                {
                    "catalog=",
                    "Permission catalog {FILE}.",
                    value => catalog = value
                },
                {
                    "platform=",
                    "Target {PLATFORM}: windows, macos, or linux.",
                    value => platform = value
                },
                {
                    "configuration=",
                    "Build {CONFIGURATION}: Release or Debug.",
                    value => configuration = value
                },
                Section,
                "Arguments:",
                {
                    "<output>",
                    "Resolved manifest output {FILE}.",
                    value => output = value
                },
                new HelpOption(),
                (_, _) => ValueTask.FromResult(CapabilityCommand.Resolve(
                    NeoToolCommands.Required(capabilities, "--capabilities"),
                    NeoToolCommands.Required(catalog, "--catalog"),
                    NeoToolCommands.Required(platform, "--platform"),
                    NeoToolCommands.Required(configuration, "--configuration"),
                    NeoToolCommands.Required(output, "<output>"))),
            },
        };
    }

    private static Command CreateInitCommand()
    {
        string? config = null;
        string? frontendRoot = null;
        string? devUrl = null;
        string? dist = null;
        string? identifier = null;
        string? displayName = null;
        string? packageManager = null;
        var devCommand = new List<string>();
        var buildCommand = new List<string>();
        var packageManagerCommand = new List<string>();
        var dryRun = false;
        var force = false;
        return new Command("init", "Create a validated neoastra.json without destructive rewriting.")
        {
            new CommandUsage(),
            {
                "config=",
                "Destination {FILE} (default: neoastra.json).",
                value => config = value
            },
            {
                "frontend-root=",
                "Frontend root {DIRECTORY}.",
                value => frontendRoot = value
            },
            {
                "dev-command=",
                "Development command argument; repeat for each {ARGUMENT}.",
                devCommand
            },
            {
                "dev-url=",
                "Exact loopback development {URL}.",
                value => devUrl = value
            },
            {
                "build-command=",
                "Production command argument; repeat for each {ARGUMENT}.",
                buildCommand
            },
            {
                "dist=",
                "Frontend output {DIRECTORY}.",
                value => dist = value
            },
            {
                "identifier=",
                "Stable application {IDENTIFIER}.",
                value => identifier = value
            },
            {
                "display-name=",
                "Application display {NAME}.",
                value => displayName = value
            },
            {
                "package-manager=",
                "Package {MANAGER}: npm, pnpm, yarn, bun, or none.",
                value => packageManager = value
            },
            {
                "package-manager-command=",
                "Package-manager command prefix; repeat for each {ARGUMENT}.",
                packageManagerCommand
            },
            {
                "dry-run",
                "Print the validated configuration without writing it.",
                value => dryRun = value is not null
            },
            {
                "force",
                "Replace the destination after creating a non-overwriting backup.",
                value => force = value is not null
            },
            new HelpOption(),
            (_, _) => ValueTask.FromResult(NeoToolCommands.Init(new NeoInitOptions(
                config,
                NeoToolCommands.Required(frontendRoot, "--frontend-root"),
                devCommand,
                NeoToolCommands.Required(devUrl, "--dev-url"),
                buildCommand,
                NeoToolCommands.Required(dist, "--dist"),
                NeoToolCommands.Required(identifier, "--identifier"),
                NeoToolCommands.Required(displayName, "--display-name"),
                NeoToolCommands.Required(packageManager, "--package-manager"),
                packageManagerCommand,
                dryRun,
                force))),
        };
    }
}
