// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
namespace NeoAstra.Tooling;

internal static class NeoPackageManagerCommandResolver
{
    private static readonly (string Executable, string[] Prefix)[] NpmManagers =
    [
        ("volta", ["volta", "run", "npm"]),
        ("mise", ["mise", "exec", "--", "npm"]),
        ("asdf", ["asdf", "exec", "npm"]),
    ];

    internal static NeoCommand Discover(string packageManager, string workingDirectory, Func<string, bool>? isAvailable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageManager);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (packageManager == "none")
        {
            return new NeoCommand(["none"]);
        }

        isAvailable ??= executable => NeoChildProcess.CanResolveExecutable(executable, workingDirectory);
        if (isAvailable(packageManager))
        {
            return new NeoCommand([packageManager]);
        }

        if (packageManager == "npm")
        {
            if (isAvailable("fnm"))
            {
                // fnm does not resolve extensionless npm on Windows and needs a version when a
                // project has no version hint. Its default alias is the least surprising fallback.
                var npm = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";
                return HasFnmVersionHint(workingDirectory)
                    ? new NeoCommand(["fnm", "exec", npm])
                    : new NeoCommand(["fnm", "exec", "--using=default", npm]);
            }

            foreach (var candidate in NpmManagers)
            {
                if (isAvailable(candidate.Executable))
                {
                    return new NeoCommand(candidate.Prefix);
                }
            }
        }

        // Keep the conventional command so process startup reports the usual actionable error.
        return new NeoCommand([packageManager]);
    }

    private static bool HasFnmVersionHint(string workingDirectory)
    {
        return File.Exists(Path.Combine(workingDirectory, ".nvmrc"))
            || File.Exists(Path.Combine(workingDirectory, ".node-version"));
    }

    internal static NeoCommand Apply(NeoResolvedProject project, NeoCommand command)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        if (project.PackageManager == "none" || !IsPackageManagerInvocation(command.Executable, project.PackageManager))
        {
            return command;
        }

        return new NeoCommand([.. project.PackageManagerCommand.Arguments, .. command.Arguments.Skip(1)]);
    }

    private static bool IsPackageManagerInvocation(string executable, string packageManager)
    {
        if (Path.IsPathFullyQualified(executable) || executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            return false;
        }

        return Path.GetFileNameWithoutExtension(executable).Equals(packageManager, StringComparison.OrdinalIgnoreCase);
    }
}