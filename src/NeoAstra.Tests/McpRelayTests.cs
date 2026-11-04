// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NeoAstra.Tooling;
using App = NeoAstra.Mcp;

namespace NeoAstra.Tests;

// The relay of a development session and the MCP server of `dotnet neoastra mcp`, end to end in the test process: applications
// connect with the connector of NeoAstra.Mcp, and an agent talks to the server with the MCP client of the SDK.
[TestClass]
public sealed class McpRelayTests
{
    [TestMethod]
    public async Task Relay_AcceptsOnlyTheTokenOfItsSessionOnItsPaths()
    {
        await using var relay = NeoMcpRelay.Start();
        Assert.IsTrue(relay.Url.IsLoopback);
        Assert.IsTrue(relay.Token.Length >= 40);
        Assert.AreNotEqual(relay.Token, NeoMcpRelay.NewToken());

        await AssertRefusedAsync(new Uri(relay.Url, "/neoastra/mcp/agent"), null);
        await AssertRefusedAsync(new Uri(relay.Url, "/neoastra/mcp/agent"), "Bearer " + relay.Token[..^1]);
        await AssertRefusedAsync(new Uri(relay.Url, "/other"), "Bearer " + relay.Token);

        // An application proves the token for the challenge of the relay; one that cannot is closed and never listed.
        using (var application = new ClientWebSocket())
        {
            await application.ConnectAsync(new Uri(relay.Url, "/neoastra/mcp"), CancellationToken.None);
            var challenge = App.NeoMcpWire.Parse((await NeoMcpRelay.ReceiveAsync(application, CancellationToken.None))!.Value);
            Assert.AreEqual(App.NeoMcpMessageKind.Challenge, challenge.Kind);
            var proof = App.NeoMcpWire.Proof("wrong-token-0123456789", App.NeoMcpWire.ApplicationRole, challenge.Nonce!);
            await application.SendAsync(App.NeoMcpWire.Hello(proof, App.NeoMcpWire.NewNonce()), WebSocketMessageType.Text, true, CancellationToken.None);
            Assert.IsNull(await NeoMcpRelay.ReceiveAsync(application, CancellationToken.None));
            Assert.AreEqual(0, relay.Applications.Count);
        }

        using var agent = new ClientWebSocket();
        agent.Options.SetRequestHeader("Authorization", "Bearer " + relay.Token);
        await agent.ConnectAsync(new Uri(relay.Url, "/neoastra/mcp/agent"), CancellationToken.None);
        Assert.AreEqual(WebSocketState.Open, agent.State);
    }

    [TestMethod]
    public async Task Relay_GivesApplicationsThatDidNotProveTheTokenLittleRoom()
    {
        await using var relay = NeoMcpRelay.Start();
        var applicationUrl = new Uri(relay.Url, "/neoastra/mcp");

        // A hello larger than any hello ends the connection before the relay reads it all.
        using (var large = new ClientWebSocket())
        {
            await large.ConnectAsync(applicationUrl, CancellationToken.None);
            Assert.IsNotNull(await NeoMcpRelay.ReceiveAsync(large, CancellationToken.None));
            await large.SendAsync(new byte[App.NeoMcpWire.MaximumHandshakeMessageSize + 1], WebSocketMessageType.Text, true, CancellationToken.None);
            Assert.IsNull(await NeoMcpRelay.ReceiveAsync(large, CancellationToken.None));
        }

        // Only a few connections may wait in the handshake at once; the next one is refused until one of them ends.
        var waiting = new List<ClientWebSocket>();
        try
        {
            for (var index = 0; index < NeoMcpRelay.MaximumHandshakes; index++)
            {
                var socket = new ClientWebSocket();
                waiting.Add(socket);
                await socket.ConnectAsync(applicationUrl, CancellationToken.None);
                Assert.IsNotNull(await NeoMcpRelay.ReceiveAsync(socket, CancellationToken.None));
            }
            await AssertRefusedAsync(applicationUrl, null);

            waiting[0].Abort();
            await using var phone = Connect(relay, new FakeTools(), new("Sample", "ios", "iPhone 17e", 7));
            await WaitUntilAsync(() => relay.Applications.Count == 1);
        }
        finally
        {
            foreach (var socket in waiting) socket.Dispose();
        }
    }

    [TestMethod]
    public async Task Relay_AcceptsApplicationsButNoAgentOnTheAddressOfTheNetwork()
    {
        // For a phone on the local network, the relay listens on the private address of the machine there too.
        Assert.ThrowsExactly<ArgumentException>(() => NeoMcpRelay.Start(System.Net.IPAddress.Parse("8.8.8.8")));
        Assert.ThrowsExactly<ArgumentException>(() => NeoMcpRelay.Start(System.Net.IPAddress.IPv6Loopback));
        Assert.IsTrue(NeoMcpRelay.IsPrivateIPv4(System.Net.IPAddress.Parse("192.168.1.20")));
        Assert.IsTrue(NeoMcpRelay.IsPrivateIPv4(System.Net.IPAddress.Parse("172.20.0.5")));
        Assert.IsFalse(NeoMcpRelay.IsPrivateIPv4(System.Net.IPAddress.Parse("172.32.0.5")));
        Assert.IsFalse(NeoMcpRelay.IsPrivateIPv4(System.Net.IPAddress.Loopback));
        if (NeoMcpRelay.FindNetworkAddress() is not { } address) { Assert.Inconclusive("This machine has no private IPv4 address."); return; }
        await using var relay = NeoMcpRelay.Start(address);
        // Windows lets only an administrator listen on the address of the network; the relay goes on with the loopback interface.
        if (relay.NetworkUrl is null) { Assert.IsTrue(relay.Url.IsLoopback); Assert.Inconclusive($"This user may not listen on {address}."); return; }
        Assert.AreEqual(new Uri($"ws://{address}:{relay.Port}/"), relay.NetworkUrl);
        await AssertRefusedAsync(new Uri(relay.NetworkUrl!, "/neoastra/mcp/agent"), "Bearer " + relay.Token);

        await using var phone = Connect(relay.NetworkUrl!, relay.Token, new FakeTools(), new("Sample", "ios", "iPhone SE", 7));
        await WaitUntilAsync(() => relay.Applications.Count == 1);
        Assert.AreEqual("iPhone SE", relay.Applications[0].Info.Device);
    }

    [TestMethod]
    public async Task Tunnel_CarriesTheConnectionsOfTheViewToTheDevelopmentServerAndBack()
    {
        // The development server: an echo on the loopback interface of the machine, which upper-cases what it reads.
        using var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        server.Start();
        var serving = Task.Run(async () =>
        {
            while (true)
            {
                var client = await server.AcceptTcpClientAsync();
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[64 * 1024];
                        int read;
                        while ((read = await stream.ReadAsync(buffer)) > 0)
                            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(System.Text.Encoding.ASCII.GetString(buffer, 0, read).ToUpperInvariant()));
                    }
                });
            }
        });
        await using var relay = NeoMcpRelay.Start(null, (System.Net.IPEndPoint)server.LocalEndpoint);

        // The phone: the tunnel listens on its loopback interface, at a port of its own here.
        var tunnel = new App.NeoMcpTunnel(FreePort());
        tunnel.Start();
        await using var phone = new App.NeoMcpConnector(new FakeTools().Named("Sample"), new(relay.Url, relay.Token), new("Sample", "ios", "iPhone12,8", 1), TimeSpan.FromMilliseconds(100), tunnel);
        phone.Start();
        await WaitUntilAsync(() => relay.Applications.Count == 1);

        // Two connections of the view at once, each with its own stream, both ways; a large message comes back whole.
        using var first = new System.Net.Sockets.TcpClient();
        using var second = new System.Net.Sockets.TcpClient();
        await first.ConnectAsync(System.Net.IPAddress.Loopback, tunnel.Port);
        await second.ConnectAsync(System.Net.IPAddress.Loopback, tunnel.Port);
        Assert.AreEqual("GET / HTTP/1.1", await RoundTripAsync(first, "get / http/1.1"));
        Assert.AreEqual("HELLO", await RoundTripAsync(second, "hello"));
        var large = new string('a', 200_000);
        Assert.AreEqual(large.ToUpperInvariant(), await RoundTripAsync(first, large));

        // The view ends a connection: the server sees the end of its stream, and the other connection goes on.
        first.Close();
        Assert.AreEqual("STILL", await RoundTripAsync(second, "still"));

        // The relay goes away: the connections of the view end instead of waiting.
        await relay.DisposeAsync();
        var buffer = new byte[16];
        var ended = await second.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0, ended);
        server.Stop();
        _ = serving;
    }

    [TestMethod]
    public async Task Tunnel_AStreamThatNobodyReadsHoldsUpNeitherTheCallsNorTheOtherStreams()
    {
        // The development server: a connection that starts with F floods the view, which does not read it; one that starts with
        // S is never read by the server, while the view floods it.
        using var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        server.Start();
        using var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await server.AcceptTcpClientAsync(stop.Token);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        var first = new byte[1];
                        if (await stream.ReadAsync(first, stop.Token) == 0) return;
                        var chunk = new byte[64 * 1024];
                        try
                        {
                            if (first[0] == (byte)'F') while (true) await stream.WriteAsync(chunk, stop.Token);
                            await Task.Delay(Timeout.Infinite, stop.Token);
                        }
                        catch (Exception exception) when (exception is IOException or OperationCanceledException) { /* the stream ended */ }
                    }
                });
            }
        });
        await using var relay = NeoMcpRelay.Start(null, (System.Net.IPEndPoint)server.LocalEndpoint);
        var tunnel = new App.NeoMcpTunnel(FreePort());
        tunnel.Start();
        await using var phone = new App.NeoMcpConnector(new FakeTools().Named("Sample"), new(relay.Url, relay.Token), new("Sample", "ios", "iPhone12,8", 1), TimeSpan.FromMilliseconds(100), tunnel);
        phone.Start();
        await WaitUntilAsync(() => relay.Applications.Count == 1);
        await using var agent = await AgentClient.StartAsync(() => new(relay.Url, relay.Token, Environment.ProcessId));

        using var flooded = new System.Net.Sockets.TcpClient();
        await flooded.ConnectAsync(System.Net.IPAddress.Loopback, tunnel.Port);
        await flooded.GetStream().WriteAsync("F"u8.ToArray());
        using var sink = new System.Net.Sockets.TcpClient();
        await sink.ConnectAsync(System.Net.IPAddress.Loopback, tunnel.Port);
        var flooding = Task.Run(async () =>
        {
            var chunk = new byte[64 * 1024];
            chunk[0] = (byte)'S';
            try { while (true) await sink.GetStream().WriteAsync(chunk, stop.Token); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { /* the tunnel ended the stream */ }
        });
        await Task.Delay(TimeSpan.FromSeconds(2));

        var call = agent.Client.CallToolAsync("take_screenshot", new Dictionary<string, object?>()).AsTask();
        Assert.AreEqual("Took a screenshot of Sample.", Text(await call.WaitAsync(TimeSpan.FromSeconds(10))));
        using var other = new System.Net.Sockets.TcpClient();
        await other.ConnectAsync(System.Net.IPAddress.Loopback, tunnel.Port);
        await other.GetStream().WriteAsync("x"u8.ToArray());
        await stop.CancelAsync();
        server.Stop();
        await flooding.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task Tunnel_ListensOnBothLoopbackAddressesThatADevelopmentUrlMayName()
    {
        // A development URL names 127.0.0.1 or ::1: the view of the phone connects to either at the port of the tunnel.
        if (!System.Net.Sockets.Socket.OSSupportsIPv6) { Assert.Inconclusive("This machine has no IPv6."); return; }
        await using var tunnel = new App.NeoMcpTunnel(FreePort());
        tunnel.Start();
        foreach (var address in new[] { System.Net.IPAddress.Loopback, System.Net.IPAddress.IPv6Loopback })
        {
            using var view = new System.Net.Sockets.TcpClient(address.AddressFamily);
            await view.ConnectAsync(address, tunnel.Port);
            Assert.IsTrue(view.Connected, $"The tunnel does not listen on {address}.");
        }
    }

    [TestMethod]
    public async Task Tunnel_EndsAStreamWhenTheSessionHasNoDevelopmentServer()
    {
        await using var relay = NeoMcpRelay.Start();
        var tunnel = new App.NeoMcpTunnel(FreePort());
        tunnel.Start();
        await using var phone = new App.NeoMcpConnector(new FakeTools().Named("Sample"), new(relay.Url, relay.Token), new("Sample", "ios", "iPhone12,8", 1), TimeSpan.FromMilliseconds(100), tunnel);
        phone.Start();
        await WaitUntilAsync(() => relay.Applications.Count == 1);
        using var view = new System.Net.Sockets.TcpClient();
        await view.ConnectAsync(System.Net.IPAddress.Loopback, tunnel.Port);
        await view.GetStream().WriteAsync("GET / HTTP/1.1\r\n\r\n"u8.ToArray());
        Assert.AreEqual(0, await view.GetStream().ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.ThrowsExactly<ArgumentException>(() => NeoMcpRelay.Start(null, new System.Net.IPEndPoint(System.Net.IPAddress.Parse("192.168.1.2"), 5173)));
    }

    private static async Task<string> RoundTripAsync(System.Net.Sockets.TcpClient client, string text)
    {
        var stream = client.GetStream();
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes);
        var answer = new byte[bytes.Length];
        var total = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (total < answer.Length)
        {
            var read = await stream.ReadAsync(answer.AsMemory(total), timeout.Token);
            if (read == 0) throw new IOException("The tunnel ended the connection.");
            total += read;
        }
        return System.Text.Encoding.ASCII.GetString(answer);
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        return ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
    }

    [TestMethod]
    public async Task Agent_ReachesTheToolsOfTheConnectedApplicationThroughTheRelay()
    {
        await using var relay = NeoMcpRelay.Start();
        var session = new NeoMcpRelaySession(relay.Url, relay.Token, Environment.ProcessId);
        await using var agent = await AgentClient.StartAsync(() => session);

        // A session without an application offers the tools of the server and says why there is nothing else.
        CollectionAssert.AreEquivalent(new[] { "list_apps", "select_app" }, (await agent.Client.ListToolsAsync()).Select(static tool => tool.Name).ToArray());
        var none = await agent.Client.CallToolAsync("take_snapshot", new Dictionary<string, object?>());
        Assert.IsTrue(none.IsError);
        StringAssert.Contains(Text(none), "no application is connected");

        var host = new FakeTools();
        await using var phone = Connect(relay, host, new("Sample", "ios", "iPhone 17e", 101));
        await agent.WaitToolsChangedAsync();
        var tools = (await agent.Client.ListToolsAsync()).ToDictionary(static tool => tool.Name);
        CollectionAssert.IsSubsetOf(new[] { "list_apps", "select_app", "take_screenshot", "wait_for" }, tools.Keys.ToArray());
        Assert.IsTrue(tools["wait_for"].ProtocolTool.Annotations!.ReadOnlyHint);
        Assert.IsFalse(tools["take_screenshot"].ProtocolTool.Annotations!.ReadOnlyHint);

        var screenshot = await agent.Client.CallToolAsync("take_screenshot", new Dictionary<string, object?>());
        Assert.IsFalse(screenshot.IsError);
        Assert.AreEqual("Took a screenshot of Sample.", Text(screenshot));
        var image = screenshot.Content.OfType<ImageContentBlock>().Single();
        Assert.AreEqual("image/png", image.MimeType);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, image.DecodedData.ToArray());

        // The arguments of the agent reach the tool of the application as they were sent.
        var waited = await agent.Client.CallToolAsync("wait_for", new Dictionary<string, object?> { ["text"] = new[] { "ready" } });
        Assert.AreEqual("Found ready", Text(waited));

        // A second application becomes the one the tools act on until the agent chooses another.
        await using var desktop = Connect(relay, new FakeTools(), new("Desktop", "macos", "mac", 202));
        await agent.WaitToolsChangedAsync();
        StringAssert.Contains(Text(await agent.Client.CallToolAsync("list_apps", new Dictionary<string, object?>())), "2: Desktop (macos, mac, process 202) [selected]");
        Assert.AreEqual("Took a screenshot of Desktop.", Text(await agent.Client.CallToolAsync("take_screenshot", new Dictionary<string, object?>())));
        var selected = await agent.Client.CallToolAsync("select_app", new Dictionary<string, object?> { ["app"] = "1" });
        StringAssert.Contains(Text(selected), "1: Sample (ios, iPhone 17e, process 101) [selected]");
        Assert.AreEqual("Took a screenshot of Sample.", Text(await agent.Client.CallToolAsync("take_screenshot", new Dictionary<string, object?>())));
        Assert.IsTrue((await agent.Client.CallToolAsync("select_app", new Dictionary<string, object?> { ["app"] = "9" })).IsError);
        Assert.IsTrue((await agent.Client.CallToolAsync("no_such_tool", new Dictionary<string, object?>())).IsError);

        // The chosen application goes away: the tools act on the one that is left.
        // select_app announced a change of the tools too, so the test waits for the list itself rather than for a notification.
        await phone.DisposeAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (Text(await agent.Client.CallToolAsync("list_apps", new Dictionary<string, object?>())).Contains("1: Sample", StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("The server did not notice that the application went away.");
            await Task.Delay(20);
        }
        Assert.AreEqual("Took a screenshot of Desktop.", Text(await agent.Client.CallToolAsync("take_screenshot", new Dictionary<string, object?>())));
    }

    [TestMethod]
    public async Task Agent_CancelsTheCallInTheApplicationWhenTheAgentCancelsItsRequest()
    {
        await using var relay = NeoMcpRelay.Start();
        var session = new NeoMcpRelaySession(relay.Url, relay.Token, Environment.ProcessId);
        var host = new FakeTools();
        await using var phone = Connect(relay, host, new("Sample", "android", "Pixel", 101));
        await WaitUntilAsync(() => relay.Applications.Count == 1);

        // The agent speaks JSON-RPC itself here: it cancels its request with notifications/cancelled, as MCP asks of a client.
        await using var agent = await RawAgent.StartAsync(() => session);
        await agent.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}""");
        Assert.AreEqual(1, (await agent.ReceiveResponseAsync()).GetProperty("id").GetInt32());
        await agent.SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        for (var id = 2; ; id++)
        {
            await agent.SendAsync($$"""{"jsonrpc":"2.0","id":{{id}},"method":"tools/list"}""");
            var tools = (await agent.ReceiveResponseAsync()).GetProperty("result").GetProperty("tools").EnumerateArray().Select(static tool => tool.GetProperty("name").GetString()).ToArray();
            if (tools.Contains("wait_for")) break;
            if (DateTime.UtcNow > deadline) Assert.Fail("The server did not list the tools of the application.");
            await Task.Delay(50);
        }

        await agent.SendAsync("""{"jsonrpc":"2.0","id":100,"method":"tools/call","params":{"name":"wait_for","arguments":{"text":["never"]}}}""");
        await host.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await agent.SendAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":100,"reason":"test"}}""");
        Assert.IsTrue(await host.WaitCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [TestMethod]
    public async Task Agent_GetsAnErrorWhenTheApplicationGoesAwayDuringACall()
    {
        await using var relay = NeoMcpRelay.Start();
        var session = new NeoMcpRelaySession(relay.Url, relay.Token, Environment.ProcessId);
        await using var agent = await AgentClient.StartAsync(() => session);
        var host = new FakeTools();
        var phone = Connect(relay, host, new("Sample", "android", "Pixel", 101));
        await agent.WaitToolsChangedAsync();

        var pending = agent.Client.CallToolAsync("wait_for", new Dictionary<string, object?> { ["text"] = new[] { "never" } }).AsTask();
        await host.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await phone.DisposeAsync();
        var answer = await pending.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.IsTrue(answer.IsError);
        StringAssert.Contains(Text(answer), "disconnected");
        Assert.IsTrue(await host.WaitCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [TestMethod]
    public async Task Agent_SaysThatNoSessionRunsAndFindsOneThatStartsLater()
    {
        NeoMcpRelaySession? session = null;
        await using var agent = await AgentClient.StartAsync(() => session);
        var call = await agent.Client.CallToolAsync("list_apps", new Dictionary<string, object?>());
        StringAssert.Contains(Text(call), "No `dotnet neoastra dev` session runs");
        Assert.IsTrue((await agent.Client.CallToolAsync("take_snapshot", new Dictionary<string, object?>())).IsError);

        await using var relay = NeoMcpRelay.Start();
        session = new(relay.Url, relay.Token, Environment.ProcessId);
        await using var phone = Connect(relay, new FakeTools(), new("Sample", "ios", "iPhone 17e", 101));
        await WaitUntilAsync(() => relay.Applications.Count == 1);
        Assert.AreEqual("Took a screenshot of Sample.", Text(await agent.Client.CallToolAsync("take_screenshot", new Dictionary<string, object?>())));

        // The session ends: the server says so.
        await relay.DisposeAsync();
        session = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!Text(await agent.Client.CallToolAsync("list_apps", new Dictionary<string, object?>())).Contains("No `dotnet neoastra dev` session runs", StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("The server did not notice the end of the session.");
            await Task.Delay(50);
        }
    }

    [TestMethod]
    public async Task Agent_FollowsASessionThatRestartsWithTheApplicationsOfTheNewOne()
    {
        NeoMcpRelaySession? session = null;
        await using var agent = await AgentClient.StartAsync(() => session);
        for (var round = 1; round <= 3; round++)
        {
            await using var relay = NeoMcpRelay.Start();
            await using var phone = Connect(relay, new FakeTools(), new("Sample" + round, "ios", "iPhone 17e", 100 + round));
            await WaitUntilAsync(() => relay.Applications.Count == 1);
            session = new(relay.Url, relay.Token, Environment.ProcessId);
            // The connection to the session before ends while the server already connects to this one: what it leaves behind
            // must not hide the application of this session.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (Text(await agent.Client.CallToolAsync("take_screenshot", new Dictionary<string, object?>())) != $"Took a screenshot of Sample{round}.")
            {
                if (DateTime.UtcNow > deadline) Assert.Fail($"The server did not follow the session of round {round}.");
                await Task.Delay(50);
            }
            StringAssert.Contains(Text(await agent.Client.CallToolAsync("list_apps", new Dictionary<string, object?>())), "Sample" + round);
        }
    }

    [TestMethod]
    public void Discovery_IsPerProjectPrivateToTheUserAndIgnoresAnEndedSession()
    {
        var directory = Path.Combine(Path.GetTempPath(), "neoastra-mcp-discovery-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(Path.GetTempPath(), "some-project");
        try
        {
            Assert.IsNull(NeoMcpRelayDiscovery.TryRead(directory, project));
            var session = new NeoMcpRelaySession(new Uri("ws://127.0.0.1:4711/"), "token-0123456789abcdef", Environment.ProcessId);
            NeoMcpRelayDiscovery.Write(directory, project, session);
            Assert.AreEqual(session, NeoMcpRelayDiscovery.TryRead(directory, project + Path.DirectorySeparatorChar));
            Assert.IsNull(NeoMcpRelayDiscovery.TryRead(directory, project + "-other"));
            var path = NeoMcpRelayDiscovery.PathFor(directory, project, session.Token);
            if (!OperatingSystem.IsWindows()) Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

            // Another session's token does not remove the file; its own does.
            NeoMcpRelayDiscovery.Delete(directory, project, "another-token-0123456789");
            Assert.IsTrue(File.Exists(path));
            NeoMcpRelayDiscovery.Delete(directory, project, session.Token);
            Assert.IsFalse(File.Exists(path));

            // A second session of the project starts before the first one ended: the server follows the newer one, and the end of
            // the older one leaves the newer one found.
            NeoMcpRelayDiscovery.Write(directory, project, session);
            File.SetLastWriteTimeUtc(NeoMcpRelayDiscovery.PathFor(directory, project, session.Token), DateTime.UtcNow.AddMinutes(-1));
            var newer = session with { Url = new Uri("ws://127.0.0.1:4712/"), Token = "newer-token-0123456789abcdef" };
            NeoMcpRelayDiscovery.Write(directory, project, newer);
            Assert.AreEqual(newer, NeoMcpRelayDiscovery.TryRead(directory, project));
            NeoMcpRelayDiscovery.Delete(directory, project, session.Token);
            Assert.AreEqual(newer, NeoMcpRelayDiscovery.TryRead(directory, project));
            NeoMcpRelayDiscovery.Delete(directory, project, newer.Token);
            Assert.IsNull(NeoMcpRelayDiscovery.TryRead(directory, project));

            // A session that ended without removing its file is not followed, and the next session that starts removes the file.
            var ended = session with { Token = "ended-token-0123456789abcdef", ProcessId = int.MaxValue };
            NeoMcpRelayDiscovery.Write(directory, project, ended);
            Assert.IsNull(NeoMcpRelayDiscovery.TryRead(directory, project));
            NeoMcpRelayDiscovery.Write(directory, project, session);
            Assert.IsFalse(File.Exists(NeoMcpRelayDiscovery.PathFor(directory, project, ended.Token)));
            Assert.AreEqual(session, NeoMcpRelayDiscovery.TryRead(directory, project));
            NeoMcpRelayDiscovery.Delete(directory, project, session.Token);

            // A session whose process ended, or that names another machine, is not followed.
            NeoMcpRelayDiscovery.Write(directory, project, session with { ProcessId = int.MaxValue });
            Assert.IsNull(NeoMcpRelayDiscovery.TryRead(directory, project));
            NeoMcpRelayDiscovery.Write(directory, project, session with { Url = new Uri("ws://192.168.1.2:4711/") });
            Assert.IsNull(NeoMcpRelayDiscovery.TryRead(directory, project));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static App.NeoMcpConnector Connect(NeoMcpRelay relay, FakeTools host, App.NeoMcpApplicationInfo info) => Connect(relay.Url, relay.Token, host, info);

    private static App.NeoMcpConnector Connect(Uri url, string token, FakeTools host, App.NeoMcpApplicationInfo info)
    {
        var connector = new App.NeoMcpConnector(host.Named(info.Application), new(url, token), info, TimeSpan.FromMilliseconds(100));
        connector.Start();
        return connector;
    }

    private static async Task AssertRefusedAsync(Uri url, string? authorization)
    {
        using var socket = new ClientWebSocket();
        if (authorization is not null) socket.Options.SetRequestHeader("Authorization", authorization);
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(url, CancellationToken.None));
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("The condition was not met in time.");
            await Task.Delay(20);
        }
    }

    // An agent: the MCP client of the SDK, connected to the server of `dotnet neoastra mcp` through pipes of the test.
    private sealed class AgentClient : IAsyncDisposable
    {
        private readonly NeoMcpAgentServer _server;
        private readonly CancellationTokenSource _stop = new();
        private readonly System.Threading.Channels.Channel<bool> _changes = System.Threading.Channels.Channel.CreateUnbounded<bool>();
        private Task _running = Task.CompletedTask;

        private AgentClient(NeoMcpAgentServer server) => _server = server;

        internal McpClient Client { get; private set; } = null!;

        internal static async Task<AgentClient> StartAsync(Func<NeoMcpRelaySession?> findSession)
        {
            var toServer = new Pipe();
            var toClient = new Pipe();
            var server = new NeoMcpAgentServer(findSession);
            var agent = new AgentClient(server);
            agent._running = Task.Run(() => server.RunAsync(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), "neoastra"), agent._stop.Token));
            var options = new McpClientOptions
            {
                Handlers = new McpClientHandlers
                {
                    NotificationHandlers =
                    [
                        new(NotificationMethods.ToolListChangedNotification, (_, _) => { agent._changes.Writer.TryWrite(true); return ValueTask.CompletedTask; }),
                    ],
                },
            };
            agent.Client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()), options);
            return agent;
        }

        internal async Task WaitToolsChangedAsync()
            => await _changes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _stop.CancelAsync();
            await _server.DisposeAsync();
            try { await _running.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception) { /* the transport ended with the client */ }
            _stop.Dispose();
        }
    }

    // An agent that writes the JSON-RPC lines of MCP itself, connected to the server through pipes of the test.
    private sealed class RawAgent : IAsyncDisposable
    {
        private readonly NeoMcpAgentServer _server;
        private readonly Pipe _toServer = new();
        private readonly Pipe _toClient = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly StreamReader _reader;
        private Task _running = Task.CompletedTask;

        private RawAgent(NeoMcpAgentServer server)
        {
            _server = server;
            _reader = new StreamReader(_toClient.Reader.AsStream());
        }

        internal static Task<RawAgent> StartAsync(Func<NeoMcpRelaySession?> findSession)
        {
            var agent = new RawAgent(new NeoMcpAgentServer(findSession));
            agent._running = Task.Run(() => agent._server.RunAsync(new StreamServerTransport(agent._toServer.Reader.AsStream(), agent._toClient.Writer.AsStream(), "neoastra"), agent._stop.Token));
            return Task.FromResult(agent);
        }

        internal async Task SendAsync(string line)
        {
            await _toServer.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(line + "\n"));
            await _toServer.Writer.FlushAsync();
        }

        // The next response, skipping the notifications of the server.
        internal async Task<JsonElement> ReceiveResponseAsync()
        {
            while (true)
            {
                var line = await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) ?? throw new InvalidOperationException("The server ended.");
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("id", out _)) return document.RootElement.Clone();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _toServer.Writer.CompleteAsync();
            await _server.DisposeAsync();
            try { await _running.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception) { /* the transport ended */ }
            _reader.Dispose();
            _stop.Dispose();
        }
    }

    private sealed class FakeTools : App.INeoMcpToolHost
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        private string _name = "";

        internal TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> WaitCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<App.NeoMcpToolInfo> Tools { get; } = [new("take_screenshot", "Takes a screenshot", Schema, false), new("wait_for", "Waits for a text", Schema, true)];

        public async ValueTask<App.NeoMcpToolOutcome> CallToolAsync(string name, JsonElement? arguments, CancellationToken cancellationToken)
        {
            if (name == "take_screenshot") return new($"Took a screenshot of {_name}.", [new("image/png", new byte[] { 1, 2, 3 })], false);
            var text = arguments!.Value.GetProperty("text")[0].GetString();
            if (text == "ready") return new("Found ready", [], false);
            WaitStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { WaitCanceled.TrySetResult(true); throw; }
            return new("unreachable", [], true);
        }

        internal FakeTools Named(string name) { _name = name; return this; }
    }
}
