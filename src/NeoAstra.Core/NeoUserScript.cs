// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra;

/// <summary>Represents a persistent document script registered with a browser view.</summary>
public sealed class NeoUserScript : IAsyncDisposable
{
    private readonly NeoAstra _webView;
    private readonly string _identifier;
    private int _disposed;

    internal NeoUserScript(NeoAstra webView, string identifier, string? worldName = null, string? bindingName = null,
        EventHandler<NeoScriptMessageReceivedEventArgs>? messageHandler = null)
    {
        _webView = webView;
        _identifier = identifier;
        WorldName = worldName;
        BindingName = bindingName;
        MessageReceived = messageHandler;
    }

    /// <summary>Gets the name of the isolated world of the script, or <see langword="null"/> for a script of the page.</summary>
    public string? WorldName { get; }

    /// <summary>Gets the name of the binding of the script (<see cref="NeoScriptOptions.BindingName"/>), or <see langword="null"/> for none.</summary>
    public string? BindingName { get; }

    /// <summary>Occurs on the application UI thread when the script posts a string through its binding.</summary>
    /// <remarks>
    /// Only the script's own world reaches the binding, and only its main frame posts through it: the page cannot raise this
    /// event, and its messages raise <see cref="NeoAstra.MessageReceived"/> instead. Nothing is raised once the script is disposed.
    /// </remarks>
    public event EventHandler<NeoScriptMessageReceivedEventArgs>? MessageReceived;

    /// <summary>Delivers a string to the binding of the script in the current main-frame document, as a <c>message</c> event.</summary>
    /// <param name="message">The string, at most <see cref="NeoAstraOptions.MaximumMessageSize"/> bytes of UTF-8.</param>
    /// <param name="cancellationToken">Cancels the call before it is submitted.</param>
    /// <returns>A task that completes once the backend accepted the message.</returns>
    /// <remarks>
    /// The message is data of the event, never script. On Android the WebView answers a world only through the last message of
    /// its main frame in the current document, so the script posts first in each document; a navigation forgets that channel.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not valid UTF-16 or is larger than the maximum message size of the view.</exception>
    /// <exception cref="InvalidOperationException">The script has no binding, or (Android) its world has not posted in the current document.</exception>
    /// <exception cref="NotSupportedException">The backend or the loaded native library has no script channels.</exception>
    /// <exception cref="ObjectDisposedException">The script or its view was disposed.</exception>
    public ValueTask PostMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (BindingName is null) throw new InvalidOperationException("The script has no binding (NeoScriptOptions.BindingName).");
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = _webView.Environment.Application.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            Post(message);
            return ValueTask.CompletedTask;
        }

        return dispatcher.InvokeAsync(() => Post(message), cancellationToken);
    }

    private void Post(string message)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _webView.PostScriptMessage(_identifier, message);
    }

    internal void RaiseMessageReceived(NeoScriptMessageReceivedEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { MessageReceived?.Invoke(this, args); } catch { }
    }

    /// <summary>Removes the script from future documents, with its binding.</summary>
    /// <returns>A task that completes after removal on the application UI thread.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // No message of the binding reaches the script once it is disposed, whatever the engine still delivers.
        _webView.UnregisterScriptChannel(this);
        MessageReceived = null;
        try
        {
            var dispatcher = _webView.Environment.Application.Dispatcher;
            if (dispatcher.CheckAccess())
            {
                _webView.RemoveScript(_identifier);
            }
            else
            {
                await dispatcher.InvokeAsync(() => _webView.RemoveScript(_identifier));
            }
        }
        catch (ObjectDisposedException)
        {
            // Releasing the owning view implicitly removes all of its scripts.
        }
    }
}

/// <summary>
/// The scripts of a view that have a binding, by the world and the binding that the native library registered for them: a
/// message of a binding reaches only the script that owns it. The native library refuses a second script with the same binding in
/// a world, so a name pair belongs to one script at a time.
/// </summary>
/// <typeparam name="T">The owner of a binding.</typeparam>
internal sealed class NeoScriptChannelRegistry<T>
    where T : class
{
    private readonly Lock _sync = new();
    private readonly Dictionary<(string World, string Binding), T> _channels = [];

    internal bool TryAdd(string world, string binding, T owner)
    {
        lock (_sync) return _channels.TryAdd((world, binding), owner);
    }

    // Removes the binding only for its owner, so that a later script with the same names keeps its own.
    internal void Remove(string world, string binding, T owner)
    {
        lock (_sync)
        {
            if (_channels.TryGetValue((world, binding), out var current) && ReferenceEquals(current, owner)) _channels.Remove((world, binding));
        }
    }

    internal T? Find(string world, string binding)
    {
        lock (_sync) return _channels.GetValueOrDefault((world, binding));
    }

    internal void Clear()
    {
        lock (_sync) _channels.Clear();
    }
}
