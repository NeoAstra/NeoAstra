// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace NeoAstra.Mcp;

/// <summary>
/// The tunnel of a phone to the development server of its session: a listener on the loopback interface of the phone, at the
/// port of the development server and on both <c>127.0.0.1</c> and <c>::1</c>, either of which the development URL names, whose connections go as streams through the sealed connection to the relay, which connects
/// each of them to the development server on the machine. The view loads the page from <c>127.0.0.1</c> as in a simulator,
/// with the updates of the server, and nothing outside the phone reaches the listener.
/// </summary>
internal sealed class NeoMcpTunnel : IAsyncDisposable
{
    private const int ChunkSize = 32 * 1024;
    private static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(15);

    private readonly TcpListener _listener;
    private TcpListener? _listenerV6;
    private readonly ConcurrentDictionary<string, TcpClient> _streams = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Func<byte[], CancellationToken, Task>? _send;
    private TaskCompletionSource _attached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _accepting = Task.CompletedTask;
    private int _next;

    internal NeoMcpTunnel(int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    /// <summary>Gets the port that the tunnel listens on.</summary>
    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Starts to listen; a connection waits until the connection to the relay is up.</summary>
    /// <exception cref="SocketException">The port is in use on the phone.</exception>
    internal void Start()
    {
        _listener.Start();
        // A phone without IPv6 on its loopback interface keeps the IPv4 listener, which the development URL names by default.
        var listenerV6 = new TcpListener(IPAddress.IPv6Loopback, Port);
        try
        {
            listenerV6.Start();
            _listenerV6 = listenerV6;
        }
        catch (SocketException)
        {
            listenerV6.Dispose();
        }
        _accepting = _listenerV6 is { } v6 ? Task.WhenAll(AcceptLoopAsync(_listener), AcceptLoopAsync(v6)) : AcceptLoopAsync(_listener);
    }

    /// <summary>Sends the streams through a new connection to the relay.</summary>
    internal void Attach(Func<byte[], CancellationToken, Task> send)
    {
        lock (_gate)
        {
            _send = send;
            _attached.TrySetResult();
        }
    }

    /// <summary>Ends the streams of a connection to the relay that went away.</summary>
    internal void Detach()
    {
        lock (_gate)
        {
            _send = null;
            if (_attached.Task.IsCompleted) _attached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        foreach (var id in _streams.Keys) Close(id);
    }

    /// <summary>Writes bytes of the relay to the connection of the view.</summary>
    internal async Task WriteAsync(string stream, byte[] data, CancellationToken cancellationToken)
    {
        if (!_streams.TryGetValue(stream, out var client)) return;
        try { await client.GetStream().WriteAsync(data, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException) { Close(stream); }
    }

    /// <summary>Ends the connection of the view when the development server ended its side.</summary>
    internal void Close(string stream)
    {
        if (_streams.TryRemove(stream, out var client)) client.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        _listenerV6?.Stop();
        try { await _accepting.ConfigureAwait(false); }
        catch (Exception) { /* the listener is stopped */ }
        Detach();
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
            _ = Task.Run(() => PumpAsync(client));
        }
    }

    // Bytes of the view go to the relay until the view or the relay ends the stream.
    private async Task PumpAsync(TcpClient client)
    {
        Task attached;
        lock (_gate) attached = _attached.Task;
        try { await attached.WaitAsync(AttachTimeout, _stop.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException) { client.Dispose(); return; }

        var id = Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture);
        Func<byte[], CancellationToken, Task>? send;
        lock (_gate) send = _send;
        if (send is null) { client.Dispose(); return; }
        _streams[id] = client;
        var buffer = new byte[ChunkSize];
        try
        {
            await send(NeoMcpWire.StreamOpen(id), _stop.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            while (true)
            {
                var read = await stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (read == 0) break;
                await send(NeoMcpWire.StreamData(id, buffer.AsSpan(0, read)), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException or System.Net.WebSockets.WebSocketException)
        {
            // The view, the relay, or the application ended the stream.
        }
        if (_streams.ContainsKey(id))
        {
            Close(id);
            try { await send(NeoMcpWire.StreamClose(id), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is ObjectDisposedException or OperationCanceledException or InvalidOperationException or System.Net.WebSockets.WebSocketException) { /* the relay is gone */ }
        }
    }
}
