using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using NeoAstra.Rpc;

namespace NeoAstra.Tests;

[TestClass]
public sealed class WindowsRpcSessionRecoveryTests
{
    [TestMethod]
    public async Task HostClosedRpcSessionIsReportedToItsDocumentAndAReloadReconnects()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This regression drives a WebView2 document through the RPC view binding.");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = new ConcurrentQueue<NeoRpcDiagnostic>();
        var stage = "startup";
        var thread = new Thread(() =>
        {
            try
            {
                NeoApplication.Run(new NeoApplicationOptions { ShutdownMode = NeoApplicationShutdownMode.Explicit }, async application =>
                {
                    try
                    {
                        var window = application.CreateWindow(new NeoWindowOptions { Label = "main", Width = 640, Height = 480 });
                        application.MainWindow = window;
                        window.Show();
                        stage = "browser creation";
                        await using var environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions
                        {
                            CustomSchemes = [NeoCustomScheme.Application("app", new RecoveryResourceProvider())],
                        });
                        await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), new NeoAstraOptions
                        {
                            ViewLabel = "main", BridgePolicy = NeoBridgePolicy.TrustedOrigins, BridgeOrigins = ["app://neoastra"],
                        });
                        // One request per second and one tolerated denial: a second back-to-back call closes the
                        // session on the host while its document keeps running.
                        var builder = new NeoRpcBuilder(new NeoRpcOptions
                        {
                            RequestRatePerSecond = 1,
                            RequestRateBurst = 1,
                            AbuseClosureThreshold = 1,
                            DiagnosticSink = new Sink(diagnostics),
                        });
                        builder.AddCommand<bool, bool>("test.ping", (value, _, _) => ValueTask.FromResult(value),
                            WindowsRpcSessionRecoveryJsonContext.Default.Boolean, WindowsRpcSessionRecoveryJsonContext.Default.Boolean);
                        await using var rpc = builder.Build();
                        await using var binding = NeoRpcViewBinding.Bind(rpc, view);
                        await view.NavigateAsync(new Uri("app://neoastra/index.html"));

                        stage = "first handshake";
                        await WaitAsync(view, "globalThis.ready === true", timeout.Token);
                        var firstSession = await view.EvaluateScriptAsync("globalThis.session", timeout.Token);

                        stage = "host closes the session";
                        _ = await view.EvaluateScriptAsync("globalThis.ping(); globalThis.ping(); true", timeout.Token);
                        await WaitAsync(view, "globalThis.closes.length === 1", timeout.Token);
                        Assert.AreEqual("\"rpc_session_closed\"", await view.EvaluateScriptAsync("globalThis.closes[0]", timeout.Token));
                        Assert.AreEqual("[true,false]", await view.EvaluateScriptAsync("globalThis.results.map(frame => frame.ok)", timeout.Token));

                        stage = "revoked document stays disconnected";
                        _ = await view.EvaluateScriptAsync("globalThis.ready = false; globalThis.hello(); globalThis.ping(); true", timeout.Token);
                        await Task.Delay(300, timeout.Token);
                        Assert.AreEqual("false", await view.EvaluateScriptAsync("globalThis.ready", timeout.Token), "A revoked document must not handshake again.");
                        Assert.AreEqual("2", await view.EvaluateScriptAsync("globalThis.results.length", timeout.Token), "A revoked document must not be answered.");
                        Assert.AreEqual(0, rpc.ActiveSessionCount);

                        stage = "reload reconnects";
                        _ = await view.EvaluateScriptAsync("location.reload(); true", timeout.Token);
                        await WaitAsync(view, $"globalThis.ready === true && globalThis.session !== {firstSession} && globalThis.results.length === 0", timeout.Token);
                        _ = await view.EvaluateScriptAsync("globalThis.ping(); true", timeout.Token);
                        await WaitAsync(view, "globalThis.results.length === 1", timeout.Token);
                        Assert.AreEqual("true", await view.EvaluateScriptAsync("globalThis.results[0].ok", timeout.Token));
                        Assert.AreEqual(1, rpc.ActiveSessionCount);

                        stage = "window close";
                        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        window.Closed += (_, _) => closed.TrySetResult();
                        window.Close();
                        await closed.Task.WaitAsync(timeout.Token);
                    }
                    finally
                    {
                        application.ForceShutdown();
                    }
                });
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(40)); }
        catch (TimeoutException) { Assert.Fail($"RPC session recovery test hung during {stage}."); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { Assert.Fail($"RPC session recovery test timed out during {stage}."); }
        catch (NeoAstraNativeLibraryException) { Assert.Inconclusive("The Windows native runtime is not staged."); }

        var revocations = diagnostics.Where(value => value.Code == NeoRpcErrorCodes.ConnectionClosed).ToArray();
        Assert.HasCount(1, revocations, "The binding reports the revocation once and drops no frame silently.");
        Assert.AreEqual(NeoRpcDiagnosticLevel.Warning, revocations[0].Level);
        StringAssert.Contains(revocations[0].Message, "closed the transport connection");
    }

    private static async Task WaitAsync(global::NeoAstra.NeoAstra view, string condition, CancellationToken cancellationToken)
    {
        while (true)
        {
            // A reloading document has no script context to evaluate in for a moment.
            try { if (await view.EvaluateScriptAsync(condition, cancellationToken) == "true") return; }
            catch (Exception exception) when (exception is not OperationCanceledException) { }
            await Task.Delay(20, cancellationToken);
        }
    }

    private sealed class Sink(ConcurrentQueue<NeoRpcDiagnostic> values) : INeoRpcDiagnosticSink
    {
        public void Write(NeoRpcDiagnostic diagnostic) => values.Enqueue(diagnostic);
    }

    private sealed class RecoveryResourceProvider : INeoResourceProvider
    {
        public NeoResourceResponse GetResponse(NeoResourceRequest request) => NeoResourceResponse.FromBytes("""
            <!doctype html><title>RPC session recovery regression</title><script>
            const transport = globalThis[Symbol.for('@neoastra/client/transport/v1')];
            let nextId = 0;
            globalThis.results = [];
            globalThis.closes = [];
            transport.setReceiveHandler(frame => {
                if (frame.kind === 'hello_ack') { globalThis.session = frame.runtime.documentSessionId; globalThis.ready = true; }
                if (frame.kind === 'result') globalThis.results.push(frame);
                if (frame.kind === 'close') globalThis.closes.push(frame.reason ?? frame.code);
            });
            globalThis.hello = () => transport.send({neoastra:1,kind:'hello',protocol:{major:1,minor:0},features:['invoke'],client:{name:'recovery-test',version:'1.0'}});
            globalThis.ping = () => transport.send({neoastra: 1, kind: 'invoke', id: String(++nextId), command: 'test.ping', args: true});
            globalThis.hello();
            </script>
            """u8.ToArray(), "text/html; charset=utf-8");
    }
}

[JsonSerializable(typeof(bool))]
internal sealed partial class WindowsRpcSessionRecoveryJsonContext : JsonSerializerContext;
