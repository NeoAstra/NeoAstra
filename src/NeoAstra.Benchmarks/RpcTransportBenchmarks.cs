using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using NeoAstra;
using NeoAstra.Rpc;

/// <summary>
/// RPC frames through the transport of a view, without a browser: the transport coordinator of a view authenticates the
/// frames of the page and writes the envelope of the frames of the host, on a simulated UI thread, as a view binding
/// does. Only the native call that hands a message to the browser, or that gives one, is left out.
/// </summary>
internal sealed class RpcTransportBenchmarks(int iterations, TimeSpan timeout, RpcBenchmarks.Report report)
{
    private const string BlobCommand = "benchmark.blob";
    private const string UploadCommand = "benchmark.upload";
    private const string EchoCommand = "benchmark.echo";
    private const int LargeBytes = 1024 * 1024;

    internal async ValueTask RunAsync()
    {
        await MeasureLargeResultAsync();
        MeasureInboundOnUiThread("small", Invoke("ui-small", EchoCommand, "{\"value\":1}"));
        MeasureInboundOnUiThread("1 MB", Invoke("ui-large", UploadCommand, $"{{\"data\":\"{new string('x', LargeBytes)}\"}}"));
        await MeasureLargeRequestAsync();
    }

    // A command that answers 1 MB: the time and allocation of the whole call, and the time its frame holds the UI thread.
    private async ValueTask MeasureLargeResultAsync()
    {
        var count = Math.Clamp(iterations, 10, 200);
        await using var host = CreateHost();
        using var ui = new UiThread();
        var view = new ViewTransport();
        await using var session = view.OpenSession(host, ui);
        var request = Invoke("large", BlobCommand, $"{{\"value\":{LargeBytes}}}");

        for (var index = 0; index < 3; index++) await session.ReceiveAsync(request.Replace("\"large\"", $"\"warmup-{index}\"", StringComparison.Ordinal));
        ui.Reset();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        for (var index = 0; index < count; index++) await session.ReceiveAsync(request.Replace("\"large\"", $"\"large-{index}\"", StringComparison.Ordinal)).AsTask().WaitAsync(timeout);
        stopwatch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        if (view.Posted != count + 3) throw new InvalidOperationException("The large-result benchmark lost a result.");
        report("rpc 1 MB result round-trip", stopwatch.Elapsed.TotalMilliseconds / count, "ms/op", count, "serialization, frame, envelope and post of a 1 MB result through the transport of a view");
        report("rpc 1 MB result allocation", allocated / (double)count / LargeBytes, "MB/op", count, "managed allocation of all threads per call, in MiB");
        report("rpc 1 MB result UI thread", ui.BusyMicroseconds / count, "us/op", count, "time the posting of its frame holds the UI thread (envelope, size check, native string), without the native post");
    }

    // A frame of the page as the native library gives it, authenticated on the UI thread until it is handed to the binding.
    private void MeasureInboundOnUiThread(string name, string frame)
    {
        var view = new ViewTransport();
        var envelope = Encoding.UTF8.GetBytes(view.Envelope(frame));
        var count = Math.Clamp(iterations * (envelope.Length > 4096 ? 2 : 1000), 20, 100_000);
        for (var index = 0; index < 10; index++) view.ReceiveOnUiThread(envelope);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        for (var index = 0; index < count; index++) view.ReceiveOnUiThread(envelope);
        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        report($"rpc {name} inbound frame UI thread", stopwatch.Elapsed.TotalMicroseconds / count, "us/frame", count, $"decode and authentication of a {envelope.Length}-byte envelope until it is handed to the binding");
        report($"rpc {name} inbound frame UI allocation", allocated / (double)count, "B/frame", count, "managed allocation of the UI thread per frame");
    }

    // A command that receives 1 MB: what the frame costs from the native buffer to the handler, on every thread.
    private async ValueTask MeasureLargeRequestAsync()
    {
        var count = Math.Clamp(iterations, 10, 200);
        await using var host = CreateHost();
        using var ui = new UiThread();
        var view = new ViewTransport();
        await using var session = view.OpenSession(host, ui);
        var data = new string('x', LargeBytes);
        byte[] Frame(string id) => Encoding.UTF8.GetBytes(view.Envelope(Invoke(id, UploadCommand, $"{{\"data\":\"{data}\"}}")));
        var warmup = Enumerable.Range(0, 3).Select(index => Frame($"warmup-{index}")).ToArray();
        var frames = Enumerable.Range(0, count).Select(index => Frame($"upload-{index}")).ToArray();

        foreach (var frame in warmup) await view.ReceiveAsync(frame, session);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        foreach (var frame in frames) await view.ReceiveAsync(frame, session).AsTask().WaitAsync(timeout);
        stopwatch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        if (view.Posted != count + 3) throw new InvalidOperationException("The large-request benchmark lost a result.");
        report("rpc 1 MB request round-trip", stopwatch.Elapsed.TotalMilliseconds / count, "ms/op", count, "authentication, parse and deserialization of a 1 MB request, and its small answer");
        report("rpc 1 MB request allocation", allocated / (double)count / LargeBytes, "MB/op", count, "managed allocation of all threads per call, in MiB (the 1 MB string the handler receives included)");
    }

    private static NeoRpcHost CreateHost()
    {
        var builder = new NeoRpcBuilder(new NeoRpcOptions { AuthorizationService = BenchmarkAuthorization.Instance });
        builder.AddCommand<RpcEchoRequest, RpcEchoResponse>(
            EchoCommand,
            static (request, _, _) => ValueTask.FromResult(new RpcEchoResponse(request.Value)),
            BenchmarkTransportJsonContext.Default.RpcEchoRequest,
            BenchmarkTransportJsonContext.Default.RpcEchoResponse);
        builder.AddCommand<RpcEchoRequest, RpcBlob>(
            BlobCommand,
            static (request, _, _) => ValueTask.FromResult(new RpcBlob(new string('x', request.Value))),
            BenchmarkTransportJsonContext.Default.RpcEchoRequest,
            BenchmarkTransportJsonContext.Default.RpcBlob);
        builder.AddCommand<RpcBlob, RpcEchoResponse>(
            UploadCommand,
            static (request, _, _) => ValueTask.FromResult(new RpcEchoResponse(request.Data.Length)),
            BenchmarkTransportJsonContext.Default.RpcBlob,
            BenchmarkTransportJsonContext.Default.RpcEchoResponse);
        return builder.Build();
    }

    private static string Invoke(string id, string command, string args)
        => $"{{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"{id}\",\"command\":\"{command}\",\"args\":{args}}}";

    // The transport of one view and the managed part of its native posts: what NeoAstra and NeoRpcViewBinding do with a
    // frame, from the native buffer of the page to the session, and from the session to the native call of the host.
    private sealed class ViewTransport
    {
        private readonly NeoTransportCoordinator _coordinator;
        private int _posted;

        internal ViewTransport()
        {
            var options = new NeoAstraOptions { ViewLabel = "benchmark", BridgePolicy = NeoBridgePolicy.TrustEntireView };
            _coordinator = new NeoTransportCoordinator(options, new NeoRuntimeInfo("benchmark", "1", "1", "benchmark", "benchmark", 1UL << 2, false), _ => { }, _ => { }, idFactory: () => "benchmark-session");
            _coordinator.NavigationStarted();
            _coordinator.NavigationCommitted();
            ReceiveOnUiThread(Encoding.UTF8.GetBytes(Envelope("{\"neoastra\":1,\"kind\":\"hello\",\"protocol\":{\"major\":1,\"minor\":0},\"features\":[\"invoke\",\"cancel\",\"events\"],\"client\":{\"name\":\"benchmark\",\"version\":\"1\"}}")));
            _coordinator.NavigationCompleted(succeeded: true);
            if (!_coordinator.IsConnected) throw new InvalidOperationException("The benchmark transport did not connect.");
            Limits = (options.MaximumMessageSize, options.Transport.MaximumJsonDepth);
        }

        internal int Posted => Volatile.Read(ref _posted);

        private (uint MaximumMessageSize, int MaximumJsonDepth) Limits { get; }

        internal string Envelope(string frame)
            => $"{{\"__neoastraTransport\":1,\"hostViewBinding\":\"{_coordinator.HostViewBinding}\",\"rendererDocumentId\":\"benchmark-document\",\"frame\":{frame}}}";

        internal NeoRpcSession OpenSession(NeoRpcHost host, UiThread ui)
            => host.OpenSessionCore(
                new NeoRpcSessionIdentity("benchmark", "benchmark-session"),
                static (_, _) => throw new InvalidOperationException("A view session sends in batches."),
                async frames => NeoRpcViewBinding.ToRefusals(await ui.InvokeAsync(() => global::NeoAstra.NeoAstra.PostTrustedFrames(_coordinator, "benchmark-session", frames, Post)), frames.Count),
                null,
                null,
                NeoRpcOutboundLimits.Of(Limits.MaximumMessageSize, Limits.MaximumJsonDepth));

        // NeoAstra.PostNativeMessage without the native call.
        private void Post(string envelope)
        {
            using var native = new global::NeoAstra.Interop.Utf8String(envelope);
            Interlocked.Increment(ref _posted);
        }

        // NeoAstra.DispatchWebMessage, from the UTF-8 the native library gives: the frame it hands to the binding.
        internal string? ReceiveOnUiThread(byte[] nativeText)
        {
            var json = Encoding.UTF8.GetString(nativeText);
            var result = _coordinator.Receive(json);
            return result.Kind == NeoTransportReceiveKind.Application ? result.ApplicationJson : null;
        }

        internal ValueTask ReceiveAsync(byte[] nativeText, NeoRpcSession session)
            => session.ReceiveAsync(ReceiveOnUiThread(nativeText) ?? throw new InvalidOperationException("The benchmark frame was not delivered."));
    }

    // One thread that runs what is posted to it in order and measures how long it is busy, as the UI thread of a view.
    internal sealed class UiThread : IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(Action Callback, TaskCompletionSource Completion)> _queue = [];
        private readonly Thread _thread;
        private long _busy;

        internal UiThread()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "NeoAstra benchmark UI thread" };
            _thread.Start();
        }

        internal double BusyMicroseconds => Interlocked.Read(ref _busy) * 1_000_000d / Stopwatch.Frequency;

        internal void Reset() => Interlocked.Exchange(ref _busy, 0);

        internal async Task<T> InvokeAsync<T>(Func<T> callback)
        {
            T result = default!;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add((() => result = callback(), completion));
            await completion.Task.ConfigureAwait(false);
            return result;
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
                var start = Stopwatch.GetTimestamp();
                try { callback(); completion.TrySetResult(); }
                catch (Exception exception) { completion.TrySetException(exception); }
                Interlocked.Add(ref _busy, Stopwatch.GetTimestamp() - start);
            }
        }
    }
}

internal sealed record RpcBlob(string Data);

[JsonSerializable(typeof(RpcEchoRequest))]
[JsonSerializable(typeof(RpcEchoResponse))]
[JsonSerializable(typeof(RpcBlob))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class BenchmarkTransportJsonContext : JsonSerializerContext;
