using System.Text;
using System.Text.Json;
using NeoAstra;
using WebView = NeoAstra.NeoAstra;

// A main page with a second, isolated web view placed over a rectangle of it, as a browser pane beside an application.
internal static class Program
{
    private const string BrowserPage = """
        <!doctype html><html lang="en"><title>Isolated page</title>
        <body style="font: 16px system-ui; margin: 2rem; background: #fff3c4; color: #222">
        <h1>Isolated page</h1>
        <p>This page has its own cookies and storage, no bridge, and no access to <code>app://</code>.</p>
        <input aria-label="Typed text" placeholder="Typed text survives a move to the other window">
        <p><button>Click: the toolbar world tells the host</button></p>
        </body></html>
        """;

    // Runs in a world that the page cannot see, and is the only code that reaches the binding.
    private const string ToolbarScript = """
        document.addEventListener("click", event => {
          if (event.isTrusted) sampleToolbar.postMessage(event.target.tagName.toLowerCase());
        }, true);
        """;

    [STAThread]
    private static int Main() => NeoApplication.Run(
        new NeoApplicationOptions { ApplicationName = "NeoAstra Embedded View Sample" },
        RunAsync);

    private static async ValueTask RunAsync(NeoApplication application)
    {
        var mainWindow = application.CreateWindow(new NeoWindowOptions { Label = "main", Title = "NeoAstra embedded view", Width = 1100, Height = 760, IsVisible = true });
        var otherWindow = application.CreateWindow(new NeoWindowOptions { Label = "other", Title = "NeoAstra embedded view: other window", Width = 900, Height = 640, IsVisible = false });
        application.MainWindow = mainWindow;

        var assets = new NeoDirectoryResourceProvider(Path.Combine(AppContext.BaseDirectory, "assets"));
        var appEnvironment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions
        {
            CustomSchemes = [NeoCustomScheme.Application("app", assets)],
        });

        // Isolation is a separate environment with a data folder of its own: a profile of the application's environment would
        // share its cookies and its scheme, and WebView2 refuses two sets of options on one data folder.
        var browserEnvironment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions
        {
            IsPrivate = true,
            UserDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeoAstra", "sample-embedded-view"),
            CustomSchemes = [NeoCustomScheme.Application("page", new BrowserPageProvider())],
        });

        // Safe here only because these two pages are the sample's own assets. The isolated view has no bridge at all.
        NeoAstraOptions ApplicationView(string label) => new() { ViewLabel = label, BridgePolicy = NeoBridgePolicy.TrustEntireView };
        var mainView = await appEnvironment.CreateWebViewAsync(NeoAstraHost.FillWindow(mainWindow), ApplicationView("main"));
        var otherView = await appEnvironment.CreateWebViewAsync(NeoAstraHost.FillWindow(otherWindow), ApplicationView("other"));
        var browser = await browserEnvironment.CreateWebViewAsync(
            NeoAstraHost.BoundedInWindow(mainWindow),
            new NeoAstraOptions { ViewLabel = "browser", Bounds = new NeoRect(0, 0, 320, 240) });

        var sample = new EmbeddedViewSample(mainWindow, otherWindow, mainView, otherView, browser);
        mainView.MessageReceived += (_, message) => sample.Handle(mainView, message.Json);
        otherView.MessageReceived += (_, message) => sample.Handle(otherView, message.Json);
        browser.KeyChordPressed += sample.OnKeyChord;
        otherWindow.CloseRequested += sample.OnOtherWindowClosing;

        if (browserEnvironment.GetCapability(NeoCapability.ScriptWorldChannel).SupportLevel != NeoSupportLevel.None)
        {
            await browser.AddScriptAsync(ToolbarScript, new NeoScriptOptions
            {
                IsolatedWorld = true,
                WorldName = "sample.toolbar",
                BindingName = "sampleToolbar",
                MainFrameOnly = true,
                InjectAtDocumentEnd = true,
                MessageHandler = (_, message) => sample.ShowStatus($"The toolbar world reports a click on <{message.Message}> from {message.Origin}."),
            });
        }

        await browser.NavigateAsync(new Uri("page://sample/index.html"));
        await mainView.NavigateAsync(new Uri("app://neoastra/index.html"));
        await otherView.NavigateAsync(new Uri("app://neoastra/index.html"));
    }

    private sealed class BrowserPageProvider : INeoResourceProvider
    {
        public NeoResourceResponse GetResponse(NeoResourceRequest request) =>
            NeoResourceResponse.FromBytes(Encoding.UTF8.GetBytes(BrowserPage), "text/html; charset=utf-8");
    }

    private sealed class EmbeddedViewSample(NeoWindow mainWindow, NeoWindow otherWindow, WebView mainView, WebView otherView, WebView browser)
    {
        private WebView? _movedTo;

        private WebView Anchor => _movedTo ?? mainView;

        public void Handle(WebView source, string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kind)) return;
            switch (kind.GetString())
            {
                case "place" when ReferenceEquals(source, Anchor):
                    browser.PlaceOver(source, root.GetProperty("x").GetDouble(), root.GetProperty("y").GetDouble(), root.GetProperty("width").GetDouble(), root.GetProperty("height").GetDouble());
                    break;
                case "toggle":
                    browser.IsVisible = !browser.IsVisible;
                    ShowStatus(browser.IsVisible ? "The view is shown, with its page as it was." : "The view is hidden, and keeps its page.");
                    break;
                case "focus" when browser.IsVisible:
                    browser.Focus();
                    ShowStatus("The view has the keyboard. Ctrl+Shift+K or Cmd+Shift+K reaches the application, not the page.");
                    break;
                case "move":
                    MoveTo(ReferenceEquals(Anchor, mainView) ? otherView : mainView);
                    break;
            }
        }

        public void OnKeyChord(object? sender, NeoKeyChordEventArgs chord)
        {
            if (chord.Chord is not ("Ctrl+Shift+K" or "Shift+Meta+K")) return;
            chord.Handled = true;
            ShowStatus($"The application took {chord.Chord}. The page did not get it.");
        }

        public ValueTask OnOtherWindowClosing(NeoWindowCloseRequest request)
        {
            if (request.CanCancel && request.Reason == NeoWindowCloseReason.User)
            {
                request.Cancel();
                if (ReferenceEquals(Anchor, otherView)) MoveTo(mainView);
                otherWindow.Hide();
            }

            return ValueTask.CompletedTask;
        }

        public void ShowStatus(string text) => Post(Anchor, $$"""{"neoastra":1,"kind":"status","text":"{{JsonEncodedText.Encode(text)}}"}""");

        // The anchor reports its placeholder, and the view moves there without a reload.
        private void MoveTo(WebView anchor)
        {
            _movedTo = anchor;
            var window = ReferenceEquals(anchor, mainView) ? mainWindow : otherWindow;
            window.Show();
            window.Activate();
            Post(anchor, """{"neoastra":1,"kind":"report"}""");
            ShowStatus("The view moved here, with its page state and typed text.");
        }

        private static void Post(WebView view, string json) => _ = PostAsync(view, json);

        private static async Task PostAsync(WebView view, string json)
        {
            try { await view.PostMessageAsync(json); }
            catch (Exception exception) { Console.Error.WriteLine(exception.Message); }
        }
    }
}
