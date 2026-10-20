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
}
