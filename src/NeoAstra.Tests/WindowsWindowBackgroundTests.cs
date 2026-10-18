// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;

namespace NeoAstra.Tests;

[TestClass]
public sealed class WindowsWindowBackgroundTests
{
    private const uint WmEraseBackground = 0x0014;
    private static readonly Guid Controller2Interface = new("C979903E-D4CA-4228-92EB-47EE3FA96EAB");

    [TestMethod]
    [Timeout(60000)]
    public async Task WindowBackgroundColorPaintsTheWindowAndTheBrowserAndFollowsChanges()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This regression reads the Win32 window and WebView2 controller background.");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stage = "startup";
        var thread = new Thread(() =>
        {
            try
            {
                NeoApplication.Run(new NeoApplicationOptions { ApplicationName = "NeoAstra background test", QueueInitialLaunchEvent = false, ShutdownMode = NeoApplicationShutdownMode.Explicit }, async application =>
                {
                    try
                    {
                        var dark = new NeoColor(12, 34, 56);
                        var window = application.CreateWindow(new NeoWindowOptions { Label = "background-window", Title = "Background", Width = 320, Height = 200, BackgroundColor = dark });
                        var hwnd = window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
                        stage = "window erased with its color";
                        Assert.AreEqual(dark, ErasedColor(hwnd), "The window must erase its client area with its background color.");

                        stage = "browser created with the window color";
                        await using var environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions
                        {
                            UserDataRoot = TestUserData.Shared,
                            CustomSchemes = [NeoCustomScheme.Application("app", new BlankProvider())],
                        });
                        await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), new NeoAstraOptions { ViewLabel = "background-view" });
                        var controller = view.GetNativeHandle(NeoNativeHandleKind.WebView2Controller).Value;
                        Assert.AreEqual(dark, DefaultBackground(controller), "The browser must show the window color before its page paints.");

                        stage = "color changed at run time";
                        var light = new NeoColor(240, 230, 220);
                        window.BackgroundColor = light;
                        Assert.AreEqual(light, window.BackgroundColor);
                        Assert.AreEqual(light, ErasedColor(hwnd));
                        Assert.AreEqual(light, DefaultBackground(controller));
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
        catch (TimeoutException) { Assert.Fail($"The background test hung during {stage}."); }
        catch (NeoAstraNativeLibraryException) { Assert.Inconclusive("The Windows native runtime is not staged."); }
    }

    // What the window paints when asked to erase, in a memory device context: independent of the screen and of other windows.
    private static NeoColor ErasedColor(nint window)
    {
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, 16, 16);
        var previous = SelectObject(memory, bitmap);
        try
        {
            SendMessage(window, WmEraseBackground, (nuint)memory, 0);
            var pixel = GetPixel(memory, 8, 8);
            return new NeoColor((byte)(pixel & 0xff), (byte)((pixel >> 8) & 0xff), (byte)((pixel >> 16) & 0xff));
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    // ICoreWebView2Controller2::get_DefaultBackgroundColor, through the COM table of the controller NeoAstra hands out.
    private static NeoColor DefaultBackground(nint controller)
    {
        var iid = Controller2Interface;
        Assert.AreEqual(0, Method<QueryInterfaceMethod>(controller, 0)(controller, ref iid, out var controller2));
        try
        {
            Assert.AreEqual(0, Method<GetColorMethod>(controller2, 26)(controller2, out var color)); // COREWEBVIEW2_COLOR: A, R, G, B bytes.
            return new NeoColor((byte)((color >> 8) & 0xff), (byte)((color >> 16) & 0xff), (byte)((color >> 24) & 0xff), (byte)(color & 0xff));
        }
        finally { Method<ReleaseMethod>(controller2, 2)(controller2); }
    }

    private static T Method<T>(nint instance, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size));

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryInterfaceMethod(nint instance, ref Guid iid, out nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetColorMethod(nint instance, out uint color);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseMethod(nint instance);

    private sealed class BlankProvider : INeoResourceProvider
    {
        public NeoResourceResponse GetResponse(NeoResourceRequest request)
            => NeoResourceResponse.FromBytes("<!doctype html><title>background</title>"u8.ToArray(), "text/html; charset=utf-8");
    }

#pragma warning disable SYSLIB1054 // Test-only Win32 inspection does not need source-generated interop.
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint context);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, uint message, nuint wide, nint value);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint context);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint context, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint context, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint context);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint context, int x, int y);
#pragma warning restore SYSLIB1054
}
