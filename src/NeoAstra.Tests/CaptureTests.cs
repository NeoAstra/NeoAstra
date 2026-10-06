// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Tests;

[TestClass]
public sealed class CaptureTests
{
    // A red box at a known place on a white page, and a blue one below the first screen.
    private const string CapturePage = """
        <!doctype html><title>Capture</title>
        <style>
          html, body { margin: 0; background: #fff; }
          #red { position: absolute; left: 20px; top: 30px; width: 100px; height: 50px; background: #f00; }
          #blue { position: absolute; left: 0; top: 1500px; width: 200px; height: 100px; background: #00f; }
          #end { position: absolute; left: 0; top: 2990px; width: 10px; height: 10px; }
        </style>
        <div id="red"></div><div id="blue"></div><div id="end"></div>
        """;

    [TestMethod]
    public void PixelSizeIsReadFromPngAndJpegHeaders()
    {
        byte[] png =
        [
            0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0, 0, 0x03, 0x20, 0, 0, 0x02, 0x58, 8, 6, 0, 0, 0,
        ];
        Assert.AreEqual((800, 600), NeoCapturedImage.ReadPixelSize(NeoCaptureFormat.Png, png));

        // SOI, an APP0 segment to skip, a fill byte, then SOF0 with height 0x0102 and width 0x0304.
        byte[] jpeg =
        [
            0xff, 0xd8, 0xff, 0xe0, 0x00, 0x04, 0x4a, 0x46, 0xff, 0xff, 0xc0, 0x00, 0x0b, 0x08, 0x01, 0x02, 0x03, 0x04, 0x03, 0x01, 0x11, 0x00,
        ];
        Assert.AreEqual((0x0304, 0x0102), NeoCapturedImage.ReadPixelSize(NeoCaptureFormat.Jpeg, jpeg));

        Assert.AreEqual((0, 0), NeoCapturedImage.ReadPixelSize(NeoCaptureFormat.Png, jpeg));
        Assert.AreEqual((0, 0), NeoCapturedImage.ReadPixelSize(NeoCaptureFormat.Jpeg, png));
        Assert.AreEqual((0, 0), NeoCapturedImage.ReadPixelSize(NeoCaptureFormat.Jpeg, [0xff, 0xd8, 0xff, 0xc0, 0x00]));
    }

    [TestMethod]
    public void CaptureOptionsRejectInconsistentValues()
    {
        new NeoCaptureOptions().Validate();
        new NeoCaptureOptions { Format = NeoCaptureFormat.Jpeg, Quality = 100, Region = new NeoRect(-5, 0, 10, 10) }.Validate();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new NeoCaptureOptions { Format = (NeoCaptureFormat)2 }.Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new NeoCaptureOptions { Quality = 0 }.Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new NeoCaptureOptions { Quality = 101 }.Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new NeoCaptureOptions { Region = new NeoRect(0, 0, 0, 10) }.Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new NeoCaptureOptions { FullPage = true, Region = new NeoRect(0, 0, 10, 10) }.Validate());
    }

    [TestMethod]
    public async Task ViewportRegionAndFullPageCapturesShowThePage()
    {
        await LiveBrowser.RunAsync(new Dictionary<string, string> { ["capture.html"] = CapturePage }, async session =>
        {
            var view = session.View;
            Assert.IsTrue(session.Environment.GetCapability(NeoCapability.CaptureViewport).IsSupported);
            Assert.IsTrue(session.Environment.GetCapability(NeoCapability.CaptureFullPage).IsSupported);
            await session.NavigateAsync("capture.html");
            var scale = double.Parse((await view.EvaluateScriptAsync("devicePixelRatio", session.CancellationToken))!, System.Globalization.CultureInfo.InvariantCulture);
            var viewportWidth = int.Parse((await view.EvaluateScriptAsync("innerWidth", session.CancellationToken))!);
            var viewportHeight = int.Parse((await view.EvaluateScriptAsync("innerHeight", session.CancellationToken))!);

            session.Stage = "viewport capture";
            var viewport = await view.CaptureAsync(cancellationToken: session.CancellationToken);
            Assert.AreEqual(NeoCaptureFormat.Png, viewport.Format);
            Assert.AreEqual("image/png", viewport.ContentType);
            var pixels = TestPng.Decode(viewport.Data.Span);
            Assert.AreEqual((viewport.Width, viewport.Height), (pixels.Width, pixels.Height));
            Assert.AreEqual(viewportWidth * scale, pixels.Width, 2);
            Assert.AreEqual(viewportHeight * scale, pixels.Height, 2);
            AssertColor((255, 0, 0), pixels.GetPixel((int)(70 * scale), (int)(55 * scale)), "inside the red box");
            AssertColor((255, 255, 255), pixels.GetPixel((int)(10 * scale), (int)(10 * scale)), "left of the red box");
            AssertColor((255, 255, 255), pixels.GetPixel((int)(70 * scale), (int)(90 * scale)), "below the red box");

            session.Stage = "region capture";
            var region = await view.CaptureAsync(new NeoCaptureOptions { Region = new NeoRect(20, 30, 100, 50) }, session.CancellationToken);
            pixels = TestPng.Decode(region.Data.Span);
            Assert.AreEqual(100 * scale, pixels.Width, 1);
            Assert.AreEqual(50 * scale, pixels.Height, 1);
            AssertColor((255, 0, 0), pixels.GetPixel(1, 1), "top-left of the region");
            AssertColor((255, 0, 0), pixels.GetPixel(pixels.Width - 2, pixels.Height - 2), "bottom-right of the region");

            session.Stage = "region capture clipped to the viewport";
            var clipped = await view.CaptureAsync(new NeoCaptureOptions { Region = new NeoRect(-30, -20, 60, 60) }, session.CancellationToken);
            pixels = TestPng.Decode(clipped.Data.Span);
            Assert.AreEqual(30 * scale, pixels.Width, 1);
            Assert.AreEqual(40 * scale, pixels.Height, 1);
            AssertColor((255, 0, 0), pixels.GetPixel(pixels.Width - 2, pixels.Height - 2), "the red corner of the clipped region");
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
                await view.CaptureAsync(new NeoCaptureOptions { Region = new NeoRect(5000, 5000, 10, 10) }, session.CancellationToken));

            session.Stage = "region capture of a scrolled document";
            await session.RunAsync("scrollTo(0, 1480); true");
            await session.WaitUntilAsync("scrollY === 1480");
            var scrolled = await view.CaptureAsync(new NeoCaptureOptions { Region = new NeoRect(0, 20, 200, 100) }, session.CancellationToken);
            pixels = TestPng.Decode(scrolled.Data.Span);
            AssertColor((0, 0, 255), pixels.GetPixel(pixels.Width / 2, pixels.Height / 2), "the blue box after scrolling");
            await session.RunAsync("scrollTo(0, 0); true");
            await session.WaitUntilAsync("scrollY === 0");

            session.Stage = "full-page capture";
            var documentHeight = int.Parse((await view.EvaluateScriptAsync("document.documentElement.scrollHeight", session.CancellationToken))!);
            Assert.IsGreaterThan(viewportHeight, documentHeight);
            var fullPage = await view.CaptureAsync(new NeoCaptureOptions { FullPage = true }, session.CancellationToken);
            pixels = TestPng.Decode(fullPage.Data.Span);
            Assert.AreEqual(documentHeight * scale, pixels.Height, 2);
            AssertColor((255, 0, 0), pixels.GetPixel((int)(70 * scale), (int)(55 * scale)), "the red box of the full page");
            AssertColor((0, 0, 255), pixels.GetPixel((int)(100 * scale), (int)(1550 * scale)), "the blue box below the first screen");

            session.Stage = "JPEG capture";
            var jpeg = await view.CaptureAsync(new NeoCaptureOptions { Format = NeoCaptureFormat.Jpeg, Quality = 60 }, session.CancellationToken);
            Assert.AreEqual("image/jpeg", jpeg.ContentType);
            Assert.AreEqual((byte)0xff, jpeg.Data.Span[0]);
            Assert.AreEqual((byte)0xd8, jpeg.Data.Span[1]);
            Assert.AreEqual((viewport.Width, viewport.Height), (jpeg.Width, jpeg.Height));

            session.Stage = "capture of a hidden view";
            session.Window.Hide();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await view.CaptureAsync(cancellationToken: session.CancellationToken));
            session.Window.Show();
        });
    }

    private static void AssertColor((byte Red, byte Green, byte Blue) expected, (byte Red, byte Green, byte Blue) actual, string where)
    {
        // Color management and JPEG-free PNG output keep flat colors exact; a small tolerance covers display profiles.
        Assert.IsTrue(
            Math.Abs(expected.Red - actual.Red) <= 12 && Math.Abs(expected.Green - actual.Green) <= 12 && Math.Abs(expected.Blue - actual.Blue) <= 12,
            $"Expected {expected} {where} but found {actual}.");
    }
}
