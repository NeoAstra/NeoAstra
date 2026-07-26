using System.Diagnostics;
using System.Text.Json.Serialization;
using NeoAstra;
using NeoAstra.Rpc;

internal static class Program
{
    private static int _nextViewLabel;
    private static readonly Uri FixtureUri = new("benchmark://fixture/index.html");

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Browser benchmarks were not run. Pass --run to opt in; no browser was opened.");
            return 0;
        }

        if (!BenchmarkOptions.TryParse(args, out var options))
        {
            Console.Error.WriteLine(
                "Usage: dotnet run --project src/NeoAstra.Benchmarks -c Release -- --run [--quick] " +
                "[--iterations <1-10000>] [--lifecycle-iterations <1-100>] [--timeout-seconds <5-300>] [--idle-seconds <1-10>]");
            return 2;
        }

        try
        {
            return NeoApplication.Run(
                new NeoApplicationOptions
                {
                    ApplicationName = "NeoAstra Benchmarks",
                    ShutdownMode = NeoApplicationShutdownMode.Explicit,
                },
                application => RunAsync(application, options));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL benchmarks");
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async ValueTask RunAsync(NeoApplication application, BenchmarkOptions options)
    {
        var runner = new BenchmarkRunner(application, options);
        await runner.RunAsync();
        application.Shutdown();
    }

    private sealed class BenchmarkRunner(NeoApplication application, BenchmarkOptions options)
    {
        private readonly string _assetsPath = Path.Combine(AppContext.BaseDirectory, "assets");
        private BenchmarkReporter? _reporter;

        internal async ValueTask RunAsync()
        {
            if (!Directory.Exists(_assetsPath))
            {
                throw new DirectoryNotFoundException($"Benchmark assets were not copied to '{_assetsPath}'.");
            }

            Console.WriteLine(
                $"MODE quick={options.Quick.ToString().ToLowerInvariant()} iterations={options.Iterations} " +
                $"lifecycleIterations={options.LifecycleIterations} timeout={options.Timeout.TotalSeconds:F0}s network=disabled");
            Console.WriteLine(
                "NOTE Measurements include NeoAstra, native backend, browser engine, OS scheduling, and machine noise. " +
                "Engine startup and process behavior are not controlled entirely by NeoAstra.");
            Console.WriteLine("NOTE Compare results against same-machine, same-engine baselines; do not compare unlike platforms as absolute acceptance thresholds.");

            var provider = new MeasuredResourceProvider(new NeoDirectoryResourceProvider(_assetsPath));
            var environmentOptions = CreateEnvironmentOptions(provider);

            // Warm engine/environment paths before timed work. This intentionally means the environment
            // result below is warm creation, not a claim about cold browser-engine startup.
            await using (var warmupEnvironment = await application.CreateEnvironmentAsync(environmentOptions)
                             .AsTask().WaitAsync(options.Timeout))
            {
                _ = warmupEnvironment.RuntimeInfo;
            }

            var environmentWatch = Stopwatch.StartNew();
            await using var environment = await application.CreateEnvironmentAsync(environmentOptions)
                .AsTask().WaitAsync(options.Timeout);
            environmentWatch.Stop();

            _reporter = new BenchmarkReporter(environment.RuntimeInfo);
            _reporter.Measurement(
                "environment creation time",
                environmentWatch.Elapsed.TotalMilliseconds,
                "ms",
                1,
                "warm environment creation after one untimed warm-up; not cold engine startup");

            if (!IsSupported(environment, NeoCapability.CustomScheme))
            {
                throw new PlatformNotSupportedException(
                    $"The local benchmark fixture requires custom schemes. {CapabilityReason(environment, NeoCapability.CustomScheme)}");
            }

            var bridgeMode = GetBridgeMode(environment);
            var window = CreateHiddenWindow("NeoAstra benchmark host");
            try
            {
                await WarmUpViewAsync(environment, window, bridgeMode);
                await MeasureDispatcherAsync();
                await MeasureRpcDispatchAsync();
                await MeasureViewCreationAsync(environment, window);

                await using var view = await environment.CreateWebViewAsync(
                    NeoAstraHost.FillWindow(window),
                    CreateViewOptions(bridgeMode)).AsTask().WaitAsync(options.Timeout);
                await NavigateAndWaitAsync(view, FixtureUri);
                _ = await view.EvaluateScriptAsync("1 + 1").AsTask().WaitAsync(options.Timeout);

                await MeasureManagedNativeCallsAsync(environment, view);
                await MeasureJavaScriptRoundTripsAsync(view);
                await MeasureLocalAssetsAsync(view, provider);
                await MeasureMessagingAsync(view, bridgeMode);
                await MeasureRepeatedLifecycleAndMemoryAsync(environment, window);
                await MeasureIdleCpuAsync();
            }
            finally
            {
                await window.DisposeAsync();
            }

            Console.WriteLine("DONE benchmark run completed");
        }

        private async ValueTask WarmUpViewAsync(
            NeoEnvironment environment,
            NeoWindow window,
            BridgeMode bridgeMode)
        {
            await using var warmupView = await environment.CreateWebViewAsync(
                NeoAstraHost.FillWindow(window),
                CreateViewOptions(bridgeMode)).AsTask().WaitAsync(options.Timeout);
            await NavigateAndWaitAsync(warmupView, FixtureUri);
            _ = await warmupView.EvaluateScriptAsync("42").AsTask().WaitAsync(options.Timeout);
            _ = warmupView.ZoomFactor;
            await Task.Delay(100);
        }

        private async ValueTask MeasureDispatcherAsync()
        {
            var warmupCount = Math.Min(10, options.Iterations);
            await Task.Run(async () =>
            {
                for (var index = 0; index < warmupCount; index++)
                {
                    await application.Dispatcher.InvokeAsync(static () => { });
                }
            }).WaitAsync(options.Timeout);

            var stopwatch = Stopwatch.StartNew();
            await Task.Run(async () =>
            {
                for (var index = 0; index < options.Iterations; index++)
                {
                    await application.Dispatcher.InvokeAsync(static () => { });
                }
            }).WaitAsync(options.Timeout);
            stopwatch.Stop();

            Reporter.Measurement(
                "native dispatch latency",
                MicrosecondsPerOperation(stopwatch.Elapsed, options.Iterations),
                "us/op",
                options.Iterations,
                "background managed thread -> native UI dispatch -> managed callback completion");
        }

        private async ValueTask MeasureRpcDispatchAsync()
        {
            var count = Math.Max(10, options.Iterations);
            var builder = new NeoRpcBuilder(new NeoRpcOptions
            {
                MaximumRetainedRequestIds = count + 16,
                MaximumConcurrentInvocations = 1,
                MaximumConcurrentInvocationsPerSession = 1,
            });
            builder.AddCommand<RpcEchoRequest, RpcEchoResponse>(
                "benchmark.echo",
                static (request, _, _) => ValueTask.FromResult(new RpcEchoResponse(request.Value)),
                BenchmarkRpcJsonContext.Default.RpcEchoRequest,
                BenchmarkRpcJsonContext.Default.RpcEchoResponse);
            await using var host = builder.Build();
            var completed = 0;
            await using var session = host.OpenSession(
                new NeoRpcSessionIdentity("benchmark", "benchmark-document"),
                (_, _) => { completed++; return ValueTask.CompletedTask; });

            await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"warmup\",\"command\":\"benchmark.echo\",\"args\":{\"value\":1}}");
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var stopwatch = Stopwatch.StartNew();
            for (var index = 0; index < count; index++)
                await session.ReceiveAsync($"{{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"rpc-{index}\",\"command\":\"benchmark.echo\",\"args\":{{\"value\":{index}}}}}");
            stopwatch.Stop();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            if (completed != count + 1) throw new InvalidOperationException("The RPC benchmark lost a terminal result.");
            Reporter.Measurement("rpc small invoke round-trip", MicrosecondsPerOperation(stopwatch.Elapsed, count), "us/op", count, "JSON parse + source-generated metadata dispatch + serialization + in-memory send");
            Reporter.Measurement("rpc small invoke allocation", allocated / (double)count, "B/op", count, "managed current-thread allocation; excludes browser and native transport");
        }

        private async ValueTask MeasureManagedNativeCallsAsync(
            NeoEnvironment environment,
            NeoAstra.NeoAstra view)
        {
            if (!IsSupported(environment, NeoCapability.Zoom))
            {
                Reporter.Skip("managed-to-native call overhead", CapabilityReason(environment, NeoCapability.Zoom));
                return;
            }

            for (var index = 0; index < 10; index++) _ = view.ZoomFactor;
            var callCount = Math.Max(100, options.Iterations * 20);
            var accumulator = 0d;
            var stopwatch = Stopwatch.StartNew();
            for (var index = 0; index < callCount; index++) accumulator += view.ZoomFactor;
            stopwatch.Stop();
            GC.KeepAlive(accumulator);

            Reporter.Measurement(
                "managed-to-native call overhead",
                MicrosecondsPerOperation(stopwatch.Elapsed, callCount),
                "us/op",
                callCount,
                "NeoAstra.ZoomFactor native getter; excludes cached managed properties");
            await ValueTask.CompletedTask;
        }

        private async ValueTask MeasureViewCreationAsync(NeoEnvironment environment, NeoWindow window)
        {
            var count = options.Quick ? Math.Min(3, options.LifecycleIterations) : options.LifecycleIterations;
            var samples = new double[count];
            for (var index = 0; index < count; index++)
            {
                var stopwatch = Stopwatch.StartNew();
                await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window))
                    .AsTask().WaitAsync(options.Timeout);
                stopwatch.Stop();
                samples[index] = stopwatch.Elapsed.TotalMilliseconds;
            }

            Reporter.Measurement(
                "view creation time",
                samples.Average(),
                "ms/op",
                count,
                $"warm mean; min={samples.Min():F3} ms max={samples.Max():F3} ms; engine process reuse is backend-controlled");
        }

        private async ValueTask MeasureJavaScriptRoundTripsAsync(NeoAstra.NeoAstra view)
        {
            for (var index = 0; index < 5; index++)
            {
                _ = await view.EvaluateScriptAsync("6 * 7").AsTask().WaitAsync(options.Timeout);
            }

            var stopwatch = Stopwatch.StartNew();
            for (var index = 0; index < options.Iterations; index++)
            {
                var result = await view.EvaluateScriptAsync("6 * 7").AsTask().WaitAsync(options.Timeout);
                if (result != "42") throw new InvalidOperationException($"Unexpected JavaScript result '{result}'.");
            }
            stopwatch.Stop();

            Reporter.Measurement(
                "JavaScript evaluation round trips",
                MicrosecondsPerOperation(stopwatch.Elapsed, options.Iterations),
                "us/round-trip",
                options.Iterations,
                "managed call through engine execution and native-to-managed result callback");
        }

        private async ValueTask MeasureLocalAssetsAsync(
            NeoAstra.NeoAstra view,
            MeasuredResourceProvider provider)
        {
            await NavigateAndWaitAsync(view, new Uri($"{FixtureUri}?warmup=1"));
            provider.Reset();

            var loadCount = options.Quick ? 2 : Math.Min(10, options.Iterations);
            var stopwatch = Stopwatch.StartNew();
            for (var index = 0; index < loadCount; index++)
            {
                await NavigateAndWaitAsync(view, new Uri($"{FixtureUri}?iteration={index}"));
            }
            stopwatch.Stop();

            Reporter.Measurement(
                "local asset loading",
                stopwatch.Elapsed.TotalMilliseconds / loadCount,
                "ms/navigation",
                loadCount,
                "warm local custom-scheme document navigation; no network server");

            var snapshot = provider.Snapshot();
            if (snapshot.FileBackedResponses == 0)
            {
                Reporter.Skip(
                    "file-backed custom-scheme responses",
                    "The backend completed local navigation but did not request a file-backed response during the measured interval.");
            }
            else
            {
                Reporter.Measurement(
                    "file-backed custom-scheme responses",
                    snapshot.ProviderMicroseconds / snapshot.FileBackedResponses,
                    "us/provider-call",
                    snapshot.FileBackedResponses,
                    "synchronous provider lookup returning NeoResourceResponse.FromFile; browser I/O continues natively");
            }
        }

        private async ValueTask MeasureMessagingAsync(NeoAstra.NeoAstra view, BridgeMode bridgeMode)
        {
            if (bridgeMode == BridgeMode.Disabled)
            {
                const string reason = "No safely authenticated bridge policy is available on this platform/backend.";
                Reporter.Skip("small-message throughput", reason);
                Reporter.Skip("large-message throughput", reason);
                Reporter.Skip("native-to-managed callback overhead", reason);
                return;
            }

            var readyDeadline = Stopwatch.StartNew();
            while (readyDeadline.Elapsed < options.Timeout)
            {
                var ready = await view.EvaluateScriptAsync("globalThis.__benchmarkTransportConnected === true");
                if (string.Equals(ready, "true", StringComparison.Ordinal)) break;
                await Task.Delay(10);
            }
            if (readyDeadline.Elapsed >= options.Timeout)
            {
                throw new TimeoutException("The @neoastra/client benchmark handshake did not complete.");
            }

            _ = await MeasureMessageBurstAsync(view, 5, 16);

            var smallCount = options.Quick ? Math.Min(100, options.Iterations * 5) : Math.Max(500, options.Iterations * 10);
            var small = await MeasureMessageBurstAsync(view, smallCount, 32);
            Reporter.Measurement(
                "small-message throughput",
                smallCount / small.TotalSeconds,
                "messages/s",
                smallCount,
                "32-character JSON payloads sent by local JavaScript to managed code");
            Reporter.Measurement(
                "native-to-managed callback overhead",
                MicrosecondsPerOperation(small, smallCount),
                "us/callback",
                smallCount,
                "same warmed small-message burst; includes backend delivery and managed event dispatch");

            _ = await MeasureMessageBurstAsync(view, 1, 256 * 1024);
            var largeCount = options.Quick ? 3 : Math.Max(10, options.Iterations / 5);
            var large = await MeasureMessageBurstAsync(view, largeCount, 256 * 1024);
            var payloadMiB = largeCount * 256d / 1024d;
            Reporter.Measurement(
                "large-message throughput",
                payloadMiB / large.TotalSeconds,
                "MiB/s",
                largeCount,
                "256 KiB JSON string payloads sent by local JavaScript to managed code");
        }

        private async ValueTask<TimeSpan> MeasureMessageBurstAsync(
            NeoAstra.NeoAstra view,
            int count,
            int payloadSize)
        {
            var received = 0;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnMessage(object? _, NeoWebMessageReceivedEventArgs message)
            {
                if (!message.Json.Contains("\"kind\":\"benchmark\"", StringComparison.Ordinal)) return;
                if (Interlocked.Increment(ref received) == count) completion.TrySetResult();
            }

            view.MessageReceived += OnMessage;
            try
            {
                var stopwatch = Stopwatch.StartNew();
                _ = await view.EvaluateScriptAsync(
                    $"globalThis.__benchmarkSend('fixed', {count}, {payloadSize})").AsTask().WaitAsync(options.Timeout);
                await completion.Task.WaitAsync(options.Timeout);
                stopwatch.Stop();
                if (received != count)
                {
                    throw new InvalidOperationException($"Expected {count} benchmark messages but received {received}.");
                }
                return stopwatch.Elapsed;
            }
            finally
            {
                view.MessageReceived -= OnMessage;
            }
        }

        private async ValueTask MeasureRepeatedLifecycleAndMemoryAsync(
            NeoEnvironment environment,
            NeoWindow window)
        {
            await ForceCollectionAsync();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var privateBytesBefore = process.PrivateMemorySize64;
            var managedBytesBefore = GC.GetTotalMemory(forceFullCollection: false);
            var stopwatch = Stopwatch.StartNew();

            for (var index = 0; index < options.LifecycleIterations; index++)
            {
                await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window))
                    .AsTask().WaitAsync(options.Timeout);
                await NavigateAndWaitAsync(view, new Uri($"{FixtureUri}?lifecycle={index}"));
            }

            stopwatch.Stop();
            await ForceCollectionAsync();
            process.Refresh();
            var privateDelta = process.PrivateMemorySize64 - privateBytesBefore;
            var managedDelta = GC.GetTotalMemory(forceFullCollection: false) - managedBytesBefore;

            Reporter.Measurement(
                "memory after repeated view creation and destruction",
                privateDelta / (1024d * 1024d),
                "MiB private-delta",
                options.LifecycleIterations,
                $"host process only after forced GC; managed delta={managedDelta / 1024d:F1} KiB; lifecycle elapsed={stopwatch.Elapsed.TotalMilliseconds:F1} ms; browser child memory excluded");
        }

        private async ValueTask MeasureIdleCpuAsync()
        {
            await Task.Delay(250);
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var stopwatch = Stopwatch.StartNew();
            await Task.Delay(options.IdleDuration);
            stopwatch.Stop();
            process.Refresh();
            var cpu = process.TotalProcessorTime - cpuBefore;
            var normalizedPercent = cpu.TotalSeconds / (stopwatch.Elapsed.TotalSeconds * Environment.ProcessorCount) * 100d;

            Reporter.Measurement(
                "idle CPU",
                normalizedPercent,
                "% host CPU",
                1,
                $"{stopwatch.Elapsed.TotalSeconds:F1}s idle sample normalized across {Environment.ProcessorCount} logical processors; browser child CPU excluded");
        }

        private NeoWindow CreateHiddenWindow(string title)
            => application.CreateWindow(new NeoWindowOptions
            {
                Title = title,
                Width = 640,
                Height = 480,
                IsVisible = false,
                ShowInTaskbar = false,
            });

        private async ValueTask NavigateAndWaitAsync(NeoAstra.NeoAstra view, Uri uri)
        {
            var completion = new TaskCompletionSource<NeoNavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnNavigation(object? _, NeoNavigationCompletedEventArgs navigation)
            {
                if (navigation.Uri is null ||
                    !string.Equals(navigation.Uri.Scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(navigation.Uri.Host, uri.Host, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(navigation.Uri.AbsolutePath, uri.AbsolutePath, StringComparison.Ordinal)) return;

                if (navigation.IsSuccess)
                {
                    completion.TrySetResult(navigation);
                }
                else
                {
                    completion.TrySetException(new InvalidOperationException(
                        $"Navigation to '{navigation.Uri}' failed with {navigation.ErrorCode} (native {navigation.NativeErrorCode})."));
                }
            }

            view.NavigationCompleted += OnNavigation;
            try
            {
                await view.NavigateAsync(uri);
                await completion.Task.WaitAsync(options.Timeout);
            }
            finally
            {
                view.NavigationCompleted -= OnNavigation;
            }
        }

        private BenchmarkReporter Reporter
            => _reporter ?? throw new InvalidOperationException("Runtime reporting has not been initialized.");
    }

    private sealed class MeasuredResourceProvider(INeoResourceProvider inner) : INeoResourceProvider
    {
        private long _providerTicks;
        private int _fileBackedResponses;

        public NeoResourceResponse? GetResponse(NeoResourceRequest request)
        {
            var started = Stopwatch.GetTimestamp();
            var response = inner.GetResponse(request);
            Interlocked.Add(ref _providerTicks, Stopwatch.GetTimestamp() - started);
            if (response?.FilePath is not null) Interlocked.Increment(ref _fileBackedResponses);
            return response;
        }

        internal void Reset()
        {
            Interlocked.Exchange(ref _providerTicks, 0);
            Interlocked.Exchange(ref _fileBackedResponses, 0);
        }

        internal ProviderSnapshot Snapshot()
        {
            var ticks = Interlocked.Read(ref _providerTicks);
            var responses = Volatile.Read(ref _fileBackedResponses);
            return new ProviderSnapshot(ticks * 1_000_000d / Stopwatch.Frequency, responses);
        }
    }

    private sealed class BenchmarkReporter(NeoRuntimeInfo runtime)
    {
        private readonly string _backend = Quote(runtime.BackendName);
        private readonly string _platform = Quote($"{runtime.OperatingSystem}/{runtime.Architecture}");

        internal void Measurement(
            string category,
            double value,
            string unit,
            int samples,
            string detail)
        {
            Console.WriteLine(
                $"RESULT category={Quote(category)} backend={_backend} platform={_platform} " +
                $"value={value:F3} unit={Quote(unit)} samples={samples} detail={Quote(detail)}");
        }

        internal void Skip(string category, string reason)
        {
            Console.WriteLine(
                $"SKIP category={Quote(category)} backend={_backend} platform={_platform} reason={Quote(reason)}");
        }

        private static string Quote(string value)
            => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static NeoEnvironmentOptions CreateEnvironmentOptions(INeoResourceProvider provider)
        => new()
        {
            IsPrivate = true,
            CustomSchemes = [NeoCustomScheme.Application("benchmark", provider)],
        };

    private static NeoAstraOptions CreateViewOptions(BridgeMode bridgeMode)
    {
        var options = new NeoAstraOptions
        {
            MaximumMessageSize = 1024 * 1024,
            ViewLabel = $"benchmark-{Interlocked.Increment(ref _nextViewLabel)}",
        };
        if (bridgeMode == BridgeMode.TrustedOrigins)
        {
            options.BridgePolicy = NeoBridgePolicy.TrustedOrigins;
            options.BridgeOrigins = ["benchmark://fixture"];
        }
        else if (bridgeMode == BridgeMode.TrustEntireView)
        {
            options.BridgePolicy = NeoBridgePolicy.TrustEntireView;
        }
        return options;
    }

    private static BridgeMode GetBridgeMode(NeoEnvironment environment)
    {
        if (OperatingSystem.IsLinux()) return BridgeMode.TrustEntireView;
        if ((OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) &&
            IsSupported(environment, NeoCapability.MessageOrigin))
        {
            return BridgeMode.TrustedOrigins;
        }
        return BridgeMode.Disabled;
    }

    private static bool IsSupported(NeoEnvironment environment, NeoCapability capability)
        => environment.GetCapability(capability).SupportLevel != NeoSupportLevel.None;

    private static string CapabilityReason(NeoEnvironment environment, NeoCapability capability)
    {
        var info = environment.GetCapability(capability);
        return info.Details is null
            ? $"{capability} support is {info.SupportLevel}."
            : $"{capability} support is {info.SupportLevel}: {info.Details}";
    }

    private static double MicrosecondsPerOperation(TimeSpan elapsed, int count)
        => elapsed.TotalMicroseconds / count;

    private static async ValueTask ForceCollectionAsync()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(100);
    }

    private readonly record struct ProviderSnapshot(double ProviderMicroseconds, int FileBackedResponses);

    private enum BridgeMode
    {
        Disabled,
        TrustedOrigins,
        TrustEntireView,
    }

    private sealed record BenchmarkOptions(
        bool Quick,
        int Iterations,
        int LifecycleIterations,
        TimeSpan Timeout,
        TimeSpan IdleDuration)
    {
        internal static bool TryParse(string[] args, out BenchmarkOptions options)
        {
            var quick = false;
            var sawRun = false;
            int? iterations = null;
            int? lifecycleIterations = null;
            var timeoutSeconds = 30;
            var idleSeconds = 2;

            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--run" when !sawRun:
                        sawRun = true;
                        break;
                    case "--quick" when !quick:
                        quick = true;
                        break;
                    case "--iterations" when TryReadInt(args, ref index, 1, 10_000, out var parsedIterations):
                        iterations = parsedIterations;
                        break;
                    case "--lifecycle-iterations" when TryReadInt(args, ref index, 1, 100, out var parsedLifecycle):
                        lifecycleIterations = parsedLifecycle;
                        break;
                    case "--timeout-seconds" when TryReadInt(args, ref index, 5, 300, out var parsedTimeout):
                        timeoutSeconds = parsedTimeout;
                        break;
                    case "--idle-seconds" when TryReadInt(args, ref index, 1, 10, out var parsedIdle):
                        idleSeconds = parsedIdle;
                        break;
                    default:
                        options = null!;
                        return false;
                }
            }

            options = new BenchmarkOptions(
                quick,
                iterations ?? (quick ? 20 : 100),
                lifecycleIterations ?? (quick ? 3 : 10),
                TimeSpan.FromSeconds(timeoutSeconds),
                TimeSpan.FromSeconds(idleSeconds));
            return sawRun;
        }

        private static bool TryReadInt(
            string[] args,
            ref int index,
            int minimum,
            int maximum,
            out int value)
        {
            if (index + 1 < args.Length &&
                int.TryParse(args[index + 1], out value) &&
                value >= minimum &&
                value <= maximum)
            {
                index++;
                return true;
            }

            value = 0;
            return false;
        }
    }
}

internal sealed record RpcEchoRequest(int Value);
internal sealed record RpcEchoResponse(int Value);

[JsonSerializable(typeof(RpcEchoRequest))]
[JsonSerializable(typeof(RpcEchoResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class BenchmarkRpcJsonContext : JsonSerializerContext;
