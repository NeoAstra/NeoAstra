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

#pragma warning disable SYSLIB1054 // Test-only Win32 interop does not need source-generated interop.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SetFocus(nint window);
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
