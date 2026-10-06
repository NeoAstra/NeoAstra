// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Tests;

[TestClass]
public sealed class ReloadTests
{
    [TestMethod]
    public async Task ReloadLoadsTheDocumentAgainWithOrWithoutTheCache()
    {
        await LiveBrowser.RunAsync(new Dictionary<string, string> { ["reload.html"] = "<!doctype html><title>Reload</title><p>Reload</p>" }, async session =>
        {
            var view = session.View;
            await session.NavigateAsync("reload.html");
            foreach (var ignoreCache in new[] { false, true })
            {
                session.Stage = ignoreCache ? "reload without the cache" : "reload";
                await session.RunAsync("globalThis.marker = 'set'; true");
                var completed = new TaskCompletionSource<NeoNavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnCompleted(object? sender, NeoNavigationCompletedEventArgs args) => completed.TrySetResult(args);
                view.NavigationCompleted += OnCompleted;
                try
                {
                    view.Reload(ignoreCache);
                    var navigation = await completed.Task.WaitAsync(session.CancellationToken);
                    Assert.IsTrue(navigation.IsSuccess);
                    Assert.AreEqual(LiveBrowserSession.Page("reload.html"), navigation.Uri);
                }
                finally
                {
                    view.NavigationCompleted -= OnCompleted;
                }

                // The document is a new one: what a script left in the old one is gone.
                await session.WaitUntilAsync("document.readyState === 'complete' && typeof globalThis.marker === 'undefined'");
            }
        });
    }
}
