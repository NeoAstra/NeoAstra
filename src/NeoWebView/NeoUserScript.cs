// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoWebView;

/// <summary>Represents a persistent document script registered with a browser view.</summary>
public sealed class NeoUserScript : IAsyncDisposable
{
    private readonly NeoWebView _webView;
    private readonly string _identifier;
    private int _disposed;

    internal NeoUserScript(NeoWebView webView, string identifier)
    {
        _webView = webView;
        _identifier = identifier;
    }

    /// <summary>Removes the script from future documents.</summary>
    /// <returns>A task that completes after removal on the application UI thread.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

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
