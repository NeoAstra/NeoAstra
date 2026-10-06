// TEMPORARY: diagnostics for the "100,000 small messages" failure on macOS 15. This file is removed with the
// investigation; nothing in it is part of the conformance harness.
using System.Diagnostics;
using System.Text.RegularExpressions;
using NeoAstra;

internal static partial class Program
{
    private sealed partial class ConformanceSuite
    {
        private const string DiagnosticPost = "globalThis.__fixturePostMessage({ kind: 'stress', value: i })";

        private async ValueTask RunMessageFloodDiagnosticsAsync(NeoEnvironment environment, BridgeMode bridgeMode)
        {
            var started = DateTime.Now;
            try
            {
                Console.WriteLine($"DIAG start {started:HH:mm:ss.fff} os={Environment.OSVersion} processors={Environment.ProcessorCount}");
                Console.WriteLine($"DIAG sw_vers: {RunTool("/usr/bin/sw_vers", []).ReplaceLineEndings(" | ")}");
                Console.WriteLine("DIAG WebKit.framework CFBundleVersion: " + RunTool("/usr/bin/defaults",
                    ["read", "/System/Library/Frameworks/WebKit.framework/Versions/A/Resources/Info", "CFBundleVersion"]).Trim());

                // Batches that stay under the suspected limit, each one awaited by the host before the next is posted.
                await DiagnoseAsync(environment, bridgeMode, "batches 10 x 10000", 100_000, waitForTransport: true, async (view, received, clock) =>
                {
                    for (var batch = 0; batch < 10; batch++)
                    {
                        var loop = await view.EvaluateScriptAsync(BurstScript(10_000));
                        var target = (batch + 1) * 10_000;
                        var atReturn = received();
                        var returnedAt = clock.ElapsedMilliseconds;
                        var arrived = await WaitForCountAsync(received, target, TimeSpan.FromSeconds(15));
                        Console.WriteLine($"DIAG batches 10 x 10000: batch {batch} loop={loop} ms, evaluation returned at {returnedAt} ms with received={atReturn}, " +
                                          $"received={received()} at {clock.ElapsedMilliseconds} ms arrived={arrived}");
                        if (!arrived) break;
                    }
                });

                // A sender that holds messages back while 4096 are not acknowledged, with the Promise of postMessage.
                await DiagnoseAsync(environment, bridgeMode, "window 4096 of 100000", 100_000, waitForTransport: true, async (view, received, clock) =>
                {
                    var result = await view.EvaluateScriptAsync(
                        "(() => { const handler = globalThis.webkit.messageHandlers._neoastra_transport_v1; const limit = 4096; const queue = []; " +
                        "let head = 0, inFlight = 0; const stats = globalThis.__diagWindow = { fulfilled: 0, rejected: 0, maxQueued: 0, returned: '' }; " +
                        "const settle = () => { inFlight--; while (inFlight < limit && head < queue.length) post(queue[head++]); " +
                        "if (head === queue.length) { queue.length = 0; head = 0; } }; " +
                        "const post = value => { inFlight++; const result = handler.postMessage(value); stats.returned = Object.prototype.toString.call(result); " +
                        "Promise.resolve(result).then(() => { stats.fulfilled++; settle(); }, () => { stats.rejected++; settle(); }); }; " +
                        "const send = value => { if (inFlight < limit && head === queue.length) post(value); " +
                        "else { queue.push(value); stats.maxQueued = Math.max(stats.maxQueued, queue.length - head); } }; " +
                        "const t = performance.now(); for (let i = 0; i < 100000; i++) send({ kind: 'stress', value: i }); " +
                        "return Math.round(performance.now() - t); })()");
                    Console.WriteLine($"DIAG window 4096 of 100000: loop={result} ms received={received()} at {clock.ElapsedMilliseconds} ms");
                    await WaitForCountAsync(received, 100_000, TimeSpan.FromSeconds(25));
                    Console.WriteLine("DIAG window 4096 of 100000: stats=" + await view.EvaluateScriptAsync("JSON.stringify(globalThis.__diagWindow)"));
                });

                // Single bursts on both sides of 50,000.
                foreach (var count in new[] { 20_000, 45_000, 60_000, 80_000 })
                {
                    var label = $"burst {count}";
                    await DiagnoseAsync(environment, bridgeMode, label, count, waitForTransport: true, async (view, received, clock) =>
                    {
                        var loop = await view.EvaluateScriptAsync(BurstScript(count));
                        Console.WriteLine($"DIAG {label}: evaluation returned loop={loop} ms received={received()} at {clock.ElapsedMilliseconds} ms");
                        await WaitForCountAsync(received, count, TimeSpan.FromSeconds(20));
                    });
                }

                // The page yields between chunks of 1000 with a timer, and the host gives no feedback.
                await DiagnoseAsync(environment, bridgeMode, "timer chunks 100 x 1000", 100_000, waitForTransport: true, async (view, received, clock) =>
                {
                    _ = await view.EvaluateScriptAsync(
                        "(() => { let i = 0; const step = () => { const end = Math.min(i + 1000, 100000); for (; i < end; i++) " + DiagnosticPost + "; " +
                        "if (i < 100000) setTimeout(step, 0); else globalThis.__diagPaced = Math.round(performance.now() - start); }; " +
                        "const start = performance.now(); step(); })(); true");
                    await WaitForCountAsync(received, 100_000, TimeSpan.FromSeconds(25));
                    Console.WriteLine($"DIAG timer chunks 100 x 1000: received={received()} at {clock.ElapsedMilliseconds} ms page posted in " +
                                      await view.EvaluateScriptAsync("String(globalThis.__diagPaced)") + " ms");
                });

                // The scenario as it is written in the harness.
                await DiagnoseAsync(environment, bridgeMode, "burst 100000 (the scenario)", 100_000, waitForTransport: false, async (view, received, clock) =>
                {
                    Console.WriteLine("DIAG burst 100000 (the scenario): transport connected before the burst=" +
                                      await view.EvaluateScriptAsync("globalThis.__fixtureTransportConnected === true"));
                    clock.Restart();
                    try
                    {
                        var result = await view.EvaluateScriptAsync(
                            "for (let i = 0; i < 100000; i++) globalThis.__fixturePostMessage({ kind: 'stress', value: i }); true");
                        Console.WriteLine($"DIAG burst 100000 (the scenario): evaluation returned {result} received={received()} at {clock.ElapsedMilliseconds} ms");
                    }
                    catch (Exception exception)
                    {
                        Console.WriteLine($"DIAG burst 100000 (the scenario): evaluation FAILED at {clock.ElapsedMilliseconds} ms received={received()}: {Describe(exception)}");
                    }
                    await WaitForCountAsync(received, 100_000, TimeSpan.FromSeconds(5));
                    Console.WriteLine($"DIAG burst 100000 (the scenario): after the wait received={received()} at {clock.ElapsedMilliseconds} ms");
                    try
                    {
                        Console.WriteLine("DIAG burst 100000 (the scenario): a later evaluation returned " + await view.EvaluateScriptAsync("1 + 1"));
                    }
                    catch (Exception exception)
                    {
                        Console.WriteLine("DIAG burst 100000 (the scenario): a later evaluation FAILED: " + Describe(exception));
                    }
                });

                DumpWebKitLog(started);
                DumpCrashReports(started);

                // The scenario "views sharing a window" compares this text with "/index.html" in quotes and fails on macOS.
                await DiagnoseAsync(environment, bridgeMode, "location.pathname as returned", 0, waitForTransport: false, async (view, _, _) =>
                    Console.WriteLine("DIAG location.pathname as returned: " + await view.EvaluateScriptAsync("location.pathname")));
            }
            catch (Exception exception)
            {
                Console.WriteLine("DIAG aborted: " + exception);
            }
            Console.WriteLine($"DIAG end after {(DateTime.Now - started).TotalSeconds:F1} s");
        }

        private static string BurstScript(int count)
            => "(() => { const t = performance.now(); for (let i = 0; i < " + count + "; i++) " + DiagnosticPost +
               "; return Math.round(performance.now() - t); })()";

        private async ValueTask DiagnoseAsync(
            NeoEnvironment environment,
            BridgeMode bridgeMode,
            string label,
            int expected,
            bool waitForTransport,
            Func<NeoAstra.NeoAstra, Func<int>, Stopwatch, Task> body)
        {
            Console.WriteLine($"DIAG {label}: begin {DateTime.Now:HH:mm:ss.fff}");
            var window = CreateHiddenWindow("NeoAstra diagnostics " + label);
            try
            {
                await using var view = await environment.CreateWebViewAsync(
                    NeoAstraHost.FillWindow(window), CreateViewOptions(null, bridgeMode));
                await NavigateAndWaitAsync(view, IndexUri, options.Timeout);
                await WaitForDocumentLoadAsync(view, options.Timeout);
                if (waitForTransport)
                {
                    await WaitUntilScriptAsync(view, "globalThis.__fixtureTransportConnected === true",
                        "The transport did not connect.", options.Timeout);
                }

                var received = 0;
                var clock = Stopwatch.StartNew();
                void OnMessage(object? _, NeoWebMessageReceivedEventArgs message)
                {
                    if (message.Json.Contains("\"stress\"", StringComparison.Ordinal)) Interlocked.Increment(ref received);
                }
                void OnProcessFailed(object? _, NeoProcessFailedEventArgs failure)
                    => Console.WriteLine($"DIAG {label}: PROCESS FAILED at {clock.ElapsedMilliseconds} ms ({DateTime.Now:HH:mm:ss.fff}) kind={failure.Kind} " +
                                         $"crash={failure.IsCrash} recovery={failure.RecoveryAction} native={failure.NativeCode} " +
                                         $"description={failure.ProcessDescription} received={Volatile.Read(ref received)}");

                view.MessageReceived += OnMessage;
                view.ProcessFailed += OnProcessFailed;
                try
                {
                    await body(view, () => Volatile.Read(ref received), clock);
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"DIAG {label}: FAILED at {clock.ElapsedMilliseconds} ms received={Volatile.Read(ref received)}: {Describe(exception)}");
                }
                finally
                {
                    view.MessageReceived -= OnMessage;
                    view.ProcessFailed -= OnProcessFailed;
                }
                var total = Volatile.Read(ref received);
                Console.WriteLine($"DIAG {label}: end received={total}/{expected} {(total == expected ? "COMPLETE" : "INCOMPLETE")} at {clock.ElapsedMilliseconds} ms");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"DIAG {label}: setup or teardown FAILED: {Describe(exception)}");
            }
            finally
            {
                await window.DisposeAsync();
            }
        }

        private static async Task<bool> WaitForCountAsync(Func<int> received, int target, TimeSpan timeout)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < timeout)
            {
                if (received() >= target) return true;
                await Task.Delay(10);
            }
            return received() >= target;
        }

        private static string Describe(Exception exception)
            => exception is NeoAstraException neo
                ? $"{exception.GetType().Name} code={neo.Code} operation={neo.Operation} domain={neo.Domain} native={neo.NativeCode} message={neo.Message}"
                : $"{exception.GetType().Name} message={exception.Message}";

        private static void DumpWebKitLog(DateTime started)
        {
            const string predicate =
                "subsystem == \"com.apple.WebKit\" AND (category == \"IPC\" OR eventMessage CONTAINS[c] \"terminat\" OR " +
                "eventMessage CONTAINS[c] \"crash\" OR eventMessage CONTAINS[c] \"didClose\" OR eventMessage CONTAINS[c] \"misbehav\" OR " +
                "eventMessage CONTAINS[c] \"unresponsive\")";
            string[] arguments = ["show", "--start", started.ToString("yyyy-MM-dd HH:mm:ss"), "--style", "compact", "--predicate", predicate];
            var output = RunTool("/usr/bin/log", arguments);
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 3)
            {
                Console.WriteLine("DIAG log: 'log show' returned " + lines.Length + " lines (" + output.Trim().ReplaceLineEndings(" | ") + "); trying sudo");
                output = RunTool("/usr/bin/sudo", ["-n", "/usr/bin/log", .. arguments]);
                lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }
            Console.WriteLine($"DIAG log: {lines.Length} lines from the unified log since {started:HH:mm:ss}");

            var pending = new Regex(@"has (\d+) pending incoming messages, will only process (\d+)");
            var perSecond = new SortedDictionary<string, (int Lines, int MaxPending, int MinBatch)>(StringComparer.Ordinal);
            var important = new List<string>();
            var other = new List<string>();
            var firstInQueue = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                var match = pending.Match(line);
                if (match.Success)
                {
                    var second = line.Length >= 19 ? line[11..19] : "?";
                    var value = int.Parse(match.Groups[1].Value);
                    var batch = int.Parse(match.Groups[2].Value);
                    perSecond[second] = perSecond.TryGetValue(second, out var current)
                        ? (current.Lines + 1, Math.Max(current.MaxPending, value), Math.Min(current.MinBatch, batch))
                        : (1, value, batch);
                    continue;
                }
                var first = line.IndexOf("first IPC message in queue is ", StringComparison.Ordinal);
                if (first >= 0)
                {
                    var name = line[(first + "first IPC message in queue is ".Length)..].Trim();
                    firstInQueue[name] = firstInQueue.GetValueOrDefault(name) + 1;
                    continue;
                }
                if (line.Contains("incoming messages have been queued", StringComparison.Ordinal) ||
                    line.Contains("misbehav", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("terminat", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("didClose", StringComparison.Ordinal) ||
                    line.Contains("crash", StringComparison.OrdinalIgnoreCase))
                {
                    important.Add(line);
                }
                else
                {
                    other.Add(line);
                }
            }
            Console.WriteLine($"DIAG log: {important.Count} lines on termination, {other.Count} other lines");
            foreach (var line in important.Take(90)) Console.WriteLine("DIAG log! " + (line.Length > 420 ? line[..420] : line));
            foreach (var line in other.Take(40)) Console.WriteLine("DIAG log| " + (line.Length > 420 ? line[..420] : line));
            foreach (var (name, count) in firstInQueue) Console.WriteLine($"DIAG log first message in a throttled queue: {name} x{count}");
            foreach (var (second, value) in perSecond)
            {
                Console.WriteLine($"DIAG log throttling {second}: lines={value.Lines} maxPending={value.MaxPending} minBatch={value.MinBatch}");
            }
        }

        private static void DumpCrashReports(DateTime started)
        {
            foreach (var directory in new[]
                     {
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Logs/DiagnosticReports"),
                         "/Library/Logs/DiagnosticReports",
                     })
            {
                try
                {
                    var files = Directory.Exists(directory)
                        ? Directory.GetFiles(directory).Where(file => File.GetLastWriteTime(file) >= started).Select(Path.GetFileName).ToArray()
                        : [];
                    Console.WriteLine($"DIAG crash reports in {directory} since the start: {files.Length} {string.Join(", ", files)}");
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"DIAG crash reports in {directory}: {exception.Message}");
                }
            }
        }

        private static string RunTool(string fileName, string[] arguments)
        {
            try
            {
                var start = new ProcessStartInfo(fileName)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                var error = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(60_000))
                {
                    process.Kill();
                    return output + "\n(timed out)";
                }
                var errorText = error.GetAwaiter().GetResult().Trim();
                return process.ExitCode == 0 && errorText.Length == 0 ? output : $"{output}\n(exit {process.ExitCode}) {errorText}";
            }
            catch (Exception exception)
            {
                return $"(could not run {fileName}: {exception.Message})";
            }
        }
    }
}
