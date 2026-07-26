// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;

namespace NeoAstra.Rpc;

/// <summary>Owns opaque resources for one document session and closes them during teardown.</summary>
public sealed class NeoRpcResourceCollection : IAsyncDisposable
{
    private readonly NeoRpcSession _session;
    private readonly int _maximum;
    private readonly ConcurrentDictionary<string, IAsyncDisposable> _resources = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _nextId;
    private int _disposed;

    internal NeoRpcResourceCollection(NeoRpcSession session, int maximum)
    {
        _session = session;
        _maximum = maximum;
    }

    /// <summary>Gets the number of resources currently owned by the session.</summary>
    public int Count => _resources.Count;

    /// <summary>Adds an asynchronously disposable resource to this document session.</summary>
    /// <param name="resource">The resource to own.</param>
    /// <returns>An opaque handle suitable for a generated DTO.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resource"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The resource limit is exhausted.</exception>
    /// <exception cref="ObjectDisposedException">The session is closed.</exception>
    public NeoRpcResourceHandle Add(IAsyncDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_gate)
        {
            if (_resources.Count >= _maximum) throw new InvalidOperationException("The session resource limit is exhausted.");
            var id = $"res-{Interlocked.Increment(ref _nextId):x}";
            if (!_resources.TryAdd(id, resource)) throw new InvalidOperationException("A generated resource ID collided.");
            return new NeoRpcResourceHandle(id);
        }
    }

    /// <summary>Adds a synchronously disposable resource to this document session.</summary>
    /// <param name="resource">The resource to own.</param>
    /// <returns>An opaque handle suitable for a generated DTO.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resource"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The resource limit is exhausted.</exception>
    /// <exception cref="ObjectDisposedException">The session is closed.</exception>
    public NeoRpcResourceHandle Add(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return Add(new DisposableAdapter(resource));
    }

    /// <summary>Closes one resource by opaque handle. Unknown handles are ignored.</summary>
    /// <param name="handle">The session-owned opaque handle.</param>
    /// <returns>A task representing resource disposal.</returns>
    /// <exception cref="ArgumentException">The handle is malformed.</exception>
    public ValueTask CloseAsync(NeoRpcResourceHandle handle)
    {
        NeoRpcValidation.ValidateId(handle.Id, nameof(handle));
        return CloseAsync(handle.Id);
    }

    /// <summary>Closes every remaining resource. Disposal failures are contained.</summary>
    /// <returns>A task representing teardown.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var resources = _resources.ToArray();
        _resources.Clear();
        foreach (var resource in resources)
        {
            try { await resource.Value.DisposeAsync().ConfigureAwait(false); }
            catch { }
        }
    }

    internal async ValueTask CloseAsync(string id)
    {
        if (_resources.TryRemove(id, out var resource))
        {
            try { await resource.DisposeAsync().ConfigureAwait(false); }
            catch { }
        }
    }

    private sealed class DisposableAdapter(IDisposable resource) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            resource.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
