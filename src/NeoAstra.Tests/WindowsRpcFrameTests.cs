// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text;
using System.Text.Json.Serialization;
using NeoAstra.Rpc;

namespace NeoAstra.Tests;

/// <summary>
/// RPC frames between a WebView2 page and the host through the view binding: many answers posted in one turn of the UI
/// thread, large frames both ways, characters beyond ASCII, and events in their order.
/// </summary>
[TestClass]
public sealed class WindowsRpcFrameTests
{
    [TestMethod]
    public async Task FramesReachThePageAndTheHostWhole_InBatches_AndInOrder()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This regression drives a WebView2 document through the RPC view binding.");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                            UserDataRoot = TestUserData.Shared,
                            CustomSchemes = [NeoCustomScheme.Application("app", new FrameResourceProvider())],
                        });
                        await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), new NeoAstraOptions
                        {
                            ViewLabel = "main", BridgePolicy = NeoBridgePolicy.TrustedOrigins, BridgeOrigins = ["app://neoastra"],
                        });
                        var builder = new NeoRpcBuilder(new NeoRpcOptions { MaximumQueuedEventsPerSubscription = 4096 });
                        builder.AddCommand<string, string>("test.echo", static (value, _, _) => ValueTask.FromResult(value),
                            WindowsRpcFrameJsonContext.Default.String, WindowsRpcFrameJsonContext.Default.String);
                        builder.AddCommand<int, string>("test.blob", static (count, _, _) => ValueTask.FromResult(Blob(count)),
                            WindowsRpcFrameJsonContext.Default.Int32, WindowsRpcFrameJsonContext.Default.String);
                        builder.AddCommand<string, string>("test.measure", static (value, _, _) => ValueTask.FromResult($"{value.Length}:{value.Equals(Upload(value.Length / 3), StringComparison.Ordinal)}"),
                            WindowsRpcFrameJsonContext.Default.String, WindowsRpcFrameJsonContext.Default.String);
                        var changed = builder.AddEvent("test.changed", WindowsRpcFrameJsonContext.Default.String);
                        await using var rpc = builder.Build();
                        await using var binding = NeoRpcViewBinding.Bind(rpc, view);
                        await view.NavigateAsync(new Uri("app://neoastra/index.html"));

                        stage = "handshake";
                        await WaitAsync(view, "globalThis.ready === true", timeout.Token);

                        stage = "many calls in one turn of the page";
                        _ = await view.EvaluateScriptAsync("for (let i = 0; i < 300; i++) globalThis.call('test.echo', 'v' + i + '-\\xe9'); true", timeout.Token);
                        await WaitAsync(view, "globalThis.results.size === 300", timeout.Token);
                        Assert.AreEqual("true", await view.EvaluateScriptAsync("[...Array(300).keys()].every(i => globalThis.results.get(String(i + 1)).value === 'v' + i + '-\\xe9')", timeout.Token));

                        stage = "a large result";
                        _ = await view.EvaluateScriptAsync("globalThis.blob = globalThis.call('test.blob', 100000); true", timeout.Token);
                        await WaitAsync(view, "globalThis.results.has(globalThis.blob)", timeout.Token);
                        Assert.AreEqual("true", await view.EvaluateScriptAsync("globalThis.results.get(globalThis.blob).value === globalThis.expected(100000)", timeout.Token));

                        stage = "a large request";
                        _ = await view.EvaluateScriptAsync("globalThis.upload = globalThis.call('test.measure', globalThis.uploaded(200000)); true", timeout.Token);
                        await WaitAsync(view, "globalThis.results.has(globalThis.upload)", timeout.Token);
                        Assert.AreEqual("\"600000:True\"", await view.EvaluateScriptAsync("globalThis.results.get(globalThis.upload).value", timeout.Token));

                        stage = "events";
                        _ = await view.EvaluateScriptAsync("globalThis.subscribe(); true", timeout.Token);
                        await WaitAsync(view, "globalThis.subscribed === true", timeout.Token);
                        for (var index = 0; index < 200; index++) await changed.PublishAsync($"e{index}");
                        await WaitAsync(view, "globalThis.events.length === 200", timeout.Token);
                        Assert.AreEqual("true", await view.EvaluateScriptAsync("globalThis.events.every((value, i) => value === (i + 1) + ':e' + i)", timeout.Token));

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
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(70)); }
        catch (TimeoutException) { Assert.Fail($"RPC frame test hung during {stage}."); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { Assert.Fail($"RPC frame test timed out during {stage}."); }
        catch (NeoAstraNativeLibraryException) { Assert.Inconclusive("The Windows native runtime is not staged."); }
    }

    // One, two, and three UTF-8 bytes, and a surrogate pair: the frames are UTF-8 until WebView2 widens them.
    private static string Blob(int count)
    {
        var builder = new StringBuilder(count * 5);
        for (var index = 0; index < count; index++) builder.Append('a').Append((char)0xE9).Append((char)0x20AC).Append(char.ConvertFromUtf32(0x1F600));
        return builder.ToString();
    }

    private static string Upload(int count)
    {
        var builder = new StringBuilder(count * 3);
        for (var index = 0; index < count; index++) builder.Append((char)0xE9).Append('"').Append('\\');
        return builder.ToString();
    }

    private static async Task WaitAsync(global::NeoAstra.NeoAstra view, string condition, CancellationToken cancellationToken)
    {
        while (true)
        {
            try { if (await view.EvaluateScriptAsync(condition, cancellationToken) == "true") return; }
            catch (Exception exception) when (exception is not OperationCanceledException) { }
            await Task.Delay(20, cancellationToken);
        }
    }

    private sealed class FrameResourceProvider : INeoResourceProvider
    {
        public NeoResourceResponse GetResponse(NeoResourceRequest request) => NeoResourceResponse.FromBytes("""
            <!doctype html><title>RPC frames</title><script>
            const transport = globalThis[Symbol.for('@neoastra/client/transport/v1')];
            let nextId = 0;
            globalThis.results = new Map();
            globalThis.events = [];
            transport.setReceiveHandler(frame => {
                if (frame.kind === 'hello_ack') globalThis.ready = true;
                if (frame.kind === 'result') globalThis.results.set(frame.id, frame);
                if (frame.kind === 'subscribed') globalThis.subscribed = !frame.error;
                if (frame.kind === 'event') globalThis.events.push(frame.sequence + ':' + frame.value);
            });
            globalThis.call = (command, args) => { const id = String(++nextId); transport.send({neoastra: 1, kind: 'invoke', id, command, args}); return id; };
            globalThis.subscribe = () => transport.send({neoastra: 1, kind: 'subscribe', id: 'events', event: 'test.changed'});
            globalThis.expected = count => ('a\xe9\u20ac' + String.fromCodePoint(0x1F600)).repeat(count);
            globalThis.uploaded = count => '\xe9"\\'.repeat(count);
            transport.send({neoastra: 1, kind: 'hello', protocol: {major: 1, minor: 0}, features: ['invoke', 'events'], client: {name: 'frame-test', version: '1.0'}});
            </script>
            """u8.ToArray(), "text/html; charset=utf-8");
    }
}

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
internal sealed partial class WindowsRpcFrameJsonContext : JsonSerializerContext;
