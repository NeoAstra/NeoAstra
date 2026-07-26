// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace NeoAstra.Rpc;

/// <summary>Handles a typed asynchronous RPC command.</summary>
/// <typeparam name="TRequest">The declared request DTO.</typeparam>
/// <typeparam name="TResult">The declared result DTO.</typeparam>
/// <param name="request">The deserialized request.</param>
/// <param name="context">The immutable invocation context.</param>
/// <param name="cancellationToken">The request cancellation token.</param>
/// <returns>The command result.</returns>
public delegate ValueTask<TResult> NeoRpcCommandHandler<TRequest, TResult>(TRequest request, NeoRpcContext context, CancellationToken cancellationToken);

/// <summary>Handles a typed asynchronous RPC command with no result value.</summary>
/// <typeparam name="TRequest">The declared request DTO.</typeparam>
/// <param name="request">The deserialized request.</param>
/// <param name="context">The immutable invocation context.</param>
/// <param name="cancellationToken">The request cancellation token.</param>
/// <returns>A task representing command completion.</returns>
public delegate ValueTask NeoRpcVoidCommandHandler<TRequest>(TRequest request, NeoRpcContext context, CancellationToken cancellationToken);

/// <summary>Handles a typed bounded streaming RPC command.</summary>
/// <typeparam name="TRequest">The declared request DTO.</typeparam>
/// <typeparam name="TItem">The declared channel item DTO.</typeparam>
/// <param name="request">The deserialized request.</param>
/// <param name="context">The immutable invocation context.</param>
/// <param name="cancellationToken">The request cancellation token.</param>
/// <returns>The declared bounded channel source.</returns>
public delegate ValueTask<NeoRpcChannel<TItem>> NeoRpcChannelCommandHandler<TRequest, TItem>(TRequest request, NeoRpcContext context, CancellationToken cancellationToken);

/// <summary>Builds an immutable explicit RPC command and event registry.</summary>
public sealed class NeoRpcBuilder
{
    private readonly NeoRpcOptions _options;
    private readonly Dictionary<string, CommandDescriptor> _commands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EventDescriptor> _events = new(StringComparer.Ordinal);
    private readonly List<IEventHandle> _eventHandles = [];
    private readonly List<INeoRpcServiceLifetimeOwner> _serviceLifetimes = [];
    private bool _built;

    /// <summary>Initializes a builder with safe defaults.</summary>
    public NeoRpcBuilder() : this(new NeoRpcOptions()) { }

    /// <summary>Initializes a builder with explicit options.</summary>
    /// <param name="options">The host policy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A configured bound is unsafe.</exception>
    public NeoRpcBuilder(NeoRpcOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.CloneValidated();
    }

    /// <summary>Adds a generated service registration.</summary>
    /// <param name="registration">The generated registration.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registration"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The builder was already built.</exception>
    public NeoRpcBuilder Add(INeoRpcServiceRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        EnsureMutable();
        registration.Register(this);
        return this;
    }

    /// <summary>Adds an explicit service activator so the host can close its view and session scopes.</summary>
    /// <typeparam name="TService">The explicit service type.</typeparam>
    /// <param name="activator">The non-reflective service activator used by generated command handlers.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="activator"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The builder was already built.</exception>
    public NeoRpcBuilder AddServiceActivator<TService>(NeoRpcServiceActivator<TService> activator) where TService : class
    {
        ArgumentNullException.ThrowIfNull(activator);
        EnsureMutable();
        _serviceLifetimes.Add(activator);
        return this;
    }

    /// <summary>Registers one typed command using source-generated serializer metadata.</summary>
    /// <typeparam name="TRequest">The request DTO type.</typeparam>
    /// <typeparam name="TResult">The result DTO type.</typeparam>
    /// <param name="command">The explicit stable command wire name.</param>
    /// <param name="handler">The application handler.</param>
    /// <param name="requestTypeInfo">Source-generated request metadata.</param>
    /// <param name="resultTypeInfo">Source-generated result metadata.</param>
    /// <param name="options">Optional command policy.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">The command is malformed or duplicated.</exception>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The builder was already built.</exception>
    public NeoRpcBuilder AddCommand<TRequest, TResult>(
        string command,
        NeoRpcCommandHandler<TRequest, TResult> handler,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResult> resultTypeInfo,
        NeoRpcCommandOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(requestTypeInfo);
        ArgumentNullException.ThrowIfNull(resultTypeInfo);
        return AddDescriptor(command, new CommandDescriptor<TRequest, TResult>(command, handler, requestTypeInfo, resultTypeInfo, Validate(options)));
    }

    /// <summary>Registers one typed command with no result value.</summary>
    /// <typeparam name="TRequest">The request DTO type.</typeparam>
    /// <param name="command">The explicit stable command wire name.</param>
    /// <param name="handler">The application handler.</param>
    /// <param name="requestTypeInfo">Source-generated request metadata.</param>
    /// <param name="options">Optional command policy.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">The command is malformed or duplicated.</exception>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The builder was already built.</exception>
    public NeoRpcBuilder AddCommand<TRequest>(
        string command,
        NeoRpcVoidCommandHandler<TRequest> handler,
        JsonTypeInfo<TRequest> requestTypeInfo,
        NeoRpcCommandOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(requestTypeInfo);
        return AddDescriptor(command, new VoidCommandDescriptor<TRequest>(command, handler, requestTypeInfo, Validate(options)));
    }

    /// <summary>Registers one typed bounded JSON streaming command.</summary>
    /// <typeparam name="TRequest">The request DTO type.</typeparam>
    /// <typeparam name="TItem">The channel item DTO type.</typeparam>
    /// <param name="command">The explicit stable command wire name.</param>
    /// <param name="handler">The application channel handler.</param>
    /// <param name="requestTypeInfo">Source-generated request metadata.</param>
    /// <param name="options">Optional command policy.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">The command is malformed or duplicated.</exception>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The builder was already built.</exception>
    public NeoRpcBuilder AddChannelCommand<TRequest, TItem>(
        string command,
        NeoRpcChannelCommandHandler<TRequest, TItem> handler,
        JsonTypeInfo<TRequest> requestTypeInfo,
        NeoRpcCommandOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(requestTypeInfo);
        return AddDescriptor(command, new ChannelCommandDescriptor<TRequest, TItem>(command, handler, requestTypeInfo, Validate(options)));
    }

    /// <summary>Declares a typed event and obtains its publisher.</summary>
    /// <typeparam name="T">The event DTO type.</typeparam>
    /// <param name="eventName">The explicit stable event wire name.</param>
    /// <param name="typeInfo">Source-generated event metadata.</param>
    /// <param name="options">Optional subscription and overflow policy.</param>
    /// <returns>A publisher bound when the host is built.</returns>
    /// <exception cref="ArgumentException">The event name is malformed or duplicated.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="typeInfo"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The builder was already built.</exception>
    public NeoRpcEvent<T> AddEvent<T>(string eventName, JsonTypeInfo<T> typeInfo, NeoRpcEventOptions? options = null)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(typeInfo);
        ValidateWireName(eventName, nameof(eventName));
        var descriptor = new EventDescriptor(eventName, options?.CloneValidated() ?? new NeoRpcEventOptions());
        if (!_events.TryAdd(eventName, descriptor)) throw new ArgumentException($"The event '{eventName}' is already registered.", nameof(eventName));
        var handle = new NeoRpcEvent<T>(descriptor, typeInfo);
        _eventHandles.Add(handle);
        return handle;
    }

    /// <summary>Builds the immutable RPC host.</summary>
    /// <returns>A host that owns sessions and dispatch state.</returns>
    /// <exception cref="InvalidOperationException">The builder was already built.</exception>
    public NeoRpcHost Build()
    {
        EnsureMutable();
        _built = true;
        var host = new NeoRpcHost(_options, _commands, _events, _serviceLifetimes);
        foreach (var handle in _eventHandles) handle.Bind(host);
        return host;
    }

    private NeoRpcBuilder AddDescriptor(string command, CommandDescriptor descriptor)
    {
        EnsureMutable();
        ValidateWireName(command, nameof(command));
        if (!_commands.TryAdd(command, descriptor)) throw new ArgumentException($"The command '{command}' is already registered.", nameof(command));
        return this;
    }

    private NeoRpcCommandOptions Validate(NeoRpcCommandOptions? options) => (options ?? new NeoRpcCommandOptions()).CloneValidated();

    private void ValidateWireName(string value, string parameterName)
    {
        if (!NeoRpcValidation.IsWireName(value, _options.MaximumWireNameLength))
            throw new ArgumentException("A wire name must be non-empty ASCII letters, digits, '.', '-', '_', or ':' within the configured bound.", parameterName);
    }

    private void EnsureMutable()
    {
        if (_built) throw new InvalidOperationException("An RPC builder can be built only once.");
    }
}

/// <summary>Publishes one declared typed event to authorized active subscriptions.</summary>
/// <typeparam name="T">The declared event DTO.</typeparam>
public sealed class NeoRpcEvent<T> : IEventHandle
{
    private readonly EventDescriptor _descriptor;
    private readonly JsonTypeInfo<T> _typeInfo;
    private NeoRpcHost? _host;

    internal NeoRpcEvent(EventDescriptor descriptor, JsonTypeInfo<T> typeInfo)
    {
        _descriptor = descriptor;
        _typeInfo = typeInfo;
    }

    /// <summary>Publishes an event to every subscribed session.</summary>
    /// <param name="value">The event DTO.</param>
    /// <param name="cancellationToken">Cancels publication before enqueue.</param>
    /// <returns>The number of subscriptions that accepted the event.</returns>
    /// <exception cref="InvalidOperationException">The builder has not been built.</exception>
    /// <exception cref="JsonException">The value does not satisfy its generated JSON contract.</exception>
    public ValueTask<int> PublishAsync(T value, CancellationToken cancellationToken = default)
        => PublishAsync(value, static _ => true, cancellationToken);

    /// <summary>Publishes an event to sessions selected from trusted immutable context.</summary>
    /// <param name="value">The event DTO.</param>
    /// <param name="recipient">A trusted host-side recipient predicate.</param>
    /// <param name="cancellationToken">Cancels publication before enqueue.</param>
    /// <returns>The number of subscriptions that accepted the event.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="recipient"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The builder has not been built.</exception>
    /// <exception cref="JsonException">The value does not satisfy its generated JSON contract.</exception>
    public ValueTask<int> PublishAsync(T value, Func<NeoRpcContext, bool> recipient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        cancellationToken.ThrowIfCancellationRequested();
        var host = Volatile.Read(ref _host) ?? throw new InvalidOperationException("The event publisher is unavailable until its builder is built.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _typeInfo);
        return host.PublishAsync(_descriptor, bytes, recipient, cancellationToken);
    }

    void IEventHandle.Bind(NeoRpcHost host) => Volatile.Write(ref _host, host);
}

internal interface IEventHandle { void Bind(NeoRpcHost host); }

internal sealed record EventDescriptor(string Name, NeoRpcEventOptions Options);

internal abstract class CommandDescriptor
{
    protected CommandDescriptor(string name, NeoRpcCommandOptions options) { Name = name; Options = options; }
    internal string Name { get; }
    internal NeoRpcCommandOptions Options { get; }
    internal abstract object DeserializeRequest(JsonElement args);
    internal abstract ValueTask<object?> InvokeHandlerAsync(object request, NeoRpcContext context, CancellationToken cancellationToken);
    internal abstract CommandResult SerializeResult(object? result);
}

internal sealed class CommandDescriptor<TRequest, TResult>(
    string name,
    NeoRpcCommandHandler<TRequest, TResult> handler,
    JsonTypeInfo<TRequest> requestTypeInfo,
    JsonTypeInfo<TResult> resultTypeInfo,
    NeoRpcCommandOptions options) : CommandDescriptor(name, options)
{
    internal override object DeserializeRequest(JsonElement args) => args.Deserialize(requestTypeInfo) ?? throw new JsonException("An RPC request cannot be JSON null.");
    internal override async ValueTask<object?> InvokeHandlerAsync(object request, NeoRpcContext context, CancellationToken cancellationToken) => await handler((TRequest)request, context, cancellationToken).ConfigureAwait(false);
    internal override CommandResult SerializeResult(object? result) => new ValueCommandResult(JsonSerializer.SerializeToUtf8Bytes((TResult)result!, resultTypeInfo));
}

internal sealed class VoidCommandDescriptor<TRequest>(
    string name,
    NeoRpcVoidCommandHandler<TRequest> handler,
    JsonTypeInfo<TRequest> requestTypeInfo,
    NeoRpcCommandOptions options) : CommandDescriptor(name, options)
{
    internal override object DeserializeRequest(JsonElement args) => args.Deserialize(requestTypeInfo) ?? throw new JsonException("An RPC request cannot be JSON null.");
    internal override async ValueTask<object?> InvokeHandlerAsync(object request, NeoRpcContext context, CancellationToken cancellationToken) { await handler((TRequest)request, context, cancellationToken).ConfigureAwait(false); return null; }
    internal override CommandResult SerializeResult(object? result) => VoidCommandResult.Instance;
}

internal sealed class ChannelCommandDescriptor<TRequest, TItem>(
    string name,
    NeoRpcChannelCommandHandler<TRequest, TItem> handler,
    JsonTypeInfo<TRequest> requestTypeInfo,
    NeoRpcCommandOptions options) : CommandDescriptor(name, options)
{
    internal override object DeserializeRequest(JsonElement args) => args.Deserialize(requestTypeInfo) ?? throw new JsonException("An RPC request cannot be JSON null.");
    internal override async ValueTask<object?> InvokeHandlerAsync(object request, NeoRpcContext context, CancellationToken cancellationToken) => await handler((TRequest)request, context, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("A channel command returned null.");
    internal override CommandResult SerializeResult(object? result) => new ChannelCommandResult<TItem>((NeoRpcChannel<TItem>)result!);
}

internal abstract record CommandResult;
internal sealed record ValueCommandResult(byte[] Json) : CommandResult;
internal sealed record VoidCommandResult : CommandResult { internal static VoidCommandResult Instance { get; } = new(); }
internal interface IChannelCommandResult
{
    ValueTask StartAsync(NeoRpcSession session, string channelId, CancellationToken cancellationToken);
}

internal sealed record ChannelCommandResult<T>(NeoRpcChannel<T> Channel) : CommandResult, IChannelCommandResult
{
    public ValueTask StartAsync(NeoRpcSession session, string channelId, CancellationToken cancellationToken)
        => session.StartChannelAsync(channelId, Channel, cancellationToken);
}
