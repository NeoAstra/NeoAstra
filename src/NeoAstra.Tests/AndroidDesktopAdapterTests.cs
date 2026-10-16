// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using NeoAstra.Desktop;
using NeoAstra.Desktop.Clipboard;
using NeoAstra.Desktop.Dialogs;
using NeoAstra.Desktop.Notifications;
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
    public async Task Notifications_AnswerUnsupportedForActionsBeforeThePlatform()
    {
        var notifications = new AndroidNotifications();
        Assert.AreEqual(NeoSupportLevel.Limited, notifications.Support.SupportLevel);
        var request = new NeoNotificationRequest { Id = "hello", Title = "Hello", Body = "Body", Actions = [new("open", "Open")] };
        Assert.AreEqual(NeoDesktopStatus.Unsupported, await notifications.ShowAsync(request));
        await Assert.ThrowsAsync<ArgumentException>(async () => await notifications.RemoveAsync("bad id"));
    }

    [TestMethod]
    public async Task Dialogs_AnswerUnsupportedForFileDialogs()
    {
        var dialogs = new AndroidDialogs();
        var scope = new NeoFileScope([Path.GetTempPath()]);
        Assert.AreEqual(NeoDesktopStatus.Unsupported, (await dialogs.OpenFilesAsync(new() { Scope = scope })).Status);
        Assert.AreEqual(NeoDesktopStatus.Unsupported, (await dialogs.OpenFoldersAsync(new() { Scope = scope })).Status);
        Assert.AreEqual(NeoDesktopStatus.Unsupported, (await dialogs.SaveFileAsync(new() { Scope = scope })).Status);
        await Assert.ThrowsAsync<ArgumentException>(async () => await dialogs.ShowMessageAsync(new() { Message = "Hello", Buttons = [] }));
    }

    [TestMethod]
    public async Task Haptics_AnswerUnsupportedOnADesktopAndValidateTheFeedback()
    {
        var haptics = global::NeoAstra.Desktop.Haptics.NeoHaptics.CreateSystem();
        Assert.AreEqual(NeoSupportLevel.None, haptics.Support.SupportLevel);
        Assert.AreEqual(NeoDesktopStatus.Unsupported, await haptics.PlayAsync(global::NeoAstra.Desktop.Haptics.NeoHapticFeedback.Success));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await haptics.PlayAsync((global::NeoAstra.Desktop.Haptics.NeoHapticFeedback)42));
        Assert.AreEqual(NeoSupportLevel.Native, new global::NeoAstra.Desktop.Haptics.AndroidHaptics().Support.SupportLevel);
    }

    [TestMethod]
    public void PlatformCall_IsNotSupportedByTheDesktopBackends()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => NeoPlatformCall.Invoke("clipboard", "{}"));
    }

    [TestMethod]
    public void PlatformServices_ValidateTheServiceAndAreNotSupportedByTheDesktopBackends()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => NeoPlatformServices.Call("neoastra.test", "{}"));
        Assert.ThrowsExactly<ArgumentNullException>(() => NeoPlatformServices.Call(null!, "{}"));
        Assert.ThrowsExactly<ArgumentNullException>(() => NeoPlatformServices.Call("neoastra.test", null!));
        Assert.ThrowsExactly<ArgumentException>(() => NeoPlatformServices.Call("", "{}"));
        Assert.ThrowsExactly<ArgumentException>(() => NeoPlatformServices.Call("Neoastra.Test", "{}"));
        Assert.ThrowsExactly<ArgumentException>(() => NeoPlatformServices.Call(new string('a', 65), "{}"));
    }

    [TestMethod]
    public void SystemSelection_KeepsTheDesktopAdaptersOnDesktopHosts()
    {
        Assert.IsNotInstanceOfType<AndroidClipboard>(NeoClipboard.CreateSystem());
        Assert.IsNotInstanceOfType<AndroidKeystoreSafeStorage>(NeoSafeStorage.CreateSystem("dev.neoastra.tests", Path.GetTempPath()));
        Assert.IsNotInstanceOfType<AndroidNotifications>(NeoNotifications.CreateSystem("dev.neoastra.tests", "Tests"));
    }
}
