using NeoWebView;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--validate-native-library"])
        {
            var application = NeoApplication.AttachToCurrentThread(new NeoApplicationOptions
            {
                ApplicationName = "NeoWebView NativeAOT Validation",
                ShutdownMode = NeoApplicationShutdownMode.Explicit,
            });
            application.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Console.WriteLine("NeoWebView native library loaded and detached successfully.");
            return 0;
        }

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
    }
}
