// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using NeoAstra.Desktop;
using NeoAstra.Mcp;

namespace NeoAstra.Tests;

// The MCP plugin connects an application to the relay of a development session. Its messages, how it finds the relay, and how it
// serves calls are checked against a relay of the test over a real WebSocket, with a host of fake tools instead of the views.
[TestClass]
public sealed class McpPluginTests
{
    private const string Token = "session-token-0123456789";
    private static readonly NeoMcpApplicationInfo Application = new("Sample", "ios", "iPhone 17e", 4242);

    [TestMethod]
    public async Task Metadata_MatchesTheEmbeddedManifestAndDeclaresNoRendererCommand()
    {
        await using var plugin = new NeoMcpPlugin(new NeoMcpOptions());
        Assert.AreEqual(NeoMcpPlugin.Id, plugin.Metadata.Id);
        Assert.AreEqual(0, plugin.Metadata.Commands.Count);
        Assert.IsNull(plugin.Metadata.PermissionCatalog);
        using var stream = typeof(NeoMcpPlugin).Assembly.GetManifestResourceStream("NeoAstra.Mcp.neoastra.plugin.json");
        Assert.IsNotNull(stream);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        Assert.AreEqual(NeoMcpPlugin.Id, root.GetProperty("id").GetString());
        Assert.AreEqual("NeoAstra.Mcp.NeoMcpPlugin", root.GetProperty("entryPoint").GetString());
        Assert.AreEqual("none-by-default", root.GetProperty("rendererAuthority").GetString());
        Assert.AreEqual(0, root.GetProperty("commands").GetArrayLength());
        Assert.AreEqual(0, root.GetProperty("permissions").GetArrayLength());

        // The plugin registers with the others, and grants nothing to a page.
        await using var host = new NeoPluginBuilder().AddNeoAstraMcp().Build();
        Assert.AreEqual(1, host.Plugins.Count);
    }

    [TestMethod]
    public void Endpoint_ComesFromTheOptionsOrTheSessionAndOnlyForTheLoopbackInterface()
    {
        static Func<string, string?> Environment(string? url, string? token) => name => name switch
        {
            "NEOASTRA_MCP_URL" => url,
            "NEOASTRA_MCP_TOKEN" => token,
            _ => null,
        };

        // Without a session, an application connects nowhere.
        Assert.IsNull(NeoMcpEndpoint.Resolve(new NeoMcpOptions(), Environment(null, null)));
        Assert.IsNull(NeoMcpEndpoint.Resolve(new NeoMcpOptions(), Environment("ws://127.0.0.1:5000", null)));
        Assert.IsNull(NeoMcpEndpoint.Resolve(new NeoMcpOptions(), Environment(null, Token)));

        var session = NeoMcpEndpoint.Resolve(new NeoMcpOptions(), Environment(" ws://127.0.0.1:5000/ ", Token));
        Assert.IsNotNull(session);
        Assert.AreEqual(new Uri("ws://127.0.0.1:5000/"), session.Url);
        Assert.AreEqual(Token, session.Token);
        Assert.IsNotNull(NeoMcpEndpoint.Resolve(new NeoMcpOptions(), Environment("ws://[::1]:5000", Token)));
        Assert.IsNotNull(NeoMcpEndpoint.Resolve(new NeoMcpOptions(), Environment("wss://localhost:5000", Token)));

        // A session that names another machine, another scheme, or a weak token is not followed.
        Assert.IsNull(NeoMcpEndpoint.Resolve(new NeoMcpOptions(), Environment("ws://192.168.1.20:5000", Token)));
        Assert.IsNull(NeoMcpEndpoint.Resolve(new NeoMcpOptions(), Environment("ws://example.com:5000", Token)));
        Assert.IsNull(NeoMcpEndpoint.Resolve(new NeoMcpOptions(), Environment("http://127.0.0.1:5000", Token)));
        Assert.IsNull(NeoMcpEndpoint.Resolve(new NeoMcpOptions(), Environment("ws://user@127.0.0.1:5000", Token)));
        Assert.IsNull(NeoMcpEndpoint.Resolve(new NeoMcpOptions(), Environment("ws://127.0.0.1:5000", "short")));
        Assert.IsNull(NeoMcpEndpoint.Resolve(new NeoMcpOptions(), Environment("ws://127.0.0.1:5000", "has spaces in the token")));

        // The options of the application take the place of the session, and are checked when the plugin is created.
        var options = new NeoMcpOptions { RelayUrl = new Uri("ws://127.0.0.1:6000"), RelayToken = "options-token-0123456789" };
        var own = NeoMcpEndpoint.Resolve(options, Environment("ws://127.0.0.1:5000", Token));
        Assert.AreEqual(6000, own!.Url.Port);
        Assert.AreEqual("options-token-0123456789", own.Token);
        Assert.ThrowsExactly<ArgumentException>(() => new NeoMcpPlugin(new NeoMcpOptions { RelayUrl = new Uri("ws://10.0.0.2:5000") }));
        Assert.ThrowsExactly<ArgumentException>(() => new NeoPluginBuilder().AddNeoAstraMcp(new NeoMcpOptions { RelayToken = "weak" }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new NeoMcpOptions { ReconnectDelay = TimeSpan.Zero });
    }

    [TestMethod]
    public void Wire_ReadsWhatItWritesAndRefusesOtherMessages()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"uid":{"type":"string"}}}""");
        var hello = NeoMcpWire.Parse(NeoMcpWire.Hello(Application, [new("click", "Clicks", schema.RootElement, false), new("take_snapshot", "Snapshot", schema.RootElement, true)]));
        Assert.AreEqual(NeoMcpMessageKind.Hello, hello.Kind);
        Assert.AreEqual(NeoMcpWire.ProtocolVersion, hello.ProtocolVersion);
        Assert.AreEqual(Application, hello.Application);
        Assert.AreEqual(2, hello.Tools!.Count);
        Assert.AreEqual("click", hello.Tools[0].Name);
        Assert.IsFalse(hello.Tools[0].IsReadOnly);
        Assert.IsTrue(hello.Tools[1].IsReadOnly);
        Assert.AreEqual("string", hello.Tools[0].InputSchema.GetProperty("properties").GetProperty("uid").GetProperty("type").GetString());

        using var arguments = JsonDocument.Parse("""{"uid":"1_2"}""");
        var call = NeoMcpWire.Parse(NeoMcpWire.Call("7", "click", arguments.RootElement));
        Assert.AreEqual(NeoMcpMessageKind.Call, call.Kind);
        Assert.AreEqual("7", call.Id);
        Assert.AreEqual("click", call.Name);
        Assert.AreEqual("1_2", call.Arguments!.Value.GetProperty("uid").GetString());
        Assert.IsNull(NeoMcpWire.Parse(NeoMcpWire.Call("8", "list_pages", null)).Arguments);

        var result = NeoMcpWire.Parse(NeoMcpWire.Result("7", new("Took a screenshot.", [new("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47 })], false)));
        Assert.AreEqual(NeoMcpMessageKind.Result, result.Kind);
        Assert.AreEqual("Took a screenshot.", result.Outcome!.Text);
        Assert.IsFalse(result.Outcome.IsError);
        Assert.AreEqual("image/png", result.Outcome.Images.Single().ContentType);
        CollectionAssert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, result.Outcome.Images.Single().Data.ToArray());
        Assert.IsTrue(NeoMcpWire.Parse(NeoMcpWire.Result("9", NeoMcpToolOutcome.Error("No open dialog found"))).Outcome!.IsError);

        Assert.AreEqual(NeoMcpMessageKind.Cancel, NeoMcpWire.Parse(NeoMcpWire.Cancel("7")).Kind);

        foreach (var invalid in new[] { "[]", "not json", """{"type":"reboot"}""", """{"type":"call","id":"1"}""", """{"type":"call","id":1,"name":"click"}""", """{"type":"result","id":"1","text":"x","images":[{"contentType":"image/png","data":"%%"}]}""", """{"type":"hello","protocol":1,"application":"a","platform":"ios","device":"d"}""" })
            Assert.ThrowsExactly<FormatException>(() => NeoMcpWire.Parse(System.Text.Encoding.UTF8.GetBytes(invalid)), invalid);
    }

    [TestMethod]
    public async Task Connector_SaysHelloWithTheTokenAndAnswersOverlappingCalls()
    {
        await using var relay = TestRelay.Start();
        var host = new FakeToolHost();
        await using var connector = new NeoMcpConnector(host, new(relay.Url, Token), Application, TimeSpan.FromMilliseconds(100));
        connector.Start();

        var connection = await relay.AcceptAsync();
        Assert.AreEqual("Bearer " + Token, connection.Authorization);
        Assert.AreEqual(NeoMcpWire.Path, connection.Path);
        var hello = await connection.ReceiveAsync();
        Assert.AreEqual(NeoMcpMessageKind.Hello, hello.Kind);
        Assert.AreEqual(Application, hello.Application);
        CollectionAssert.AreEqual(new[] { "take_screenshot", "wait_for", "fail" }, hello.Tools!.Select(static tool => tool.Name).ToArray());

        // A call that waits does not hold up the next one; each answer carries the identifier of its call.
        using var wait = JsonDocument.Parse("""{"text":["ready"]}""");
        await connection.SendAsync(NeoMcpWire.Call("1", "wait_for", wait.RootElement));
        await connection.SendAsync(NeoMcpWire.Call("2", "take_screenshot", null));
        var screenshot = await connection.ReceiveAsync();
        Assert.AreEqual("2", screenshot.Id);
        Assert.AreEqual("Took a screenshot of the current page's viewport.", screenshot.Outcome!.Text);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, screenshot.Outcome.Images.Single().Data.ToArray());
        Assert.AreEqual("ready", (await host.WaitStarted.Task).GetProperty("text")[0].GetString());
        host.ReleaseWait.SetResult();
        var waited = await connection.ReceiveAsync();
        Assert.AreEqual("1", waited.Id);
        Assert.IsFalse(waited.Outcome!.IsError);

        // A tool that throws answers with an error instead of ending the connection.
        await connection.SendAsync(NeoMcpWire.Call("3", "fail", null));
        var failed = await connection.ReceiveAsync();
        Assert.AreEqual("3", failed.Id);
        Assert.IsTrue(failed.Outcome!.IsError);
        StringAssert.Contains(failed.Outcome.Text, "InvalidOperationException");
    }

    [TestMethod]
    public async Task Connector_CancelsACallThatTheRelayNoLongerWaitsFor()
    {
        await using var relay = TestRelay.Start();
        var host = new FakeToolHost();
        await using var connector = new NeoMcpConnector(host, new(relay.Url, Token), Application, TimeSpan.FromMilliseconds(100));
        connector.Start();
        var connection = await relay.AcceptAsync();
        await connection.ReceiveAsync();

        using var wait = JsonDocument.Parse("""{"text":["never"]}""");
        await connection.SendAsync(NeoMcpWire.Call("1", "wait_for", wait.RootElement));
        await host.WaitStarted.Task;
        await connection.SendAsync(NeoMcpWire.Cancel("1"));
        Assert.IsTrue(await host.WaitCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var canceled = await connection.ReceiveAsync();
        Assert.AreEqual("1", canceled.Id);
        Assert.IsTrue(canceled.Outcome!.IsError);
    }

    [TestMethod]
    public async Task Connector_ConnectsAgainWhenTheRelayGoesAwayAndStopsWhenDisposed()
    {
        await using var relay = TestRelay.Start();
        var connector = new NeoMcpConnector(new FakeToolHost(), new(relay.Url, Token), Application, TimeSpan.FromMilliseconds(100));
        connector.Start();
        Assert.ThrowsExactly<InvalidOperationException>(connector.Start);

        var first = await relay.AcceptAsync();
        await first.ReceiveAsync();
        await first.CloseAsync();
        // The application answers the close of the relay instead of dropping the connection.
        Assert.IsTrue(await first.WaitClosedAsync());

        // The development session restarted its relay: the application says hello again by itself.
        var second = await relay.AcceptAsync();
        Assert.AreEqual(NeoMcpMessageKind.Hello, (await second.ReceiveAsync()).Kind);

        await connector.DisposeAsync();
        Assert.IsTrue(await second.WaitClosedAsync());
        Assert.IsFalse(await relay.TryAcceptAsync(TimeSpan.FromMilliseconds(600)));
        Assert.ThrowsExactly<ObjectDisposedException>(connector.Start);
    }

    [TestMethod]
    public async Task Connector_KeepsTryingUntilARelayListens()
    {
        var port = TestRelay.FreePort();
        await using var connector = new NeoMcpConnector(new FakeToolHost(), new(new Uri($"ws://127.0.0.1:{port}"), Token), Application, TimeSpan.FromMilliseconds(100));
        connector.Start();
        await Task.Delay(400);
        await using var relay = TestRelay.Start(port);
        var connection = await relay.AcceptAsync();
        Assert.AreEqual(NeoMcpMessageKind.Hello, (await connection.ReceiveAsync()).Kind);
    }

    [TestMethod]
    public async Task Embedded_ServesTheToolsOfTheApplicationToAnAgentOverItsOwnStreams()
    {
        // A desktop application started by an agent serves MCP itself, over standard input and output; pipes stand for them here.
        var host = new FakeToolHost();
        var toServer = new System.IO.Pipelines.Pipe();
        var toClient = new System.IO.Pipelines.Pipe();
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(() => NeoMcpEmbeddedServer.RunAsync(host, "Sample", new ModelContextProtocol.Server.StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), "Sample"), stop.Token));
        var client = await ModelContextProtocol.Client.McpClient.CreateAsync(new ModelContextProtocol.Protocol.StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
        try
        {
            Assert.AreEqual("Sample", client.ServerInfo!.Name);
            var tools = (await client.ListToolsAsync()).ToDictionary(static tool => tool.Name);
            CollectionAssert.AreEquivalent(new[] { "take_screenshot", "wait_for", "fail" }, tools.Keys.ToArray());
            Assert.IsTrue(tools["wait_for"].ProtocolTool.Annotations!.ReadOnlyHint);

            var screenshot = await client.CallToolAsync("take_screenshot", new Dictionary<string, object?>());
            Assert.IsFalse(screenshot.IsError);
            var image = screenshot.Content.OfType<ModelContextProtocol.Protocol.ImageContentBlock>().Single();
            Assert.AreEqual("image/png", image.MimeType);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, image.DecodedData.ToArray());

            // The arguments reach the tool as the agent sent them.
            var waiting = client.CallToolAsync("wait_for", new Dictionary<string, object?> { ["text"] = new[] { "ready" } }).AsTask();
            Assert.AreEqual("ready", (await host.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10))).GetProperty("text")[0].GetString());
            host.ReleaseWait.SetResult();
            Assert.IsFalse((await waiting.WaitAsync(TimeSpan.FromSeconds(10))).IsError);

            Assert.IsTrue((await client.CallToolAsync("fail", new Dictionary<string, object?>())).IsError);
            Assert.IsTrue((await client.CallToolAsync("no_such_tool", new Dictionary<string, object?>())).IsError);
        }
        finally
        {
            await client.DisposeAsync();
        }

        // The agent closed its side: the server ends, which is when the application quits.
        await toServer.Writer.CompleteAsync();
        await serving.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class FakeToolHost : INeoMcpToolHost
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

        internal TaskCompletionSource<JsonElement> WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseWait { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> WaitCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<NeoMcpToolInfo> Tools { get; } = [new("take_screenshot", "Takes a screenshot", Schema, false), new("wait_for", "Waits for a text", Schema, true), new("fail", "Throws", Schema, false)];

        public async ValueTask<NeoMcpToolOutcome> CallToolAsync(string name, JsonElement? arguments, CancellationToken cancellationToken)
        {
            switch (name)
            {
                case "take_screenshot":
                    return new("Took a screenshot of the current page's viewport.", [new("image/png", new byte[] { 1, 2, 3 })], false);
                case "wait_for":
                    WaitStarted.TrySetResult(arguments!.Value.Clone());
                    try { await ReleaseWait.Task.WaitAsync(cancellationToken); }
                    catch (OperationCanceledException) { WaitCanceled.TrySetResult(true); throw; }
                    return new("Element matching one of [\"ready\"] found.", [], false);
                default:
                    throw new InvalidOperationException("The tool failed.");
            }
        }
    }

    // A relay of the test: a WebSocket listener on the loopback interface whose connections the test drives.
    private sealed class TestRelay : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Channel<RelayConnection> _connections = Channel.CreateUnbounded<RelayConnection>();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accepting;

        private TestRelay(int port)
        {
            Url = new Uri($"ws://127.0.0.1:{port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _accepting = AcceptLoopAsync();
        }

        internal Uri Url { get; }

        internal static TestRelay Start(int port = 0) => new(port == 0 ? FreePort() : port);

        internal static int FreePort()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        internal async Task<RelayConnection> AcceptAsync() => await _connections.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));

        internal async Task<bool> TryAcceptAsync(TimeSpan timeout)
        {
            try { await _connections.Reader.ReadAsync().AsTask().WaitAsync(timeout); return true; }
            catch (TimeoutException) { return false; }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception) when (_stop.IsCancellationRequested) { return; }
                catch (HttpListenerException) { return; }
                if (!context.Request.IsWebSocketRequest) { context.Response.StatusCode = 400; context.Response.Close(); continue; }
                var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                await _connections.Writer.WriteAsync(new RelayConnection(socket, context.Request.Headers["Authorization"], context.Request.Url!.AbsolutePath));
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Close();
            try { await _accepting; } catch (Exception) { /* the listener is closed */ }
            _stop.Dispose();
        }
    }

    private sealed class RelayConnection(WebSocket socket, string? authorization, string path)
    {
        internal string? Authorization { get; } = authorization;

        internal string Path { get; } = path;

        internal Task SendAsync(byte[] message) => socket.SendAsync(message, WebSocketMessageType.Text, true, CancellationToken.None);

        internal async Task<NeoMcpMessage> ReceiveAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var buffer = new ArrayBufferWriter<byte>();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.GetMemory(4096), timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("The application closed the connection.");
                buffer.Advance(result.Count);
                if (result.EndOfMessage) return NeoMcpWire.Parse(buffer.WrittenMemory);
            }
        }

        internal async Task CloseAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "relay restarts", timeout.Token);
        }

        // Whether the application closed the connection with the close handshake, rather than dropping it.
        internal async Task<bool> WaitClosedAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var buffer = new byte[4096];
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(buffer, timeout.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return true;
                }
            }
            catch (WebSocketException) { return false; }
            catch (OperationCanceledException) { return false; }
        }
    }
}
