using NeoAstra;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--validate-native-library"])
        {
            var application = NeoApplication.AttachToCurrentThread(new NeoApplicationOptions
            {
                ApplicationName = "NeoAstra NativeAOT Validation",
                ShutdownMode = NeoApplicationShutdownMode.Explicit,
            });
            application.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Console.WriteLine("NeoAstra native library loaded and detached successfully.");
            return 0;
        }

        return NeoApplication.Run(
            new NeoApplicationOptions { ApplicationName = "NeoAstra Sample" },
            async app =>
            {
                var window = app.CreateWindow(new NeoWindowOptions
                {
                    Title = "NeoAstra Sample",
                    Width = 1000,
                    Height = 700,
                    IsVisible = true,
                });
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();

                var assets = new NeoDirectoryResourceProvider(Path.Combine(AppContext.BaseDirectory, "assets"));
                await using var environment = await app.CreateEnvironmentAsync(new NeoEnvironmentOptions
                {
                    CustomSchemes = [NeoCustomScheme.Application("app", assets)],
                });
                // Safe here only because this sample loads controlled local assets with no remote dependencies.
                var viewOptions = new NeoAstraOptions { ViewLabel = "main", BridgePolicy = NeoBridgePolicy.TrustEntireView };
                await using var webView = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), viewOptions);
                webView.NavigationCompleted += (_, navigation) =>
                    Console.WriteLine($"Navigation to {navigation.Uri} succeeded: {navigation.IsSuccess}");
                webView.MessageReceived += async (_, message) =>
                {
                    Console.WriteLine($"JavaScript ({message.SourceOrigin}): {message.Json}");
                    try { await webView.PostMessageAsync("{\"neoastra\":1,\"kind\":\"sample_result\",\"from\":\"C#\",\"message\":\"Hello from NeoAstra\"}"); }
                    catch (Exception exception) { Console.Error.WriteLine(exception.Message); }
                };

                await webView.NavigateAsync(new Uri("app://neoastra/index.html"));
                await closed.Task;
            });
    }
}
