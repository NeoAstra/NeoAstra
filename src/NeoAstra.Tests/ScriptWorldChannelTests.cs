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
}
