// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;

namespace NeoAstra.Rpc;

/// <summary>Owns opaque resources for one document session and closes them during teardown.</summary>
public sealed class NeoRpcResourceCollection : IAsyncDisposable
{
    private readonly NeoRpcSession _session;
    private readonly int _maximum;
    private readonly ConcurrentDictionary<string, ResourceEntry> _resources = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _nextId;
    private long _bytes;
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
    /// <exception cref="NeoRpcException">The resource limit is exhausted.</exception>
    /// <exception cref="ObjectDisposedException">The session is closed.</exception>
    public NeoRpcResourceHandle Add(IAsyncDisposable resource) => Add(resource, 0);

    /// <summary>Adds an asynchronously disposable resource with its conservative retained-byte estimate.</summary>
    /// <param name="resource">The resource to own.</param><param name="estimatedBytes">Non-negative retained bytes charged to session and application limits.</param>
    /// <returns>An opaque session-owned handle.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resource"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="estimatedBytes"/> is negative.</exception>
    /// <exception cref="NeoRpcException">A session, view, application, or byte limit is exhausted.</exception>
    /// <exception cref="ObjectDisposedException">The session is closed.</exception>
    public NeoRpcResourceHandle Add(IAsyncDisposable resource, long estimatedBytes)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (estimatedBytes < 0) throw new ArgumentOutOfRangeException(nameof(estimatedBytes));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_resources.Count >= _maximum) throw new NeoRpcException(NeoRpcErrorCodes.TooManyRequests, "The session resource limit is exhausted.", retryable: true);
            if (!_session.TryAddResource(estimatedBytes, _bytes)) throw new NeoRpcException(NeoRpcErrorCodes.TooManyRequests, "A resource count or byte limit is exhausted.", retryable: true);
            var id = $"res-{Interlocked.Increment(ref _nextId):x}";
            if (!_resources.TryAdd(id, new(resource, estimatedBytes))) { _session.RemoveResource(estimatedBytes); throw new InvalidOperationException("A generated resource ID collided."); }
            _bytes += estimatedBytes;
            return new NeoRpcResourceHandle(id);
        }
    }

    /// <summary>Adds a synchronously disposable resource to this document session.</summary>
    /// <param name="resource">The resource to own.</param>
    /// <returns>An opaque handle suitable for a generated DTO.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resource"/> is <see langword="null"/>.</exception>
    /// <exception cref="NeoRpcException">The resource limit is exhausted.</exception>
    /// <exception cref="ObjectDisposedException">The session is closed.</exception>
    public NeoRpcResourceHandle Add(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return Add(new DisposableAdapter(resource), 0);
    }

    /// <summary>Adds a synchronously disposable resource with its conservative retained-byte estimate.</summary>
    /// <param name="resource">The resource to own.</param><param name="estimatedBytes">Non-negative retained bytes charged to session and application limits.</param>
    /// <returns>An opaque session-owned handle.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resource"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="estimatedBytes"/> is negative.</exception>
    /// <exception cref="NeoRpcException">A session, view, application, or byte limit is exhausted.</exception>
    /// <exception cref="ObjectDisposedException">The session is closed.</exception>
    public NeoRpcResourceHandle Add(IDisposable resource, long estimatedBytes)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return Add(new DisposableAdapter(resource), estimatedBytes);
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
        KeyValuePair<string, ResourceEntry>[] resources;
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            resources = _resources.ToArray();
        }
        foreach (var resource in resources) await CloseEntryAsync(resource.Key, resource.Value).ConfigureAwait(false);
    }

    internal async ValueTask CloseAsync(string id)
    {
        if (!_resources.TryGetValue(id, out var resource)) return;
        await CloseEntryAsync(id, resource).ConfigureAwait(false);
    }

    private async ValueTask CloseEntryAsync(string id, ResourceEntry resource)
    {
        if (!resource.TryBeginClose())
        {
            await resource.Completion.ConfigureAwait(false);
            return;
        }
        lock (_gate) _bytes -= resource.Bytes;
        _session.RemoveResource(resource.Bytes);
        try { await resource.Resource.DisposeAsync().ConfigureAwait(false); }
        catch { }
        finally
        {
            _resources.TryRemove(new KeyValuePair<string, ResourceEntry>(id, resource));
            resource.Complete();
        }
    }

    private sealed class ResourceEntry(IAsyncDisposable resource, long bytes)
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _closing;
        internal IAsyncDisposable Resource { get; } = resource;
        internal long Bytes { get; } = bytes;
        internal Task Completion => _completion.Task;
        internal bool TryBeginClose() => Interlocked.CompareExchange(ref _closing, 1, 0) == 0;
        internal void Complete() => _completion.TrySetResult();
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
