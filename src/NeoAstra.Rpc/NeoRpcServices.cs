// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Rpc;

/// <summary>Creates a service from trusted immutable invocation context.</summary>
/// <typeparam name="TService">The explicit service type.</typeparam>
/// <param name="context">The trusted invocation context.</param>
/// <returns>The created service.</returns>
public delegate TService NeoRpcServiceFactory<out TService>(NeoRpcContext context) where TService : class;

/// <summary>Provides explicit AOT-safe service activation and lifetime ownership for generated registration.</summary>
/// <typeparam name="TService">The explicit service type.</typeparam>
public sealed class NeoRpcServiceActivator<TService> : INeoRpcServiceLifetimeOwner where TService : class
{
    private readonly NeoRpcServiceFactory<TService> _factory;
    private readonly NeoRpcServiceLifetime _lifetime;
    private readonly Dictionary<string, ScopeEntry> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScopeEntry> _views = new(StringComparer.Ordinal);
    private readonly object _lifecycleLock = new();
    private ScopeEntry? _singleton;
    private int _disposed;

    /// <summary>Initializes an explicit service activator.</summary>
    /// <param name="factory">The non-reflective service factory.</param>
    /// <param name="lifetime">The explicit ownership lifetime.</param>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lifetime"/> is undefined.</exception>
    public NeoRpcServiceActivator(NeoRpcServiceFactory<TService> factory, NeoRpcServiceLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (!Enum.IsDefined(lifetime)) throw new ArgumentOutOfRangeException(nameof(lifetime));
        _factory = factory;
        _lifetime = lifetime;
    }

    /// <summary>Invokes a callback with an instance resolved according to the explicit lifetime.</summary>
    /// <typeparam name="TResult">The callback result type.</typeparam>
    /// <param name="context">The immutable invocation context.</param>
    /// <param name="callback">The generated strongly typed callback.</param>
    /// <returns>The callback result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="callback"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The RPC host or scope is disposed.</exception>
    public async ValueTask<TResult> InvokeAsync<TResult>(NeoRpcContext context, Func<TService, ValueTask<TResult>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_lifetime == NeoRpcServiceLifetime.PerInvocation)
        {
            var transient = Create(context);
            try { return await callback(transient).ConfigureAwait(false); }
            finally { await DisposeServiceAsync(transient).ConfigureAwait(false); }
        }

        await using var lease = GetScope(context).Acquire(context);
        return await callback(lease.Service).ConfigureAwait(false);
    }

    /// <summary>Invokes a void callback with an instance resolved according to the explicit lifetime.</summary>
    /// <param name="context">The immutable invocation context.</param>
    /// <param name="callback">The generated strongly typed callback.</param>
    /// <returns>A task representing callback completion and transient disposal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="callback"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The RPC host or scope is disposed.</exception>
    public async ValueTask InvokeAsync(NeoRpcContext context, Func<TService, ValueTask> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        await InvokeAsync(context, async service => { await callback(service).ConfigureAwait(false); return true; }).ConfigureAwait(false);
    }

    async ValueTask INeoRpcServiceLifetimeOwner.CloseSessionAsync(string documentSessionId)
    {
        ScopeEntry? entry;
        lock (_lifecycleLock) _sessions.TryGetValue(documentSessionId, out entry);
        if (entry is null) return;
        try { await entry.CloseAsync().ConfigureAwait(false); }
        finally { lock (_lifecycleLock) if (_sessions.TryGetValue(documentSessionId, out var current) && ReferenceEquals(current, entry)) _sessions.Remove(documentSessionId); }
    }

    async ValueTask INeoRpcServiceLifetimeOwner.CloseViewAsync(string viewLabel)
    {
        ScopeEntry? entry;
        lock (_lifecycleLock) _views.TryGetValue(viewLabel, out entry);
        if (entry is null) return;
        try { await entry.CloseAsync().ConfigureAwait(false); }
        finally { lock (_lifecycleLock) if (_views.TryGetValue(viewLabel, out var current) && ReferenceEquals(current, entry)) _views.Remove(viewLabel); }
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        ScopeEntry[] entries;
        lock (_lifecycleLock)
        {
            entries = _sessions.Values.Concat(_views.Values).Append(_singleton).Where(static entry => entry is not null).Cast<ScopeEntry>().Distinct().ToArray();
            _sessions.Clear(); _views.Clear(); _singleton = null;
        }
        foreach (var entry in entries) await entry.CloseAsync().ConfigureAwait(false);
    }

    private ScopeEntry GetScope(NeoRpcContext context)
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _lifetime switch
            {
                NeoRpcServiceLifetime.ApplicationSingleton => _singleton ??= new ScopeEntry(this),
                NeoRpcServiceLifetime.PerView => GetOrAdd(_views, context.ViewLabel),
                NeoRpcServiceLifetime.PerDocumentSession => GetOrAdd(_sessions, context.DocumentSessionId),
                _ => throw new InvalidOperationException("Transient services are resolved only inside an invocation."),
            };
        }
    }

    private ScopeEntry GetOrAdd(Dictionary<string, ScopeEntry> scopes, string key)
    {
        if (scopes.TryGetValue(key, out var entry)) return entry;
        entry = new ScopeEntry(this);
        scopes.Add(key, entry);
        return entry;
    }

    private TService Create(NeoRpcContext context) => _factory(context) ?? throw new InvalidOperationException("An RPC service factory returned null.");

    private static async ValueTask DisposeServiceAsync(TService service)
    {
        if (service is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (service is IDisposable disposable) disposable.Dispose();
    }

    private sealed class ScopeEntry(NeoRpcServiceActivator<TService> owner)
    {
        private readonly object _lock = new();
        private TaskCompletionSource? _drained;
        private TService? _service;
        private int _active;
        private bool _closing;
        private Task? _closeTask;

        internal ServiceLease Acquire(NeoRpcContext context)
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_closing, owner);
                _service ??= owner.Create(context);
                _active++;
                return new ServiceLease(this, _service);
            }
        }

        internal ValueTask CloseAsync()
        {
            lock (_lock)
            {
                if (_closeTask is not null) return new(_closeTask);
                _closing = true;
                var service = _service;
                _service = null;
                Task? drained = null;
                if (_active != 0) { _drained = new(TaskCreationOptions.RunContinuationsAsynchronously); drained = _drained.Task; }
                _closeTask = Task.Run(() => CloseCoreAsync(service, drained));
                return new(_closeTask);
            }
        }

        private static async Task CloseCoreAsync(TService? service, Task? drained)
        {
            if (drained is not null) await drained.ConfigureAwait(false);
            if (service is not null) await DisposeServiceAsync(service).ConfigureAwait(false);
        }

        private void Release()
        {
            lock (_lock)
            {
                if (--_active == 0 && _closing) _drained?.TrySetResult();
            }
        }

        internal sealed class ServiceLease(ScopeEntry owner, TService service) : IAsyncDisposable
        {
            private int _disposed;
            internal TService Service { get; } = service;
            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release();
                return ValueTask.CompletedTask;
            }
        }
    }
}

internal interface INeoRpcServiceLifetimeOwner : IAsyncDisposable
{
    ValueTask CloseSessionAsync(string documentSessionId);
    ValueTask CloseViewAsync(string viewLabel);
}
