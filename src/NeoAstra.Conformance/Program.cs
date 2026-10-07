using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using NeoAstra;

internal static partial class Program
{
    private static readonly Uri IndexUri = new("conformance://fixture/index.html");
    private static readonly Uri SecondUri = new("conformance://fixture/second.html");
    private static readonly Uri RedirectUri = new("conformance://fixture/redirect.html");
    private static readonly Uri RedirectedUri = new("conformance://fixture/redirected.html");
    private static int _nextViewLabel;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Browser conformance was not run. Pass --run to opt in; no browser was opened.");
            return 0;
        }

        if (!HarnessOptions.TryParse(args, out var options))
        {
            Console.Error.WriteLine(
                "Usage: dotnet run --project src/NeoAstra.Conformance -c Release -- --run [--stress] [--timeout-seconds <5-300>]");
            return 2;
        }

        try
        {
            return NeoApplication.Run(
                new NeoApplicationOptions
                {
                    ApplicationName = "NeoAstra Browser Conformance",
                    ShutdownMode = NeoApplicationShutdownMode.Explicit,
                },
                application => RunAsync(application, options));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL browser conformance");
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async ValueTask RunAsync(NeoApplication application, HarnessOptions options)
    {
        var suite = new ConformanceSuite(application, options);
        await suite.RunAsync();
        application.Shutdown();
    }

    private sealed partial class ConformanceSuite(NeoApplication application, HarnessOptions options)
    {
        private readonly Stopwatch _total = Stopwatch.StartNew();
        private int _passed;
        private int _skipped;

        internal async ValueTask RunAsync()
        {
            var assetsPath = Path.Combine(AppContext.BaseDirectory, "assets");
            Require(Directory.Exists(assetsPath), $"Fixture assets were not copied to '{assetsPath}'.");

            var provider = new ObservedResourceProvider(new NeoDirectoryResourceProvider(assetsPath));
            var environmentOptions = CreateEnvironmentOptions(provider);
            await using var environment = await application.CreateEnvironmentAsync(environmentOptions).AsTask().WaitAsync(options.Timeout);
            PrintRuntime(environment.RuntimeInfo);
            Console.WriteLine($"MODE stress={options.Stress.ToString().ToLowerInvariant()} timeout={options.Timeout.TotalSeconds:F0}s network=disabled interactive=disabled");

            RequireCapability(environment, NeoCapability.CustomScheme, "The fixture requires custom-scheme navigation.");
            var supportsDocumentStart = IsSupported(environment, NeoCapability.ScriptDocumentStart);
            var supportsCookies = IsSupported(environment, NeoCapability.Cookies) &&
                                  IsSupported(environment, NeoCapability.ProfileEphemeral);
            var bridgeMode = GetBridgeMode(environment);

            NeoProfile? firstProfile = null;
            NeoProfile? secondProfile = null;
            if (supportsCookies)
            {
                firstProfile = await environment.CreateProfileAsync(new NeoProfileOptions
                {
                    Name = "neoastra-conformance-a",
                    IsEphemeral = true,
                }).AsTask().WaitAsync(options.Timeout);
                secondProfile = await environment.CreateProfileAsync(new NeoProfileOptions
                {
                    Name = "neoastra-conformance-b",
                    IsEphemeral = true,
                }).AsTask().WaitAsync(options.Timeout);
            }

            var window = CreateHiddenWindow("NeoAstra Browser Conformance");
            NeoAstra.NeoAstra? view = null;
            NeoUserScript? documentStartScript = null;
            try
            {
                await RunCaseAsync("empty trusted-origin list is rejected (never allow-all)", async () =>
                {
                    NeoAstra.NeoAstra? unexpectedView = null;
                    try
                    {
                        unexpectedView = await environment.CreateWebViewAsync(
                            NeoAstraHost.FillWindow(window),
                            new NeoAstraOptions
                            {
                                Profile = firstProfile,
                                BridgePolicy = NeoBridgePolicy.TrustedOrigins,
                                BridgeOrigins = Array.Empty<string>(),
                            });
                    }
                    catch (ArgumentException)
                    {
                        return;
                    }
                    finally
                    {
                        if (unexpectedView is not null) await unexpectedView.DisposeAsync();
                    }

                    throw new InvalidOperationException("TrustedOrigins unexpectedly accepted an empty origin list.");
                });

                view = await environment.CreateWebViewAsync(
                    NeoAstraHost.FillWindow(window),
                    CreateViewOptions(firstProfile, bridgeMode)).AsTask().WaitAsync(options.Timeout);

                if (supportsDocumentStart)
                {
                    documentStartScript = await view.AddScriptAsync(
                        "globalThis.__neoDocumentStart = 'injected'; globalThis.__neoDocumentStartReadyState = document.readyState;",
                        new NeoScriptOptions { MainFrameOnly = false }).AsTask().WaitAsync(options.Timeout);
                }

                await RunCaseAsync("custom-scheme navigation, local assets, and file-backed resource streaming", async () =>
                {
                    await NavigateAndWaitAsync(view, IndexUri, options.Timeout);
                    await WaitForDocumentLoadAsync(view, options.Timeout);
                    using var result = await EvaluateJsonAsync(view,
                        "({ externalAsset: globalThis.__fixtureExternalAsset ?? null, " +
                        "style: getComputedStyle(document.documentElement).getPropertyValue('--neoastra-fixture-style').trim(), " +
                        "title: document.title })");
                    var root = result.RootElement;
                    Require(root.GetProperty("externalAsset").GetString() == "loaded", "The external fixture script did not load.");
                    Require(root.GetProperty("style").GetString() == "loaded", "The external fixture stylesheet did not load.");
                    Require(root.GetProperty("title").GetString() == "NeoAstra conformance index", "The local document title was not observed.");
                    Require(provider.SawPath("/index.html") && provider.SawPath("/fixture.js") && provider.SawPath("/fixture.css"),
                        "The resource provider did not observe the expected document and subresources.");
                    Require(provider.FileBackedResponseCount >= 3, "Expected directory assets to use file-backed responses.");
                });

                await RunCaseAsync("JavaScript evaluation", async () =>
                {
                    using var result = await EvaluateJsonAsync(view, "({ answer: 6 * 7, text: 'complete' })");
                    Require(result.RootElement.GetProperty("answer").GetInt32() == 42, "JavaScript evaluation returned the wrong value.");
                    Require(result.RootElement.GetProperty("text").GetString() == "complete", "The JavaScript result changed.");
                });

                await RunCaseAsync("JavaScript results without a JSON form", async () =>
                {
                    // A number that is not finite and a date have no JSON form of their own. A backend reports null
                    // for the number and null, an empty object, or its text for the date; neither may fail the
                    // evaluation or the host.
                    var number = await view.EvaluateScriptAsync("0 / 0");
                    Require(number is null or "null", $"A number that is not finite was reported as {number}.");
                    var date = await view.EvaluateScriptAsync("new Date(0)");
                    Require(date is null or "null" or "{}" || date.StartsWith("\"1970-01-01T00:00:00", StringComparison.Ordinal), $"A date was reported as {date}.");
                });

                await RunPromiseCaseAsync("Promise results", async () =>
                {
                    using var result = await EvaluateJsonAsync(view,
                        "Promise.resolve({ answer: 6 * 7, text: 'promise-complete' })");
                    return result.RootElement.ValueKind == JsonValueKind.Object &&
                           result.RootElement.TryGetProperty("answer", out var answer) && answer.GetInt32() == 42 &&
                           result.RootElement.TryGetProperty("text", out var text) && text.GetString() == "promise-complete";
                }, "The active backend serializes the Promise object instead of awaiting its result.",
                    "The active backend rejects the Promise object as an unsupported result type instead of awaiting its result.");

                await RunOptionalCaseAsync("JavaScript exceptions", async () =>
                {
                    try
                    {
                        _ = await view.EvaluateScriptAsync("throw new Error('neo-conformance-sentinel')");
                        return false;
                    }
                    catch (NeoAstraException)
                    {
                        return true;
                    }
                }, "The active backend reports successful execution with a null result when JavaScript throws.");

                if (supportsDocumentStart)
                {
                    await RunCaseAsync("document-start injection", async () =>
                    {
                        using var result = await EvaluateJsonAsync(view,
                            "({ sawInjection: globalThis.__fixtureSawDocumentStartScript, " +
                            "injectionReadyState: globalThis.__neoDocumentStartReadyState })");
                        var root = result.RootElement;
                        Require(root.GetProperty("sawInjection").GetBoolean(), "The page script ran before the document-start sentinel was available.");
                        Require(root.GetProperty("injectionReadyState").ValueKind == JsonValueKind.String,
                            "The injection did not record a document state.");
                    });
                }
                else
                {
                    Skip("document-start injection", CapabilityReason(environment, NeoCapability.ScriptDocumentStart));
                }

                await RunCaseAsync("navigation, history, and redirects", async () =>
                {
                    await NavigateAndWaitAsync(view, SecondUri, options.Timeout);
                    await WaitUntilAsync(() => view.CanGoBack, "Backward history did not become available.", options.Timeout);
                    await NavigateCommandAndWaitAsync(view, IndexUri, view.GoBack, options.Timeout);
                    await WaitUntilAsync(() => view.CanGoForward, "Forward history did not become available.", options.Timeout);
                    await NavigateCommandAndWaitAsync(view, SecondUri, view.GoForward, options.Timeout);
                    await NavigateAndWaitAsync(view, RedirectUri, options.Timeout, RedirectedUri);
                    Require(view.Source is not null && SameLocation(view.Source, RedirectedUri), "The client redirect did not update the final source.");
                });

                await NavigateAndWaitAsync(view, IndexUri, options.Timeout);
                await RunCaseAsync("local storage", async () =>
                {
                    using var result = await EvaluateJsonAsync(view,
                        "(() => { localStorage.removeItem('neo-conformance'); localStorage.setItem('neo-conformance', 'stored'); " +
                        "const value = localStorage.getItem('neo-conformance'); localStorage.removeItem('neo-conformance'); return value; })()");
                    Require(result.RootElement.GetString() == "stored", "Local storage did not round trip the fixture value.");
                });

                await RunPromiseCaseAsync("IndexedDB", async () =>
                {
                    using var result = await EvaluateJsonAsync(view,
                        "(async () => { const name = 'neo-conformance-db'; await new Promise(resolve => { const request = indexedDB.deleteDatabase(name); " +
                        "request.onsuccess = request.onerror = request.onblocked = resolve; }); const db = await new Promise((resolve, reject) => { " +
                        "const request = indexedDB.open(name, 1); request.onupgradeneeded = () => request.result.createObjectStore('values'); " +
                        "request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error); }); " +
                        "const value = await new Promise((resolve, reject) => { const transaction = db.transaction('values', 'readwrite'); " +
                        "transaction.objectStore('values').put('stored', 'key'); transaction.oncomplete = () => resolve('stored'); " +
                        "transaction.onerror = () => reject(transaction.error); }); db.close(); indexedDB.deleteDatabase(name); return value; })()");
                    return result.RootElement.ValueKind == JsonValueKind.String && result.RootElement.GetString() == "stored";
                }, "The IndexedDB probe is asynchronous and the active backend does not await Promise results.",
                    "The IndexedDB probe is asynchronous and the active backend rejects its Promise result instead of awaiting it.");

                if (supportsCookies)
                {
                    await RunCaseAsync("cookies and ephemeral-profile isolation", async () =>
                    {
                        await using var isolationWindow = CreateHiddenWindow("NeoAstra profile isolation conformance");
                        await using var isolationView = await environment.CreateWebViewAsync(
                            NeoAstraHost.FillWindow(isolationWindow),
                            new NeoAstraOptions { Profile = secondProfile }).AsTask().WaitAsync(options.Timeout);
                        var cookieUri = new Uri("https://conformance.invalid/");
                        var cookie = new NeoCookie("neo_conformance", "profile-a", "conformance.invalid")
                        {
                            IsSecure = true,
                            Expires = DateTimeOffset.UtcNow.AddHours(1),
                        };
                        await firstProfile!.SetCookieAsync(cookie);
                        var firstCookies = await firstProfile.GetCookiesAsync(cookieUri);
                        var secondCookies = await secondProfile!.GetCookiesAsync(cookieUri);
                        Require(firstCookies.Any(item => item.Name == cookie.Name && item.Value == cookie.Value),
                            "The cookie was not returned from its profile.");
                        Require(secondCookies.All(item => item.Name != cookie.Name),
                            "The cookie leaked into a separate ephemeral profile.");
                        await firstProfile.DeleteCookieAsync(cookie);
                        firstCookies = await firstProfile.GetCookiesAsync(cookieUri);
                        Require(firstCookies.All(item => item.Name != cookie.Name), "The cookie was not deleted.");
                    });
                }
                else
                {
                    Skip("cookies and profile isolation", "Cookie management or ephemeral profiles are unavailable on this backend.");
                }

                Skip("JavaScript dialogs",
                    IsSupported(environment, NeoCapability.ScriptDialogs)
                        ? "The hidden noninteractive host does not reliably surface engine dialogs; validate this capability in an interactive platform run."
                        : CapabilityReason(environment, NeoCapability.ScriptDialogs));

                if (bridgeMode != BridgeMode.Disabled)
                {
                    await RunCaseAsync("small and large bidirectional JSON messaging with ABI 1.8 trust policy", async () =>
                    {
                        await WaitUntilScriptAsync(view,
                            "globalThis.__fixtureTransportConnected === true",
                            "The @neoastra/client handshake did not complete.", options.Timeout);
                        var incoming = await WaitForMessageAsync(view, "fixture", async () =>
                        {
                            _ = await view.EvaluateScriptAsync(
                                "globalThis.__fixturePostMessage({ kind: 'fixture', value: 42 }); true");
                        }, options.Timeout);
                        using (var json = JsonDocument.Parse(incoming.Json))
                        {
                            Require(json.RootElement.GetProperty("value").GetInt32() == 42, "The incoming JSON payload changed.");
                        }
                        if (bridgeMode == BridgeMode.TrustedOrigins)
                        {
                            Require(incoming.IsMainFrame, "The trusted message was not identified as a main-frame message.");
                            Require(incoming.SourceOrigin is not null &&
                                    incoming.SourceOrigin.Scheme == "conformance" &&
                                    incoming.SourceOrigin.Host == "fixture",
                                $"The trusted message reported an unexpected origin: {incoming.SourceOrigin}.");
                        }
                        else
                        {
                            Require(incoming.SourceOrigin is null,
                                "Linux TrustEntireView must report the unavailable sender origin as null, not as verified.");
                        }

                        await view.PostMessageAsync("{\"neoastra\":1,\"kind\":\"host\",\"value\":17}");
                        await WaitUntilScriptAsync(view,
                            "globalThis.__fixtureHostMessages.some(value => value.kind === 'host' && value.value === 17)",
                            "The page did not receive host JSON.", options.Timeout);

                        var large = await WaitForMessageAsync(view, "large", async () =>
                        {
                            _ = await view.EvaluateScriptAsync(
                                "globalThis.__fixturePostMessage({ kind: 'large', payload: 'x'.repeat(128 * 1024) }); true");
                        }, options.Timeout);
                        using var largeJson = JsonDocument.Parse(large.Json);
                        Require(largeJson.RootElement.GetProperty("payload").GetString()?.Length == 128 * 1024,
                            "The 128 KiB JSON payload changed.");
                    });
                }
                else
                {
                    Skip("small and large messaging",
                        "TrustedOrigins requires authenticated origins on Windows/macOS; this platform/backend cannot safely enable the bridge.");
                }

                ReportBrowserScenarioSkips(environment);

                await RunCaseAsync("teardown callback quiescence", async () =>
                {
                    var callbacks = 0;
                    if (bridgeMode != BridgeMode.Disabled)
                    {
                        void CountTick(object? _, NeoWebMessageReceivedEventArgs message)
                        {
                            if (HasKind(message.Json, "tick")) Interlocked.Increment(ref callbacks);
                        }

                        view.MessageReceived += CountTick;
                        _ = await view.EvaluateScriptAsync(
                            "globalThis.__fixtureTick = setInterval(() => globalThis.__fixturePostMessage({ kind: 'tick' }), 20); true");
                        await WaitUntilAsync(() => Volatile.Read(ref callbacks) >= 3,
                            "The fixture did not produce teardown probe callbacks.", options.Timeout);
                    }
                    else
                    {
                        view.NavigationCompleted += (_, _) => Interlocked.Increment(ref callbacks);
                        await NavigateAndWaitAsync(view, IndexUri, options.Timeout);
                    }

                    if (documentStartScript is not null)
                    {
                        await documentStartScript.DisposeAsync();
                        documentStartScript = null;
                    }
                    await view.DisposeAsync();
                    var afterDispose = Volatile.Read(ref callbacks);
                    await Task.Delay(300);
                    Require(Volatile.Read(ref callbacks) == afterDispose, "A callback arrived after view disposal completed.");
                    view = null;
                });
            }
            finally
            {
                if (documentStartScript is not null) await documentStartScript.DisposeAsync();
                if (view is not null) await view.DisposeAsync();
                await window.DisposeAsync();
                if (secondProfile is not null) await secondProfile.DisposeAsync();
                if (firstProfile is not null) await firstProfile.DisposeAsync();
            }

            await RunAutomationScenariosAsync(environment);
            await RunLifecycleScenariosAsync(environment, environmentOptions);
            _total.Stop();
            Console.WriteLine($"PASS browser conformance: {_passed} passed, {_skipped} skipped, {_total.Elapsed.TotalSeconds:F2} s");
        }

        private async ValueTask RunLifecycleScenariosAsync(
            NeoEnvironment environment,
            NeoEnvironmentOptions environmentOptions)
        {
            var lifecycleCount = options.Stress ? 25 : 3;
            await RunCaseAsync($"repeated view creation and destruction ({lifecycleCount} iterations)", async () =>
            {
                var window = CreateHiddenWindow("NeoAstra lifecycle conformance");
                try
                {
                    for (var index = 0; index < lifecycleCount; index++)
                    {
                        await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window));
                        await NavigateAndWaitAsync(view, IndexUri, options.Timeout);
                    }
                }
                finally
                {
                    await window.DisposeAsync();
                }
            });

            await RunCaseAsync("multiple concurrent views", async () =>
            {
                var firstWindow = CreateHiddenWindow("NeoAstra concurrent conformance A");
                var secondWindow = CreateHiddenWindow("NeoAstra concurrent conformance B");
                try
                {
                    await using var firstView = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(firstWindow));
                    await using var secondView = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(secondWindow));
                    var firstNavigation = NavigateAndWaitAsync(firstView, IndexUri, options.Timeout).AsTask();
                    var secondNavigation = NavigateAndWaitAsync(secondView, SecondUri, options.Timeout).AsTask();
                    await Task.WhenAll(firstNavigation, secondNavigation);
                }
                finally
                {
                    await secondWindow.DisposeAsync();
                    await firstWindow.DisposeAsync();
                }
            });

            await RunCaseAsync("views sharing a window", async () =>
            {
                // A window can host several views, the newest over the others. A view under another one is not
                // drawn, but it is still alive, and it outlives a newer view that goes away.
                var window = CreateHiddenWindow("NeoAstra shared window conformance");
                // The path is read as a value: a backend may write the slash of a JSON string in its escaped form.
                static async ValueTask<string?> PathAsync(NeoAstra.NeoAstra view)
                {
                    using var path = await EvaluateJsonAsync(view, "location.pathname");
                    return path.RootElement.GetString();
                }

                try
                {
                    await using var firstView = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window));
                    await NavigateAndWaitAsync(firstView, IndexUri, options.Timeout);
                    await using (var secondView = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window)))
                    {
                        await NavigateAndWaitAsync(secondView, SecondUri, options.Timeout);
                        var under = await PathAsync(firstView);
                        Require(under == "/index.html", $"The first view of a window did not survive a second one: it is at '{under}'.");
                        var over = await PathAsync(secondView);
                        Require(over == "/second.html", $"The second view of a window does not show its own document: it is at '{over}'.");
                    }

                    await NavigateAndWaitAsync(firstView, SecondUri, options.Timeout);
                    var left = await PathAsync(firstView);
                    Require(left == "/second.html", $"The first view of a window did not survive the end of a second one: it is at '{left}'.");
                }
                finally
                {
                    await window.DisposeAsync();
                }
            });

            await RunCaseAsync("repeated hidden top-level window lifecycle", async () =>
            {
                for (var index = 0; index < lifecycleCount; index++)
                {
                    await using var window = CreateHiddenWindow($"NeoAstra window conformance {index}");
                }
            });

            await RunCaseAsync("a window reports the scale of its display and the size it has", async () =>
            {
                var window = application.CreateWindow(new NeoWindowOptions { Title = "NeoAstra window size conformance", Width = 480, Height = 360, IsVisible = false, ShowInTaskbar = false });
                try
                {
                    // Both are known before the window is shown: the size is the one it was created with.
                    var scale = window.ScaleFactor;
                    Require(scale is >= 1 and <= 16, $"A new window reports the scale {scale}.");
                    Require(window.ClientSize == new NeoSize(480, 360), $"A new window of 480 by 360 reports {window.ClientSize}.");

                    await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window));
                    window.Show();
                    await NavigateAndWaitAsync(view, IndexUri, options.Timeout);

                    async ValueTask<(int Width, int Height, double Ratio)> PageAsync()
                    {
                        using var page = await EvaluateJsonAsync(view, "[innerWidth, innerHeight, devicePixelRatio]");
                        return (page.RootElement[0].GetInt32(), page.RootElement[1].GetInt32(), page.RootElement[2].GetDouble());
                    }

                    // The page is as wide as the window. Only Windows counts a window in pixels of which a page may take several
                    // for one of its own, and a window may be higher than its page by a title bar that it draws itself.
                    async ValueTask<bool> PageFillsWindowAsync()
                    {
                        var page = await PageAsync();
                        var client = window.ClientSize;
                        var perPixel = OperatingSystem.IsWindows() ? window.ScaleFactor : 1;
                        return page.Width > 0 && Math.Abs(client.Width / perPixel - page.Width) <= 1 && client.Height / perPixel - page.Height is >= -1 and <= 128;
                    }

                    async ValueTask RequirePageFillsWindowAsync(string when)
                    {
                        var deadline = Stopwatch.StartNew();
                        while (!await PageFillsWindowAsync())
                        {
                            if (deadline.Elapsed > TimeSpan.FromSeconds(5))
                            {
                                var page = await PageAsync();
                                throw new InvalidOperationException($"A window {when} reports {window.ClientSize} at the scale {window.ScaleFactor} while its page is {page.Width} by {page.Height}.");
                            }

                            await Task.Delay(50);
                        }
                    }

                    await RequirePageFillsWindowAsync("that was shown");
                    var shown = await PageAsync();
                    Require(Math.Abs(shown.Ratio - window.ScaleFactor * view.ZoomFactor) < 0.01, $"A window reports the scale {window.ScaleFactor} while its page is drawn with {shown.Ratio} pixels for each of its own.");

                    var sizes = new List<NeoSize>();
                    window.ClientSizeChanged += (_, args) => { lock (sizes) sizes.Add(args.NewSize); };

                    window.ClientSize = new NeoSize(520, 400);
                    await RequirePageFillsWindowAsync("that was given 520 by 400");
                    Require(window.ClientSize == new NeoSize(520, 400), $"A window that was given 520 by 400 reports {window.ClientSize}.");

                    // A maximized window has a size that nobody asked for through the library. A desktop without a window manager
                    // does not maximize a window, which then keeps its size.
                    var before = window.ClientSize;
                    var pageBefore = (await PageAsync()).Width;
                    window.State = NeoWindowState.Maximized;
                    var waited = Stopwatch.StartNew();
                    while (waited.Elapsed < TimeSpan.FromSeconds(5) && (await PageAsync()).Width == pageBefore) await Task.Delay(50);
                    await RequirePageFillsWindowAsync("that was maximized");
                    var maximized = window.ClientSize;
                    if (maximized != before)
                    {
                        bool reported;
                        lock (sizes) reported = sizes.Contains(maximized);
                        Require(reported, $"A window that was maximized to {maximized} did not report that size as a change.");
                    }

                    window.State = NeoWindowState.Normal;
                    waited.Restart();
                    while (waited.Elapsed < TimeSpan.FromSeconds(5) && window.ClientSize != before) await Task.Delay(50);
                    await RequirePageFillsWindowAsync("that is no longer maximized");
                    Require(window.ClientSize == before, $"A window that is no longer maximized reports {window.ClientSize}, and it was {before}.");
                }
                finally
                {
                    await window.DisposeAsync();
                }
            });

            await RunCaseAsync("rapid navigation cancellation and recovery", async () =>
            {
                var window = CreateHiddenWindow("NeoAstra navigation cancellation conformance");
                try
                {
                    await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window));
                    for (var index = 0; index < (options.Stress ? 100 : 10); index++)
                    {
                        await view.NavigateAsync((index & 1) == 0 ? IndexUri : SecondUri);
                        view.Stop();
                    }
                    await Task.Delay(250);
                    var recoveryUri = new Uri($"{IndexUri}?recovery={Guid.NewGuid():N}");
                    await NavigateAndWaitAsync(view, recoveryUri, options.Timeout);
                }
                finally
                {
                    await window.DisposeAsync();
                }
            });

            var environmentCount = options.Stress ? 5 : 2;
            await RunCaseAsync($"repeated environment creation ({environmentCount} iterations)", async () =>
            {
                for (var index = 0; index < environmentCount; index++)
                {
                    await using var extraEnvironment = await application.CreateEnvironmentAsync(environmentOptions);
                }
            });

            if (options.Stress)
            {
                var bridgeMode = GetBridgeMode(environment);
                if (bridgeMode == BridgeMode.Disabled)
                {
                    Skip("100,000 small messages", "A safely authenticated bridge is unavailable on this platform/backend.");
                }
                else
                {
                    await RunCaseAsync("100,000 small messages", async () =>
                    {
                        // A page posts faster than a host receives, and WKWebView ends the web content process of a page
                        // that has 50,000 messages waiting for the main thread of the application. The messages are
                        // therefore posted in bursts well under that number, each one after the burst before it has
                        // arrived; see the paragraph on message bursts in doc/known-limitations.md.
                        const int total = 100_000;
                        const int burst = 10_000;
                        var window = CreateHiddenWindow("NeoAstra message stress conformance");
                        try
                        {
                            await using var view = await environment.CreateWebViewAsync(
                                NeoAstraHost.FillWindow(window), CreateViewOptions(null, bridgeMode));
                            await NavigateAndWaitAsync(view, IndexUri, options.Timeout);
                            await WaitForDocumentLoadAsync(view, options.Timeout);
                            var received = 0;
                            NeoProcessFailedEventArgs? processFailure = null;
                            void OnMessage(object? _, NeoWebMessageReceivedEventArgs message)
                            {
                                if (HasKind(message.Json, "stress")) Interlocked.Increment(ref received);
                            }

                            void OnProcessFailed(object? _, NeoProcessFailedEventArgs failure) => Volatile.Write(ref processFailure, failure);

                            view.MessageReceived += OnMessage;
                            view.ProcessFailed += OnProcessFailed;
                            try
                            {
                                for (var posted = 0; posted < total; posted += burst)
                                {
                                    var end = posted + burst;
                                    _ = await view.EvaluateScriptAsync(
                                        $"for (let i = {posted}; i < {end}; i++) globalThis.__fixturePostMessage({{ kind: 'stress', value: i }}); true");
                                    await WaitUntilAsync(() => Volatile.Read(ref received) >= end,
                                        $"The messages up to number {end} did not all arrive.", options.Timeout);
                                }
                            }
                            catch (NeoAstraException exception) when (Volatile.Read(ref processFailure) is { } failure)
                            {
                                // An evaluation that loses its web content process fails as if its result had no supported
                                // type, so the event is what tells the two apart.
                                throw new InvalidOperationException(
                                    $"The web content process ended ({failure.Kind}) after {Volatile.Read(ref received)} of {total} messages had arrived.",
                                    exception);
                            }
                            finally
                            {
                                view.MessageReceived -= OnMessage;
                                view.ProcessFailed -= OnProcessFailed;
                            }
                            Require(received == total, $"Expected {total} messages but received {received}.");
                        }
                        finally
                        {
                            await window.DisposeAsync();
                        }
                    });
                }
            }
            else
            {
                Skip("100,000 small messages", "Pass --stress to run this bounded high-volume scenario.");
            }

            Skip("owned-window and popup closure during shutdown",
                "Destructive application shutdown needs process isolation; this in-process harness cannot continue after exercising it.");
            Skip("top-level window activation",
                "The harness keeps all windows hidden and noninteractive, so it does not steal focus to automate activation.");
            Skip("popup storms, limits, and decision timeouts",
                "The public API cannot synthesize a trusted user activation deterministically; browser popup blocking would make this test ambiguous.");
            Skip("closing a window during navigation", "Destructive close ordering is not isolated from the harness application.");
            Skip("closing a view with deferred decisions", "No deterministic local fixture can keep a portable browser decision pending.");
            Skip("cancellation during JavaScript evaluation",
                "Backend cancellation of a deliberately non-settling script can strand engine work; run this in a process-isolated stress host.");
            Skip("shutdown with pending operations", "Application shutdown terminates this in-process harness and requires a subprocess test.");
            Skip("resource-stream cancellation",
                "The current public resource-provider API is synchronous and exposes no stream-cancellation callback to assert.");
            Skip("browser-process failure", "The public API intentionally has no crash injection hook; killing a browser process is destructive.");
        }

        private NeoWindow CreateHiddenWindow(string title)
            => application.CreateWindow(new NeoWindowOptions
            {
                Title = title,
                Width = 640,
                Height = 480,
                IsVisible = false,
                ShowInTaskbar = false,
            });

        private async ValueTask RunCaseAsync(string name, Func<ValueTask> action)
        {
            var stopwatch = Stopwatch.StartNew();
            await action().AsTask().WaitAsync(options.Timeout);
            stopwatch.Stop();
            _passed++;
            Console.WriteLine($"PASS {name} ({stopwatch.Elapsed.TotalMilliseconds:F1} ms)");
        }

        private async ValueTask RunOptionalCaseAsync(string name, Func<ValueTask<bool>> action, string unsupportedReason)
        {
            var stopwatch = Stopwatch.StartNew();
            if (await action().AsTask().WaitAsync(options.Timeout))
            {
                stopwatch.Stop();
                _passed++;
                Console.WriteLine($"PASS {name} ({stopwatch.Elapsed.TotalMilliseconds:F1} ms)");
            }
            else
            {
                Skip(name, unsupportedReason);
            }
        }

        // For an optional case whose script completes with a Promise. A backend that does not await it either serializes
        // the Promise object, which the probe reports by returning false, or refuses to return it and fails the evaluation.
        // That refusal is the only failure reported as a skip: a script that throws, or anything else, still fails the run.
        private async ValueTask RunPromiseCaseAsync(
            string name,
            Func<ValueTask<bool>> action,
            string serializedReason,
            string rejectedReason)
        {
            try
            {
                await RunOptionalCaseAsync(name, action, serializedReason);
            }
            catch (NeoAstraException exception) when (IsUnsupportedResultType(exception))
            {
                Skip(name, rejectedReason);
            }
        }

        private void Skip(string name, string reason)
        {
            _skipped++;
            Console.WriteLine($"SKIP {name}: {reason}");
        }

        private void ReportBrowserScenarioSkips(NeoEnvironment environment)
        {
            Skip("permissions",
                IsSupported(environment, NeoCapability.Permissions)
                    ? "Permission prompts require a trusted user activation or device access; the noninteractive fixture cannot safely synthesize either."
                    : CapabilityReason(environment, NeoCapability.Permissions));
            Skip("downloads",
                IsSupported(environment, NeoCapability.Downloads)
                    ? "A real download mutates the filesystem; this non-destructive harness does not choose or create a destination."
                    : CapabilityReason(environment, NeoCapability.Downloads));
            Skip("file input",
                IsSupported(environment, NeoCapability.FileChooser)
                    ? "File selection exposes host paths and requires trusted user activation; no file is selected by this noninteractive harness."
                    : CapabilityReason(environment, NeoCapability.FileChooser));
            Skip("new windows", "The public API cannot synthesize trusted user activation deterministically without popup-blocker ambiguity.");
            Skip("process termination recovery", "The public API exposes recovery events but intentionally provides no destructive crash-injection hook.");
            Skip("popup opener/environment relationship and requested window features",
                IsSupported(environment, NeoCapability.TrackedPopups)
                    ? "Creating a real popup requires trusted user activation and a second hosted view; this remains a manual conformance scenario."
                    : CapabilityReason(environment, NeoCapability.TrackedPopups));
        }
    }

    private sealed class ObservedResourceProvider(INeoResourceProvider inner) : INeoResourceProvider
    {
        private readonly ConcurrentDictionary<string, byte> _paths = new(StringComparer.Ordinal);
        private int _fileBackedResponseCount;

        internal int FileBackedResponseCount => Volatile.Read(ref _fileBackedResponseCount);

        internal bool SawPath(string path) => _paths.ContainsKey(path);

        public NeoResourceResponse? GetResponse(NeoResourceRequest request)
        {
            _paths.TryAdd(request.Uri.AbsolutePath, 0);
            var response = inner.GetResponse(request);
            if (response?.FilePath is not null) Interlocked.Increment(ref _fileBackedResponseCount);
            return response;
        }
    }

    private static NeoEnvironmentOptions CreateEnvironmentOptions(INeoResourceProvider provider)
        => new()
        {
            IsPrivate = true,
            CustomSchemes = [NeoCustomScheme.Application("conformance", provider)],
        };

    private static NeoAstraOptions CreateViewOptions(NeoProfile? profile, BridgeMode bridgeMode)
    {
        var options = new NeoAstraOptions
        {
            Profile = profile,
            MaximumMessageSize = 1024 * 1024,
            ViewLabel = $"conformance-{Interlocked.Increment(ref _nextViewLabel)}",
        };
        if (bridgeMode == BridgeMode.TrustedOrigins)
        {
            options.BridgePolicy = NeoBridgePolicy.TrustedOrigins;
            options.BridgeOrigins = ["conformance://fixture"];
        }
        else if (bridgeMode == BridgeMode.TrustEntireView)
        {
            options.BridgePolicy = NeoBridgePolicy.TrustEntireView;
        }
        return options;
    }

    private static BridgeMode GetBridgeMode(NeoEnvironment environment)
    {
        if (OperatingSystem.IsLinux()) return BridgeMode.TrustEntireView;
        if ((OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) &&
            IsSupported(environment, NeoCapability.MessageOrigin))
        {
            return BridgeMode.TrustedOrigins;
        }
        return BridgeMode.Disabled;
    }

    private static async ValueTask NavigateAndWaitAsync(
        NeoAstra.NeoAstra view,
        Uri requestedUri,
        TimeSpan timeout,
        Uri? completedUri = null)
    {
        var completion = WaitForNavigationAsync(view, completedUri ?? requestedUri, timeout);
        await view.NavigateAsync(requestedUri);
        await completion;
    }

    private static async ValueTask NavigateCommandAndWaitAsync(
        NeoAstra.NeoAstra view,
        Uri expectedUri,
        Action command,
        TimeSpan timeout)
    {
        var completion = WaitForNavigationAsync(view, expectedUri, timeout);
        command();
        await completion;
    }

    private static async Task<NeoNavigationCompletedEventArgs> WaitForNavigationAsync(
        NeoAstra.NeoAstra view,
        Uri expectedUri,
        TimeSpan timeout)
    {
        var completion = new TaskCompletionSource<NeoNavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNavigation(object? _, NeoNavigationCompletedEventArgs navigation)
        {
            if (navigation.Uri is null || !SameLocation(navigation.Uri, expectedUri)) return;
            if (!navigation.IsSuccess)
            {
                completion.TrySetException(new InvalidOperationException(
                    $"Navigation to '{navigation.Uri}' failed with {navigation.ErrorCode} (native {navigation.NativeErrorCode})."));
            }
            else
            {
                completion.TrySetResult(navigation);
            }
        }

        view.NavigationCompleted += OnNavigation;
        try
        {
            return await completion.Task.WaitAsync(timeout);
        }
        finally
        {
            view.NavigationCompleted -= OnNavigation;
        }
    }

    private static async ValueTask<NeoWebMessageReceivedEventArgs> WaitForMessageAsync(
        NeoAstra.NeoAstra view,
        string kind,
        Func<ValueTask> send,
        TimeSpan timeout)
    {
        var completion = new TaskCompletionSource<NeoWebMessageReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnMessage(object? _, NeoWebMessageReceivedEventArgs message)
        {
            if (HasKind(message.Json, kind)) completion.TrySetResult(message);
        }

        view.MessageReceived += OnMessage;
        try
        {
            await send();
            return await completion.Task.WaitAsync(timeout);
        }
        finally
        {
            view.MessageReceived -= OnMessage;
        }
    }

    private static bool HasKind(string json, string expected)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("kind", out var kind) && kind.GetString() == expected;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async ValueTask<JsonDocument> EvaluateJsonAsync(NeoAstra.NeoAstra view, string script)
    {
        var json = await view.EvaluateScriptAsync(script);
        if (json is null) throw new InvalidOperationException("JavaScript evaluation unexpectedly returned null.");
        return JsonDocument.Parse(json);
    }

    // WKWebView fails an evaluation whose completion value it cannot return, a Promise included, with
    // WKErrorJavaScriptResultTypeIsUnsupported (5). A script that throws fails with WKErrorJavaScriptExceptionOccurred (4),
    // so the two are told apart by the native code: both arrive as a NeoAstraException. WebKitGTK does the same with
    // WEBKIT_JAVASCRIPT_ERROR_INVALID_RESULT (601) and WEBKIT_JAVASCRIPT_ERROR_SCRIPT_FAILED (699).
    private static bool IsUnsupportedResultType(NeoAstraException exception)
        => exception is { Domain: "wkwebview", NativeCode: 5 } or { Domain: "webkitgtk", NativeCode: 601 };

    // A backend can report a navigation as finished before a deferred module script has run (WKWebView does),
    // so state set by the fixture module is read only after the document itself has loaded.
    private static ValueTask WaitForDocumentLoadAsync(NeoAstra.NeoAstra view, TimeSpan timeout)
        => WaitUntilScriptAsync(view, "document.readyState === 'complete'", "The fixture document did not finish loading.", timeout);

    private static async ValueTask WaitUntilScriptAsync(
        NeoAstra.NeoAstra view,
        string script,
        string failureMessage,
        TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            var result = await view.EvaluateScriptAsync(script);
            if (string.Equals(result, "true", StringComparison.Ordinal)) return;
            await Task.Delay(25);
        }
        throw new TimeoutException(failureMessage);
    }

    private static async ValueTask WaitUntilAsync(Func<bool> condition, string failureMessage, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException(failureMessage);
    }

    private static bool SameLocation(Uri actual, Uri expected)
        => string.Equals(actual.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(actual.Host, expected.Host, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(actual.AbsolutePath, expected.AbsolutePath, StringComparison.Ordinal) &&
           string.Equals(actual.Query, expected.Query, StringComparison.Ordinal);

    private static bool IsSupported(NeoEnvironment environment, NeoCapability capability)
        => environment.GetCapability(capability).SupportLevel != NeoSupportLevel.None;

    private static void RequireCapability(NeoEnvironment environment, NeoCapability capability, string message)
    {
        if (!IsSupported(environment, capability))
        {
            throw new PlatformNotSupportedException($"{message} {CapabilityReason(environment, capability)}");
        }
    }

    private static string CapabilityReason(NeoEnvironment environment, NeoCapability capability)
    {
        var info = environment.GetCapability(capability);
        return info.Details is null
            ? $"{capability} support is {info.SupportLevel}."
            : $"{capability} support is {info.SupportLevel}: {info.Details}";
    }

    private static void PrintRuntime(NeoRuntimeInfo runtime)
    {
        Console.WriteLine(
            $"RUNTIME backend={runtime.BackendName} backendVersion={runtime.BackendVersion} " +
            $"browser={runtime.BrowserVersion} os={runtime.OperatingSystem} architecture={runtime.Architecture}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private enum BridgeMode
    {
        Disabled,
        TrustedOrigins,
        TrustEntireView,
    }

    private sealed record HarnessOptions(bool Stress, TimeSpan Timeout)
    {
        internal static bool TryParse(string[] args, out HarnessOptions options)
        {
            var stress = false;
            var timeoutSeconds = 20;
            var sawRun = false;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--run" when !sawRun:
                        sawRun = true;
                        break;
                    case "--stress" when !stress:
                        stress = true;
                        break;
                    case "--timeout-seconds" when index + 1 < args.Length &&
                                                   int.TryParse(args[++index], out var parsed) &&
                                                   parsed is >= 5 and <= 300:
                        timeoutSeconds = parsed;
                        break;
                    default:
                        options = null!;
                        return false;
                }
            }

            options = new HarnessOptions(stress, TimeSpan.FromSeconds(stress && timeoutSeconds == 20 ? 120 : timeoutSeconds));
            return sawRun;
        }
    }
}
