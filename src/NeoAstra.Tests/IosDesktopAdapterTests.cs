// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using NeoAstra.Desktop;
using NeoAstra.Desktop.Clipboard;
using NeoAstra.Desktop.SafeStorage;

namespace NeoAstra.Tests;

// The iOS adapters reach UIKit and the Keychain only on iOS; what they decide before that is checked on every host.
[TestClass]
public sealed class IosDesktopAdapterTests
{
    [TestMethod]
    public async Task Clipboard_AnswersUnsupportedForFormatsThatIOSDoesNotExpose()
    {
        var clipboard = new IosClipboard(null);
        Assert.AreEqual(NeoSupportLevel.Limited, clipboard.Support.SupportLevel);
        Assert.AreEqual(NeoDesktopStatus.Unsupported, (await clipboard.ReadAsync(NeoClipboardFormat.Png)).Status);
        Assert.AreEqual(NeoDesktopStatus.Unsupported, (await clipboard.ReadAsync(NeoClipboardFormat.FileList)).Status);
        Assert.AreEqual(NeoDesktopStatus.Unsupported, await clipboard.WriteAsync(NeoClipboardFormat.FileList, "[\"/tmp/a\"]"u8.ToArray()));
    }

    [TestMethod]
    public async Task SafeStorage_ValidatesKeysAndSecretsBeforeTheKeychain()
    {
        var storage = new IosKeychainSafeStorage("dev.neoastra.tests");
        Assert.AreEqual(NeoSupportLevel.Native, storage.Support.SupportLevel);
        await Assert.ThrowsAsync<ArgumentException>(async () => await storage.StoreAsync("bad key", new byte[] { 1 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await storage.StoreAsync("key", ReadOnlyMemory<byte>.Empty));
        await Assert.ThrowsAsync<ArgumentException>(async () => await storage.RetrieveAsync("bad/key"));
    }

    [TestMethod]
    public void SystemSelection_KeepsTheDesktopAdaptersOnDesktopHosts()
    {
        Assert.IsNotInstanceOfType<IosClipboard>(NeoClipboard.CreateSystem());
        Assert.IsNotInstanceOfType<IosKeychainSafeStorage>(NeoSafeStorage.CreateSystem("dev.neoastra.tests", Path.GetTempPath()));
    }
}
