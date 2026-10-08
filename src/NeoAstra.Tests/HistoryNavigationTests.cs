// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace NeoAstra.Tests;

/// <summary>History navigation on a real view: what asks for it, how a request tells it, and a view that has it turned off.</summary>
[TestClass]
public sealed class HistoryNavigationTests
{
    private static readonly Dictionary<string, string> Pages = new()
    {
        ["first.html"] = "<!doctype html><title>First</title><p>First</p>",
        ["second.html"] = "<!doctype html><title>Second</title><p>Second</p>",
        ["host.html"] = "<!doctype html><title>Host</title><iframe id=\"frame\" src=\"inner-first.html\"></iframe>",
        ["inner-first.html"] = "<!doctype html><title>Inner first</title><p>Inner first</p>",
        ["inner-second.html"] = "<!doctype html><title>Inner second</title><p>Inner second</p>",
    };

    [TestMethod]
    public async Task ANavigationRequestTellsItsKind()
    {
        await LiveBrowser.RunAsync(Pages, async session =>
        {
            var view = session.View;
            var requests = new ConcurrentQueue<NeoNavigationRequest>();
            var refuseHistory = false;
            view.NavigationRequested = request =>
            {
                requests.Enqueue(request);
                return ValueTask.FromResult(refuseHistory && request.Kind == NeoNavigationKind.BackForward ? NeoNavigationDecision.Cancel : NeoNavigationDecision.Allow);
            };
            Assert.IsTrue(session.Environment.RuntimeInfo.ControlsHistoryNavigation);

            async Task ExpectAsync(string stage, Func<Task> trigger, string page, NeoNavigationKind kind)
            {
                session.Stage = stage;
                requests.Clear();
                await trigger();
                NeoNavigationRequest? request;
                while (!requests.TryDequeue(out request)) await Task.Delay(20, session.CancellationToken);
                Assert.AreEqual(LiveBrowserSession.Page(page), request.Uri, stage);
                Assert.AreEqual(kind, request.Kind, stage);
                Assert.IsTrue(request.IsMainFrame, stage);
                Console.WriteLine($"{stage}: {request.Kind}, user-initiated {request.IsUserInitiated}");
                await session.WaitUntilAsync($"location.pathname === '/{page}' && document.readyState === 'complete' && typeof globalThis.marker === 'undefined'");
                await session.RunAsync("globalThis.marker = 'set'; true");
            }

            Task Run(Action action) { action(); return Task.CompletedTask; }

            await ExpectAsync("a navigation of the host", () => view.NavigateAsync(LiveBrowserSession.Page("first.html"), session.CancellationToken).AsTask(), "first.html", NeoNavigationKind.NewDocument);
            await ExpectAsync("a navigation of a script", () => session.RunAsync("location.href = 'second.html'; true"), "second.html", NeoNavigationKind.NewDocument);
            while (!view.CanGoBack) await Task.Delay(20, session.CancellationToken);

            // Every way to walk the history is the same request, whatever asks for it.
            await ExpectAsync("GoBack", () => Run(view.GoBack), "first.html", NeoNavigationKind.BackForward);
            while (!view.CanGoForward) await Task.Delay(20, session.CancellationToken);
            await ExpectAsync("GoForward", () => Run(view.GoForward), "second.html", NeoNavigationKind.BackForward);
            await ExpectAsync("history.back()", () => session.RunAsync("history.back(); true"), "first.html", NeoNavigationKind.BackForward);
            await ExpectAsync("history.forward()", () => session.RunAsync("history.forward(); true"), "second.html", NeoNavigationKind.BackForward);
            await ExpectAsync("history.go(-1)", () => session.RunAsync("history.go(-1); true"), "first.html", NeoNavigationKind.BackForward);
            await ExpectAsync("the forward button of a mouse", () => Run(() => BrowserInput.PostMouseButton(session.Window, forward: true)), "second.html", NeoNavigationKind.BackForward);
            await ExpectAsync("the back button of a mouse", () => Run(() => BrowserInput.PostMouseButton(session.Window, forward: false)), "first.html", NeoNavigationKind.BackForward);
            await ExpectAsync("the browser-forward command of a keyboard", () => Run(() => BrowserInput.PostBrowserCommand(session.Window, forward: true)), "second.html", NeoNavigationKind.BackForward);
            await ExpectAsync("the browser-back command of a keyboard", () => Run(() => BrowserInput.PostBrowserCommand(session.Window, forward: false)), "first.html", NeoNavigationKind.BackForward);
            await ExpectAsync("history.go(1)", () => session.RunAsync("history.go(1); true"), "second.html", NeoNavigationKind.BackForward);

            await ExpectAsync("Reload", () => Run(view.Reload), "second.html", NeoNavigationKind.Reload);
            await ExpectAsync("location.reload()", () => session.RunAsync("location.reload(); true"), "second.html", NeoNavigationKind.Reload);

            // An entry of the history with the address of the document that is shown: only the kind tells it from a reload.
            await ExpectAsync("a third document", () => view.NavigateAsync(LiveBrowserSession.Page("first.html"), session.CancellationToken).AsTask(), "first.html", NeoNavigationKind.NewDocument);
            await ExpectAsync("history.go(-2) to the same address", () => session.RunAsync("history.go(-2); true"), "first.html", NeoNavigationKind.BackForward);

            // A host refuses history navigation as such, and nothing else.
            session.Stage = "a history navigation that the host refuses";
            refuseHistory = true;
            requests.Clear();
            BrowserInput.PostMouseButton(session.Window, forward: true);
            NeoNavigationRequest? refused;
            while (!requests.TryDequeue(out refused)) await Task.Delay(20, session.CancellationToken);
            Assert.AreEqual(NeoNavigationKind.BackForward, refused.Kind);
            Assert.AreEqual(LiveBrowserSession.Page("second.html"), refused.Uri);
            await Task.Delay(300, session.CancellationToken);
            Assert.AreEqual("\"/first.html set\"", await view.EvaluateScriptAsync("location.pathname + ' ' + globalThis.marker", session.CancellationToken));
            await ExpectAsync("a reload after a refused history navigation", () => Run(view.Reload), "first.html", NeoNavigationKind.Reload);
            refuseHistory = false;

            // A move within the document loads nothing, so the view asks nothing.
            session.Stage = "an entry that the page adds for itself";
            requests.Clear();
            await session.RunAsync("history.pushState(null, '', '#part'); true");
            await session.WaitUntilAsync("location.hash === '#part'");
            await session.RunAsync("history.back(); true");
            await session.WaitUntilAsync("location.hash === ''");
            Assert.AreEqual("\"set\"", await view.EvaluateScriptAsync("globalThis.marker", session.CancellationToken));
            Assert.IsTrue(requests.IsEmpty);

            // The history of a frame is part of the history of its view.
            session.Stage = "the history of a frame";
            await session.NavigateAsync("host.html");
            await session.WaitUntilAsync("frames[0].document.title === 'Inner first' && frames[0].document.readyState === 'complete'");
            await session.RunAsync("frames[0].location.href = 'inner-second.html'; true");
            await session.WaitUntilAsync("frames[0].document.title === 'Inner second' && frames[0].document.readyState === 'complete'");
            await session.RunAsync("history.back(); true");
            await session.WaitUntilAsync("frames[0].document.title === 'Inner first'");
        });
    }

    [TestMethod]
    public async Task AViewWithoutHistoryNavigationStaysOnItsDocument()
    {
        // The features of an application shell without history navigation: an application that shows a start-up screen first.
        var features = NeoBrowserFeatures.ApplicationShell();
        features.HistoryNavigation = false;
        var options = new NeoAstraOptions { ViewLabel = "main", BrowserFeatures = features };
        await LiveBrowser.RunAsync(Pages, async session =>
        {
            var view = session.View;
            var requests = new ConcurrentQueue<NeoNavigationRequest>();
            view.NavigationRequested = request =>
            {
                requests.Enqueue(request);
                return ValueTask.FromResult(NeoNavigationDecision.Allow);
            };

            async Task ShowAsync(string page)
            {
                await session.NavigateAsync(page);
                await session.RunAsync("globalThis.marker = 'set'; true");
                requests.Clear();
            }

            // The view asks nothing, loads nothing, and keeps the document with what its scripts left in it.
            async Task StaysAsync(string stage, Action trigger, string page)
            {
                session.Stage = stage;
                trigger();
                await Task.Delay(300, session.CancellationToken);
                Assert.AreEqual($"\"/{page} set\"", await view.EvaluateScriptAsync("location.pathname + ' ' + globalThis.marker", session.CancellationToken), stage);
                Assert.IsTrue(requests.IsEmpty, stage);
            }

            void Script(string script) => _ = view.EvaluateScriptAsync(script, session.CancellationToken);

            await ShowAsync("first.html");
            await ShowAsync("second.html");
            await StaysAsync("the back button of a mouse", () => BrowserInput.PostMouseButton(session.Window, forward: false), "second.html");
            await StaysAsync("the browser-back command of a keyboard", () => BrowserInput.PostBrowserCommand(session.Window, forward: false), "second.html");
            await StaysAsync("history.back()", () => Script("history.back(); true"), "second.html");
            await StaysAsync("history.go(-1)", () => Script("history.go(-1); true"), "second.html");
            Assert.IsFalse(view.CanGoBack);
            Assert.IsFalse(view.CanGoForward);
            Assert.ThrowsExactly<InvalidOperationException>(view.GoBack);
            Assert.ThrowsExactly<InvalidOperationException>(view.GoForward);
            await StaysAsync("GoBack", () => { }, "second.html");

            session.Stage = "a reload";
            view.Reload();
            await session.WaitUntilAsync("location.pathname === '/second.html' && document.readyState === 'complete' && typeof globalThis.marker === 'undefined'");
            NeoNavigationRequest? reload;
            while (!requests.TryDequeue(out reload)) await Task.Delay(20, session.CancellationToken);
            Assert.AreEqual(NeoNavigationKind.Reload, reload.Kind);

            // An earlier entry with the address of the document that is shown is refused as well, and a reload that follows
            // it at once is not taken for it.
            await ShowAsync("first.html");
            await StaysAsync("history.go(-2) to the same address", () => Script("history.go(-2); true"), "first.html");
            session.Stage = "a reload after a refused history navigation";
            view.Reload();
            await session.WaitUntilAsync("location.pathname === '/first.html' && document.readyState === 'complete' && typeof globalThis.marker === 'undefined'");

            // The entries that the page adds for itself stay with the page.
            session.Stage = "an entry that the page adds for itself";
            await session.RunAsync("globalThis.marker = 'set'; history.pushState(null, '', '#part'); true");
            await session.WaitUntilAsync("location.hash === '#part'");
            BrowserInput.PostMouseButton(session.Window, forward: false);
            await session.WaitUntilAsync("location.hash === ''");
            requests.Clear();
            await StaysAsync("the back button of a mouse past the entries of the page", () => BrowserInput.PostMouseButton(session.Window, forward: false), "first.html");

            // A frame keeps its document too.
            session.Stage = "the history of a frame";
            await session.NavigateAsync("host.html");
            await session.WaitUntilAsync("frames[0].document.title === 'Inner first' && frames[0].document.readyState === 'complete'");
            await session.RunAsync("frames[0].location.href = 'inner-second.html'; true");
            await session.WaitUntilAsync("frames[0].document.title === 'Inner second' && frames[0].document.readyState === 'complete'");
            await session.RunAsync("history.back(); true");
            await Task.Delay(300, session.CancellationToken);
            Assert.AreEqual("\"Inner second\"", await view.EvaluateScriptAsync("frames[0].document.title", session.CancellationToken));

            // The automation of the view says why it does not go back, and still reloads.
            session.Stage = "the automation of the view";
            await using var automation = new NeoAutomation(session.Application, new NeoAutomationOptions());
            foreach (var direction in new[] { "back", "forward" })
            {
                var moved = await automation.CallToolAsync("navigate_page", $$"""{"type":"{{direction}}"}""", session.CancellationToken);
                StringAssert.StartsWith(moved.Text, $"Unable to navigate {direction} in the selected page: history navigation is turned off for this view.");
            }
            StringAssert.StartsWith((await automation.CallToolAsync("navigate_page", """{"type":"reload"}""", session.CancellationToken)).Text, "Successfully reloaded the page.");
        }, options);
    }

    [TestMethod]
    public async Task ADocumentThatReplacesItselfLeavesNoEntry()
    {
        await LiveBrowser.RunAsync(Pages, async session =>
        {
            var view = session.View;
            var requests = new ConcurrentQueue<NeoNavigationRequest>();
            view.NavigationRequested = request =>
            {
                requests.Enqueue(request);
                return ValueTask.FromResult(NeoNavigationDecision.Allow);
            };
            await session.NavigateAsync("first.html");
            requests.Clear();

            // What a host does today for a start-up screen that should not stay behind the application.
            session.Stage = "location.replace";
            await session.RunAsync("location.replace('second.html'); true");
            await session.WaitUntilAsync("location.pathname === '/second.html' && document.readyState === 'complete'");
            NeoNavigationRequest? request;
            while (!requests.TryDequeue(out request)) await Task.Delay(20, session.CancellationToken);
            Assert.AreEqual(NeoNavigationKind.NewDocument, request.Kind);
            await Task.Delay(200, session.CancellationToken);
            Assert.IsFalse(view.CanGoBack);
            Assert.AreEqual("1", await view.EvaluateScriptAsync("history.length", session.CancellationToken));
        });
    }

    /// <summary>Input of the user for the browser window of a view, posted to it without moving the pointer or pressing a key.</summary>
    private static class BrowserInput
    {
        private const uint WM_XBUTTONDOWN = 0x020B;
        private const uint WM_XBUTTONUP = 0x020C;
        private const uint WM_APPCOMMAND = 0x0319;

        /// <summary>Presses and releases the back or the forward button of a mouse over the view.</summary>
        internal static void PostMouseButton(NeoWindow window, bool forward)
        {
            var target = FindBrowserWindow(window);
            var button = forward ? 2 : 1;
            // The low word of the first parameter holds the buttons that are down (MK_XBUTTON1 or MK_XBUTTON2), the high word the button.
            Assert.IsTrue(PostMessage(target, WM_XBUTTONDOWN, (button << 16) | (forward ? 0x40 : 0x20), (20 << 16) | 20));
            Assert.IsTrue(PostMessage(target, WM_XBUTTONUP, button << 16, (20 << 16) | 20));
        }

        /// <summary>Sends the browser-back or the browser-forward command of a keyboard (APPCOMMAND_BROWSER_BACKWARD, APPCOMMAND_BROWSER_FORWARD).</summary>
        internal static void PostBrowserCommand(NeoWindow window, bool forward)
        {
            var target = FindBrowserWindow(window);
            Assert.IsTrue(PostMessage(target, WM_APPCOMMAND, target, (forward ? 2 : 1) << 16));
        }

        // The window of the browser that takes the input of a view: a descendant of the window of the host.
        private static nint FindBrowserWindow(NeoWindow window)
        {
            var parent = window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
            nint found = 0;
            var name = new StringBuilder(64);
            EnumChildWindows(parent, (child, _) =>
            {
                name.Clear();
                if (GetClassName(child, name, name.Capacity) > 0 && name.ToString() == "Chrome_WidgetWin_1") found = child;
                return found == 0;
            }, 0);
            Assert.AreNotEqual(0, found, "The browser window of the view was not found.");
            return found;
        }

        private delegate bool EnumWindow(nint window, nint parameter);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(nint parent, EnumWindow callback, nint parameter);

        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(nint window, StringBuilder name, int capacity);

        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    }
}
