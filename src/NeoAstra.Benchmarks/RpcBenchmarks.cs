using System.Collections.Concurrent;
using System.Diagnostics;
using NeoAstra.Rpc;

/// <summary>
/// RPC host measurements over in-memory sessions: no browser is involved. The throughput measurements send through a
/// simulated UI thread, as a view binding does: every batch of frames costs one hop to that thread and back, and a turn
/// of that thread costs about 20 microseconds.
/// </summary>
internal sealed class RpcBenchmarks(int iterations, TimeSpan timeout, RpcBenchmarks.Report report)
{
    internal delegate void Report(string category, double value, string unit, int samples, string detail);

    private const string EchoCommand = "benchmark.echo";
    private const string StreamCommand = "benchmark.stream";
    private const string ChangedEvent = "benchmark.changed";

    internal async ValueTask RunAsync()
    {
        await MeasureSmallInvokeAsync();
        await MeasureConcurrentInvokesAsync();
        await MeasureEventThroughputAsync();
        await MeasureChannelThroughputAsync();
    }

    private async ValueTask MeasureSmallInvokeAsync()
    {
        var count = Math.Max(200, iterations * 10);
        await using var host = CreateHost(out _);
        var completed = 0;
        await using var session = host.OpenSession(
            new NeoRpcSessionIdentity("benchmark", "benchmark-document"),
            (_, _) => { Interlocked.Increment(ref completed); return ValueTask.CompletedTask; });

        for (var index = 0; index < 10; index++) await session.ReceiveAsync(Invoke($"warmup-{index}", index));
        // Handlers and the writer of the session run on other threads than this one: count what every thread allocated.
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        for (var index = 0; index < count; index++) await session.ReceiveAsync(Invoke($"rpc-{index}", index));
        stopwatch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        if (Volatile.Read(ref completed) != count + 10) throw new InvalidOperationException("The RPC benchmark lost a terminal result.");
        report("rpc small invoke round-trip", stopwatch.Elapsed.TotalMicroseconds / count, "us/op", count, "JSON parse + source-generated metadata dispatch + serialization + in-memory send");
        report("rpc small invoke allocation", allocated / (double)count, "B/op", count, "managed allocation of all threads; excludes browser and native transport");
    }

    private async ValueTask MeasureConcurrentInvokesAsync()
    {
        const int callers = 32;
        var count = Math.Max(callers * 10, iterations * 100);
        await using var host = CreateHost(out _);
        using var ui = new SimulatedUiThread();
        var page = new SimulatedPage();
        await using var session = OpenBatchSession(host, ui, page);

        await RunCallersAsync(callers, callers * 4, "warmup");
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        await RunCallersAsync(callers, count, "call");
        stopwatch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        if (page.Results != count + callers * 4) throw new InvalidOperationException("The concurrent RPC benchmark lost a terminal result.");
        report("rpc concurrent invoke throughput", count / stopwatch.Elapsed.TotalSeconds, "calls/s", count, $"{callers} callers in one session; answers sent in batches through a simulated UI thread");
        report("rpc concurrent invoke allocation", allocated / (double)count, "B/op", count, "managed allocation of all threads per call");

        async Task RunCallersAsync(int workers, int total, string prefix)
        {
            var next = -1;
            await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
            {
                int index;
                while ((index = Interlocked.Increment(ref next)) < total) await session.ReceiveAsync(Invoke($"{prefix}-{index}", index));
            }))).WaitAsync(timeout);
        }
    }

    private async ValueTask MeasureEventThroughputAsync()
    {
        var count = Math.Clamp(iterations * 500, 1000, 65_536);
        await using var host = CreateHost(out var changed, queuedEvents: 65_536);
        using var ui = new SimulatedUiThread();
        var page = new SimulatedPage();
        await using var session = OpenBatchSession(host, ui, page);
        await session.ReceiveAsync($"{{\"neoastra\":1,\"kind\":\"subscribe\",\"id\":\"events\",\"event\":\"{ChangedEvent}\"}}");

        await PublishAsync(100);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        await PublishAsync(count);
        stopwatch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        report("rpc event throughput", count / stopwatch.Elapsed.TotalSeconds, "events/s", count, "one subscription; events sent through a simulated UI thread");
        report("rpc event allocation", allocated / (double)count, "B/event", count, "managed allocation of all threads per published and delivered event");

        async Task PublishAsync(int events)
        {
            var target = page.Events + events;
            var delivered = page.WaitForEventsAsync(target);
            for (var index = 0; index < events; index++)
                if (await changed.PublishAsync(new RpcEchoResponse(index)) != 1) throw new InvalidOperationException("The subscription refused an event.");
            await delivered.WaitAsync(timeout);
        }
    }

    private async ValueTask MeasureChannelThroughputAsync()
    {
        var count = Math.Clamp(iterations * 500, 1000, 1_000_000);
        await using var host = CreateHost(out _);
        using var ui = new SimulatedUiThread();
        var page = new SimulatedPage();
        await using var session = OpenBatchSession(host, ui, page);

        await StreamAsync("warmup", 100);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        await StreamAsync("stream", count);
        stopwatch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        report("rpc channel throughput", count / stopwatch.Elapsed.TotalSeconds, "items/s", count, "one channel acknowledged item by item by the page; sent through a simulated UI thread");
        report("rpc channel allocation", allocated / (double)count, "B/item", count, "managed allocation of all threads per streamed and acknowledged item");

        async Task StreamAsync(string id, int items)
        {
            var completed = page.WaitForChannelCompleteAsync();
            await session.ReceiveAsync($"{{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"{id}\",\"command\":\"{StreamCommand}\",\"args\":{{\"value\":{items}}}}}");
            await completed.WaitAsync(timeout);
        }
    }

    private static NeoRpcHost CreateHost(out NeoRpcEvent<RpcEchoResponse> changed, int queuedEvents = 64)
    {
        var builder = new NeoRpcBuilder(new NeoRpcOptions
        {
            // A benchmark measures the host, not its admission policy: no rate denial or abuse closure may happen.
            RequestRatePerSecond = 100_000,
            RequestRateBurst = 1_000_000,
            AbuseClosureThreshold = 10_000,
            MaximumQueuedEventsPerSubscription = queuedEvents,
            AuthorizationService = BenchmarkAuthorization.Instance,
        });
        builder.AddCommand<RpcEchoRequest, RpcEchoResponse>(
            EchoCommand,
            static (request, _, _) => ValueTask.FromResult(new RpcEchoResponse(request.Value)),
            BenchmarkRpcJsonContext.Default.RpcEchoRequest,
            BenchmarkRpcJsonContext.Default.RpcEchoResponse,
            new NeoRpcCommandOptions { Permission = "benchmark:echo", MaximumConcurrency = 4096 });
        builder.AddChannelCommand<RpcEchoRequest, RpcEchoResponse>(
            StreamCommand,
            static (request, _, _) => ValueTask.FromResult(new NeoRpcChannel<RpcEchoResponse>(Items(request.Value), BenchmarkRpcJsonContext.Default.RpcEchoResponse)),
            BenchmarkRpcJsonContext.Default.RpcEchoRequest,
            new NeoRpcCommandOptions { Permission = "benchmark:echo", MaximumConcurrency = 4096 });
        changed = builder.AddEvent(ChangedEvent, BenchmarkRpcJsonContext.Default.RpcEchoResponse, new NeoRpcEventOptions { Permission = "benchmark:echo" });
        return builder.Build();

        static async IAsyncEnumerable<RpcEchoResponse> Items(int count)
        {
            for (var index = 0; index < count; index++) yield return new RpcEchoResponse(index);
            await Task.CompletedTask;
        }
    }

    private static NeoRpcSession OpenBatchSession(NeoRpcHost host, SimulatedUiThread ui, SimulatedPage page)
    {
        NeoRpcSession? session = null;
        session = host.OpenSessionCore(
            new NeoRpcSessionIdentity("benchmark", $"benchmark-{Guid.NewGuid():N}"),
            static (_, _) => throw new InvalidOperationException("A view session sends in batches."),
            frames => new ValueTask(ui.InvokeAsync(() => page.Receive(frames, session!))),
            null,
            null);
        return session;
    }

    private static string Invoke(string id, int value)
        => $"{{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"{id}\",\"command\":\"{EchoCommand}\",\"args\":{{\"value\":{value}}}}}";

    // What the page does with the frames it is posted: it counts them, and it acknowledges each channel item as the
    // client does, through the transport and the thread-pool reader of a binding.
    private sealed class SimulatedPage
    {
        private int _results;
        private int _events;
        private TaskCompletionSource? _eventsReached;
        private int _eventsTarget;
        private TaskCompletionSource? _channelComplete;

        internal int Results => Volatile.Read(ref _results);
        internal int Events => Volatile.Read(ref _events);

        internal Task WaitForEventsAsync(int target)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _eventsTarget, target);
            Volatile.Write(ref _eventsReached, completion);
            if (Events >= target) completion.TrySetResult();
            return completion.Task;
        }

        internal Task WaitForChannelCompleteAsync()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _channelComplete, completion);
            return completion.Task;
        }

        internal void Receive(IReadOnlyList<string> frames, NeoRpcSession session)
        {
            foreach (var frame in frames)
            {
                if (frame.Contains("\"kind\":\"result\"", StringComparison.Ordinal)) Interlocked.Increment(ref _results);
                else if (frame.Contains("\"kind\":\"event\"", StringComparison.Ordinal))
                {
                    if (Interlocked.Increment(ref _events) >= Volatile.Read(ref _eventsTarget)) Volatile.Read(ref _eventsReached)?.TrySetResult();
                }
                else if (frame.Contains("\"kind\":\"channel_item\"", StringComparison.Ordinal))
                {
                    var ack = $"{{\"neoastra\":1,\"kind\":\"channel_ack\",\"channel\":\"{Read(frame, "\"channel\":\"", '"')}\",\"sequence\":{Read(frame, "\"sequence\":", ',')}}}";
                    ThreadPool.QueueUserWorkItem(static state => _ = state.Session.ReceiveAsync(state.Ack), (Session: session, Ack: ack), preferLocal: false);
                }
                else if (frame.Contains("\"kind\":\"channel_complete\"", StringComparison.Ordinal)) Volatile.Read(ref _channelComplete)?.TrySetResult();
                else if (frame.Contains("\"kind\":\"channel_error\"", StringComparison.Ordinal)) Volatile.Read(ref _channelComplete)?.TrySetException(new InvalidOperationException("The benchmark channel failed."));
            }
        }

        private static string Read(string frame, string prefix, char end)
        {
            var start = frame.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
            return frame[start..frame.IndexOf(end, start)];
        }
    }

    // One thread that runs what is posted to it in order, as the UI thread of an application does; a caller awaits a
    // completion that continues on the thread pool, as with NeoDispatcher.InvokeAsync. Each turn costs what a turn of a
    // native message loop and a native post cost, about 20 microseconds, busy: the UI thread does nothing else meanwhile.
    private sealed class SimulatedUiThread : IDisposable
    {
        internal static readonly long TurnCost = Stopwatch.Frequency * 20 / 1_000_000;

        private readonly BlockingCollection<(Action Callback, TaskCompletionSource Completion)> _queue = [];
        private readonly Thread _thread;

        internal SimulatedUiThread()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "NeoAstra benchmark UI thread" };
            _thread.Start();
        }

        internal Task InvokeAsync(Action callback)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add((callback, completion));
            return completion.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join();
            _queue.Dispose();
        }

        private void Run()
        {
            foreach (var (callback, completion) in _queue.GetConsumingEnumerable())
            {
                var end = Stopwatch.GetTimestamp() + TurnCost;
                while (Stopwatch.GetTimestamp() < end) Thread.SpinWait(16);
                try { callback(); completion.TrySetResult(); }
                catch (Exception exception) { completion.TrySetException(exception); }
            }
        }
    }
}
