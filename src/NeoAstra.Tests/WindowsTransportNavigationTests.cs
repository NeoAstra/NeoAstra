using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using NeoAstra.Rpc;

namespace NeoAstra.Tests;

/// <summary>How the RPC session of a WebView2 document lives through navigations that do or do not replace it.</summary>
[TestClass]
public sealed class WindowsTransportNavigationTests
{
    [TestMethod]
    public async Task NavigationAnsweredWithoutContentKeepsTheSessionAndAReloadReplacesIt()
    {
        await RunAsync(async (application, view, rpc, timeout) =>
        {
            var completions = new ConcurrentQueue<bool>();
            view.NavigationCompleted += (_, args) => completions.Enqueue(args.IsSuccess);
            await view.NavigateAsync(new Uri("app://neoastra/index.html"));
            await WaitAsync(view, "globalThis.ready === true", timeout);
            var firstSession = await view.EvaluateScriptAsync("globalThis.session", timeout);
            await WaitUntilAsync(() => !completions.IsEmpty, timeout);
            completions.Clear();

            // A 204 answer starts a navigation that never replaces the document.
            _ = await view.EvaluateScriptAsync("location.href = 'app://neoastra/no-content'; true", timeout);
            await WaitUntilAsync(() => !completions.IsEmpty, timeout);
            _ = await view.EvaluateScriptAsync("globalThis.ping(); true", timeout);
            await WaitAsync(view, "globalThis.results.length === 1", timeout);
            Assert.AreEqual("true", await view.EvaluateScriptAsync("globalThis.results[0].ok", timeout), "The document that stayed is still answered.");
            Assert.AreEqual(firstSession, await view.EvaluateScriptAsync("globalThis.session", timeout));
            Assert.AreEqual("0", await view.EvaluateScriptAsync("globalThis.closes.length", timeout), "The document that stayed was not told to close.");
            Assert.AreEqual(1, rpc.ActiveSessionCount);

            // A reload replaces the document, and the new one gets a session of its own.
            _ = await view.EvaluateScriptAsync("location.reload(); true", timeout);
            await WaitAsync(view, $"globalThis.ready === true && globalThis.session !== {firstSession}", timeout);
            _ = await view.EvaluateScriptAsync("globalThis.ping(); true", timeout);
            await WaitAsync(view, "globalThis.results.length === 1 && globalThis.results[0].ok === true", timeout);
            await WaitUntilAsync(() => rpc.ActiveSessionCount == 1, timeout);
        });
    }

    private static async Task RunAsync(Func<NeoApplication, global::NeoAstra.NeoAstra, NeoRpcHost, CancellationToken, Task> body)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This regression drives a WebView2 document through the RPC view binding.");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                        await using var environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions
                        {
                            UserDataRoot = TestUserData.Shared,
                            CustomSchemes = [NeoCustomScheme.Application("app", new NavigationResourceProvider())],
                        });
                        await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), new NeoAstraOptions
                        {
                            ViewLabel = "main", BridgePolicy = NeoBridgePolicy.TrustedOrigins, BridgeOrigins = ["app://neoastra"],
                        });
                        var builder = new NeoRpcBuilder(new NeoRpcOptions());
                        builder.AddCommand<bool, bool>("test.ping", (value, _, _) => ValueTask.FromResult(value),
                            WindowsTransportNavigationJsonContext.Default.Boolean, WindowsTransportNavigationJsonContext.Default.Boolean);
                        await using var rpc = builder.Build();
                        await using var binding = NeoRpcViewBinding.Bind(rpc, view);
                        await body(application, view, rpc, timeout.Token);
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
        catch (TimeoutException) { Assert.Fail("The transport navigation test hung."); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { Assert.Fail("The transport navigation test timed out."); }
        catch (NeoAstraNativeLibraryException) { Assert.Inconclusive("The Windows native runtime is not staged."); }
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

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition()) await Task.Delay(20, cancellationToken);
    }

    private sealed class NavigationResourceProvider : INeoResourceProvider
    {
        public NeoResourceResponse GetResponse(NeoResourceRequest request) => request.Uri.AbsolutePath switch
        {
            "/no-content" => NeoResourceResponse.Empty(204, "No Content"),
            _ => NeoResourceResponse.FromBytes("""
                <!doctype html><title>RPC navigation regression</title><script>
                const transport = globalThis[Symbol.for('@neoastra/client/transport/v1')];
                let nextId = 0;
                globalThis.results = [];
                globalThis.closes = [];
                transport.setReceiveHandler(frame => {
                    if (frame.kind === 'hello_ack') { globalThis.session = frame.runtime.documentSessionId; globalThis.ready = true; }
                    if (frame.kind === 'result') globalThis.results.push(frame);
                    if (frame.kind === 'close') globalThis.closes.push(frame.reason ?? frame.code);
                });
                globalThis.ping = () => transport.send({neoastra: 1, kind: 'invoke', id: String(++nextId), command: 'test.ping', args: true});
                transport.send({neoastra:1,kind:'hello',protocol:{major:1,minor:0},features:['invoke'],client:{name:'navigation-test',version:'1.0'}});
                </script>
                """u8.ToArray(), "text/html; charset=utf-8"),
        };
    }
}

[JsonSerializable(typeof(bool))]
internal sealed partial class WindowsTransportNavigationJsonContext : JsonSerializerContext;
