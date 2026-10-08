using System.Collections.Concurrent;
using NeoAstra;

internal static partial class Program
{
    private sealed partial class ConformanceSuite
    {
        // History navigation: how a navigation request tells it from a new document and from a reload, a view that has
        // it turned off, and a document that replaces itself. Each scenario has a view of its own, so that the history
        // it walks is the one it made.
        private async ValueTask RunHistoryScenariosAsync(NeoEnvironment environment)
        {
            const string kinds = "a navigation request tells a new document, a reload, and an entry of the history";
            const string turnedOff = "a view without history navigation stays on its document";
            if (environment.RuntimeInfo.ControlsHistoryNavigation)
            {
                await RunCaseAsync(kinds, () => WithViewAsync(environment, null, async (view, requests) =>
                {
                    async ValueTask ExpectAsync(Uri expected, NeoNavigationKind kind, Func<ValueTask> start)
                    {
                        requests.Clear();
                        var completion = WaitForNavigationAsync(view, expected, options.Timeout);
                        await start();
                        await completion;
                        // WebKit reports a document that is left before it has loaded as a failed navigation when the view
                        // comes back to it, so the next step waits for the document.
                        await WaitForDocumentLoadAsync(view, options.Timeout);
                        var request = requests.LastOrDefault(candidate => candidate.IsMainFrame && SameLocation(candidate.Uri, expected));
                        Require(request is not null, $"No navigation request was raised for '{expected}'.");
                        Require(request!.Kind == kind, $"The request for '{expected}' was reported as {request.Kind} instead of {kind}.");
                    }

                    await ExpectAsync(IndexUri, NeoNavigationKind.NewDocument, () => view.NavigateAsync(IndexUri));
                    await ExpectAsync(SecondUri, NeoNavigationKind.NewDocument, () => view.NavigateAsync(SecondUri));
                    await WaitUntilAsync(() => view.CanGoBack, "Backward history did not become available.", options.Timeout);
                    await ExpectAsync(IndexUri, NeoNavigationKind.BackForward, () => { view.GoBack(); return ValueTask.CompletedTask; });
                    await ExpectAsync(SecondUri, NeoNavigationKind.BackForward, async () => _ = await view.EvaluateScriptAsync("history.forward(); true"));
                    await ExpectAsync(SecondUri, NeoNavigationKind.Reload, () => { view.Reload(); return ValueTask.CompletedTask; });
                    // An entry of the history with the address of the document that is shown is not a reload.
                    await ExpectAsync(IndexUri, NeoNavigationKind.NewDocument, () => view.NavigateAsync(IndexUri));
                    await ExpectAsync(SecondUri, NeoNavigationKind.NewDocument, () => view.NavigateAsync(SecondUri));
                    await ExpectAsync(SecondUri, NeoNavigationKind.BackForward, async () => _ = await view.EvaluateScriptAsync("history.go(-2); true"));
                }));

                await RunCaseAsync(turnedOff, () => WithViewAsync(environment, new NeoBrowserFeatures { HistoryNavigation = false }, async (view, requests) =>
                {
                    await NavigateAndWaitAsync(view, IndexUri, options.Timeout);
                    await WaitForDocumentLoadAsync(view, options.Timeout);
                    await NavigateAndWaitAsync(view, SecondUri, options.Timeout);
                    await WaitForDocumentLoadAsync(view, options.Timeout);
                    _ = await view.EvaluateScriptAsync("globalThis.__neoMarker = 'set'; true");
                    requests.Clear();

                    // The page asks, the view refuses without asking its host, and the document stays as it is.
                    _ = await view.EvaluateScriptAsync("history.back(); true");
                    await Task.Delay(500);
                    using (var state = await EvaluateJsonAsync(view, "location.pathname + ' ' + globalThis.__neoMarker"))
                    {
                        Require(state.RootElement.GetString() == "/second.html set", $"The view left its document or loaded it again: '{state.RootElement.GetString()}'.");
                    }
                    Require(requests.IsEmpty, "The view asked its host about a history navigation that it refuses.");
                    Require(!view.CanGoBack && !view.CanGoForward, "The view reports a direction of its history to go in.");
                    Require(Refuses(view.GoBack) && Refuses(view.GoForward), "The view took a command of its history.");

                    var reloaded = WaitForNavigationAsync(view, SecondUri, options.Timeout);
                    view.Reload();
                    await reloaded;
                    await WaitUntilScriptAsync(view, "document.readyState === 'complete' && typeof globalThis.__neoMarker === 'undefined'",
                        "A reload did not load the document again.", options.Timeout);
                    Require(requests.Any(static request => request.Kind == NeoNavigationKind.Reload), "The reload was not reported as one.");

                    static bool Refuses(Action command)
                    {
                        try { command(); }
                        catch (InvalidOperationException) { return true; }
                        return false;
                    }
                }));
            }
            else
            {
                const string reason = "The native library predates the kind of a navigation request and the switch of history navigation.";
                Skip(kinds, reason);
                Skip(turnedOff, reason);
            }

            // No engine lets its host replace the entry of the history that is shown, or drop the history. A document does
            // it for itself with location.replace, which is what a host runs for a start-up screen that should not stay
            // behind the application.
            await RunCaseAsync("a document that replaces itself leaves no entry in the history", () => WithViewAsync(environment, null, async (view, _) =>
            {
                await NavigateAndWaitAsync(view, IndexUri, options.Timeout);
                await WaitForDocumentLoadAsync(view, options.Timeout);
                var replaced = WaitForNavigationAsync(view, SecondUri, options.Timeout);
                await view.EvaluateScriptAsync("location.replace('second.html'); true");
                await replaced;
                await WaitForDocumentLoadAsync(view, options.Timeout);
                await Task.Delay(250);
                Require(!view.CanGoBack, "The replaced document is still an entry of the history.");
                using var length = await EvaluateJsonAsync(view, "history.length");
                Require(length.RootElement.GetInt32() == 1, $"The history has {length.RootElement.GetInt32()} entries after the document replaced itself.");
            }));
        }

        // Runs the body with a view in a hidden window of its own, and with the navigation requests of that view, which are all allowed.
        private async ValueTask WithViewAsync(
            NeoEnvironment environment,
            NeoBrowserFeatures? features,
            Func<NeoAstra.NeoAstra, ConcurrentQueue<NeoNavigationRequest>, ValueTask> body)
        {
            var window = CreateHiddenWindow("NeoAstra history conformance");
            try
            {
                var viewOptions = new NeoAstraOptions();
                if (features is not null) viewOptions.BrowserFeatures = features;
                await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), viewOptions).AsTask().WaitAsync(options.Timeout);
                var requests = new ConcurrentQueue<NeoNavigationRequest>();
                view.NavigationRequested = request =>
                {
                    requests.Enqueue(request);
                    return ValueTask.FromResult(NeoNavigationDecision.Allow);
                };
                await body(view, requests);
            }
            finally
            {
                await window.DisposeAsync();
            }
        }
    }
}
