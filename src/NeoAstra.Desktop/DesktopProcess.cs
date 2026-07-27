// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Diagnostics;
using System.Text;

namespace NeoAstra.Desktop;

internal readonly record struct DesktopProcessResult(int ExitCode, byte[] Output, byte[] Error);

internal static class DesktopProcess
{
    internal static async ValueTask<DesktopProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        ReadOnlyMemory<byte> input, TimeSpan timeout, bool captureOutput, CancellationToken cancellationToken, bool closeStandardInput = false)
    {
        if (!Path.IsPathFullyQualified(executable)) throw new ArgumentException("Desktop service executables must use trusted absolute paths.", nameof(executable));
        if (arguments.Count > 128 || arguments.Any(static value => value.Length > 32_768 || value.Contains('\0'))) throw new ArgumentException("Process arguments are unbounded or contain null characters.", nameof(arguments));
        if (input.Length > NeoDesktopLimits.MaximumClipboardBytes) throw new ArgumentOutOfRangeException(nameof(input));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(timeout));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = !input.IsEmpty || closeStandardInput,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = true,
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (start.RedirectStandardInput) start.StandardInputEncoding = new UTF8Encoding(false);
        if (captureOutput) start.StandardOutputEncoding = new UTF8Encoding(false);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("The trusted desktop helper did not start.");
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var token = linked.Token;
        var outputTask = captureOutput ? ReadBoundedAsync(process.StandardOutput.BaseStream, NeoDesktopLimits.MaximumProcessOutputBytes, token) : Task.FromResult(Array.Empty<byte>());
        var errorTask = ReadBoundedAsync(process.StandardError.BaseStream, 16 * 1024, token);
        try
        {
            if (start.RedirectStandardInput)
            {
                if (!input.IsEmpty)
                {
                    await process.StandardInput.BaseStream.WriteAsync(input, token).ConfigureAwait(false);
                    await process.StandardInput.BaseStream.FlushAsync(token).ConfigureAwait(false);
                }
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            Array.Clear(error);
            return new(process.ExitCode, output, Array.Empty<byte>());
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            await ObserveAndClearAsync(outputTask).ConfigureAwait(false);
            await ObserveAndClearAsync(errorTask).ConfigureAwait(false);
            throw;
        }
    }

    internal static string FindTrustedExecutable(params string[] candidates)
    {
        foreach (var candidate in candidates)
            if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate)) return candidate;
        return string.Empty;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximum, 4096));
        var buffer = new byte[4096];
        var exceeded = false;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (exceeded) throw new InvalidDataException("A desktop helper exceeded its output limit.");
                return output.ToArray();
            }
            var available = maximum - checked((int)output.Length);
            if (read > available)
            {
                if (available > 0) output.Write(buffer, 0, available);
                exceeded = true;
            }
            else if (!exceeded) output.Write(buffer, 0, read);
        }
    }

    private static async Task ObserveAndClearAsync(Task<byte[]> task)
    {
        try { Array.Clear(await task.ConfigureAwait(false)); }
        catch { }
    }
}
