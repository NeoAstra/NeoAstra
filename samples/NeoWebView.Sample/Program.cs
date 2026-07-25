using NeoWebView;

return NeoApplication.Run(
    new NeoApplicationOptions { ApplicationName = "NeoWebView Sample" },
    async app =>
    {
        var window = app.CreateWindow(new NeoWindowOptions
        {
            Title = "NeoWebView Sample",
            Width = 1000,
            Height = 700,
            IsVisible = true,
        });
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();

        await using var environment = await app.CreateEnvironmentAsync();
        await using var webView = await environment.CreateWebViewAsync(NeoWebViewHost.FillWindow(window));
        webView.NavigationCompleted += (_, navigation) =>
            Console.WriteLine($"Navigation to {navigation.Uri} succeeded: {navigation.IsSuccess}");

        await webView.NavigateAsync(new Uri("https://example.com/"));
        await closed.Task;
    });
