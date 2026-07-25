// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using NeoWebView.Interop;

namespace NeoWebView.Tests;

[TestClass]
public sealed class ManagedApiTests
{
    [TestMethod]
    public void Utf8String_UsesPointerAndByteLengthWithoutTerminator()
    {
        const string text = "héllo\0世界";
        using var value = new Utf8String(text);

        Assert.AreEqual(Encoding.UTF8.GetByteCount(text), value.ByteLength);
        Assert.AreEqual(text, Utf8String.Decode(value.View));
    }

    [TestMethod]
    public void Cookie_ValidatesPortableFields()
    {
        var cookie = new NeoCookie("session", "value", "example.test")
        {
            IsSecure = true,
            IsHttpOnly = true,
            SameSite = NeoCookieSameSite.Strict,
        };

        Assert.IsTrue(cookie.IsSession);
        Assert.ThrowsExactly<ArgumentException>(() => new NeoCookie("bad=name", "value", "example.test"));
        Assert.ThrowsExactly<ArgumentException>(() => new NeoCookie("name", "value", "example.test", "relative"));
    }

    [TestMethod]
    public void EnvironmentOptions_RejectDuplicateSchemesAndInvalidOrigins()
    {
        var provider = new NullResourceProvider();
        var first = NeoCustomScheme.Application("app", provider);
        var second = NeoCustomScheme.Create("APP");
        second.ResourceProvider = provider;
        var options = new NeoEnvironmentOptions { CustomSchemes = [first, second] };

        Assert.ThrowsExactly<ArgumentException>(options.Validate);

        first.AllowedOrigins = ["not an origin"];
        options.CustomSchemes = [first];
        Assert.ThrowsExactly<ArgumentException>(options.Validate);

        Assert.ThrowsExactly<ArgumentException>(() => NeoCustomScheme.Create("https"));
    }

    [TestMethod]
    public void CustomScheme_ApplicationDefaultsAndResponseShapesMatchPortableContract()
    {
        var provider = new NullResourceProvider();
        var scheme = NeoCustomScheme.Application("Assets", provider);
        var bytes = NeoResourceResponse.FromBytes(new byte[] { 1, 2, 3 }, "application/octet-stream");
        var filePath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(filePath, [4, 5, 6, 7]);
            var file = NeoResourceResponse.FromFile(filePath, "application/octet-stream");

            Assert.AreEqual("assets", scheme.Name);
            Assert.IsTrue(scheme.HasAuthority);
            Assert.IsTrue(scheme.IsSecure);
            Assert.IsTrue(scheme.IsApplicationScheme);
            Assert.AreSame(provider, scheme.ResourceProvider);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, bytes.Bytes.ToArray());
            Assert.AreEqual(3, bytes.ContentLength);
            Assert.IsNull(bytes.FilePath);
            Assert.AreEqual(Path.GetFullPath(filePath), file.FilePath);
            Assert.AreEqual(4, file.ContentLength);
            Assert.IsTrue(file.Bytes.IsEmpty);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [TestMethod]
    public void PlatformBackends_SourcePairSchemeHandlingAndBridgeLimitations()
    {
        var cocoa = File.ReadAllText(FindRepositoryFile("native", "src", "macos", "cocoa_backend.mm"));
        var gtk = File.ReadAllText(FindRepositoryFile("native", "src", "linux", "gtk_backend.cpp"));
        var common = File.ReadAllText(FindRepositoryFile("native", "src", "common", "neowebview.cpp"));
        var managed = File.ReadAllText(FindRepositoryFile("src", "NeoWebView", "NeoEnvironment.cs"));

        StringAssert.Contains(cocoa, "NSObject<WKURLSchemeHandler>");
        StringAssert.Contains(cocoa, "startURLSchemeTask:");
        StringAssert.Contains(cocoa, "setURLSchemeHandler:handler");
        StringAssert.Contains(cocoa, "NSDataReadingMappedIfSafe");
        StringAssert.Contains(cocoa, "response.release(response.release_context)");
        StringAssert.Contains(cocoa, "neo_bridge_origin_allowed(view,uri)");
        StringAssert.Contains(cocoa, "NSJSONWritingFragmentsAllowed");
        StringAssert.Contains(cocoa, "message.frameInfo.mainFrame?1u:0u");
        StringAssert.Contains(gtk, "webkit_web_context_register_uri_scheme");
        StringAssert.Contains(gtk, "webkit_uri_scheme_request_get_http_method");
        StringAssert.Contains(gtk, "webkit_uri_scheme_request_get_http_headers");
        StringAssert.Contains(gtk, "webkit_uri_scheme_request_get_http_body");
        StringAssert.Contains(gtk, "maximum_resource_request_body_length");
        StringAssert.Contains(gtk, "g_file_read");
        StringAssert.Contains(gtk, "webkit_uri_scheme_request_finish_with_response");
        StringAssert.Contains(gtk, "native_headers.release()");
        StringAssert.Contains(gtk, "neo_resource_response_release_guard");
        StringAssert.Contains(gtk, "WebKitGTK custom schemes do not support service workers");
        StringAssert.Contains(common, "WebKitGTK 4.1 script messages do not expose trustworthy source-origin data");
        StringAssert.Contains(managed, "!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()");
    }

    [TestMethod]
    public void DirectoryResourceProvider_ServesAssetsAndRejectsTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"neowebview-assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "index.html"), "<h1>local</h1>");
            var provider = new NeoDirectoryResourceProvider(root);
            var request = new NeoResourceRequest(new Uri("app://neowebview/index.html"), "GET",
                new Dictionary<string, string>(), null, NeoResourceKind.Document, true, default);

            var response = provider.GetResponse(request);
            Assert.IsNotNull(response);
            Assert.AreEqual(Path.Combine(root, "index.html"), response.FilePath);
            Assert.AreEqual("text/html; charset=utf-8", response.MimeType);
            Assert.AreEqual(new FileInfo(response.FilePath!).Length, response.ContentLength);
            Assert.AreEqual("nosniff", response.Headers["X-Content-Type-Options"]);

            var head = provider.GetResponse(request with { Method = "HEAD" });
            Assert.IsNotNull(head);
            Assert.IsNull(head.FilePath);
            Assert.AreEqual(response.ContentLength, head.ContentLength);

            var traversal = provider.GetResponse(request with { Uri = new Uri("app://neowebview/%252e%252e/secret.txt") });
            Assert.IsNotNull(traversal);
            Assert.AreEqual(400, traversal.StatusCode);
            Assert.IsNull(provider.GetResponse(request with { Uri = new Uri("app://neowebview/missing.txt") }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void TimeRange_RejectsReversedRange()
    {
        var range = new NeoTimeRange(DateTimeOffset.UnixEpoch.AddDays(1), DateTimeOffset.UnixEpoch);
        Assert.ThrowsExactly<ArgumentException>(range.Validate);
    }

    [TestMethod]
    public void HostFactories_ValidatePlatformKindAndZero()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            NeoWebViewHost.FromNativeParent(new NeoNativeHandle(NeoNativeHandleKind.WebView2Core, 1)));

        if (OperatingSystem.IsWindows())
        {
            Assert.ThrowsExactly<ArgumentException>(() => NeoWebViewHost.FromWin32Hwnd(0));
            Assert.IsNotNull(NeoWebViewHost.FromWin32Hwnd(1));
            Assert.ThrowsExactly<PlatformNotSupportedException>(() => NeoWebViewHost.FromCocoaNSView(1));
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.ThrowsExactly<ArgumentException>(() => NeoWebViewHost.FromCocoaNSView(0));
            Assert.IsNotNull(NeoWebViewHost.FromCocoaNSView(1));
            Assert.ThrowsExactly<PlatformNotSupportedException>(() => NeoWebViewHost.FromWin32Hwnd(1));
        }
        else
        {
            Assert.ThrowsExactly<ArgumentException>(() => NeoWebViewHost.FromGtkWidget(0));
            Assert.IsNotNull(NeoWebViewHost.FromGtkWidget(1));
            Assert.ThrowsExactly<PlatformNotSupportedException>(() => NeoWebViewHost.FromWin32Hwnd(1));
        }
    }

    [TestMethod]
    public void NativeHandle_NamedConversionChecksKind()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.ThrowsExactly<PlatformNotSupportedException>(() =>
                new NeoNativeHandle(NeoNativeHandleKind.Win32Hwnd, 42).GetWin32Hwnd());
            return;
        }

        Assert.AreEqual((nint)42, new NeoNativeHandle(NeoNativeHandleKind.Win32Hwnd, 42).GetWin32Hwnd());
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new NeoNativeHandle(NeoNativeHandleKind.WebView2Core, 42).GetWin32Hwnd());
    }

    [TestMethod]
    public void ExceptionMapping_UsesTypedExceptionsAndPreservesDetails()
    {
        var invalid = NativeError.CreateException(
            new NativeErrorInfo(NeoErrorCode.InvalidArgument, "bad value", "test", 123),
            "unit test");
        var security = NativeError.CreateException(
            new NativeErrorInfo(NeoErrorCode.Security, "denied", "test", 456),
            "unit test");
        var native = NativeError.CreateException(
            new NativeErrorInfo(NeoErrorCode.NativeFailure, "failed", "backend", -7),
            "unit test");

        Assert.IsInstanceOfType<ArgumentException>(invalid);
        Assert.AreEqual("test", invalid.Data["NeoWebView.Domain"]);
        Assert.IsInstanceOfType<SecurityException>(security);
        var detailed = Assert.IsInstanceOfType<NeoWebViewException>(native);
        Assert.AreEqual(NeoErrorCode.NativeFailure, detailed.Code);
        Assert.AreEqual("backend", detailed.Domain);
        Assert.AreEqual(-7, detailed.NativeCode);
    }

    [TestMethod]
    public async Task NativeOperation_CompletesManagedTaskExactlyOnce()
    {
        var operation = new NativeOperation<int>(CancellationToken.None);

        operation.Complete(7);
        operation.Complete(9);

        Assert.AreEqual(7, await operation.ValueTask);
    }

    [TestMethod]
    public async Task NativeOperation_CancellationWinsButNativeCompletionCleansUp()
    {
        using var source = new CancellationTokenSource();
        var operation = new NativeOperation<int>(source.Token);

        source.Cancel();
        operation.Complete(7);

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await operation.ValueTask.AsTask());
    }

    [TestMethod]
    public async Task NativeOperation_CancellationAndCompletionRaceCompletesExactlyOnce()
    {
        const int iterations = 500;
        for (var index = 0; index < iterations; index++)
        {
            using var source = new CancellationTokenSource();
            var operation = new NativeOperation<int>(source.Token);
            await Task.WhenAll(
                Task.Run(source.Cancel),
                Task.Run(() => operation.Complete(index)));

            try
            {
                Assert.AreEqual(index, await operation.ValueTask);
            }
            catch (TaskCanceledException exception)
            {
                Assert.AreEqual(source.Token, exception.CancellationToken);
            }
        }
    }

    [TestMethod]
    public async Task DeferredDecision_UsesResultOrSafeDefault()
    {
        var accepted = await NeoWebView.ResolveDecisionAsync(
            static () => ValueTask.FromResult(7), static value => value * 2, -1, TimeSpan.FromSeconds(1));
        var failed = await NeoWebView.ResolveDecisionAsync<int, int>(
            static () => throw new InvalidOperationException("policy failed"), static value => value, -1, TimeSpan.FromSeconds(1));
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timedOut = await NeoWebView.ResolveDecisionAsync(
            () => new ValueTask<int>(pending.Task), static value => value, -1, TimeSpan.FromMilliseconds(10));

        Assert.AreEqual(14, accepted);
        Assert.AreEqual(-1, failed);
        Assert.AreEqual(-1, timedOut);
        pending.SetResult(9);
    }

    [TestMethod]
    public void Phase4Decisions_ValidateTerminalPayloads()
    {
        Assert.AreEqual(NeoDecisionAction.Download, NeoDownloadDecision.Accept("download.bin").Action);
        Assert.AreEqual(NeoDecisionAction.HandledExternal, NeoDownloadDecision.HandledExternal.Action);
        Assert.ThrowsExactly<ArgumentException>(() => NeoDownloadDecision.Accept(" "));
        CollectionAssert.AreEqual(new[] { "a.txt", "b.txt" }, NeoFileChooserDecision.Select("a.txt", "b.txt").Paths!.ToArray());
        Assert.ThrowsExactly<ArgumentException>(() => NeoFileChooserDecision.Select());
        Assert.AreEqual("secret", NeoAuthenticationDecision.Credentials("user", "secret").Password);
        Assert.AreEqual(2, NeoClientCertificateDecision.Select(2).SelectedIndex);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => NeoClientCertificateDecision.Select(-1));
        Assert.AreEqual(NeoDecisionAction.Deny, NeoTlsErrorDecision.Deny.Action);
        Assert.AreEqual(NeoDecisionAction.Cancel, NeoScriptDialogDecision.Cancel.Action);
    }

    [TestMethod]
    public void ProcessFailure_DecodesPortableKindFlagsAndRecovery()
    {
        var failure = NeoWebView.DecodeProcessFailure(
            (ulong)NeoProcessFailureKind.BrowserProcessExited | (1UL << 32) | (1UL << 34),
            -42,
            "browser");
        var unknown = NeoWebView.DecodeProcessFailure(999, 0, string.Empty);

        Assert.AreEqual(NeoProcessFailureKind.BrowserProcessExited, failure.Kind);
        Assert.IsTrue(failure.IsCrash);
        Assert.AreEqual(NeoProcessRecoveryAction.RestartApplication, failure.RecoveryAction);
        Assert.AreEqual(-42, failure.NativeCode);
        Assert.AreEqual("browser", failure.ProcessDescription);
        Assert.AreEqual(NeoProcessFailureKind.Unknown, unknown.Kind);
        Assert.IsNull(unknown.ProcessDescription);
    }

    [TestMethod]
    public void NativeLoader_ReportsActionableFailureOrLoadsCompatibleAbi()
    {
        try
        {
            NativeLibraryLoader.EnsureLoaded();
        }
        catch (NeoWebViewNativeLibraryException exception)
        {
            StringAssert.Contains(exception.Message, "neowebview_native");
            StringAssert.Contains(exception.Message, "NEOWEBVIEW_NATIVE_LIBRARY");
        }
    }

    [TestMethod]
    public async Task AttachedApplication_SmokeTestWhenDevelopmentLibraryIsAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var result = await RunStaAsync(() =>
            {
                var application = NeoApplication.AttachToCurrentThread(new NeoApplicationOptions
                {
                    ApplicationName = "NeoWebView managed smoke test",
                    ShutdownMode = NeoApplicationShutdownMode.Explicit,
                });
                try
                {
                    return application.Dispatcher.CheckAccess() && application.Windows.Count == 0;
                }
                finally
                {
                    application.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            });
            Assert.IsTrue(result);
        }
        catch (NeoWebViewNativeLibraryException)
        {
            // Native assets are optional for the managed unit-test project.
        }
    }

    [TestMethod]
    public async Task AttachedApplication_WorkerDisposalCompletesWhileHostPumps()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            await RunStaAsync(() =>
            {
                var application = NeoApplication.AttachToCurrentThread(new NeoApplicationOptions
                {
                    ApplicationName = "NeoWebView worker disposal test",
                    ShutdownMode = NeoApplicationShutdownMode.Explicit,
                });
                var disposal = Task.Run(async () => await application.DisposeAsync());
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (!disposal.IsCompleted && DateTime.UtcNow < deadline)
                {
                    PumpWindowsMessages();
                    Thread.Sleep(1);
                }

                disposal.GetAwaiter().GetResult();
                Assert.ThrowsExactly<ObjectDisposedException>(() => application.Dispatcher.Post(() => { }));
                return true;
            });
        }
        catch (NeoWebViewNativeLibraryException)
        {
            // Native assets are optional for the managed unit-test project.
        }
    }

    [TestMethod]
    public async Task AttachedApplication_ConcurrentShutdownAndDisposalIsIdempotent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            await RunStaAsync(() =>
            {
                var application = NeoApplication.AttachToCurrentThread(new NeoApplicationOptions
                {
                    ApplicationName = "NeoWebView concurrent disposal test",
                    ShutdownMode = NeoApplicationShutdownMode.Explicit,
                });
                using var start = new ManualResetEventSlim();
                var disposals = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
                {
                    start.Wait();
                    await application.DisposeAsync();
                })).ToArray();
                var shutdowns = Enumerable.Range(0, 4).Select(index => Task.Run(() =>
                {
                    start.Wait();
                    application.Shutdown(index);
                })).ToArray();
                var all = Task.WhenAll(disposals.Concat(shutdowns));
                start.Set();
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (!all.IsCompleted && DateTime.UtcNow < deadline)
                {
                    PumpWindowsMessages();
                    Thread.Sleep(1);
                }

                Assert.IsTrue(all.IsCompleted, "Concurrent application disposal did not complete while the UI loop was pumped.");
                all.GetAwaiter().GetResult();
                Assert.ThrowsExactly<ObjectDisposedException>(() => application.Dispatcher.Post(() => { }));
                return true;
            });
        }
        catch (NeoWebViewNativeLibraryException)
        {
            // Native assets are optional for the managed unit-test project.
        }
    }

    [TestMethod]
    public async Task AttachedApplication_ShutdownCancelsAcceptedManagedDispatcherWorkExactlyOnce()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            await RunStaAsync(() =>
            {
                var application = NeoApplication.AttachToCurrentThread(new NeoApplicationOptions
                {
                    ApplicationName = "NeoWebView dispatcher shutdown test",
                    ShutdownMode = NeoApplicationShutdownMode.Explicit,
                });
                var executions = 0;
                var pending = application.Dispatcher.InvokeAsync(() => Interlocked.Increment(ref executions)).AsTask();
                application.Shutdown();

                Assert.IsTrue(pending.IsCanceled);
                var cancellation = Assert.ThrowsExactly<TaskCanceledException>(() => pending.GetAwaiter().GetResult());
                Assert.IsTrue(cancellation.CancellationToken.IsCancellationRequested);
                Assert.AreEqual(0, executions);
                Assert.ThrowsExactly<ObjectDisposedException>(() => application.Dispatcher.Post(() => { }));

                application.DisposeAsync().AsTask().GetAwaiter().GetResult();
                Assert.AreEqual(0, executions);
                return true;
            });
        }
        catch (NeoWebViewNativeLibraryException)
        {
            // Native assets are optional for the managed unit-test project.
        }
    }

    [TestMethod]
    public async Task AttachedApplication_ForwardsNativeLogsAndContainsLoggerExceptions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            await RunStaAsync(() =>
            {
                var logs = new ConcurrentQueue<NeoLogMessage>();
                var application = NeoApplication.AttachToCurrentThread(new NeoApplicationOptions
                {
                    ApplicationName = "NeoWebView logging test",
                    ShutdownMode = NeoApplicationShutdownMode.Explicit,
                    LogCallback = logs.Enqueue,
                });
                application.DisposeAsync().AsTask().GetAwaiter().GetResult();

                var messages = logs.ToArray();
                Assert.IsTrue(messages.Length >= 2);
                Assert.IsTrue(messages.Any(message => message.Message.Contains("initialized", StringComparison.Ordinal)));
                Assert.IsTrue(messages.Any(message => message.Message.Contains("shutdown", StringComparison.Ordinal)));
                Assert.IsTrue(messages.All(message => message.Level == NeoLogLevel.Information));
                Assert.IsTrue(messages.All(message => message.Category == "application"));
                Assert.IsTrue(messages.All(message => message.NativeThreadId != 0 && message.TimestampNanoseconds != 0));

                var throwingApplication = NeoApplication.AttachToCurrentThread(new NeoApplicationOptions
                {
                    ApplicationName = "NeoWebView throwing logger test",
                    ShutdownMode = NeoApplicationShutdownMode.Explicit,
                    LogCallback = static _ => throw new InvalidOperationException("logger failed"),
                });
                throwingApplication.DisposeAsync().AsTask().GetAwaiter().GetResult();
                return true;
            });
        }
        catch (NeoWebViewNativeLibraryException)
        {
            // Native assets are optional for the managed unit-test project.
        }
    }

    [TestMethod]
    public async Task StandaloneAsyncCallback_SmokeTestWhenDevelopmentLibraryIsAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var run = RunStaAsync(() => NeoApplication.Run(
                new NeoApplicationOptions
                {
                    ApplicationName = "NeoWebView async callback smoke test",
                    ShutdownMode = NeoApplicationShutdownMode.Explicit,
                },
                async application =>
                {
                    var resources = new SmokeResourceProvider();
                    await using var environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions
                    {
                        CustomSchemes = [NeoCustomScheme.Application("app", resources)],
                    });
                    Assert.AreEqual("windows", environment.RuntimeInfo.OperatingSystem);
                    Assert.AreEqual(NeoSupportLevel.Native, environment.GetCapability(NeoCapability.CustomScheme).SupportLevel);
                    Assert.AreEqual(NeoSupportLevel.Native, environment.GetCapability(NeoCapability.ScriptDocumentStart).SupportLevel);
                    Assert.AreEqual(NeoSupportLevel.Native, environment.GetCapability(NeoCapability.Cookies).SupportLevel);
                    Assert.AreEqual(NeoSupportLevel.Native, environment.GetCapability(NeoCapability.Permissions).SupportLevel);
                    Assert.AreEqual(NeoSupportLevel.Native, environment.GetCapability(NeoCapability.PermissionPersistence).SupportLevel);
                    Assert.AreEqual(NeoSupportLevel.Native, environment.GetCapability(NeoCapability.Zoom).SupportLevel);
                    Assert.AreEqual(NeoSupportLevel.Native, environment.GetCapability(NeoCapability.Downloads).SupportLevel);
                    Assert.AreEqual(NeoSupportLevel.Native, environment.GetCapability(NeoCapability.TrackedPopups).SupportLevel);
                    Assert.AreEqual(NeoSupportLevel.Native, environment.GetCapability(NeoCapability.ScriptDialogs).SupportLevel);
                    Assert.AreEqual(NeoSupportLevel.None, environment.GetCapability(NeoCapability.FileChooser).SupportLevel);
                    Assert.AreEqual(NeoSupportLevel.Native, environment.GetCapability(NeoCapability.HttpAuthentication).SupportLevel);
                    Assert.AreEqual(NeoSupportLevel.Native, environment.GetCapability(NeoCapability.ClientCertificates).SupportLevel);
                    Assert.AreEqual(NeoSupportLevel.Native, environment.GetCapability(NeoCapability.TlsErrorDecisions).SupportLevel);
                    Assert.AreEqual(NeoSupportLevel.Native, environment.GetCapability(NeoCapability.FullscreenDecisions).SupportLevel);
                    var window = application.CreateWindow(new NeoWindowOptions { IsVisible = false });
                    window.MaximumClientSize = new NeoSize(1200, 900);
                    window.MinimumClientSize = new NeoSize(320, 200);
                    Assert.AreEqual(new NeoSize(1200, 900), window.MaximumClientSize);
                    Assert.AreEqual(new NeoSize(320, 200), window.MinimumClientSize);
                    window.State = NeoWindowState.Normal;
                    Assert.AreEqual(NeoWindowState.Normal, window.State);
                    await using var profile = await environment.CreateProfileAsync(new NeoProfileOptions { Name = "smoke-profile", IsEphemeral = true });
                    await using var webView = await environment.CreateWebViewAsync(
                        NeoWebViewHost.FillWindow(window),
                        new NeoWebViewOptions { Profile = profile });
                    webView.ZoomFactor = 1.25;
                    Assert.AreEqual(1.25, webView.ZoomFactor, 0.001);
                    webView.ResetZoom();
                    Assert.AreEqual(1d, webView.ZoomFactor, 0.001);
                    Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => webView.ZoomFactor = 0.1);
                    await using var userScript = await webView.AddScriptAsync("globalThis.neoWebViewInjected = 40;");
                    var navigation = new TaskCompletionSource<NeoNavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var message = new TaskCompletionSource<NeoWebMessageReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                    webView.NavigationCompleted += (_, args) => navigation.TrySetResult(args);
                    webView.MessageReceived += (_, args) => message.TrySetResult(args);
                    await webView.NavigateAsync(new Uri("app://neowebview/index.html"));
                    var completed = await navigation.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.IsTrue(completed.IsSuccess);
                    Assert.IsGreaterThanOrEqualTo(1, resources.RequestCount);
                    var received = await message.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.IsTrue(received.IsMainFrame);
                    using var receivedJson = System.Text.Json.JsonDocument.Parse(received.Json);
                    StringAssert.Contains(received.Json, "custom-scheme-ready");
                    Assert.AreEqual("42", await webView.EvaluateScriptAsync("globalThis.neoWebViewInjected + 2"));
                    var popup = new TaskCompletionSource<NeoNewWindowRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
                    webView.NewWindowRequested = request =>
                    {
                        popup.TrySetResult(request);
                        return ValueTask.FromResult(NeoNewWindowDecision.Cancel);
                    };
                    await webView.EvaluateScriptAsync("window.open('https://example.test/popup', 'smoke-popup'); true");
                    var popupRequest = await popup.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.AreEqual(new Uri("https://example.test/popup"), popupRequest.TargetUri);
                    Assert.AreEqual("smoke-popup", popupRequest.FrameName);
                    var cookie = new NeoCookie("smoke", "value", "example.test")
                    {
                        IsHttpOnly = true,
                        SameSite = NeoCookieSameSite.Lax,
                    };
                    await profile.SetCookieAsync(cookie);
                    var cookies = await profile.GetCookiesAsync(new Uri("https://example.test/"));
                    var stored = cookies.Single(value => value.Name == cookie.Name);
                    Assert.AreEqual(cookie.Value, stored.Value);
                    Assert.IsTrue(stored.IsHttpOnly);
                    Assert.AreEqual(cookie.SameSite, stored.SameSite);
                    await profile.DeleteCookieAsync(stored);
                    Assert.IsFalse((await profile.GetCookiesAsync(new Uri("https://example.test/"))).Any(value => value.Name == cookie.Name), $"Deleted cookie remained: domain={stored.Domain}, path={stored.Path}, session={stored.IsSession}");
                    await profile.SetCookieAsync(cookie);
                    await profile.ClearDataAsync(NeoBrowsingDataKinds.Cookies);
                    Assert.IsFalse((await profile.GetCookiesAsync(new Uri("https://example.test/"))).Any(value => value.Name == cookie.Name));
                    application.Shutdown(17);
                }));

            Assert.AreEqual(17, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        catch (NeoWebViewNativeLibraryException)
        {
            // Native assets are optional for the managed unit-test project.
        }
    }

    private static Task<T> RunStaAsync<T>(Func<T> callback)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(callback());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "NeoWebView test STA",
        };
        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }
        thread.Start();
        return completion.Task;
    }

    private static void PumpWindowsMessages()
    {
        while (PeekMessageW(out var message, 0, 0, 0, 1))
        {
            TranslateMessage(in message);
            DispatchMessageW(in message);
        }
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(path)) return path;
        }

        throw new FileNotFoundException($"Could not locate repository file '{Path.Combine(parts)}' from '{AppContext.BaseDirectory}'.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal nint Window;
        internal uint Message;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int X;
        internal int Y;
        internal uint Private;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out NativeMessage message, nint window, uint minimum, uint maximum, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in NativeMessage message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(in NativeMessage message);

    private sealed class NullResourceProvider : INeoResourceProvider
    {
        public NeoResourceResponse? GetResponse(NeoResourceRequest request) => null;
    }

    private sealed class SmokeResourceProvider : INeoResourceProvider
    {
        private int _requestCount;

        internal int RequestCount => Volatile.Read(ref _requestCount);

        public NeoResourceResponse? GetResponse(NeoResourceRequest request)
        {
            Interlocked.Increment(ref _requestCount);
            if (!request.Uri.AbsolutePath.Equals("/index.html", StringComparison.Ordinal)) return null;
            const string html = "<!doctype html><title>smoke</title><script>chrome.webview.postMessage({message:'custom-scheme-ready'})</script>";
            return NeoResourceResponse.FromBytes(Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8");
        }
    }
}
