// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Tests;

/// <summary>A view leads to the window that hosts it, and a view in a native parent to none.</summary>
[TestClass]
public sealed class ViewWindowTests
{
    [TestMethod]
    [Timeout(60000)]
    public async Task AViewLeadsToItsWindow_AndAViewInANativeParentToNone()
    {
        await LiveBrowser.RunAsync(new Dictionary<string, string> { ["index.html"] = "<!doctype html><title>view window</title>" }, async session =>
        {
            Assert.AreSame(session.Window, session.View.Window);

            session.Stage = "a view in another window";
            var other = session.Application.CreateWindow(new NeoWindowOptions { Label = "other-window", Title = "Other", Width = 400, Height = 300 });
            await using (var view = await session.Environment.CreateWebViewAsync(NeoAstraHost.FillWindow(other), new NeoAstraOptions { ViewLabel = "other-view" }))
            {
                Assert.AreSame(other, view.Window);
                Assert.IsTrue(session.Application.TryGetWindow(view.Window!.Label!, out var found));
                Assert.AreSame(other, found);
            }

            session.Stage = "a view in a native parent";
            var parent = session.Window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
            await using (var embedded = await session.Environment.CreateWebViewAsync(NeoAstraHost.FromWin32Hwnd(parent), new NeoAstraOptions { ViewLabel = "embedded-view" }))
            {
                Assert.IsNull(embedded.Window);
            }
        });
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task ABoundedViewKeepsItsBounds_BesideTheViewThatFillsTheWindow()
    {
        await LiveBrowser.RunAsync(new Dictionary<string, string> { ["index.html"] = "<!doctype html><title>bounded view</title>" }, async session =>
        {
            const string PhysicalSize = "Math.round(innerWidth * devicePixelRatio) + 'x' + Math.round(innerHeight * devicePixelRatio)";
            var fillingSize = await StableResultAsync(session.View, PhysicalSize);

            session.Stage = "a bounded view in the window";
            await using var bounded = await session.Environment.CreateWebViewAsync(
                NeoAstraHost.BoundedInWindow(session.Window),
                new NeoAstraOptions { ViewLabel = "bounded-view", Bounds = new NeoRect(20, 30, 320, 240) });
            await bounded.LoadHtmlAsync("<!doctype html><title>bounded</title>");

            Assert.AreSame(session.Window, bounded.Window);
            Assert.AreEqual("\"320x240\"", await bounded.EvaluateScriptAsync(PhysicalSize));
            Assert.AreEqual(fillingSize, await StableResultAsync(session.View, PhysicalSize));

            session.Stage = "a bounded view without a size";
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
                await session.Environment.CreateWebViewAsync(
                    NeoAstraHost.BoundedInWindow(session.Window),
                    new NeoAstraOptions { ViewLabel = "empty-view", Bounds = new NeoRect(0, 0, 0, 240) }));
        });
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task SetBoundsMovesABoundedView_AndIsRefusedForAViewThatFillsItsWindow()
    {
        await LiveBrowser.RunAsync(new Dictionary<string, string> { ["index.html"] = "<!doctype html><title>set bounds</title>" }, async session =>
        {
            const string PhysicalSize = "Math.round(innerWidth * devicePixelRatio) + 'x' + Math.round(innerHeight * devicePixelRatio)";
            await using var bounded = await session.Environment.CreateWebViewAsync(
                NeoAstraHost.BoundedInWindow(session.Window),
                new NeoAstraOptions { ViewLabel = "moved-view", Bounds = new NeoRect(20, 30, 320, 240) });
            await bounded.LoadHtmlAsync("<!doctype html><title>moved</title>");
            Assert.AreEqual(new NeoRect(20, 30, 320, 240), bounded.Bounds);

            session.Stage = "a new rectangle";
            bounded.SetBounds(new NeoRect(40, 50, 200, 160));
            Assert.AreEqual(new NeoRect(40, 50, 200, 160), bounded.Bounds);
            Assert.AreEqual("\"200x160\"", await StableResultAsync(bounded, PhysicalSize));

            session.Stage = "bounds that are refused";
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => bounded.SetBounds(new NeoRect(0, 0, 0, 10)));
            Assert.AreEqual(new NeoRect(40, 50, 200, 160), bounded.Bounds);
            Assert.ThrowsExactly<InvalidOperationException>(() => session.View.SetBounds(new NeoRect(0, 0, 100, 100)));
        });
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task AHiddenViewKeepsItsPage_AndStaysHiddenWhenItsWindowComesBack()
    {
        await LiveBrowser.RunAsync(new Dictionary<string, string> { ["index.html"] = "<!doctype html><title>visibility</title>" }, async session =>
        {
            const string State = "document.visibilityState";
            await using var view = await session.Environment.CreateWebViewAsync(
                NeoAstraHost.BoundedInWindow(session.Window),
                new NeoAstraOptions { ViewLabel = "hidden-view", Bounds = new NeoRect(20, 30, 320, 240) });
            await view.LoadHtmlAsync("<!doctype html><title>hidden</title>");
            await view.EvaluateScriptAsync("window.kept = 'state'; 1");
            Assert.IsTrue(view.IsVisible);
            Assert.AreEqual("\"visible\"", await StableResultAsync(view, State));

            session.Stage = "hidden by the host";
            view.IsVisible = false;
            Assert.IsFalse(view.IsVisible);
            Assert.AreEqual("\"hidden\"", await StableResultAsync(view, State));

            session.Stage = "the window hides and comes back";
            session.Window.Hide();
            session.Window.Show();
            await Task.Delay(300);
            Assert.AreEqual("\"hidden\"", await StableResultAsync(view, State));
            Assert.AreEqual("\"visible\"", await StableResultAsync(session.View, State));

            session.Stage = "shown again";
            view.IsVisible = true;
            Assert.AreEqual("\"visible\"", await StableResultAsync(view, State));
            Assert.AreEqual("\"state\"", await view.EvaluateScriptAsync("window.kept"));
        });
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task TheKeyboardReturnsToTheViewThatHeldIt_AndLeavesAViewThatHides()
    {
        await LiveBrowser.RunAsync(new Dictionary<string, string> { ["index.html"] = "<!doctype html><title>focus</title><input autofocus>" }, async session =>
        {
            await using var bounded = await session.Environment.CreateWebViewAsync(
                NeoAstraHost.BoundedInWindow(session.Window),
                new NeoAstraOptions { ViewLabel = "focused-view", Bounds = new NeoRect(20, 30, 320, 240) });
            await bounded.LoadHtmlAsync("<!doctype html><title>bounded</title><input autofocus>");

            async Task ExpectKeyboardAsync(NeoAstra holder, NeoAstra other, string stage)
            {
                session.Stage = stage;
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    if (await holder.EvaluateScriptAsync("document.hasFocus()") == "true" &&
                        await other.EvaluateScriptAsync("document.hasFocus()") == "false")
                    {
                        return;
                    }

                    await Task.Delay(50);
                }

                Assert.Fail($"The keyboard is not in the expected view: {stage}.");
            }

            bounded.Focus();
            await ExpectKeyboardAsync(bounded, session.View, "focus on the bounded view");
            session.View.Focus();
            await ExpectKeyboardAsync(session.View, bounded, "focus back on the view that fills the window");

            bounded.Focus();
            await ExpectKeyboardAsync(bounded, session.View, "focus on the bounded view again");
            var parent = session.Window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
            SetFocus(0);
            SetFocus(parent);
            await ExpectKeyboardAsync(bounded, session.View, "the window gets the keyboard back");

            bounded.IsVisible = false;
            await ExpectKeyboardAsync(session.View, bounded, "the view that holds the keyboard hides");
            session.Stage = "a hidden view cannot take the keyboard";
            Assert.ThrowsExactly<InvalidOperationException>(bounded.Focus);
        });
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task PlaceOverFollowsTheAnchor_AcrossWindowsWithoutReloading()
    {
        await LiveBrowser.RunAsync(new Dictionary<string, string> { ["index.html"] = "<!doctype html><title>anchor</title>" }, async session =>
        {
            const string PhysicalSize = "Math.round(innerWidth * devicePixelRatio) + 'x' + Math.round(innerHeight * devicePixelRatio)";
            Assert.AreEqual(NeoSupportLevel.Native, session.Environment.GetCapability(NeoCapability.ViewPlacement).SupportLevel);
            var other = session.Application.CreateWindow(new NeoWindowOptions { Label = "other-anchor", Title = "Other", Width = 700, Height = 500 });
            other.Show();
            await using var otherAnchor = await session.Environment.CreateWebViewAsync(NeoAstraHost.FillWindow(other), new NeoAstraOptions { ViewLabel = "other-anchor-view" });
            await otherAnchor.LoadHtmlAsync("<!doctype html><title>other</title>");
            await using var placed = await session.Environment.CreateWebViewAsync(
                NeoAstraHost.BoundedInWindow(session.Window),
                new NeoAstraOptions { ViewLabel = "placed-view", Bounds = new NeoRect(0, 0, 100, 100) });
            await placed.LoadHtmlAsync("<!doctype html><title>placed</title><input id=typed>");
            await placed.EvaluateScriptAsync("window.kept = 'state'; document.getElementById('typed').value = 'typed'; 1");
            var navigations = 0;
            placed.NavigationCompleted += (_, _) => navigations++;

            static double Ratio(string? value) => double.Parse(value!, System.Globalization.CultureInfo.InvariantCulture);
            static NeoRect Expected(double ratio, double x, double y, double width, double height)
            {
                static int Whole(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
                var left = Whole(x * ratio);
                var top = Whole(y * ratio);
                return new NeoRect(left, top, Whole((x + width) * ratio) - left, Whole((y + height) * ratio) - top);
            }

            session.Stage = "over a rectangle of the view of its window";
            var ratio = Ratio(await session.View.EvaluateScriptAsync("devicePixelRatio"));
            placed.PlaceOver(session.View, 10, 20, 200, 100);
            Assert.AreSame(session.Window, placed.Window);
            Assert.AreEqual(Expected(ratio, 10, 20, 200, 100), placed.Bounds);

            session.Stage = "over a view of another window";
            ratio = Ratio(await otherAnchor.EvaluateScriptAsync("devicePixelRatio"));
            placed.PlaceOver(otherAnchor, 30, 40, 220, 120);
            Assert.AreSame(other, placed.Window);
            var moved = Expected(ratio, 30, 40, 220, 120);
            Assert.AreEqual(moved, placed.Bounds);
            Assert.AreEqual($"\"{moved.Width}x{moved.Height}\"", await StableResultAsync(placed, PhysicalSize));
            Assert.AreEqual("\"state/typed\"", await placed.EvaluateScriptAsync("window.kept + '/' + document.getElementById('typed').value"));
            Assert.AreEqual(0, navigations);

            session.Stage = "with the zoom of the anchor";
            otherAnchor.ZoomFactor = 1.5;
            await StableResultAsync(otherAnchor, "devicePixelRatio");
            ratio = Ratio(await otherAnchor.EvaluateScriptAsync("devicePixelRatio"));
            Assert.IsTrue(ratio >= 1.5, $"The anchor is not zoomed: {ratio}.");
            placed.PlaceOver(otherAnchor, 10, 10, 100, 60);
            Assert.AreEqual(Expected(ratio, 10, 10, 100, 60), placed.Bounds);

            session.Stage = "calls that are refused";
            Assert.ThrowsExactly<ArgumentException>(() => placed.PlaceOver(placed, 0, 0, 10, 10));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => placed.PlaceOver(session.View, 0, 0, 0, 10));
            Assert.ThrowsExactly<InvalidOperationException>(() => session.View.PlaceOver(otherAnchor, 0, 0, 10, 10));

            session.Stage = "the owners of the view follow its window";
            var windowsClosed = 0;
            var moves = 0;
            var stopObserving = placed.ObserveWindow(() => windowsClosed++, () => moves++);
            var third = session.Application.CreateWindow(new NeoWindowOptions { Label = "third-anchor", Title = "Third", Width = 500, Height = 400 });
            third.Show();
            await using var thirdAnchor = await session.Environment.CreateWebViewAsync(NeoAstraHost.FillWindow(third), new NeoAstraOptions { ViewLabel = "third-anchor-view" });
            placed.PlaceOver(thirdAnchor, 0, 0, 50, 50);
            Assert.AreEqual(1, moves);
            Assert.AreSame(third, placed.Window);
            var otherClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            other.Closed += (_, _) => otherClosed.TrySetResult();
            other.Close();
            await otherClosed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, windowsClosed, "The window that the view left must no longer count as its window.");
            var thirdClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            third.Closed += (_, _) => thirdClosed.TrySetResult();
            third.Close();
            await thirdClosed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, windowsClosed);
            stopObserving();
        });
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task AChordTheHostTakesNeverReachesThePage_AndOtherKeysDo()
    {
        await LiveBrowser.RunAsync(new Dictionary<string, string> { ["index.html"] = "<!doctype html><title>keys</title>" }, async session =>
        {
            const byte Control = 0x11, Shift = 0x10;
            Assert.AreEqual("0", await session.View.EvaluateScriptAsync("window.keys=[];for(const t of ['keydown','keyup'])addEventListener(t,e=>keys.push(t+':'+e.code+'/'+e.key),true);keys.length"));
            var parent = session.Window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
            // Windows gives the foreground to a background process only just after it sent input.
            keybd_event(0x87, 0, 0, 0);
            keybd_event(0x87, 0, 2, 0);
            SetForegroundWindow(parent);
            if (GetForegroundWindow() != parent)
            {
                Assert.Inconclusive("This test needs an interactive desktop that gives its window the foreground.");
                return;
            }

            session.View.Focus();
            for (var attempt = 0; attempt < 100 && await session.View.EvaluateScriptAsync("document.hasFocus()") != "true"; attempt++) await Task.Delay(50);

            var chords = new List<string>();
            session.View.KeyChordPressed += (_, chord) =>
            {
                chords.Add(chord.Chord);
                chord.Handled = chord.Chord == "Ctrl+Shift+K";
            };

            static void Press(byte first, byte second, byte key)
            {
                // The page reads the physical key from the scan code.
                static byte Scan(byte virtualKey) => (byte)MapVirtualKeyW(virtualKey, 0);
                keybd_event(first, Scan(first), 0, 0);
                keybd_event(second, Scan(second), 0, 0);
                keybd_event(key, Scan(key), 0, 0);
                keybd_event(key, Scan(key), 2, 0);
                keybd_event(second, Scan(second), 2, 0);
                keybd_event(first, Scan(first), 2, 0);
            }

            session.Stage = "a chord that the host takes";
            Press(Control, Shift, (byte)'K');
            for (var attempt = 0; attempt < 100 && !chords.Contains("Ctrl+Shift+K"); attempt++) await Task.Delay(50);
            CollectionAssert.Contains(chords, "Ctrl+Shift+K");

            session.Stage = "a chord that the host leaves to the page";
            Press(Control, Shift, (byte)'9');
            string? keys = null;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                keys = await session.View.EvaluateScriptAsync("keys.join(' ')");
                if (keys is not null && keys.Contains("keyup:ControlLeft", StringComparison.Ordinal) && keys.Contains("keyup:Digit9", StringComparison.Ordinal)) break;
                await Task.Delay(50);
            }

            CollectionAssert.Contains(chords, "Ctrl+Shift+9");
            StringAssert.Contains(keys, "keydown:Digit9");
            Assert.IsFalse(keys!.Contains("KeyK", StringComparison.Ordinal), $"The page got a chord that the host took: {keys}");
            StringAssert.Contains(keys, "keydown:ControlLeft");
        });
    }

#pragma warning disable SYSLIB1054 // Test-only Win32 interop does not need source-generated interop.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SetFocus(nint window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetForegroundWindow(nint window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);
#pragma warning restore SYSLIB1054

    // A view reports its first size before the layout of its window has reached it.
    private static async Task<string?> StableResultAsync(NeoAstra view, string script)
    {
        var last = await view.EvaluateScriptAsync(script);
        for (var unchanged = 0; unchanged < 3;)
        {
            await Task.Delay(150);
            var current = await view.EvaluateScriptAsync(script);
            unchanged = current == last ? unchanged + 1 : 0;
            last = current;
        }

        return last;
    }

    [TestMethod]
    public void ABoundedHostNeedsAWindow()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => NeoAstraHost.BoundedInWindow(null!));
    }
}
