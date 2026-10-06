// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra;

/// <summary>
/// Browser automation for the views of an application, with the operations of Chrome DevTools MCP: pages, text
/// snapshots, input, navigation, script evaluation, screenshots, and console and network logs.
/// </summary>
/// <remarks>
/// <para>
/// Creating an instance turns automation on for the application; nothing is injected into a view before that. Each
/// view becomes a <see cref="NeoAutomationPage"/>. The typed operations are on the page, and <see cref="Tools"/> with
/// <see cref="CallToolAsync(string, System.Text.Json.JsonElement, CancellationToken)"/> offer the same operations as named tools with JSON arguments, ready to be surfaced
/// through a Model Context Protocol server. NeoAstra does not open such a server itself.
/// </para>
/// <para>
/// Automation gives its caller full control over the pages it drives, and through them over whatever those pages may
/// ask of the application. Create it only for a caller that the application trusts as much as its own user, such as
/// a development or test build.
/// </para>
/// </remarks>
public sealed partial class NeoAutomation : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly List<NeoAutomationPage> _pages = [];
    private readonly Dictionary<(NeoEnvironment Environment, string Name), NeoProfile> _isolatedProfiles = [];
    private readonly UiContext _context;
    private NeoAutomationPage? _selected;
    private NeoAutomationNewPageRequest? _pageBeingCreated;
    private string? _selectionNote;
    private int _nextPageId = 1;
    private int _disposed;

    /// <summary>Turns browser automation on for an application.</summary>
    /// <param name="application">The application whose views become pages.</param>
    /// <param name="options">The automation options, or <see langword="null"/> for the defaults.</param>
    /// <remarks>
    /// Create the instance before the views navigate where that is possible: a document that is already loaded keeps
    /// the console messages and requests it produced before, and on Windows its JavaScript dialogs still open as
    /// WebView2 dialogs until the view loads another document.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="application"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An allowed directory of the options is empty.</exception>
    public NeoAutomation(NeoApplication application, NeoAutomationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        Application = application;
        Options = (options ?? new NeoAutomationOptions()).Clone();
        _context = new UiContext(application.Dispatcher);
        _tools = CreateTools(Options);
        application.ViewRegistered += OnViewRegistered;
        if (application.Dispatcher.CheckAccess()) AttachExistingViews();
        else Post(AttachExistingViews);
    }

    /// <summary>Gets the application whose views this instance drives.</summary>
    public NeoApplication Application { get; }

    /// <summary>Gets the open pages, in the order they were first seen.</summary>
    public IReadOnlyList<NeoAutomationPage> Pages
    {
        get { lock (_sync) return _pages.ToArray(); }
    }

    /// <summary>Gets the page that tool calls act on when they name none, or <see langword="null"/> when no page is open.</summary>
    public NeoAutomationPage? SelectedPage
    {
        get { lock (_sync) return _selected; }
    }

    internal NeoAutomationOptions Options { get; }

    /// <summary>Finds the page with an identifier.</summary>
    /// <param name="pageId">The identifier of the page.</param>
    /// <returns>The page.</returns>
    /// <exception cref="NeoAutomationException">No open page has the identifier.</exception>
    public NeoAutomationPage GetPage(int pageId)
    {
        lock (_sync)
        {
            foreach (var page in _pages)
            {
                if (page.Id == pageId) return page;
            }
        }

        throw new NeoAutomationException("no-page", "No page found");
    }

    /// <summary>Finds the page of a view.</summary>
    /// <param name="view">The view.</param>
    /// <returns>The page, or <see langword="null"/> when automation does not drive the view.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="view"/> is <see langword="null"/>.</exception>
    public NeoAutomationPage? FindPage(NeoAstra view)
    {
        ArgumentNullException.ThrowIfNull(view);
        lock (_sync)
        {
            foreach (var page in _pages)
            {
                if (ReferenceEquals(page.View, view)) return page;
            }
        }

        return null;
    }

    /// <summary>Makes a page the one that tool calls act on when they name none.</summary>
    /// <param name="page">The page to select.</param>
    /// <param name="bringToFront">Whether to activate the window of the page.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes after the page was selected.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="page"/> is <see langword="null"/>.</exception>
    /// <exception cref="NeoAutomationException">The page is closed or belongs to another automation.</exception>
    public ValueTask SelectPageAsync(NeoAutomationPage page, bool bringToFront = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new ValueTask(InvokeAsync(() =>
        {
            lock (_sync)
            {
                if (!_pages.Contains(page)) throw new NeoAutomationException("no-page", "No page found");
                _selected = page;
                _selectionNote = null;
            }

            if (bringToFront && page.View.OwnedWindow is { IsClosed: false } window)
            {
                if (!window.IsVisible) window.Show();
                window.Activate();
            }

            return Task.FromResult(true);
        }, cancellationToken).AsTask());
    }

    /// <summary>Opens a page, loads an address in it, and selects it.</summary>
    /// <param name="url">The address to load.</param>
    /// <param name="background">Whether the page opens without being brought to the front.</param>
    /// <param name="isolatedContext">The name of an isolated browser context for the page, or <see langword="null"/>.</param>
    /// <param name="timeout">How long to wait for the document, or <see langword="null"/> for <see cref="NeoAutomationOptions.NavigationTimeout"/>.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The new page. A document that fails to load still leaves the page open.</returns>
    /// <exception cref="ArgumentException"><paramref name="url"/> is not absolute.</exception>
    /// <exception cref="NeoAutomationException">The address is not allowed, or the page could not be created.</exception>
    public ValueTask<NeoAutomationPage> NewPageAsync(Uri url, bool background = false, string? isolatedContext = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ValidateNavigationUrl(url);
        return InvokeAsync(async () =>
        {
            ThrowIfDisposed();
            var request = new NeoAutomationNewPageRequest(url, background, string.IsNullOrEmpty(isolatedContext) ? null : isolatedContext);
            var create = Options.NewPageHandler ?? CreateDefaultPageAsync;
            NeoAstra view;
            // The view registers itself while it is created; it is told then which request it answers.
            _pageBeingCreated = request;
            try { view = await create(request, cancellationToken) ?? throw new NeoAutomationException("no-page", "The new-page handler of the application returned no view."); }
            finally { _pageBeingCreated = null; }
            var page = FindPage(view) ?? AddPage(view, request.IsolatedContext, ignoreFilter: true) ?? throw new ObjectDisposedException(nameof(NeoAutomation));
            lock (_sync)
            {
                _selected = page;
                _selectionNote = null;
            }

            await page.NavigateAsync(new NeoAutomationNavigationOptions { Url = url, Timeout = timeout }, cancellationToken);
            return page;
        }, cancellationToken);
    }

    /// <summary>Closes a page. The last open page cannot be closed.</summary>
    /// <param name="page">The page to close.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns><see langword="true"/> when the page was asked to close, or <see langword="false"/> when it is the last one.</returns>
    /// <remarks>
    /// The window of the page is asked to close, as its user would ask, so the application can still refuse. A view
    /// in a window that NeoAstra does not own is disposed instead.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="page"/> is <see langword="null"/>.</exception>
    /// <exception cref="NeoAutomationException">The page is closed already or belongs to another automation.</exception>
    public ValueTask<bool> ClosePageAsync(NeoAutomationPage page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        return InvokeAsync(async () =>
        {
            lock (_sync)
            {
                if (!_pages.Contains(page)) throw new NeoAutomationException("no-page", "No page found");
                if (_pages.Count == 1) return false;
            }

            if (page.View.OwnedWindow is { IsClosed: false } window)
            {
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnClosed(object? sender, EventArgs args) => closed.TrySetResult();
                window.Closed += OnClosed;
                try
                {
                    window.Close();
                    // An application may refuse or delay the close; the page then stays listed.
                    await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
                }
                finally
                {
                    window.Closed -= OnClosed;
                }

                if (closed.Task.IsCompleted && !page.IsClosed) await page.View.DisposeAsync();
            }
            else
            {
                await page.View.DisposeAsync();
            }

            return true;
        }, cancellationToken);
    }

    /// <summary>Turns automation off: the views are no longer driven, and their own dialog handlers apply again.</summary>
    /// <returns>A task that completes after the pages were released.</returns>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        Application.ViewRegistered -= OnViewRegistered;
        void Release()
        {
            NeoAutomationPage[] pages;
            lock (_sync)
            {
                pages = _pages.ToArray();
                _pages.Clear();
                _selected = null;
            }

            foreach (var page in pages) page.Detach();
        }

        if (Application.Dispatcher.CheckAccess())
        {
            Release();
            return ValueTask.CompletedTask;
        }

        try { return Application.Dispatcher.InvokeAsync(Release); }
        catch (ObjectDisposedException) { return ValueTask.CompletedTask; }
    }

    // -------------------------------------------------------------------------------------------------------------
    // Pages
    // -------------------------------------------------------------------------------------------------------------

    private void AttachExistingViews()
    {
        foreach (var view in Application.GetRegisteredViews()) AddPage(view, null, ignoreFilter: false);
    }

    private void OnViewRegistered(NeoAstra view)
    {
        // A view that automation itself asked for is a page whatever the filter says.
        if (_pageBeingCreated is { } request) AddPage(view, request.IsolatedContext, ignoreFilter: true);
        else AddPage(view, null, ignoreFilter: false);
    }

    private NeoAutomationPage? AddPage(NeoAstra view, string? isolatedContext, bool ignoreFilter)
    {
        if (Volatile.Read(ref _disposed) != 0) return null;
        if (FindPage(view) is { } existing) return existing;
        if (!ignoreFilter)
        {
            try { if (Options.PageFilter is { } filter && !filter(view)) return null; }
            catch { return null; }
        }

        NeoAutomationPage page;
        lock (_sync)
        {
            page = new NeoAutomationPage(this, view, _nextPageId++, isolatedContext);
            _pages.Add(page);
            _selected ??= page;
        }

        _context.Run(page.Attach);
        return page;
    }

    internal void RemovePage(NeoAutomationPage page)
    {
        lock (_sync)
        {
            if (!_pages.Remove(page)) return;
            if (ReferenceEquals(_selected, page))
            {
                _selected = _pages.Count != 0 ? _pages[0] : null;
                _selectionNote = _selected is null
                    ? "Note: the previously selected page was closed."
                    : $"Note: the previously selected page was closed. Page {_selected.Id} is now selected.";
            }
        }

        page.Detach();
    }

    internal string? TakeSelectionNote()
    {
        lock (_sync)
        {
            var note = _selectionNote;
            _selectionNote = null;
            return note;
        }
    }

    internal bool IsSelected(NeoAutomationPage page)
    {
        lock (_sync) return ReferenceEquals(_selected, page);
    }

    /// <summary>Refuses an address that automation may not open.</summary>
    internal void ValidateNavigationUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri) throw new ArgumentException($"Invalid URL: \"{url.OriginalString}\". URLs must be absolute.", nameof(url));
        if (!Options.AllowScriptEvaluation && url.Scheme is "javascript" or "data" or "vbscript")
        {
            throw new NeoAutomationException("not-allowed", $"Navigating to {url.Scheme}: URLs is not allowed when JavaScript evaluation is disabled.");
        }
    }

    // Opens a plain window on the environment of the selected page. The view has no bridge to the host, so the new
    // page can ask nothing of the application.
    private async ValueTask<NeoAstra> CreateDefaultPageAsync(NeoAutomationNewPageRequest request, CancellationToken cancellationToken)
    {
        var template = SelectedPage ?? throw new NeoAutomationException("no-page",
            "A new page takes its browser environment from an open page, and none is open. Set NeoAutomationOptions.NewPageHandler to create pages.");
        var environment = template.View.Environment;
        var profile = template.View.Profile;
        if (request.IsolatedContext is { } name)
        {
            if (!_isolatedProfiles.TryGetValue((environment, name), out profile))
            {
                // Pages of one isolated context share an ephemeral profile, and no other page uses it.
                var profileName = "neoastra-automation-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)), 0, 12);
                profile = await environment.CreateProfileAsync(new NeoProfileOptions { Name = profileName, IsEphemeral = true }, cancellationToken);
                _isolatedProfiles[(environment, name)] = profile;
            }
        }

        var size = template.View.OwnedWindow is { IsClosed: false } source ? source.ClientSize : new NeoSize(1024, 768);
        var window = Application.CreateWindow(new NeoWindowOptions { Title = "NeoAstra", Width = Math.Max(size.Width, 320), Height = Math.Max(size.Height, 240) });
        NeoAstra view;
        try
        {
            view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), new NeoAstraOptions { Profile = profile }, cancellationToken);
        }
        catch
        {
            await window.DisposeAsync();
            throw;
        }

        // The window and its view belong to automation: they go away together.
        window.Closed += (_, _) => _ = RunDetached(async () =>
        {
            await view.DisposeAsync();
            await window.DisposeAsync();
        });
        window.Show();
        if (!request.Background) window.Activate();
        return view;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    // -------------------------------------------------------------------------------------------------------------
    // UI thread
    // -------------------------------------------------------------------------------------------------------------

    /// <summary>Runs an asynchronous operation on the UI thread, where every continuation of it also runs.</summary>
    internal ValueTask<T> InvokeAsync<T>(Func<Task<T>> body, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled<T>(cancellationToken);
        if (Application.Dispatcher.CheckAccess()) return new ValueTask<T>(_context.Run(body));
        return new ValueTask<T>(Hop());

        async Task<T> Hop()
        {
            var started = await Application.Dispatcher.InvokeAsync(() => _context.Run(body), cancellationToken).ConfigureAwait(false);
            return await started.ConfigureAwait(false);
        }
    }

    /// <summary>Starts an operation on the UI thread that nobody waits for. It must be called on that thread.</summary>
    internal Task RunDetached(Func<Task> body) => _context.Run(async () =>
    {
        try { await body(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A detached operation only maintains state; a view that goes away under it is expected.
        }

        return true;
    });

    internal void Post(Action action)
    {
        try { Application.Dispatcher.Post(() => _context.Run(action)); }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            // The application is shutting down.
        }
    }

    /// <summary>
    /// A synchronization context that brings every continuation of an automation operation back to the UI thread,
    /// whatever context the host application has on that thread.
    /// </summary>
    private sealed class UiContext(NeoDispatcher dispatcher) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            try { dispatcher.Post(() => Run(() => callback(state))); }
            catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
            {
                // A continuation after shutdown has nothing left to act on.
            }
        }

        public override void Send(SendOrPostCallback callback, object? state)
        {
            if (dispatcher.CheckAccess()) Run(() => callback(state));
            else dispatcher.InvokeAsync(() => Run(() => callback(state))).AsTask().GetAwaiter().GetResult();
        }

        public override SynchronizationContext CreateCopy() => this;

        internal void Run(Action action)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { action(); }
            finally { SetSynchronizationContext(previous); }
        }

        internal T Run<T>(Func<T> function)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { return function(); }
            finally { SetSynchronizationContext(previous); }
        }
    }
}
