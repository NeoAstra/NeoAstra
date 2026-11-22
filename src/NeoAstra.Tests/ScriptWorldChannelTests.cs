// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Tests;

// The protected channel of an isolated-world script, without a browser: the options that ask for it, what a native library
// without it answers, and the routing of its messages to the script that owns the binding. The native tests
// (neoastra_script_world_tests, neoastra_macos_script_world_tests) drive the engines.
[TestClass]
public sealed class ScriptWorldChannelTests
{
    [TestMethod]
    public void Options_GiveABindingOnlyToANamedIsolatedWorldOfTheMainFrame()
    {
        Channel("codealta.annotations", "codeAltaAnnotations").Validate();
        new NeoScriptOptions { IsolatedWorld = true }.Validate();
        new NeoScriptOptions { IsolatedWorld = true, WorldName = "neoastra.default" }.Validate();

        Assert.ThrowsExactly<ArgumentException>(() => new NeoScriptOptions { BindingName = "codeAltaAnnotations", MainFrameOnly = true }.Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new NeoScriptOptions { IsolatedWorld = true, BindingName = "codeAltaAnnotations", MainFrameOnly = true }.Validate());
        var allFrames = Channel("codealta.annotations", "codeAltaAnnotations");
        allFrames.MainFrameOnly = false;
        Assert.ThrowsExactly<ArgumentException>(() => allFrames.Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new NeoScriptOptions { WorldName = "codealta.annotations" }.Validate());

        foreach (var world in new[] { "page world", "x/y", new string('w', 129), "é" })
        {
            Assert.ThrowsExactly<ArgumentException>(() => Channel(world, "codeAltaAnnotations").Validate(), world);
        }

        foreach (var binding in new[] { "", "1binding", "a-b", new string('b', 65), "_neoastra_transport_v1", "_neoastraAnything", "webkit", "chrome", "window", "document" })
        {
            Assert.ThrowsExactly<ArgumentException>(() => Channel("codealta.annotations", binding).Validate(), binding);
        }
    }

    [TestMethod]
    public void ALibraryWithoutTheChannel_RefusesABindingAndReportsNoCapability()
    {
        var old = Runtime(buildFeatures: 0b1_1111);
        var current = Runtime(buildFeatures: 0b11_1111);
        Assert.IsFalse(old.HasScriptWorldChannels);
        Assert.IsTrue(current.HasScriptWorldChannels);

        Assert.IsInstanceOfType<NotSupportedException>(NeoAstra.CheckScriptChannelSupport(old, Channel("codealta.annotations", "codeAltaAnnotations")));
        Assert.IsNull(NeoAstra.CheckScriptChannelSupport(old, new NeoScriptOptions { IsolatedWorld = true }));
        Assert.IsNull(NeoAstra.CheckScriptChannelSupport(current, Channel("codealta.annotations", "codeAltaAnnotations")));

        var unknown = NeoEnvironment.CapabilityUnknownToLibrary(old, NeoCapability.ScriptWorldChannel);
        Assert.IsNotNull(unknown);
        Assert.AreEqual(NeoSupportLevel.None, unknown.SupportLevel);
        Assert.IsNull(NeoEnvironment.CapabilityUnknownToLibrary(current, NeoCapability.ScriptWorldChannel));
        Assert.IsNull(NeoEnvironment.CapabilityUnknownToLibrary(old, NeoCapability.ScriptIsolatedWorld));

        // The placed views came with the channel: an older library answers that it has none, instead of refusing the question.
        var noPlacement = NeoEnvironment.CapabilityUnknownToLibrary(old, NeoCapability.ViewPlacement);
        Assert.IsNotNull(noPlacement);
        Assert.AreEqual(NeoSupportLevel.None, noPlacement.SupportLevel);
        Assert.IsNull(NeoEnvironment.CapabilityUnknownToLibrary(current, NeoCapability.ViewPlacement));
    }

    [TestMethod]
    public void Messages_ReachOnlyTheScriptThatOwnsTheWorldAndBinding()
    {
        var channels = new NeoScriptChannelRegistry<string>();
        Assert.IsTrue(channels.TryAdd("codealta.annotations", "codeAltaAnnotations", "toolbar"));
        Assert.IsTrue(channels.TryAdd("codealta.other", "codeAltaAnnotations", "other"));
        Assert.IsFalse(channels.TryAdd("codealta.annotations", "codeAltaAnnotations", "impostor"));

        Assert.AreEqual("toolbar", channels.Find("codealta.annotations", "codeAltaAnnotations"));
        Assert.AreEqual("other", channels.Find("codealta.other", "codeAltaAnnotations"));
        // The names come from the native registration, never from the message: a binding of another world or name finds nothing.
        Assert.IsNull(channels.Find("codealta.annotations", "otherBinding"));
        Assert.IsNull(channels.Find("neoastra.default", "codeAltaAnnotations"));

        // A removed script receives nothing more, and only its own registration is removed.
        channels.Remove("codealta.annotations", "codeAltaAnnotations", "impostor");
        Assert.AreEqual("toolbar", channels.Find("codealta.annotations", "codeAltaAnnotations"));
        channels.Remove("codealta.annotations", "codeAltaAnnotations", "toolbar");
        Assert.IsNull(channels.Find("codealta.annotations", "codeAltaAnnotations"));
        Assert.IsTrue(channels.TryAdd("codealta.annotations", "codeAltaAnnotations", "again"));

        channels.Clear();
        Assert.IsNull(channels.Find("codealta.annotations", "codeAltaAnnotations"));
        Assert.IsNull(channels.Find("codealta.other", "codeAltaAnnotations"));
    }

    [TestMethod]
    public void MessageEventArgs_CarryTheIdentityOfTheScriptApartFromThePage()
    {
        var args = new NeoScriptMessageReceivedEventArgs("{\"send\":true}", new Uri("https://example.test"), "codealta.annotations", "codeAltaAnnotations");
        Assert.AreEqual("{\"send\":true}", args.Message);
        Assert.AreEqual(new Uri("https://example.test"), args.Origin);
        Assert.AreEqual("codealta.annotations", args.WorldName);
        Assert.AreEqual("codeAltaAnnotations", args.BindingName);
        Assert.IsFalse(typeof(NeoScriptMessageReceivedEventArgs).IsAssignableTo(typeof(NeoWebMessageReceivedEventArgs)));
    }

    [TestMethod]
    public void InitialMessageHandlerReceivesReadyBeforeTheAsyncCallerCanSubscribe()
    {
        var messages = new List<string>();
        var options = Channel("codealta.annotations", "codeAltaAnnotations");
        options.MessageHandler = (_, args) => messages.Add(args.Message);
        options.Validate();
        // No native view is needed to exercise registration-time delivery; this script is never posted to or disposed.
        var script = new NeoUserScript(null!, "test-script", options.WorldName, options.BindingName, options.MessageHandler);
        script.RaiseMessageReceived(new NeoScriptMessageReceivedEventArgs("ready", null, options.WorldName!, options.BindingName!));
        CollectionAssert.AreEqual(new[] { "ready" }, messages);
        var invalid = new NeoScriptOptions { MessageHandler = options.MessageHandler };
        Assert.ThrowsExactly<ArgumentException>(() => invalid.Validate());
    }

    private static NeoScriptOptions Channel(string world, string binding) => new()
    {
        IsolatedWorld = true,
        WorldName = world,
        BindingName = binding,
        MainFrameOnly = true,
    };

    private static NeoRuntimeInfo Runtime(ulong buildFeatures) => new("test", "1", "1", "test", "arm64", buildFeatures, false);

    [TestMethod]
    [Timeout(60000)]
    public async Task ThePageCannotReachTheBindingOfAWorld_OnWebView2()
    {
        var pages = new Dictionary<string, string>
        {
            // A srcdoc frame has the origin of its parent, so that the page can really look into it.
            ["index.html"] = "<!doctype html><title>hostile</title><iframe id=same srcdoc=\"<title>frame</title>\"></iframe>",
        };
        await LiveBrowser.RunAsync(pages, async session =>
        {
            var messages = new List<NeoScriptMessageReceivedEventArgs>();
            const string Source = "exampleToolbar.onmessage = event => exampleToolbar.postMessage('echo:' + event.data); exampleToolbar.postMessage('ready:' + typeof window.pageSecret);";
            var options = new NeoScriptOptions
            {
                IsolatedWorld = true,
                WorldName = "example.toolbar",
                BindingName = "exampleToolbar",
                MainFrameOnly = true,
                MessageHandler = (_, message) => messages.Add(message),
            };

            session.Stage = "a script whose operation is canceled first";
            using (var cancellation = new CancellationTokenSource())
            {
                var canceled = session.View.AddScriptAsync(Source, options, cancellation.Token);
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(async () => await canceled);
            }

            // The binding is free again once the protocol has answered the canceled request.
            NeoUserScript? added = null;
            for (var attempt = 0; attempt < 100 && added is null; attempt++)
            {
                try { added = await session.View.AddScriptAsync(Source, options); }
                catch (InvalidOperationException) { await Task.Delay(25); }
            }

            Assert.IsNotNull(added, "A canceled script kept its binding.");
            await using var script = added;

            async Task WaitForAsync(Func<NeoScriptMessageReceivedEventArgs, bool> condition)
            {
                for (var attempt = 0; attempt < 200 && !messages.Any(condition); attempt++) await Task.Delay(25);
                Assert.IsTrue(messages.Any(condition), "The expected message of the world did not arrive: " + string.Join(" | ", messages.Select(static message => message.Message)));
            }

            session.Stage = "the first document with the script";
            await session.NavigateAsync("index.html");
            await WaitForAsync(static message => message.Message.StartsWith("ready:", StringComparison.Ordinal));
            for (var attempt = 0; attempt < 200 && await session.View.EvaluateScriptAsync("document.readyState === 'complete' && !!document.getElementById('same')") != "true"; attempt++) await Task.Delay(25);
            Assert.AreEqual("true", await session.View.EvaluateScriptAsync("!!document.getElementById('same')"));
            await Task.Delay(300);
            Assert.AreEqual(1, messages.Count, "Only the top-level frame posts: " + string.Join(" | ", messages.Select(static message => message.Message)));
            Assert.AreEqual("ready:undefined", messages[0].Message);
            Assert.AreEqual("example.toolbar", messages[0].WorldName);
            Assert.AreEqual(LiveBrowser.Origin, messages[0].Origin?.GetLeftPart(UriPartial.Authority));

            session.Stage = "what the page tries";
            await session.View.EvaluateScriptAsync("window.pageSecret = 'secret'; 1");
            foreach (var attack in new[]
            {
                "typeof window.exampleToolbar",
                "Object.getOwnPropertyNames(window).some(name => name === 'exampleToolbar' || name.startsWith('_neoastraChannel')) ? 'found' : 'undefined'",
                "new Function('return typeof exampleToolbar')()",
                "typeof document.getElementById('same').contentWindow.exampleToolbar",
                "typeof window._neoastraChannel1",
                "(() => { const frame = document.createElement('iframe'); document.body.append(frame); const type = typeof frame.contentWindow.exampleToolbar; frame.remove(); return type; })()",
            })
            {
                // A script that throws evaluates to null: the failure then says why.
                var result = await session.View.EvaluateScriptAsync($"(() => {{ try {{ return {attack}; }} catch (error) {{ return 'threw ' + error.name + ': ' + error.message + ' frame=' + !!document.getElementById('same') + ' html=' + document.documentElement.outerHTML.slice(0, 160); }} }})()");
                Assert.AreEqual("\"undefined\"", result, attack);
            }

            await session.View.EvaluateScriptAsync("(() => { try { exampleToolbar.postMessage('forged'); } catch { } try { _neoastraChannel1('forged'); } catch { } return 1; })()");
            await Task.Delay(300);
            Assert.AreEqual(1, messages.Count, "The page raised a message: " + string.Join(" | ", messages.Select(static message => message.Message)));

            session.Stage = "the host answers the world";
            const string Hostile = "quote' \" \\ </script> ${1+1} \u2603";
            await script.PostMessageAsync(Hostile);
            await WaitForAsync(static message => message.Message.StartsWith("echo:", StringComparison.Ordinal));
            Assert.AreEqual("echo:" + Hostile, messages[^1].Message);

            session.Stage = "a new document";
            var before = messages.Count;
            await session.NavigateAsync("index.html?again");
            for (var attempt = 0; attempt < 200 && messages.Count == before; attempt++) await Task.Delay(25);
            Assert.AreEqual("ready:undefined", messages[before].Message);
        });
    }

    // The document that a script was disposed in keeps the object of its binding, under the name that a script added later
    // takes: what the host posts to that later script must not reach the object of the disposed one.
    [TestMethod]
    [Timeout(60000)]
    public async Task AScriptThatReplacesADisposedOne_NeverPostsToTheObjectOfTheDisposedOne_OnWebView2()
    {
        await LiveBrowser.RunAsync(new Dictionary<string, string> { ["index.html"] = "<!doctype html><title>replaced</title>" }, async session =>
        {
            var messages = new List<string>();
            NeoScriptOptions Options() => new()
            {
                IsolatedWorld = true,
                WorldName = "example.toolbar",
                BindingName = "exampleToolbar",
                MainFrameOnly = true,
                MessageHandler = (_, message) => messages.Add(message.Message),
            };

            // Each script writes what its binding receives into the document, which the page reads.
            static string Source(string name) =>
                $"exampleToolbar.onmessage = event => document.documentElement.setAttribute('data-{name}', event.data); exampleToolbar.postMessage('ready:{name}');";

            async Task<string?> AttributeAsync(string name, string? expected)
            {
                string? value = null;
                for (var attempt = 0; attempt < 200 && (value = await session.View.EvaluateScriptAsync($"document.documentElement.getAttribute('data-{name}')")) != expected; attempt++) await Task.Delay(25);
                return value;
            }

            async Task WaitForAsync(string message)
            {
                for (var attempt = 0; attempt < 200 && !messages.Contains(message); attempt++) await Task.Delay(25);
                CollectionAssert.Contains(messages, message);
            }

            session.Stage = "the first script in its document";
            var first = await session.View.AddScriptAsync(Source("first"), Options());
            await session.NavigateAsync("index.html");
            await WaitForAsync("ready:first");
            await first.PostMessageAsync("to-first");
            Assert.AreEqual("\"to-first\"", await AttributeAsync("first", "\"to-first\""));

            session.Stage = "a script that replaces it while the document is shown";
            await first.DisposeAsync();
            await using var second = await session.View.AddScriptAsync(Source("second"), Options());
            await second.PostMessageAsync("to-second");
            await Task.Delay(300);
            Assert.AreEqual("\"to-first\"", await session.View.EvaluateScriptAsync("document.documentElement.getAttribute('data-first')"), "The disposed script received a message of the script that replaced it.");

            session.Stage = "the replacement in the next document";
            await session.NavigateAsync("index.html?again");
            await WaitForAsync("ready:second");
            await second.PostMessageAsync("to-second");
            Assert.AreEqual("\"to-second\"", await AttributeAsync("second", "\"to-second\""));
        });
    }
}
