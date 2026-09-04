// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace NeoAstra.Tooling;

internal static class NeoOriginPolicy
{
    internal static Uri ValidateDevelopmentUrl(string value, bool allowRemote)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.HostNameType is UriHostNameType.Unknown or UriHostNameType.Dns)
            throw new NeoToolException("development_origin", "The development URL must use HTTP(S), an IP-literal host, and no credentials or fragment.");
        var loopback = IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address) && (address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback));
        if (!loopback && !allowRemote)
            throw new NeoToolException("remote_development_origin", "The development URL must use exactly 127.0.0.1 or ::1 unless allowRemoteDevServer is explicitly enabled.");
        return uri;
    }

    internal static bool SameOrigin(Uri left, Uri right) => string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;
}

internal interface INeoChildProcess : IAsyncDisposable
{
    int? ExitCode { get; }

    Task<int> Completion { get; }

    Task StopAsync(TimeSpan timeout);
}

internal interface INeoProcessFactory
{
    INeoChildProcess Start(NeoProcessStart start);
}

internal sealed record NeoProcessStart(string Label, NeoCommand Command, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment, IReadOnlySet<string> SecretEnvironment, Action<string, bool> Log);
internal sealed class NeoProcessFactory : INeoProcessFactory
{
    public INeoChildProcess Start(NeoProcessStart start) => new NeoChildProcess(start);
}

internal sealed class NeoChildProcess : INeoChildProcess
{
    private readonly Process _process;
    private readonly Task _stdout;
    private readonly Task _stderr;
    private readonly Task<int> _completion;
    private int _stopping;
    internal NeoChildProcess(NeoProcessStart start)
    {
        if (!Directory.Exists(start.WorkingDirectory))
            throw new NeoToolException("process_working_directory", $"{start.Label} working directory does not exist.");
        var executable = ResolveExecutable(start.Command.Executable, start.WorkingDirectory);
        var info = new ProcessStartInfo
        {
            FileName = executable.Path,
            WorkingDirectory = start.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in executable.PrefixArguments)
            info.ArgumentList.Add(argument);
        foreach (var argument in start.Command.Arguments.Skip(1))
            info.ArgumentList.Add(argument);
        foreach (var pair in start.Environment)
            info.Environment[pair.Key] = pair.Value;
        _process = new Process
        {
            StartInfo = info,
            EnableRaisingEvents = true
        };
        try
        {
            if (!_process.Start())
                throw new NeoToolException("process_start", $"Unable to start {start.Label}.");
        }
        catch (Exception exception) when (exception is not NeoToolException)
        {
            _process.Dispose();
            throw new NeoToolException("process_start", $"Unable to start {start.Label}; verify the configured executable.");
        }

        _stdout = PumpAsync(_process.StandardOutput, false, start);
        _stderr = PumpAsync(_process.StandardError, true, start);
        _completion = CompleteAsync();
    }

    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;
    public Task<int> Completion => _completion;

    public async Task StopAsync(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            try
            {
                await _completion.WaitAsync(timeout).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
                throw new NeoToolException("process_teardown_timeout", "A child process tree did not terminate within the bounded shutdown period.");
            }
        }

        var started = Stopwatch.StartNew();
        if (!_process.HasExited)
        {
            var graceful = false;
            try
            {
                graceful = OperatingSystem.IsWindows() ? _process.CloseMainWindow() : NeoSignals.Kill(_process.Id, 15) == 0;
            }
            catch (InvalidOperationException)
            {
            }

            if (graceful)
            {
                var grace = timeout < TimeSpan.FromSeconds(2) ? timeout : TimeSpan.FromSeconds(2);
                try
                {
                    await _completion.WaitAsync(grace).ConfigureAwait(false);
                    return;
                }
                catch (TimeoutException)
                {
                }
            }

            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        var remaining = timeout - started.Elapsed;
        if (remaining <= TimeSpan.Zero)
            throw new NeoToolException("process_teardown_timeout", "A child process tree did not terminate within the bounded shutdown period.");
        try
        {
            await _completion.WaitAsync(remaining).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new NeoToolException("process_teardown_timeout", "A child process tree did not terminate within the bounded shutdown period.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        finally
        {
            _process.Dispose();
        }
    }

    private async Task<int> CompleteAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(_stdout, _stderr).ConfigureAwait(false);
        return _process.ExitCode;
    }

    private static async Task PumpAsync(StreamReader reader, bool error, NeoProcessStart start)
    {
        const int maximumLineCharacters = 16 * 1024;
        var buffer = new char[1024];
        var line = new StringBuilder(maximumLineCharacters);
        var truncated = false;
        while (await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) is var read && read != 0)
        {
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] == '\n')
                {
                    Emit();
                    continue;
                }

                if (buffer[index] == '\r')
                    continue;
                if (line.Length < maximumLineCharacters)
                    line.Append(buffer[index]);
                else
                    truncated = true;
            }
        }

        if (line.Length != 0 || truncated)
            Emit();
        void Emit()
        {
            var value = line.ToString();
            foreach (var name in start.SecretEnvironment)
                if (start.Environment.TryGetValue(name, out var secret) && secret.Length != 0)
                    value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            if (truncated)
                value += " [output truncated]";
            start.Log(value, error);
            line.Clear();
            truncated = false;
        }
    }

    internal static (string Path, IReadOnlyList<string> PrefixArguments) ResolveExecutable(string executable, string workingDirectory)
    {
        if (Path.IsPathFullyQualified(executable) || executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            var path = Path.GetFullPath(executable, workingDirectory);
            if (!File.Exists(path))
                throw new NeoToolException("executable_missing", $"Configured executable '{executable}' was not found.");
            if (OperatingSystem.IsWindows() && Path.GetExtension(path) is ".cmd" or ".bat" or ".ps1")
                throw new NeoToolException("executable_shell_shim", "Shell scripts are not executed implicitly; configure an executable command array.");
            return (path, Array.Empty<string>());
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows() ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';') : [string.Empty];
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim('"'), executable.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? executable : executable + extension);
                if (!File.Exists(candidate))
                    continue;
                if (!OperatingSystem.IsWindows() || candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || candidate.EndsWith(".com", StringComparison.OrdinalIgnoreCase))
                    return (candidate, Array.Empty<string>());
                var packageManager = ResolveWindowsPackageManager(executable, Path.GetDirectoryName(candidate)!);
                if (packageManager is not null)
                    return packageManager.Value;
            }

        throw new NeoToolException("executable_missing", $"Configured executable '{executable}' was not found on PATH; NeoAstra never installs it automatically.");
    }

    internal static bool CanResolveExecutable(string executable, string workingDirectory)
    {
        try
        {
            _ = ResolveExecutable(executable, workingDirectory);
            return true;
        }
        catch (NeoToolException exception) when (exception.Code is "executable_missing" or "executable_shell_shim")
        {
            return false;
        }
    }

    private static (string Path, IReadOnlyList<string> PrefixArguments)? ResolveWindowsPackageManager(string executable, string directory)
    {
        var node = Path.Combine(directory, "node.exe");
        if (!File.Exists(node))
            return null;
        var script = executable.ToLowerInvariant() switch
        {
            "npm" or "npm.cmd" => Path.Combine(directory, "node_modules", "npm", "bin", "npm-cli.js"),
            "npx" or "npx.cmd" => Path.Combine(directory, "node_modules", "npm", "bin", "npx-cli.js"),
            "pnpm" or "pnpm.cmd" => Path.Combine(directory, "node_modules", "corepack", "dist", "pnpm.js"),
            "yarn" or "yarn.cmd" => Path.Combine(directory, "node_modules", "corepack", "dist", "yarn.js"),
            _ => string.Empty,
        };
        return script.Length != 0 && File.Exists(script) ? (node, new[] { script }) : null;
    }
}

internal static partial class NeoSignals
{
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    internal static partial int Kill(int processId, int signal);
}

internal interface INeoReadinessProbe
{
    Task WaitAsync(Uri url, TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class NeoReadinessProbe : INeoReadinessProbe
{
    public async Task WaitAsync(Uri url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
        try
        {
            while (true)
            {
                timeoutSource.Token.ThrowIfCancellationRequested();
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
                    if (ValidateResponse(url, response))
                        return;
                }
                catch (OperationCanceledException) when (!timeoutSource.IsCancellationRequested)
                {
                    // A single HTTP timeout is retryable within the overall readiness deadline.
                }
                catch (HttpRequestException)
                {
                }

                await Task.Delay(100, timeoutSource.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NeoToolException("readiness_timeout", "The development server did not become ready before the bounded timeout.");
        }
    }

    internal static bool ValidateResponse(Uri configuredUrl, HttpResponseMessage response)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            var location = response.Headers.Location;
            if (location is null || !location.IsAbsoluteUri || !NeoOriginPolicy.SameOrigin(configuredUrl, location))
                throw new NeoToolException("readiness_redirect", "The development server redirected readiness to an unexpected origin.");
            throw new NeoToolException("readiness_redirect", "Readiness redirects are rejected; configure the exact final development URL.");
        }

        return response.IsSuccessStatusCode;
    }
}

internal sealed class NeoDevelopmentOrchestrator(INeoProcessFactory processFactory, INeoReadinessProbe readiness)
{
    internal async Task<int> RunAsync(NeoResolvedProject project, CancellationToken cancellationToken)
    {
        if (project.AllowRemoteDevServer)
            Console.Error.WriteLine("WARNING: insecure remote development server opt-in is active; release policy is unchanged.");
        static void Log(string label, string line, bool error) => (error ? Console.Error : Console.Out).WriteLine($"[{label}] {line}");
        var contractEnvironment = new Dictionary<string, string>(project.Environment, StringComparer.OrdinalIgnoreCase)
        {
            ["NeoAstraBuildFrontend"] = "false"
        };
        await using (var contract = processFactory.Start(new("contract", project.ContractCommand, project.ProjectDirectory, contractEnvironment, project.SecretEnvironment, (line, error) => Log("contract", line, error))))
        {
            var contractExit = await contract.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (contractExit != 0)
                return contractExit;
        }

        var frontendCommand = NeoPackageManagerCommandResolver.Apply(project, project.DevCommand);
        await using var frontend = processFactory.Start(new("frontend", frontendCommand, project.FrontendRoot, project.Environment, project.SecretEnvironment, (line, error) => Log("frontend", line, error)));
        try
        {
            using var readinessStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var ready = readiness.WaitAsync(project.DevUrl, TimeSpan.FromSeconds(60), readinessStop.Token);
            await Task.WhenAny(ready, frontend.Completion).ConfigureAwait(false);
            if (frontend.Completion.IsCompleted)
            {
                readinessStop.Cancel();
                // The frontend exit is authoritative; observe an abandoned probe's cancellation/failure.
                try { await ready.ConfigureAwait(false); } catch (Exception) { }
                var frontendExit = await frontend.Completion.ConfigureAwait(false);
                return frontendExit == 0 ? 1 : frontendExit;
            }
            await ready.ConfigureAwait(false);
            var backendEnvironment = new Dictionary<string, string>(project.Environment, StringComparer.OrdinalIgnoreCase)
            {
                ["NEOASTRA_ENVIRONMENT"] = "Development",
                ["NEOASTRA_DEV_URL"] = project.DevUrl.AbsoluteUri,
                ["NeoAstraBuildFrontend"] = "false",
            };
            await using var backend = processFactory.Start(new("backend", project.BackendCommand, project.ProjectDirectory, backendEnvironment, project.SecretEnvironment, (line, error) => Log("backend", line, error)));
            using var registration = cancellationToken.Register(() =>
            {
            });
            var cancellation = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var completed = await Task.WhenAny(frontend.Completion, backend.Completion, cancellation).ConfigureAwait(false);
            if (completed == cancellation)
                return 0;
            var exit = completed == frontend.Completion ? await frontend.Completion.ConfigureAwait(false) : await backend.Completion.ConfigureAwait(false);
            return exit == 0 ? 1 : exit;
        }
        finally
        {
            await frontend.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}
