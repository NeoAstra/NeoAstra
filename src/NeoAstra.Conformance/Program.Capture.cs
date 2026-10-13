using System.Collections.Concurrent;
using NeoAstra;

internal static partial class Program
{
    private sealed partial class ConformanceSuite
    {
        // Capture of a microphone by a page of an application scheme: what the page is given to ask with, and what the
        // host is told when it asks. The view is one of its own, so that the answers of the host are those of this scenario.
        // Its window is shown: WebKit keeps the request of a page for the time when the page is visible.
        private async ValueTask RunCaptureScenariosAsync(NeoEnvironment environment)
        {
            const string secure = "a page of an application scheme is a secure context with audio worklets";
            const string available = "a page of an application scheme can ask for a microphone";
            const string asked = "a microphone request reaches the host with its origin";
            if (!IsSupported(environment, NeoCapability.Permissions))
            {
                Skip(secure, CapabilityReason(environment, NeoCapability.Permissions));
                Skip(available, CapabilityReason(environment, NeoCapability.Permissions));
                Skip(asked, CapabilityReason(environment, NeoCapability.Permissions));
                return;
            }

            var window = CreateHiddenWindow("NeoAstra capture conformance");
            try
            {
                await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), new NeoAstraOptions()).AsTask().WaitAsync(options.Timeout);
                window.Show();
                var requests = new ConcurrentQueue<NeoPermissionRequest>();
                view.PermissionRequested = request =>
                {
                    requests.Enqueue(request);
                    return ValueTask.FromResult(request.Kind == NeoPermissionKind.Microphone ? NeoPermissionDecision.AllowOnce : NeoPermissionDecision.DenyOnce);
                };
                await NavigateAndWaitAsync(view, IndexUri, options.Timeout);
                await WaitForDocumentLoadAsync(view, options.Timeout);
                await WaitUntilScriptAsync(view, "document.visibilityState === 'visible'", "The page of a shown window is not visible.", options.Timeout);

                using var page = await EvaluateJsonAsync(view,
                    "({ secure: isSecureContext, devices: typeof navigator.mediaDevices, " +
                    "getUserMedia: typeof navigator.mediaDevices?.getUserMedia, worklet: typeof globalThis.AudioWorkletNode })");
                var root = page.RootElement;
                await RunCaseAsync(secure, () =>
                {
                    Require(root.GetProperty("secure").GetBoolean(), "A page of an application scheme is not a secure context.");
                    Require(root.GetProperty("worklet").GetString() == "function", "The page has no AudioWorkletNode.");
                    return ValueTask.CompletedTask;
                });

                // WKWebView gives a page its media devices only in an application that could capture: one whose bundle says why
                // it wants a microphone or a camera, or that has the access already. The process of this harness has no bundle.
                var hasDevices = root.GetProperty("devices").GetString() == "object" && root.GetProperty("getUserMedia").GetString() == "function";
                if (!hasDevices && OperatingSystem.IsMacOS())
                {
                    const string noBundle = "WKWebView gives a page no navigator.mediaDevices in a process whose bundle has no NSMicrophoneUsageDescription.";
                    Skip(available, noBundle);
                    Skip(asked, noBundle);
                    return;
                }

                await RunCaseAsync(available, () =>
                {
                    Require(hasDevices, $"The page has no navigator.mediaDevices.getUserMedia: {root.GetRawText()}.");
                    return ValueTask.CompletedTask;
                });

                // The page is not awaited: a backend does not return the result of a Promise everywhere.
                _ = await view.EvaluateScriptAsync(
                    "globalThis.__neoCapture = { state: 'pending' }; (async () => { const result = { state: 'pending' }; try { " +
                    "const stream = await navigator.mediaDevices.getUserMedia({ audio: true }); " +
                    "result.tracks = stream.getAudioTracks().length; " +
                    "const context = new AudioContext(); await context.audioWorklet.addModule('capture-meter.js'); " +
                    "context.createMediaStreamSource(stream).connect(new AudioWorkletNode(context, 'meter')); " +
                    "stream.getTracks().forEach(track => track.stop()); await context.close(); result.state = 'captured'; " +
                    "} catch (error) { result.state = String(error.name); result.message = String(error.message); } " +
                    "globalThis.__neoCapture = result; })(); true");
                await WaitUntilScriptAsync(view, "globalThis.__neoCapture.state !== 'pending'", "The page got no answer to its microphone request.", options.Timeout);
                using var capture = await EvaluateJsonAsync(view, "globalThis.__neoCapture");
                var state = capture.RootElement.GetProperty("state").GetString();
                Console.WriteLine($"NOTE microphone capture: {capture.RootElement.GetRawText()}, requests to the host: {string.Join(", ", requests.Select(static request => $"{request.Kind} from {request.Origin?.ToString() ?? "no origin"}"))}");

                var microphone = requests.FirstOrDefault(static request => request.Kind == NeoPermissionKind.Microphone);
                if (microphone is null)
                {
                    // The browser answers by itself where it has no microphone, or where the system keeps the process from one.
                    Require(state != "captured", "The page captured a microphone that the host was not asked about.");
                    Skip(asked, $"The browser refused the page before it asked the host ({state}): no microphone, or no access of this process to one.");
                }
                else
                {
                    await RunCaseAsync(asked, () =>
                    {
                        var origin = new Uri(IndexUri.GetLeftPart(UriPartial.Authority));
                        Require(microphone.Origin is not null && Uri.Compare(microphone.Origin, origin, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0,
                            $"The request came from '{microphone.Origin?.ToString() ?? "no origin"}' instead of '{origin}'.");
                        // What follows the answer of the host depends on the machine: a capture, or an error of its device.
                        Require(state != "NotAllowedError", $"The page was refused although the host allowed it: {capture.RootElement.GetRawText()}.");
                        return ValueTask.CompletedTask;
                    });
                }
            }
            finally
            {
                await window.DisposeAsync();
            }
        }
    }
}
