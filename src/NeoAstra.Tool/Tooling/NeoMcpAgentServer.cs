// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NeoAstra.Tool.Shared.Mcp;

namespace NeoAstra.Tooling;

/// <summary>
/// The MCP server of <c>dotnet neoastra mcp</c>: it offers an agent the browser automation tools of an application of the
/// development session of the project, which it reaches through the relay of the session, and two tools of its own to list the
/// connected applications and to choose one. It finds the session when it needs it, so it can start before the session does,
/// and a call without a session or an application says what to do instead of waiting.
/// </summary>
internal sealed class NeoMcpAgentServer : IAsyncDisposable
{
    internal const string ListApplicationsTool = "list_apps";
    internal const string SelectApplicationTool = "select_app";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SessionPollInterval = TimeSpan.FromSeconds(1);

    private readonly Func<NeoMcpRelaySession?> _findSession;
    private readonly SemaphoreSlim _connecting = new(1, 1);
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<NeoMcpToolOutcome>> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private ClientWebSocket? _socket;
    private Task? _receiving;
    private TaskCompletionSource _firstList = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyList<NeoMcpConnectedApplication> _applications = [];
    private string? _selected;
    private int _nextCall;
    private int _disposed;

    internal NeoMcpAgentServer(Func<NeoMcpRelaySession?> findSession) => _findSession = findSession ?? throw new ArgumentNullException(nameof(findSession));

    /// <summary>Gets or sets what runs when the tools change, such as when an application connects; the server tells the agent.</summary>
    internal Func<Task>? ToolsChanged { get; set; }

    /// <summary>Serves an agent over a transport, such as standard input and output, until it goes away or the token is canceled.</summary>
    internal async Task RunAsync(ITransport transport, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        await using var server = McpServer.Create(transport, CreateOptions());
        ToolsChanged = async () =>
        {
            try { await server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, _stop.Token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { /* the agent is gone or not initialized yet */ }
        };
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var watch = WatchSessionAsync(watching.Token);
        try { await server.RunAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            await watching.CancelAsync().ConfigureAwait(false);
            await watch.ConfigureAwait(false);
            ToolsChanged = null;
        }
    }

    // A session that starts after the server, or the next one after a session ended, is found without a call of the agent, so
    // that the agent hears of the tools of its applications.
    private async Task WatchSessionAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SessionPollInterval);
        try
        {
            do
            {
                try { await TryConnectAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OperationCanceledException) { /* the next tick tries again */ }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { /* the server stops */ }
    }

    internal McpServerOptions CreateOptions() => new()
    {
        ServerInfo = new Implementation { Name = "neoastra", Version = typeof(NeoMcpAgentServer).Assembly.GetName().Version?.ToString() ?? "0.0.0" },
        Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = true } },
        ServerInstructions = "Drives the pages of a NeoAstra application of the `dotnet neoastra dev` session of this project, on the desktop, in a simulator or an emulator, or on a phone. "
            + $"The tools are those of Chrome DevTools MCP. {ListApplicationsTool} lists the connected applications and {SelectApplicationTool} chooses the one the tools act on.",
        Handlers = new McpServerHandlers
        {
            ListToolsHandler = async (_, cancellationToken) => new ListToolsResult { Tools = [.. await ListToolsAsync(cancellationToken).ConfigureAwait(false)] },
            CallToolHandler = async (request, cancellationToken) =>
            {
                var parameters = request.Params ?? throw new ArgumentException("A tool call names a tool.");
                return await CallToolAsync(parameters.Name, parameters.Arguments, cancellationToken).ConfigureAwait(false);
            },
        },
    };

    internal async ValueTask<IReadOnlyList<ModelContextProtocol.Protocol.Tool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        await TryConnectAsync(cancellationToken).ConfigureAwait(false);
        var tools = new List<ModelContextProtocol.Protocol.Tool>
        {
            OwnTool(ListApplicationsTool, "Lists the applications connected to the development session of this project, and which one the other tools act on.", """{"type":"object","properties":{}}"""),
            OwnTool(SelectApplicationTool, "Chooses the application that the other tools act on. Call list_apps to get the identifiers.",
                """{"type":"object","properties":{"app":{"type":"string","description":"The identifier of the application, from list_apps."}},"required":["app"]}"""),
        };
        if (Current() is { } application)
        {
            foreach (var tool in application.Tools)
            {
                tools.Add(new()
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    InputSchema = tool.InputSchema,
                    Annotations = new ToolAnnotations { ReadOnlyHint = tool.IsReadOnly },
                });
            }
        }
        return tools;
    }

    internal async ValueTask<CallToolResult> CallToolAsync(string name, IDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var connected = await TryConnectAsync(cancellationToken).ConfigureAwait(false);
        switch (name)
        {
            case ListApplicationsTool:
                return Text(DescribeApplications(connected), false);
            case SelectApplicationTool:
            {
                var id = arguments is not null && arguments.TryGetValue("app", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (id is null || !_applications.Any(application => application.Id == id))
                    return Text($"No application '{id}' is connected. {DescribeApplications(connected)}", true);
                _selected = id;
                await NotifyToolsChangedAsync().ConfigureAwait(false);
                return Text(DescribeApplications(connected), false);
            }
        }

        if (!connected) return Text(NoSession, true);
        if (Current() is not { } target) return Text(NoApplication, true);
        if (!target.Tools.Any(tool => tool.Name == name)) return Text($"The application {target.Info.Application} has no tool '{name}'.", true);
        var outcome = await ForwardAsync(target.Id, name, Arguments(arguments), cancellationToken).ConfigureAwait(false);
        var result = new CallToolResult { IsError = outcome.IsError, Content = [new TextContentBlock { Text = outcome.Text }] };
        foreach (var image in outcome.Images) result.Content.Add(ImageContentBlock.FromBytes(image.Data, image.ContentType));
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        _socket?.Abort();
        if (_receiving is { } receiving)
        {
            try { await receiving.ConfigureAwait(false); }
            catch (Exception) { /* the connection is aborted */ }
        }
        _socket?.Dispose();
        _stop.Dispose();
    }

    private const string NoSession = "No `dotnet neoastra dev` session runs for this project. Start one, for example `dotnet neoastra dev --platform ios`, and call the tool again.";
    private const string NoApplication = "The development session runs, but no application is connected to it yet. An application connects once it started with the NeoAstra.Mcp plugin; call list_apps to check.";

    private string DescribeApplications(bool connected)
    {
        if (!connected) return NoSession;
        var applications = _applications;
        if (applications.Count == 0) return NoApplication;
        var current = Current();
        var builder = new StringBuilder("## Applications\n");
        foreach (var application in applications)
        {
            builder.Append(application.Id).Append(": ").Append(application.Info.Application)
                .Append(" (").Append(application.Info.Platform).Append(", ").Append(application.Info.Device)
                .Append(", process ").Append(application.Info.ProcessId.ToString(CultureInfo.InvariantCulture)).Append(')');
            if (ReferenceEquals(application, current)) builder.Append(" [selected]");
            builder.Append('\n');
        }
        return builder.ToString().TrimEnd('\n');
    }

    // The application that the tools act on: the chosen one while it is connected, otherwise the one that connected last.
    private NeoMcpConnectedApplication? Current()
    {
        var applications = _applications;
        return applications.FirstOrDefault(application => application.Id == _selected) ?? applications.LastOrDefault();
    }

    private async Task<NeoMcpToolOutcome> ForwardAsync(string target, string name, JsonElement? arguments, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextCall).ToString(CultureInfo.InvariantCulture);
        var pending = new TaskCompletionSource<NeoMcpToolOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;
        try
        {
            await SendAsync(NeoMcpWire.Call(id, name, arguments, target), cancellationToken).ConfigureAwait(false);
            return await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { await SendAsync(NeoMcpWire.Cancel(id, target), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or InvalidOperationException) { /* the session is gone */ }
            throw;
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            return NeoMcpToolOutcome.Error("The development session ended before the application answered.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendAsync(byte[] message, CancellationToken cancellationToken)
    {
        var socket = _socket ?? throw new InvalidOperationException("No session is connected.");
        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await socket.SendAsync(message, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false); }
        finally { _send.Release(); }
    }

    // Connects to the relay of the session of the project when there is one and the server is not connected yet.
    private async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        if (_socket is { State: WebSocketState.Open }) return true;
        await _connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_socket is { State: WebSocketState.Open }) return true;
            var session = _findSession();
            if (session is null) return false;
            _socket?.Dispose();
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + session.Token);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            timeout.CancelAfter(ConnectTimeout);
            try
            {
                await socket.ConnectAsync(new UriBuilder(session.Url) { Path = NeoMcpWire.AgentPath }.Uri, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                return false;
            }
            _firstList = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _socket = socket;
            _receiving = Task.Run(() => ReceiveLoopAsync(socket));
            // The relay lists the applications first; the tools of the agent come from that list.
            try { await _firstList.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return socket.State == WebSocketState.Open; }
            return socket.State == WebSocketState.Open;
        }
        finally
        {
            _connecting.Release();
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket)
    {
        try
        {
            while (true)
            {
                var message = await NeoMcpRelay.ReceiveAsync(socket, _stop.Token).ConfigureAwait(false);
                if (message is null) break;
                var parsed = NeoMcpWire.Parse(message.Value);
                switch (parsed.Kind)
                {
                    case NeoMcpMessageKind.Applications:
                        var before = Current()?.Id;
                        var changed = !SameApplications(_applications, parsed.Applications!);
                        _applications = parsed.Applications!;
                        _firstList.TrySetResult();
                        if (changed || before != Current()?.Id) await NotifyToolsChangedAsync().ConfigureAwait(false);
                        break;
                    case NeoMcpMessageKind.Result:
                        if (_pending.TryRemove(parsed.Id!, out var pending)) pending.TrySetResult(parsed.Outcome!);
                        break;
                    default:
                        throw new FormatException("The relay sends only the applications and results.");
                }
            }
        }
        catch (Exception exception) when (exception is WebSocketException or FormatException or OperationCanceledException or ObjectDisposedException)
        {
            // The session ended or the server stops.
        }
        finally
        {
            _firstList.TrySetResult();
            foreach (var pending in _pending.Values) pending.TrySetResult(NeoMcpToolOutcome.Error("The development session ended before the application answered."));
            var had = _applications.Count != 0;
            _applications = [];
            if (had && !_stop.IsCancellationRequested) await NotifyToolsChangedAsync().ConfigureAwait(false);
        }
    }

    private async Task NotifyToolsChangedAsync()
    {
        if (ToolsChanged is { } changed) await changed().ConfigureAwait(false);
    }

    private static bool SameApplications(IReadOnlyList<NeoMcpConnectedApplication> before, IReadOnlyList<NeoMcpConnectedApplication> after)
        => before.Count == after.Count && before.Zip(after).All(static pair => pair.First.Id == pair.Second.Id);

    private static ModelContextProtocol.Protocol.Tool OwnTool(string name, string description, string schema) => new()
    {
        Name = name,
        Description = description,
        InputSchema = JsonDocument.Parse(schema).RootElement.Clone(),
        Annotations = new ToolAnnotations { ReadOnlyHint = name == ListApplicationsTool },
    };

    private static CallToolResult Text(string text, bool isError) => new() { IsError = isError, Content = [new TextContentBlock { Text = text }] };

    private static JsonElement? Arguments(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0) return null;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in arguments)
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
