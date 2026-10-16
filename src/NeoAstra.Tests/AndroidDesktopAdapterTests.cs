// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using NeoAstra.Desktop;
using NeoAstra.Desktop.Clipboard;
using NeoAstra.Desktop.SafeStorage;
using NeoAstra.Interop;

namespace NeoAstra.Tests;

// The Android adapters reach Java through the native platform call only on Android; what they decide before that is checked on every host.
[TestClass]
public sealed class AndroidDesktopAdapterTests
{
    [TestMethod]
    public async Task Clipboard_AnswersUnsupportedForFormatsThatAndroidDoesNotExpose()
    {
        var clipboard = new AndroidClipboard(null);
        Assert.AreEqual(NeoSupportLevel.Limited, clipboard.Support.SupportLevel);
        Assert.AreEqual(NeoDesktopStatus.Unsupported, (await clipboard.ReadAsync(NeoClipboardFormat.Png)).Status);
        Assert.AreEqual(NeoDesktopStatus.Unsupported, (await clipboard.ReadAsync(NeoClipboardFormat.FileList)).Status);
        Assert.AreEqual(NeoDesktopStatus.Unsupported, await clipboard.WriteAsync(NeoClipboardFormat.FileList, "[\"/tmp/a\"]"u8.ToArray()));
    }

    [TestMethod]
    public async Task SafeStorage_ValidatesKeysAndSecretsBeforeTheKeystore()
    {
        var storage = new AndroidKeystoreSafeStorage("dev.neoastra.tests");
        Assert.AreEqual(NeoSupportLevel.Native, storage.Support.SupportLevel);
        await Assert.ThrowsAsync<ArgumentException>(async () => await storage.StoreAsync("bad key", new byte[] { 1 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await storage.StoreAsync("key", ReadOnlyMemory<byte>.Empty));
        await Assert.ThrowsAsync<ArgumentException>(async () => await storage.RetrieveAsync("bad/key"));
    }

    [TestMethod]
    public void PlatformCall_IsNotSupportedByTheDesktopBackends()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => NeoPlatformCall.Invoke("clipboard", "{}"));
    }

    [TestMethod]
    public void SystemSelection_KeepsTheDesktopAdaptersOnDesktopHosts()
    {
        Assert.IsNotInstanceOfType<AndroidClipboard>(NeoClipboard.CreateSystem());
        Assert.IsNotInstanceOfType<AndroidKeystoreSafeStorage>(NeoSafeStorage.CreateSystem("dev.neoastra.tests", Path.GetTempPath()));
    }
}
