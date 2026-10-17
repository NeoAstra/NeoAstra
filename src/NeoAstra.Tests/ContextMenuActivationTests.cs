// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using NeoAstra.Desktop;
using NeoAstra.Desktop.Menus;

namespace NeoAstra.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ContextMenuActivationTests
{
    private static readonly NeoMenuItem[] Items = [NeoMenuItem.Command("run", "Run", "test.run")];

    [TestMethod]
    [DataRow("remove")]
    [DataRow("replace")]
    [DataRow("recreate")]
    [DataRow("dispose")]
    [DataRow("owner-close")]
    [DataRow("owner-close-unobserved")]
    public async Task WindowsContextMenuKeepsTrackingResourcesAliveAndRejectsRetiredSelections(string mutation)
    {
        await using var fixture = new WindowsContextMenuFixture();
        var commands = 0;
        fixture.Commands.Register("test.run", _ => { commands++; return ValueTask.CompletedTask; });
        await fixture.Service.SetMenuAsync(WindowsContextMenuFixture.TargetId, Items);
        fixture.DuringPopup = () => Mutate(fixture, mutation);

        Assert.AreEqual(NeoDesktopStatus.Success, fixture.Show());

        Assert.AreEqual(1, fixture.PopupCount);
        Assert.IsEmpty(fixture.Violations, "The tracked HMENU cannot be destroyed inside the native tracking loop.");
        Assert.AreEqual(0, commands, "A retired entry or closed owner cannot dispatch the returned selection.");
        await fixture.DisposeAsync();
        Assert.AreEqual(0, fixture.LiveResources);
        Assert.IsEmpty(fixture.Violations);
    }

    [TestMethod]
    public async Task WindowsContextMenuReleasesNestedTrackingResourcesOnlyAfterTheOutermostPopupReturns()
    {
        await using var fixture = new WindowsContextMenuFixture();
        await fixture.Service.SetMenuAsync(WindowsContextMenuFixture.TargetId, Items);
        fixture.DuringPopup = () =>
        {
            Mutate(fixture, "replace");
            fixture.DuringPopup = () => Mutate(fixture, "dispose");
            Assert.AreEqual(NeoDesktopStatus.Success, fixture.Show());
        };

        Assert.AreEqual(NeoDesktopStatus.Success, fixture.Show());

        Assert.AreEqual(2, fixture.PopupCount);
        Assert.IsEmpty(fixture.Violations);
        await fixture.DisposeAsync();
        Assert.AreEqual(0, fixture.LiveResources);
        Assert.IsEmpty(fixture.Violations);
    }

    [TestMethod]
    public async Task WindowsContextMenuFailureStillReleasesResourcesRetiredByDisposal()
    {
        await using var fixture = new WindowsContextMenuFixture();
        await fixture.Service.SetMenuAsync(WindowsContextMenuFixture.TargetId, Items);
        fixture.DuringPopup = () =>
        {
            Mutate(fixture, "dispose");
            throw new InvalidOperationException("Popup failed after nested disposal.");
        };

        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Show());

        await fixture.DisposeAsync();
        Assert.AreEqual(0, fixture.LiveResources);
        Assert.IsEmpty(fixture.Violations);
    }

    [TestMethod]
    public async Task WindowsContextMenuStillDispatchesSelectionsForAnUnchangedLiveOwner()
    {
        await using var fixture = new WindowsContextMenuFixture();
        var commands = 0;
        fixture.Commands.Register("test.run", _ => { commands++; return ValueTask.CompletedTask; });
        await fixture.Service.SetMenuAsync(WindowsContextMenuFixture.TargetId, Items);

        Assert.AreEqual(NeoDesktopStatus.Success, fixture.Show());

        Assert.AreEqual(1, commands);
        Assert.IsEmpty(fixture.Violations);
        await fixture.DisposeAsync();
        Assert.AreEqual(0, fixture.LiveResources);
        Assert.IsEmpty(fixture.Violations);
    }

    private static void Mutate(WindowsContextMenuFixture fixture, string mutation)
    {
        switch (mutation)
        {
            case "remove": Assert.IsTrue(fixture.Service.RemoveMenu(WindowsContextMenuFixture.TargetId)); break;
            case "replace": fixture.Service.SetMenuAsync(WindowsContextMenuFixture.TargetId, Items).GetAwaiter().GetResult(); break;
            case "recreate": Assert.IsTrue(fixture.Service.RemoveMenu(WindowsContextMenuFixture.TargetId)); fixture.Service.SetMenuAsync(WindowsContextMenuFixture.TargetId, Items).GetAwaiter().GetResult(); break;
            case "dispose": Assert.IsTrue(fixture.Service.DisposeAsync().IsCompletedSuccessfully); break;
            case "owner-close": fixture.CloseOwner(notifyPresenter: true); break;
            case "owner-close-unobserved": fixture.CloseOwner(notifyPresenter: false); break;
            default: Assert.Fail("Unknown mutation: " + mutation); break;
        }
    }
}
