// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace NeoAstra.Mcp;

/// <summary>Runs the tools that an application offers to the relay.</summary>
internal interface INeoMcpToolHost
{
    IReadOnlyList<NeoMcpToolInfo> Tools { get; }

    /// <summary>Runs a tool; a tool that cannot do what it was asked answers with an error instead of throwing.</summary>
    ValueTask<NeoMcpToolOutcome> CallToolAsync(string name, JsonElement? arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Keeps an application connected to the relay of a development session: it connects out, says who it is, runs each call it is
/// sent, and connects again when the relay goes away, until it is disposed. The application never listens on a port.
/// </summary>
internal sealed class NeoMcpConnector : IAsyncDisposable
{
    // Messages from the relay are calls and cancellations, small next to the results that go the other way.
    private const int MaximumIncomingSize = 4 * 1024 * 1024;

    private readonly INeoMcpToolHost _host;
    private readonly NeoMcpEndpoint _endpoint;
    private readonly NeoMcpApplicationInfo _application;
    private readonly TimeSpan _reconnectDelay;
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;
    private int _disposed;

    private readonly NeoMcpTunnel? _tunnel;

    internal NeoMcpConnector(INeoMcpToolHost host, NeoMcpEndpoint endpoint, NeoMcpApplicationInfo application, TimeSpan reconnectDelay)
        : this(host, endpoint, application, reconnectDelay, null)
    {
    }

    /// <summary>Creates a connector that also tunnels the connections of the view to the development server through the relay.</summary>
    /// <remarks>The tunnel is started already, so that it listens before the view navigates; the connector disposes it.</remarks>
    internal NeoMcpConnector(INeoMcpToolHost host, NeoMcpEndpoint endpoint, NeoMcpApplicationInfo application, TimeSpan reconnectDelay, NeoMcpTunnel? tunnel)
    {
        _tunnel = tunnel;
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _reconnectDelay = reconnectDelay;
    }

    /// <summary>Starts to connect in the background.</summary>
    /// <exception cref="InvalidOperationException">The connector was already started.</exception>
    /// <exception cref="ObjectDisposedException">The connector is disposed.</exception>
    internal void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _run, Task.CompletedTask, null) is not null) throw new InvalidOperationException("The connector is already started.");
        _run = Task.Run(() => RunAsync(_stop.Token));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_run is { } run)
        {
            try { await run.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* the run ends with the stop */ }
        }
        if (_tunnel is { } tunnel) await tunnel.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await ServeAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is WebSocketException or IOException or FormatException or HttpRequestException or OperationCanceledException)
            {
                // The relay is not there yet, went away, or said something this version does not read: try again later.
            }

            try { await Task.Delay(_reconnectDelay, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        await socket.ConnectAsync(RelayAddress(_endpoint.Url), cancellationToken).ConfigureAwait(false);

        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var link = new Link(socket);
        var calls = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var running = new List<Task>();
        // Canceling a receive would abort the socket without a close; a stop sends the close instead, and the receive ends with
        // the answer of the relay, or with the abort of a relay that does not answer.
        Task? stopping = null;
        using var stop = cancellationToken.Register(() => stopping = link.StopAsync(receiving: true));
        try
        {
            // Both sides prove the token of the session without sending it: the relay first, for the nonce of the application,
            // before the application runs anything that it is sent. Everything after is sealed with the keys of the session.
            var challenge = await link.ReceiveAsync(connection.Token).ConfigureAwait(false) is { } first ? NeoMcpWire.Parse(first) : null;
            if (challenge is not { Kind: NeoMcpMessageKind.Challenge, Nonce: { } relayNonce }) throw new FormatException("The relay did not send its challenge.");
            var nonce = NeoMcpWire.NewNonce();
            var proof = NeoMcpWire.Proof(_endpoint.Token, NeoMcpWire.ApplicationRole, relayNonce);
            await link.SendAsync(NeoMcpWire.Hello(_application, _host.Tools, proof, nonce), connection.Token).ConfigureAwait(false);
            var welcome = await link.ReceiveAsync(connection.Token).ConfigureAwait(false) is { } second ? NeoMcpWire.Parse(second) : null;
            if (welcome is not { Kind: NeoMcpMessageKind.Welcome } || !NeoMcpWire.IsValidProof(_endpoint.Token, NeoMcpWire.RelayRole, nonce, welcome.Proof))
                throw new FormatException("The relay did not prove the token of the session.");
            link.Seal(NeoMcpChannel.Create(_endpoint.Token, relayNonce, nonce, isRelay: false));
            _tunnel?.Attach(link.SendAsync);
            while (true)
            {
                var message = await link.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);
                if (message is null) break;
                var parsed = NeoMcpWire.Parse(message.Value);
                switch (parsed.Kind)
                {
                    case NeoMcpMessageKind.Call:
                        running.RemoveAll(static task => task.IsCompleted);
                        running.Add(RunCallAsync(link, calls, parsed.Id!, parsed.Name!, parsed.Arguments, connection.Token));
                        break;
                    case NeoMcpMessageKind.Cancel:
                        if (calls.TryRemove(parsed.Id!, out var call)) await call.CancelAsync().ConfigureAwait(false);
                        break;
                    case NeoMcpMessageKind.StreamData when _tunnel is not null:
                        await _tunnel.WriteAsync(parsed.Stream!, parsed.Data!, connection.Token).ConfigureAwait(false);
                        break;
                    case NeoMcpMessageKind.StreamClose when _tunnel is not null:
                        _tunnel.Close(parsed.Stream!);
                        break;
                    default:
                        throw new FormatException("The relay sends only calls, cancellations, and the streams of the tunnel.");
                }
            }
        }
        finally
        {
            _tunnel?.Detach();
            await stop.DisposeAsync().ConfigureAwait(false);
            await connection.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(running).ConfigureAwait(false); }
            catch (Exception) { /* each call reports its own outcome; the connection is gone */ }
            foreach (var call in calls.Values) call.Dispose();
            // The application ends the connection with the handshake: when it answers the close of the relay, and when it leaves a
            // relay that broke the protocol or could not prove the token.
            if (stopping is null && socket.State is WebSocketState.Open or WebSocketState.CloseReceived) await link.StopAsync(receiving: false).ConfigureAwait(false);
            if (stopping is { } pending) await pending.ConfigureAwait(false);
        }
    }

    private async Task RunCallAsync(Link link, ConcurrentDictionary<string, CancellationTokenSource> calls, string id, string name, JsonElement? arguments, CancellationToken connection)
    {
        using var call = CancellationTokenSource.CreateLinkedTokenSource(connection);
        if (!calls.TryAdd(id, call))
        {
            await SendResultAsync(link, id, NeoMcpToolOutcome.Error($"A call with the identifier '{id}' is already running."), connection).ConfigureAwait(false);
            return;
        }

        NeoMcpToolOutcome outcome;
        try
        {
            // The tool runs off the receiving loop, which keeps reading the cancellations of the relay meanwhile.
            await Task.Yield();
            outcome = await _host.CallToolAsync(name, arguments, call.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (call.IsCancellationRequested)
        {
            outcome = NeoMcpToolOutcome.Error("The call was canceled.");
        }
        catch (Exception exception)
        {
            outcome = NeoMcpToolOutcome.Error($"The tool '{name}' failed in the application: {exception.GetType().Name}.");
        }
        finally
        {
            calls.TryRemove(new KeyValuePair<string, CancellationTokenSource>(id, call));
        }

        await SendResultAsync(link, id, outcome, connection).ConfigureAwait(false);
    }

    private static async Task SendResultAsync(Link link, string id, NeoMcpToolOutcome outcome, CancellationToken connection)
    {
        try { await link.SendAsync(NeoMcpWire.Result(id, outcome), connection).ConfigureAwait(false); }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException) { /* the relay is gone and no longer waits */ }
    }

    // The connection to the relay: its messages are text until the handshake, then sealed in binary messages by the channel of
    // the session, in the order in which they are sent.
    private sealed class Link(ClientWebSocket socket) : IDisposable
    {
        private readonly SemaphoreSlim _send = new(1, 1);
        private NeoMcpChannel? _channel;

        internal void Seal(NeoMcpChannel channel) => _channel = channel;

        internal async Task SendAsync(byte[] message, CancellationToken cancellationToken)
        {
            await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_channel is { } channel) await socket.SendAsync(channel.Seal(message), WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
                else await socket.SendAsync(message, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            finally { _send.Release(); }
        }

        // A whole message, opened once the channel is sealed, or null when the relay closed the connection.
        internal async Task<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken)
        {
            var buffer = new ArrayBufferWriter<byte>(4096);
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.GetMemory(4096), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                buffer.Advance(result.Count);
                if (buffer.WrittenCount > MaximumIncomingSize)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "The message is too large.", cancellationToken).ConfigureAwait(false);
                    throw new FormatException("The relay sent a message larger than the application accepts.");
                }
                if (!result.EndOfMessage) continue;
                if (_channel is { } channel)
                {
                    if (result.MessageType != WebSocketMessageType.Binary) throw new FormatException("The relay sends sealed messages after the handshake.");
                    return channel.Open(buffer.WrittenSpan);
                }
                if (result.MessageType != WebSocketMessageType.Text) throw new FormatException("The relay sends text messages during the handshake.");
                return buffer.WrittenMemory;
            }
        }

        // Sends the close, then gives the relay a moment to answer it before the socket is aborted. While the loop still receives,
        // it reads the answer; once it ended, the close reads the answer itself.
        internal async Task StopAsync(bool receiving)
        {
            try
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _send.WaitAsync(closing.Token).ConfigureAwait(false);
                try
                {
                    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        if (receiving || socket.State == WebSocketState.CloseReceived) await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, closing.Token).ConfigureAwait(false);
                        else await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closing.Token).ConfigureAwait(false);
                    }
                }
                finally { _send.Release(); }
                while (socket.State is not (WebSocketState.Closed or WebSocketState.Aborted) && !closing.IsCancellationRequested)
                    await Task.Delay(20, closing.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException) { /* the relay is gone already */ }
            finally
            {
                if (socket.State is not WebSocketState.Closed) socket.Abort();
            }
        }

        public void Dispose()
        {
            _channel?.Dispose();
            _send.Dispose();
        }
    }

    private static Uri RelayAddress(Uri url)
        => url.AbsolutePath is "" or "/" ? new UriBuilder(url) { Path = NeoMcpWire.Path }.Uri : url;
}
