// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using NeoAstra.Tool.Shared.Mcp;

namespace NeoAstra.Tooling;

/// <summary>
/// The relay of a development session: a WebSocket listener on the loopback interface that the applications of the session
/// connect out to, and that the MCP server of an agent reaches them through. The agent presents the token of the session; an
/// application and the relay prove it to each other without sending it. The relay lists the connected applications for the
/// agent, forwards its calls to the application it names, and answers a call whose application goes away with an error instead
/// of leaving it waiting. For a phone that is not connected through a forwarding of the loopback interface, the relay also
/// listens on an address of the local network, where it accepts applications only.
/// </summary>
internal sealed class NeoMcpRelay : IAsyncDisposable
{
    // An application or an agent that connects says hello within this time, or the relay closes the connection.
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpListener _listener;
    private readonly HttpListener? _networkListener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, ApplicationConnection> _applications = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<AgentConnection, byte> _agents = new();
    private readonly Task _accepting;
    private readonly Task _acceptingNetwork;
    private int _nextApplication;
    // The connections of applications that did not prove the token yet: a few at a time, so that a machine on the local network
    // that opens many cannot tie up the relay.
    private int _handshaking;
    internal const int MaximumHandshakes = 16;
    private int _nextCall;
    private int _disposed;

    private readonly IPEndPoint? _tunnelTarget;

    private NeoMcpRelay(HttpListener listener, HttpListener? networkListener, IPAddress? networkAddress, int port, string token, IPEndPoint? tunnelTarget)
    {
        _tunnelTarget = tunnelTarget;
        _listener = listener;
        _networkListener = networkListener;
        Port = port;
        Token = token;
        Url = new Uri($"ws://127.0.0.1:{port}/");
        NetworkUrl = networkAddress is null ? null : new UriBuilder("ws", networkAddress.ToString(), port, "/").Uri;
        _accepting = AcceptLoopAsync(listener, applicationsOnly: false);
        _acceptingNetwork = networkListener is null ? Task.CompletedTask : AcceptLoopAsync(networkListener, applicationsOnly: true);
    }

    /// <summary>Gets the address of the relay on the local network, which applications on a phone connect to, or <see langword="null"/> when it has none or the system did not let it listen there.</summary>
    internal Uri? NetworkUrl { get; }

    /// <summary>Gets the port of the relay on the loopback interface.</summary>
    internal int Port { get; }

    /// <summary>Gets the address that applications and agents connect to.</summary>
    internal Uri Url { get; }

    /// <summary>Gets the token of the session, which an agent presents as a bearer token and an application proves without sending it.</summary>
    internal string Token { get; }

    /// <summary>Gets the applications connected now.</summary>
    internal IReadOnlyList<NeoMcpConnectedApplication> Applications
        => _applications.Values.Select(static connection => connection.Description).OrderBy(static application => int.Parse(application.Id, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    /// <summary>Starts a relay on a free port of the loopback interface, with a new random token.</summary>
    internal static NeoMcpRelay Start() => Start(null);

    /// <summary>Starts a relay that also accepts applications on an address of the local network, for a phone that reaches the machine there.</summary>
    /// <exception cref="ArgumentException">The address is not a private IPv4 address.</exception>
    internal static NeoMcpRelay Start(IPAddress? networkAddress) => Start(networkAddress, null);

    /// <summary>Starts a relay that also tunnels the streams of applications to the development server at a loopback address.</summary>
    /// <exception cref="ArgumentException">The address is not a private IPv4 address, or the development server is not on the loopback interface.</exception>
    internal static NeoMcpRelay Start(IPAddress? networkAddress, IPEndPoint? tunnelTarget)
    {
        if (tunnelTarget is not null && !IPAddress.IsLoopback(tunnelTarget.Address))
            throw new ArgumentException("The relay tunnels to a development server on the loopback interface only.", nameof(tunnelTarget));
        if (networkAddress is not null && !IsPrivateIPv4(networkAddress))
            throw new ArgumentException("The relay listens on a private IPv4 address of the local network only.", nameof(networkAddress));
        // A port that the system just handed out can be taken again before the listener binds it; try a few.
        for (var attempt = 0; ; attempt++)
        {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException) when (attempt < 4)
            {
                listener.Close();
                continue;
            }
            // Windows lets a user without administrator rights listen on the loopback interface only: the relay goes on without
            // the address of the network then, and NetworkUrl says so.
            var network = networkAddress is null ? null : TryListen($"http://{networkAddress}:{port}/");
            return new NeoMcpRelay(listener, network, network is null ? null : networkAddress, port, NewToken(), tunnelTarget);
        }
    }

    private static HttpListener? TryListen(string prefix)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
            return listener;
        }
        catch (HttpListenerException)
        {
            listener.Close();
            return null;
        }
    }

    /// <summary>Finds the private IPv4 address of this machine on the local network, preferring an interface with a gateway.</summary>
    /// <returns>The address, or <see langword="null"/> when the machine has none.</returns>
    internal static IPAddress? FindNetworkAddress()
    {
        var candidates = new List<(IPAddress Address, bool HasGateway)>();
        foreach (var networkInterface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (networkInterface.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
            var properties = networkInterface.GetIPProperties();
            var hasGateway = properties.GatewayAddresses.Any(static gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any));
            foreach (var unicast in properties.UnicastAddresses)
                if (IsPrivateIPv4(unicast.Address)) candidates.Add((unicast.Address, hasGateway));
        }
        return candidates.OrderByDescending(static candidate => candidate.HasGateway).Select(static candidate => candidate.Address).FirstOrDefault();
    }

    // 10.0.0.0/8, 172.16.0.0/12, and 192.168.0.0/16: the addresses of a local network, never of the internet.
    internal static bool IsPrivateIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168);
    }

    internal static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Close();
        _networkListener?.Close();
        try { await Task.WhenAll(_accepting, _acceptingNetwork).ConfigureAwait(false); }
        catch (Exception) { /* the listeners are closed */ }
        foreach (var application in _applications.Values) application.Socket.Abort();
        foreach (var agent in _agents.Keys) agent.Socket.Abort();
        _stop.Dispose();
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private async Task AcceptLoopAsync(HttpListener listener, bool applicationsOnly)
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (_stop.IsCancellationRequested) { return; }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }
            _ = Task.Run(() => HandleAsync(context, applicationsOnly));
        }
    }

    private async Task HandleAsync(HttpListenerContext context, bool applicationsOnly)
    {
        var path = context.Request.Url?.AbsolutePath ?? "";
        var isApplication = path == NeoMcpWire.Path;
        // Agents reach the relay on the loopback interface only, never on the local network.
        if (!isApplication && (applicationsOnly || path != NeoMcpWire.AgentPath)) { Reject(context, 404); return; }
        if (!context.Request.IsWebSocketRequest) { Reject(context, 400); return; }
        if (!isApplication && !IsAuthorized(context.Request.Headers["Authorization"])) { Reject(context, 401); return; }
        var handshake = isApplication ? new Handshake(this) : null;
        if (handshake is { Admitted: false }) { Reject(context, 503); return; }
        WebSocket socket;
        try { socket = (await context.AcceptWebSocketAsync(null).ConfigureAwait(false)).WebSocket; }
        catch (Exception exception) when (exception is WebSocketException or HttpListenerException or ObjectDisposedException) { handshake?.End(); return; }
        try
        {
            if (handshake is not null) await ServeApplicationAsync(socket, handshake).ConfigureAwait(false);
            else await ServeAgentAsync(socket).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or FormatException or OperationCanceledException or ObjectDisposedException)
        {
            // The other side went away or broke the protocol; its connection ends here.
        }
        finally
        {
            handshake?.End();
            await CloseAsync(socket).ConfigureAwait(false);
            socket.Dispose();
        }
    }

    // One place among the handshakes that run, given back once, when the application proved the token or went away.
    private sealed class Handshake
    {
        private readonly NeoMcpRelay _relay;
        private int _ended;

        internal Handshake(NeoMcpRelay relay)
        {
            _relay = relay;
            Admitted = Interlocked.Increment(ref relay._handshaking) <= MaximumHandshakes;
            if (!Admitted) End();
        }

        internal bool Admitted { get; }

        internal void End()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0) Interlocked.Decrement(ref _relay._handshaking);
        }
    }

    private bool IsAuthorized(string? authorization)
    {
        const string Scheme = "Bearer ";
        if (authorization is null || !authorization.StartsWith(Scheme, StringComparison.Ordinal)) return false;
        var presented = System.Text.Encoding.UTF8.GetBytes(authorization[Scheme.Length..]);
        var expected = System.Text.Encoding.UTF8.GetBytes(Token);
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    private static void Reject(HttpListenerContext context, int status)
    {
        try { context.Response.StatusCode = status; context.Response.Close(); }
        catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException) { /* the client is gone */ }
    }

    private async Task ServeApplicationAsync(WebSocket socket, Handshake handshake)
    {
        var challenge = NeoMcpWire.NewNonce();
        NeoMcpChannel channel;
        NeoMcpMessage description;
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            // Until the application proved the token, it gets little time and room: a hello is small.
            waiting.CancelAfter(HelloTimeout);
            await socket.SendAsync(NeoMcpWire.Challenge(challenge), WebSocketMessageType.Text, true, waiting.Token).ConfigureAwait(false);
            var first = await ReceiveAsync(socket, waiting.Token, WebSocketMessageType.Text, NeoMcpWire.MaximumHandshakeMessageSize).ConfigureAwait(false);
            if (first is null) return;
            var hello = NeoMcpWire.Parse(first.Value);
            if (hello.Kind != NeoMcpMessageKind.Hello) throw new FormatException("An application says hello first.");
            if (hello.ProtocolVersion != NeoMcpWire.ProtocolVersion) throw new FormatException($"The application speaks protocol {hello.ProtocolVersion}; this tool speaks {NeoMcpWire.ProtocolVersion}.");
            // An application that does not prove the token of the session is not listed, and the relay proves nothing to it.
            if (!NeoMcpWire.IsValidProof(Token, NeoMcpWire.ApplicationRole, challenge, hello.Proof)) throw new FormatException("The application did not prove the token of the session.");
            await socket.SendAsync(NeoMcpWire.Welcome(NeoMcpWire.Proof(Token, NeoMcpWire.RelayRole, hello.Nonce!)), WebSocketMessageType.Text, true, waiting.Token).ConfigureAwait(false);
            // Everything after the welcome is sealed with the keys of the session, in both directions; the first sealed message of
            // the application says who it is and which tools it offers.
            channel = NeoMcpChannel.Create(Token, challenge, hello.Nonce!, isRelay: true);
            try
            {
                var second = await ReceiveAsync(socket, waiting.Token, WebSocketMessageType.Binary).ConfigureAwait(false);
                if (second is null) { channel.Dispose(); return; }
                description = NeoMcpWire.Parse(channel.Open(second.Value.Span));
                if (description.Kind != NeoMcpMessageKind.Describe) throw new FormatException("An application describes itself after the welcome.");
            }
            catch
            {
                channel.Dispose();
                throw;
            }
        }

        handshake.End();
        var id = Interlocked.Increment(ref _nextApplication).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var connection = new ApplicationConnection(socket, new(id, description.Application!, description.Tools!), channel);
        _applications[id] = connection;
        await BroadcastApplicationsAsync().ConfigureAwait(false);
        try
        {
            while (true)
            {
                var message = await ReceiveAsync(socket, _stop.Token, WebSocketMessageType.Binary).ConfigureAwait(false);
                if (message is null) break;
                var parsed = NeoMcpWire.Parse(connection.Open(message.Value.Span));
                switch (parsed.Kind)
                {
                    case NeoMcpMessageKind.Result:
                        if (connection.Pending.TryRemove(parsed.Id!, out var pending)) pending.TrySetResult(parsed.Outcome!);
                        break;
                    case NeoMcpMessageKind.StreamOpen:
                        OpenStream(connection, parsed.Stream!);
                        break;
                    case NeoMcpMessageKind.StreamData:
                        WriteStream(connection, parsed.Stream!, parsed.Data!);
                        break;
                    case NeoMcpMessageKind.StreamClose:
                        if (connection.Streams.TryRemove(parsed.Stream!, out var closed)) closed.Dispose();
                        break;
                    default:
                        throw new FormatException("An application sends only results and the streams of its tunnel after it described itself.");
                }
            }
        }
        finally
        {
            _applications.TryRemove(id, out _);
            foreach (var pending in connection.Pending.Values) pending.TrySetResult(NeoMcpToolOutcome.Error($"The application {connection.Description.Info.Application} disconnected before it answered."));
            foreach (var stream in connection.Streams.Values) stream.Dispose();
            connection.Streams.Clear();
            await BroadcastApplicationsAsync().ConfigureAwait(false);
            connection.Dispose();
        }
    }

    private async Task ServeAgentAsync(WebSocket socket)
    {
        var agent = new AgentConnection(socket);
        _agents[agent] = 0;
        var calls = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var running = new List<Task>();
        try
        {
            await agent.SendAsync(NeoMcpWire.ApplicationList(Applications), _stop.Token).ConfigureAwait(false);
            while (true)
            {
                var message = await ReceiveAsync(socket, _stop.Token).ConfigureAwait(false);
                if (message is null) break;
                var parsed = NeoMcpWire.Parse(message.Value);
                switch (parsed.Kind)
                {
                    case NeoMcpMessageKind.Call:
                        running.RemoveAll(static task => task.IsCompleted);
                        var call = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        if (!calls.TryAdd(parsed.Id!, call))
                        {
                            call.Dispose();
                            await agent.SendAsync(NeoMcpWire.Result(parsed.Id!, NeoMcpToolOutcome.Error($"A call with the identifier '{parsed.Id}' is already running.")), _stop.Token).ConfigureAwait(false);
                            break;
                        }
                        running.Add(ForwardAsync(agent, calls, parsed, call));
                        break;
                    case NeoMcpMessageKind.Cancel:
                        if (calls.TryRemove(parsed.Id!, out var canceled)) await NeoMcpWire.CancelQuietlyAsync(canceled).ConfigureAwait(false);
                        break;
                    default:
                        throw new FormatException("An agent sends only calls and cancellations.");
                }
            }
        }
        finally
        {
            _agents.TryRemove(agent, out _);
            foreach (var call in calls.Values) await call.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(running).ConfigureAwait(false); }
            catch (Exception) { /* each call answers for itself; the agent is gone */ }
            agent.Dispose();
        }
    }

    private async Task ForwardAsync(AgentConnection agent, ConcurrentDictionary<string, CancellationTokenSource> calls, NeoMcpMessage call, CancellationTokenSource cancellation)
    {
        NeoMcpToolOutcome outcome;
        try
        {
            outcome = await CallApplicationAsync(call.Target, call.Name!, call, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            calls.TryRemove(new KeyValuePair<string, CancellationTokenSource>(call.Id!, cancellation));
            cancellation.Dispose();
        }
        try { await agent.SendAsync(NeoMcpWire.Result(call.Id!, outcome), _stop.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException) { /* the agent is gone */ }
    }

    private async Task<NeoMcpToolOutcome> CallApplicationAsync(string? target, string name, NeoMcpMessage call, CancellationToken cancellationToken)
    {
        if (target is null || !_applications.TryGetValue(target, out var application))
            return NeoMcpToolOutcome.Error(target is null ? "The call names no application." : $"No application {target} is connected to the development session.");
        var id = Interlocked.Increment(ref _nextCall).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var pending = new TaskCompletionSource<NeoMcpToolOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        application.Pending[id] = pending;
        try
        {
            await application.SendAsync(NeoMcpWire.Call(id, name, call.Arguments), cancellationToken).ConfigureAwait(false);
            // The application may have gone away while the call was sent: its pending calls were answered without this one.
            if (!_applications.ContainsKey(target)) pending.TrySetResult(NeoMcpToolOutcome.Error($"The application {application.Description.Info.Application} disconnected before it answered."));
            return await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { await application.SendAsync(NeoMcpWire.Cancel(id), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or OperationCanceledException) { /* the application is gone too */ }
            return NeoMcpToolOutcome.Error("The call was canceled.");
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException)
        {
            return NeoMcpToolOutcome.Error($"The application {application.Description.Info.Application} disconnected before it answered.");
        }
        finally
        {
            application.Pending.TryRemove(id, out _);
        }
    }

    // A stream of the tunnel of an application: a connection to the development server, whose bytes go back to the application
    // until either side ends it. A relay without a development server, or a server that refuses, ends the stream at once.
    private void OpenStream(ApplicationConnection connection, string id)
    {
        var stream = new TunnelStream(_tunnelTarget?.AddressFamily ?? AddressFamily.InterNetwork);
        if (_tunnelTarget is null || !connection.Streams.TryAdd(id, stream))
        {
            stream.Dispose();
            _ = SendQuietlyAsync(connection, NeoMcpWire.StreamClose(id));
            return;
        }
        stream.Connected = ConnectAsync(stream, _tunnelTarget);
        // The bytes of the application go to the server on their own, so that a server that does not read them holds up only
        // this stream; a stream that cannot be written ends.
        stream.Writer = new NeoMcpStreamWriter(async () => await stream.Connected.ConfigureAwait(false), () => _ = EndStreamAsync(connection, id, stream), _stop.Token);
        _ = Task.Run(async () =>
        {
            var buffer = new byte[32 * 1024];
            try
            {
                var server = await stream.Connected.ConfigureAwait(false);
                while (true)
                {
                    var read = await server.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    await connection.SendAsync(NeoMcpWire.StreamData(id, buffer.AsSpan(0, read)), _stop.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException or WebSocketException or InvalidOperationException)
            {
                // The development server, the application, or the relay ended the stream.
            }
            if (connection.Streams.TryRemove(new KeyValuePair<string, TunnelStream>(id, stream)))
            {
                stream.Dispose();
                await SendQuietlyAsync(connection, NeoMcpWire.StreamClose(id)).ConfigureAwait(false);
            }
        });
    }

    private async Task<NetworkStream> ConnectAsync(TunnelStream stream, IPEndPoint target)
    {
        await stream.Client.ConnectAsync(target, _stop.Token).ConfigureAwait(false);
        return stream.Client.GetStream();
    }

    private static void WriteStream(ApplicationConnection connection, string id, byte[] data)
    {
        if (connection.Streams.TryGetValue(id, out var stream) && !stream.Writer!.TryWrite(data)) _ = EndStreamAsync(connection, id, stream);
    }

    private static async Task EndStreamAsync(ApplicationConnection connection, string id, TunnelStream stream)
    {
        if (!connection.Streams.TryRemove(new KeyValuePair<string, TunnelStream>(id, stream))) return;
        stream.Dispose();
        await SendQuietlyAsync(connection, NeoMcpWire.StreamClose(id)).ConfigureAwait(false);
    }

    private static async Task SendQuietlyAsync(Connection connection, byte[] message)
    {
        try { await connection.SendAsync(message, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { /* the application is gone */ }
    }

    private sealed class TunnelStream(AddressFamily family) : IDisposable
    {
        internal TcpClient Client { get; } = new(family) { NoDelay = true };

        internal Task<NetworkStream>? Connected { get; set; }

        internal NeoMcpStreamWriter? Writer { get; set; }

        public void Dispose()
        {
            Writer?.Complete();
            Client.Dispose();
        }
    }

    private async Task BroadcastApplicationsAsync()
    {
        var message = NeoMcpWire.ApplicationList(Applications);
        foreach (var agent in _agents.Keys)
        {
            try { await agent.SendAsync(message, _stop.Token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException) { /* that agent is gone */ }
        }
    }

    private static async Task CloseAsync(WebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, closing.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException) { /* the other side is gone */ }
    }

    // A whole text message, or null when the other side closed the connection.
    internal static Task<ReadOnlyMemory<byte>?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
        => ReceiveAsync(socket, cancellationToken, WebSocketMessageType.Text);

    // A whole message of the given type, or null when the other side closed the connection.
    internal static Task<ReadOnlyMemory<byte>?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken, WebSocketMessageType type)
        => ReceiveAsync(socket, cancellationToken, type, NeoMcpWire.MaximumMessageSize);

    // A whole message of the given type and at most the given size, or null when the other side closed the connection.
    internal static async Task<ReadOnlyMemory<byte>?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken, WebSocketMessageType type, int maximumSize)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.GetMemory(16384), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            buffer.Advance(result.Count);
            if (buffer.WrittenCount > maximumSize) throw new FormatException("The message is larger than the relay accepts.");
            if (result.EndOfMessage)
            {
                if (result.MessageType != type) throw new FormatException($"The messages here are {type} messages.");
                return buffer.WrittenMemory;
            }
        }
    }

    private abstract class Connection(WebSocket socket) : IDisposable
    {
        private readonly SemaphoreSlim _send = new(1, 1);

        internal WebSocket Socket { get; } = socket;

        // Seals in the order of sending: the channel counts the messages it seals.
        internal async Task SendAsync(byte[] message, CancellationToken cancellationToken)
        {
            await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Channel is { } channel) await Socket.SendAsync(channel.Seal(message), WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
                else await Socket.SendAsync(message, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            finally { _send.Release(); }
        }

        protected virtual NeoMcpChannel? Channel => null;

        public virtual void Dispose() => _send.Dispose();
    }

    private sealed class ApplicationConnection(WebSocket socket, NeoMcpConnectedApplication description, NeoMcpChannel channel) : Connection(socket)
    {
        protected override NeoMcpChannel? Channel => channel;

        internal ReadOnlyMemory<byte> Open(ReadOnlySpan<byte> message) => channel.Open(message);

        public override void Dispose()
        {
            base.Dispose();
            channel.Dispose();
        }

        internal NeoMcpConnectedApplication Description { get; } = description;

        internal ConcurrentDictionary<string, TaskCompletionSource<NeoMcpToolOutcome>> Pending { get; } = new(StringComparer.Ordinal);

        internal ConcurrentDictionary<string, TunnelStream> Streams { get; } = new(StringComparer.Ordinal);
    }

    private sealed class AgentConnection(WebSocket socket) : Connection(socket);
}
