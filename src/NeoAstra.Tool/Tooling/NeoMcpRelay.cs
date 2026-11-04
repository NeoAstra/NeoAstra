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
/// connect out to, and that the MCP server of an agent reaches them through. Both present the token of the session. The relay
/// lists the connected applications for the agent, forwards its calls to the application it names, and answers a call whose
/// application goes away with an error instead of leaving it waiting.
/// </summary>
internal sealed class NeoMcpRelay : IAsyncDisposable
{
    // An application or an agent that connects says hello within this time, or the relay closes the connection.
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, ApplicationConnection> _applications = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<AgentConnection, byte> _agents = new();
    private readonly Task _accepting;
    private int _nextApplication;
    private int _nextCall;
    private int _disposed;

    private NeoMcpRelay(HttpListener listener, int port, string token)
    {
        _listener = listener;
        Port = port;
        Token = token;
        Url = new Uri($"ws://127.0.0.1:{port}/");
        _accepting = AcceptLoopAsync();
    }

    /// <summary>Gets the port of the relay on the loopback interface.</summary>
    internal int Port { get; }

    /// <summary>Gets the address that applications and agents connect to.</summary>
    internal Uri Url { get; }

    /// <summary>Gets the token of the session, which every connection presents as a bearer token.</summary>
    internal string Token { get; }

    /// <summary>Gets the applications connected now.</summary>
    internal IReadOnlyList<NeoMcpConnectedApplication> Applications
        => _applications.Values.Select(static connection => connection.Description).OrderBy(static application => int.Parse(application.Id, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    /// <summary>Starts a relay on a free port of the loopback interface, with a new random token.</summary>
    internal static NeoMcpRelay Start()
    {
        // A port that the system just handed out can be taken again before the listener binds it; try a few.
        for (var attempt = 0; ; attempt++)
        {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return new NeoMcpRelay(listener, port, NewToken());
            }
            catch (HttpListenerException) when (attempt < 4)
            {
                listener.Close();
            }
        }
    }

    internal static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Close();
        try { await _accepting.ConfigureAwait(false); }
        catch (Exception) { /* the listener is closed */ }
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

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (_stop.IsCancellationRequested) { return; }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "";
        var isApplication = path == NeoMcpWire.Path;
        if (!isApplication && path != NeoMcpWire.AgentPath) { Reject(context, 404); return; }
        if (!context.Request.IsWebSocketRequest) { Reject(context, 400); return; }
        if (!IsAuthorized(context.Request.Headers["Authorization"])) { Reject(context, 401); return; }
        WebSocket socket;
        try { socket = (await context.AcceptWebSocketAsync(null).ConfigureAwait(false)).WebSocket; }
        catch (Exception exception) when (exception is WebSocketException or HttpListenerException or ObjectDisposedException) { return; }
        try
        {
            if (isApplication) await ServeApplicationAsync(socket).ConfigureAwait(false);
            else await ServeAgentAsync(socket).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or FormatException or OperationCanceledException or ObjectDisposedException)
        {
            // The other side went away or broke the protocol; its connection ends here.
        }
        finally
        {
            await CloseAsync(socket).ConfigureAwait(false);
            socket.Dispose();
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

    private async Task ServeApplicationAsync(WebSocket socket)
    {
        NeoMcpMessage hello;
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            waiting.CancelAfter(HelloTimeout);
            var first = await ReceiveAsync(socket, waiting.Token).ConfigureAwait(false);
            if (first is null) return;
            hello = NeoMcpWire.Parse(first.Value);
        }
        if (hello.Kind != NeoMcpMessageKind.Hello) throw new FormatException("An application says hello first.");
        if (hello.ProtocolVersion != NeoMcpWire.ProtocolVersion) throw new FormatException($"The application speaks protocol {hello.ProtocolVersion}; this tool speaks {NeoMcpWire.ProtocolVersion}.");

        var id = Interlocked.Increment(ref _nextApplication).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var connection = new ApplicationConnection(socket, new(id, hello.Application!, hello.Tools!));
        _applications[id] = connection;
        await BroadcastApplicationsAsync().ConfigureAwait(false);
        try
        {
            while (true)
            {
                var message = await ReceiveAsync(socket, _stop.Token).ConfigureAwait(false);
                if (message is null) break;
                var parsed = NeoMcpWire.Parse(message.Value);
                if (parsed.Kind != NeoMcpMessageKind.Result) throw new FormatException("An application sends only results after its hello.");
                if (connection.Pending.TryRemove(parsed.Id!, out var pending)) pending.TrySetResult(parsed.Outcome!);
            }
        }
        finally
        {
            _applications.TryRemove(id, out _);
            foreach (var pending in connection.Pending.Values) pending.TrySetResult(NeoMcpToolOutcome.Error($"The application {connection.Description.Info.Application} disconnected before it answered."));
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
                        if (calls.TryRemove(parsed.Id!, out var canceled)) await canceled.CancelAsync().ConfigureAwait(false);
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
    internal static async Task<ReadOnlyMemory<byte>?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.GetMemory(16384), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            buffer.Advance(result.Count);
            if (buffer.WrittenCount > NeoMcpWire.MaximumMessageSize) throw new FormatException("The message is larger than the relay accepts.");
            if (result.EndOfMessage)
            {
                if (result.MessageType != WebSocketMessageType.Text) throw new FormatException("The messages are text messages.");
                return buffer.WrittenMemory;
            }
        }
    }

    private abstract class Connection(WebSocket socket) : IDisposable
    {
        private readonly SemaphoreSlim _send = new(1, 1);

        internal WebSocket Socket { get; } = socket;

        internal async Task SendAsync(byte[] message, CancellationToken cancellationToken)
        {
            await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await Socket.SendAsync(message, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false); }
            finally { _send.Release(); }
        }

        public void Dispose() => _send.Dispose();
    }

    private sealed class ApplicationConnection(WebSocket socket, NeoMcpConnectedApplication description) : Connection(socket)
    {
        internal NeoMcpConnectedApplication Description { get; } = description;

        internal ConcurrentDictionary<string, TaskCompletionSource<NeoMcpToolOutcome>> Pending { get; } = new(StringComparer.Ordinal);
    }

    private sealed class AgentConnection(WebSocket socket) : Connection(socket);
}
