// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

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
        var first = NeoCustomScheme.Application("app");
        var second = NeoCustomScheme.Create("APP");
        var options = new NeoEnvironmentOptions { CustomSchemes = [first, second] };

        Assert.ThrowsExactly<ArgumentException>(options.Validate);

        first.AllowedOrigins = ["not an origin"];
        options.CustomSchemes = [first];
        Assert.ThrowsExactly<ArgumentException>(options.Validate);
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
                    await using var environment = await application.CreateEnvironmentAsync();
                    Assert.AreEqual("windows", environment.RuntimeInfo.OperatingSystem);
                    Assert.AreEqual(NeoSupportLevel.Native, environment.GetCapability(NeoCapability.ScriptDocumentStart).SupportLevel);
                    Assert.AreEqual(NeoSupportLevel.Native, environment.GetCapability(NeoCapability.Cookies).SupportLevel);
                    Assert.AreEqual(NeoSupportLevel.Native, environment.GetCapability(NeoCapability.Permissions).SupportLevel);
                    Assert.AreEqual(NeoSupportLevel.Native, environment.GetCapability(NeoCapability.PermissionPersistence).SupportLevel);
                    Assert.AreEqual(NeoSupportLevel.Native, environment.GetCapability(NeoCapability.Zoom).SupportLevel);
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
                    webView.NavigationCompleted += (_, args) => navigation.TrySetResult(args);
                    await webView.LoadHtmlAsync("<!doctype html><title>smoke</title><p>NeoWebView</p>");
                    var completed = await navigation.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.IsTrue(completed.IsSuccess);
                    Assert.AreEqual("42", await webView.EvaluateScriptAsync("globalThis.neoWebViewInjected + 2"));
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
}
