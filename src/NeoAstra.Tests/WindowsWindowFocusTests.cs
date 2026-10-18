// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;

namespace NeoAstra.Tests;

[TestClass]
public sealed class WindowsWindowFocusTests
{
    [TestMethod]
    [Timeout(60000)]
    public async Task KeyboardGivenToTheWindowGoesToItsBrowser()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This regression drives the keyboard focus of a Win32 window.");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stage = "startup";
        var thread = new Thread(() =>
        {
            try
            {
                NeoApplication.Run(new NeoApplicationOptions { ApplicationName = "NeoAstra window focus test", QueueInitialLaunchEvent = false, ShutdownMode = NeoApplicationShutdownMode.Explicit }, async application =>
                {
                    try
                    {
                        var window = application.CreateWindow(new NeoWindowOptions { Label = "focus-window", Title = "Focus", Width = 480, Height = 320 });
                        window.Show();
                        stage = "browser creation";
                        // The same custom scheme as the other WebView2 tests, which share a browser of the same options.
                        await using var environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions
                        {
                            UserDataRoot = TestUserData.Shared,
                            CustomSchemes = [NeoCustomScheme.Application("app", new FocusPageProvider())],
                        });
                        await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), new NeoAstraOptions { ViewLabel = "focus-view" });
                        await view.NavigateAsync(new Uri("app://neoastra/index.html"));

                        stage = "view created in a window that holds the keyboard";
                        var parent = window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
                        await WaitForBrowserFocusAsync(parent, "A view created in a window that holds the keyboard must take it.", timeout.Token);

                        stage = "keyboard given to the window";
                        // What activation does after a switch to another application and back: the window itself gets the keyboard.
                        SetFocus(0);
                        SetFocus(parent);
                        await WaitForBrowserFocusAsync(parent, "The keyboard given to the window must go to its browser, where the page can be typed into.", timeout.Token);
                    }
                    finally { application.ForceShutdown(); }
                });
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(50)); }
        catch (TimeoutException) { Assert.Fail($"The window focus test hung during {stage}."); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { Assert.Fail($"The window focus test timed out during {stage}: the window kept the keyboard."); }
        catch (NeoAstraNativeLibraryException) { Assert.Inconclusive("The Windows native runtime is not staged."); }
    }

    private sealed class FocusPageProvider : INeoResourceProvider
    {
        public NeoResourceResponse GetResponse(NeoResourceRequest request)
            => NeoResourceResponse.FromBytes("<!doctype html><title>focus</title><input id=prompt>"u8.ToArray(), "text/html; charset=utf-8");
    }

    private static async Task WaitForBrowserFocusAsync(nint window, string message, CancellationToken cancellationToken)
    {
        for (var index = 0; index < 250 && GetFocus() == window; index++) await Task.Delay(20, cancellationToken);
        var focus = GetFocus();
        Assert.AreNotEqual(window, focus, message);
        Assert.IsTrue(focus != 0 && IsChild(window, focus), message);
    }

#pragma warning disable SYSLIB1054 // Test-only Win32 interop does not need source-generated interop.
    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetFocus();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(nint parent, nint window);
#pragma warning restore SYSLIB1054
}
