// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;

namespace NeoAstra.Tests;

[TestClass]
public sealed class ScriptDialogTests
{
    [TestMethod]
    public async Task ScriptDialogsReachTheHandlerAndWaitForItsDecision()
    {
        await LiveBrowser.RunAsync(new Dictionary<string, string> { ["dialog.html"] = "<!doctype html><title>Dialogs</title><p>Dialogs</p>" }, async session =>
        {
            var view = session.View;
            var requests = new ConcurrentQueue<NeoScriptDialogRequest>();
            var decision = new TaskCompletionSource<NeoScriptDialogDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            // WebView2 applies the switch to the documents it loads afterwards, so the handler comes before the navigation.
            view.ScriptDialogRequested = request =>
            {
                requests.Enqueue(request);
                return new ValueTask<NeoScriptDialogDecision>(decision.Task);
            };
            await session.NavigateAsync("dialog.html");

            session.Stage = "confirm";
            await session.RunAsync("globalThis.answer = 'pending'; setTimeout(() => { globalThis.answer = confirm('Proceed?'); }, 0); true");
            var request = await WaitForRequestAsync(requests, session.CancellationToken);
            Assert.AreEqual(NeoScriptDialogKind.Confirm, request.Kind);
            Assert.AreEqual("Proceed?", request.Message);
            // The page waits for the decision: its timer callback has not returned yet.
            await Task.Delay(200, session.CancellationToken);
            Assert.IsFalse(decision.Task.IsCompleted);
            decision.SetResult(NeoScriptDialogDecision.Accept);
            await session.WaitUntilAsync("globalThis.answer === true");

            session.Stage = "prompt";
            decision = new TaskCompletionSource<NeoScriptDialogDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            await session.RunAsync("globalThis.answer = 'pending'; setTimeout(() => { globalThis.answer = prompt('Name?', 'nobody'); }, 0); true");
            request = await WaitForRequestAsync(requests, session.CancellationToken);
            Assert.AreEqual(NeoScriptDialogKind.Prompt, request.Kind);
            Assert.AreEqual("Name?", request.Message);
            Assert.AreEqual("nobody", request.DefaultText);
            decision.SetResult(NeoScriptDialogDecision.AcceptPrompt("NeoAstra"));
            await session.WaitUntilAsync("globalThis.answer === 'NeoAstra'");

            session.Stage = "canceled prompt";
            decision = new TaskCompletionSource<NeoScriptDialogDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            await session.RunAsync("globalThis.answer = 'pending'; setTimeout(() => { globalThis.answer = prompt('Name?'); }, 0); true");
            _ = await WaitForRequestAsync(requests, session.CancellationToken);
            decision.SetResult(NeoScriptDialogDecision.Cancel);
            await session.WaitUntilAsync("globalThis.answer === null");

            session.Stage = "alert";
            decision = new TaskCompletionSource<NeoScriptDialogDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            await session.RunAsync("globalThis.answer = 'pending'; setTimeout(() => { alert('Done'); globalThis.answer = 'alerted'; }, 0); true");
            request = await WaitForRequestAsync(requests, session.CancellationToken);
            Assert.AreEqual(NeoScriptDialogKind.Alert, request.Kind);
            Assert.AreEqual("Done", request.Message);
            decision.SetResult(NeoScriptDialogDecision.Accept);
            await session.WaitUntilAsync("globalThis.answer === 'alerted'");
        });
    }

    private static async Task<NeoScriptDialogRequest> WaitForRequestAsync(ConcurrentQueue<NeoScriptDialogRequest> requests, CancellationToken cancellationToken)
    {
        NeoScriptDialogRequest? request;
        while (!requests.TryDequeue(out request)) await Task.Delay(20, cancellationToken);
        return request;
    }
}
