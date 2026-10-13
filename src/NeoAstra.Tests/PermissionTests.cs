// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Text.Json;

namespace NeoAstra.Tests;

[TestClass]
public sealed class PermissionTests
{
    // The browser then has a microphone and a camera that need no hardware, and still asks for the permission to use them.
    private const string FakeDevices = "--use-fake-device-for-media-stream";

    private static readonly Dictionary<string, string> Pages = new()
    {
        // The page starts by itself when asked to: a script of the host would count as an action of the user in WebView2.
        ["capture.html"] = """
            <!doctype html><title>Capture</title><p>Capture</p>
            <script>
            globalThis.capture = { state: 'pending' };
            globalThis.startCapture = async () => {
                const result = { state: 'pending', secure: isSecureContext, devices: typeof navigator.mediaDevices };
                try {
                    const stream = await navigator.mediaDevices.getUserMedia({ audio: true, video: location.search === '?both' });
                    result.tracks = stream.getAudioTracks().filter(track => track.readyState === 'live').length;
                    const context = new AudioContext();
                    await context.audioWorklet.addModule('meter.js');
                    context.createMediaStreamSource(stream).connect(new AudioWorkletNode(context, 'meter'));
                    result.state = 'captured';
                    stream.getTracks().forEach(track => track.stop());
                    await context.close();
                } catch (error) {
                    result.state = error.name;
                    result.message = error.message;
                }
                globalThis.capture = result;
            };
            if (location.search !== '') startCapture();
            </script>
            """,
        ["meter.js"] = "registerProcessor('meter', class extends AudioWorkletProcessor { process() { return true; } });",
    };

    [TestMethod]
    public async Task AMicrophoneRequestOfAnApplicationPageReachesTheHandlerAndIsGranted()
    {
        await LiveBrowser.RunAsync(Pages, async session =>
        {
            var requests = new ConcurrentQueue<NeoPermissionRequest>();
            session.View.PermissionRequested = request =>
            {
                requests.Enqueue(request);
                return ValueTask.FromResult(request.Kind == NeoPermissionKind.Microphone ? NeoPermissionDecision.AllowOnce : NeoPermissionDecision.DenyOnce);
            };

            await session.NavigateAsync("capture.html?start");
            var capture = await WaitForCaptureAsync(session);

            Assert.IsTrue(capture.GetProperty("secure").GetBoolean(), "A page of an application scheme is a secure context.");
            Assert.AreEqual("object", capture.GetProperty("devices").GetString());
            Assert.AreEqual("captured", capture.GetProperty("state").GetString(), capture.GetRawText());
            Assert.AreEqual(1, capture.GetProperty("tracks").GetInt32());

            Assert.IsTrue(requests.TryDequeue(out var request), "The handler was not asked.");
            Assert.AreEqual(NeoPermissionKind.Microphone, request.Kind);
            Assert.AreEqual(new Uri(LiveBrowser.Origin), request.Origin);
            Assert.IsFalse(request.IsUserInitiated, "The page asked by itself.");
            Assert.IsTrue(request.CanPersist, "WebView2 can keep an answer in the profile.");
        }, browserArguments: FakeDevices);
    }

    [TestMethod]
    public async Task AMicrophoneRequestIsRefusedWithoutAHandlerAndWhenTheHandlerRefuses()
    {
        await LiveBrowser.RunAsync(Pages, async session =>
        {
            session.Stage = "no handler";
            await session.NavigateAsync("capture.html?start");
            var capture = await WaitForCaptureAsync(session);
            Assert.AreEqual("NotAllowedError", capture.GetProperty("state").GetString(), capture.GetRawText());

            session.Stage = "refusing handler";
            var asked = 0;
            session.View.PermissionRequested = _ =>
            {
                Interlocked.Increment(ref asked);
                return ValueTask.FromResult(NeoPermissionDecision.DenyOnce);
            };
            await session.NavigateAsync("capture.html?start");
            capture = await WaitForCaptureAsync(session);
            Assert.AreEqual("NotAllowedError", capture.GetProperty("state").GetString(), capture.GetRawText());
            Assert.AreEqual(1, Volatile.Read(ref asked), "A refusal that is not kept does not answer the next request.");
        }, browserArguments: FakeDevices);
    }

    [TestMethod]
    public async Task ARequestForAMicrophoneAndACameraAsksForEach()
    {
        await LiveBrowser.RunAsync(Pages, async session =>
        {
            var kinds = new ConcurrentQueue<NeoPermissionKind>();
            session.View.PermissionRequested = request =>
            {
                kinds.Enqueue(request.Kind);
                return ValueTask.FromResult(NeoPermissionDecision.AllowOnce);
            };

            await session.NavigateAsync("capture.html?both");
            var capture = await WaitForCaptureAsync(session);

            Assert.AreEqual("captured", capture.GetProperty("state").GetString(), capture.GetRawText());
            CollectionAssert.AreEquivalent(new[] { NeoPermissionKind.Camera, NeoPermissionKind.Microphone }, kinds.ToArray());
        }, browserArguments: FakeDevices);
    }

    private static async Task<JsonElement> WaitForCaptureAsync(LiveBrowserSession session)
    {
        await session.WaitUntilAsync("globalThis.capture.state !== 'pending'");
        var json = await session.View.EvaluateScriptAsync("globalThis.capture", session.CancellationToken);
        Assert.IsNotNull(json);
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
