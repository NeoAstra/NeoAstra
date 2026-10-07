// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text;

namespace NeoAstra.Tests;

/// <summary>Runs a test body against a real view of the platform browser on a dedicated UI thread.</summary>
internal static class LiveBrowser
{
    internal const string Origin = "app://neoastra";

    /// <summary>Serves <paramref name="pages"/> from <c>app://neoastra/</c>, opens a window with one view, and runs the body.</summary>
    /// <param name="pages">The files of the site, by their path.</param>
    /// <param name="body">The test.</param>
    /// <param name="viewOptions">The options of the view, or <see langword="null"/> for a view with the label <c>main</c>.</param>
    /// <param name="timeout">How long the body may take, or <see langword="null"/> for a minute.</param>
    /// <param name="perMonitorDpiAware">
    /// Whether the window is one of an application that is aware of the scale of its display, which counts the window in
    /// the pixels of the display. The test host is not such an application by itself.
    /// </param>
    /// <remarks>The test is inconclusive off Windows and where the native runtime is not staged.</remarks>
    internal static async Task RunAsync(
        IReadOnlyDictionary<string, string> pages,
        Func<LiveBrowserSession, Task> body,
        NeoAstraOptions? viewOptions = null,
        TimeSpan? timeout = null,
        bool perMonitorDpiAware = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This test drives a WebView2 document; other backends are covered by the browser conformance harness.");
            return;
        }

        var limit = timeout ?? TimeSpan.FromSeconds(60);
        using var cancellation = new CancellationTokenSource(limit);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LiveBrowserSession? session = null;
        var thread = new Thread(() =>
        {
            try
            {
                // The windows that this thread creates are then sized for their monitor (per-monitor awareness, version 2).
                if (perMonitorDpiAware) SetThreadDpiAwarenessContext(-4);
                NeoApplication.Run(new NeoApplicationOptions { ShutdownMode = NeoApplicationShutdownMode.Explicit }, async application =>
                {
                    try
                    {
                        var window = application.CreateWindow(new NeoWindowOptions { Label = "main", Title = "NeoAstra live test", Width = 800, Height = 600 });
                        application.MainWindow = window;
                        window.Show();
                        await using var environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions
                        {
                            CustomSchemes = [NeoCustomScheme.Application("app", new PageProvider(pages))],
                            // WebView2 runs the views of one data folder in one browser process, which takes the windows
                            // of one kind of awareness only: the others would be refused while that process lives.
                            UserDataRoot = perMonitorDpiAware ? Path.Combine(Path.GetTempPath(), "neoastra-tests", "per-monitor-dpi-aware") : null,
                        });
                        await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), viewOptions ?? new NeoAstraOptions { ViewLabel = "main" });
                        session = new LiveBrowserSession(application, environment, window, view, cancellation.Token);
                        await body(session);
                    }
                    finally
                    {
                        application.ForceShutdown();
                    }
                });
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completion.Task.WaitAsync(limit + TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { Assert.Fail($"The live browser test hung during {session?.Stage ?? "startup"}."); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Assert.Fail($"The live browser test timed out during {session?.Stage ?? "startup"}."); }
        catch (NeoAstraNativeLibraryException) { Assert.Inconclusive("The native runtime is not staged."); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    private sealed class PageProvider(IReadOnlyDictionary<string, string> pages) : INeoResourceProvider
    {
        public NeoResourceResponse? GetResponse(NeoResourceRequest request)
        {
            var path = request.Uri.AbsolutePath.TrimStart('/');
            if (!pages.TryGetValue(path, out var content)) return null;
            var mimeType = path.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript; charset=utf-8"
                : path.EndsWith(".json", StringComparison.Ordinal) ? "application/json; charset=utf-8"
                : path.EndsWith(".txt", StringComparison.Ordinal) ? "text/plain; charset=utf-8"
                : path.EndsWith(".css", StringComparison.Ordinal) ? "text/css; charset=utf-8"
                : "text/html; charset=utf-8";
            return NeoResourceResponse.FromBytes(Encoding.UTF8.GetBytes(content), mimeType);
        }
    }
}

internal sealed class LiveBrowserSession(NeoApplication application, NeoEnvironment environment, NeoWindow window, global::NeoAstra.NeoAstra view, CancellationToken cancellationToken)
{
    internal NeoApplication Application { get; } = application;

    internal NeoEnvironment Environment { get; } = environment;

    internal NeoWindow Window { get; } = window;

    internal global::NeoAstra.NeoAstra View { get; } = view;

    internal CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>Gets or sets what the body is doing, for the message of a hang or a timeout.</summary>
    internal string Stage { get; set; } = "startup";

    internal static Uri Page(string path) => new($"{LiveBrowser.Origin}/{path}");

    /// <summary>Navigates the view and waits until the document has loaded.</summary>
    internal async Task NavigateAsync(string path)
    {
        Stage = $"navigation to {path}";
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, NeoNavigationCompletedEventArgs args) => completed.TrySetResult(args.IsSuccess);
        View.NavigationCompleted += OnCompleted;
        try
        {
            await View.NavigateAsync(Page(path), CancellationToken);
            Assert.IsTrue(await completed.Task.WaitAsync(CancellationToken), $"The navigation to {path} failed.");
        }
        finally
        {
            View.NavigationCompleted -= OnCompleted;
        }

        await WaitUntilAsync("document.readyState === 'complete'");
    }

    /// <summary>Evaluates a script that needs no result.</summary>
    internal async Task RunAsync(string script) => _ = await View.EvaluateScriptAsync(script, CancellationToken);

    /// <summary>Polls a JavaScript condition until it is <c>true</c>.</summary>
    internal async Task WaitUntilAsync(string condition)
    {
        while (true)
        {
            // A document that is being replaced has no script context to evaluate in for a moment.
            try { if (await View.EvaluateScriptAsync(condition, CancellationToken) == "true") return; }
            catch (Exception exception) when (exception is not OperationCanceledException) { }
            await Task.Delay(20, CancellationToken);
        }
    }
}
