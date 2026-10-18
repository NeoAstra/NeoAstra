// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Reflection;
using NeoAstra.Desktop.Menus;
using NeoAstra.Desktop.Tray;

namespace NeoAstra.Tests;

[TestClass]
[DoNotParallelize]
public sealed class TrayActivationTests
{
    private static readonly NeoTrayItemOptions Options = new() { Id = "main", Menu = [NeoMenuItem.Command("run", "Run", "test.run")] };

    [TestMethod]
    [DataRow(true, "remove")]
    [DataRow(true, "replace")]
    [DataRow(true, "recreate")]
    [DataRow(true, "dispose")]
    [DataRow(false, "remove")]
    [DataRow(false, "replace")]
    [DataRow(false, "recreate")]
    [DataRow(false, "dispose")]
    public async Task HandlerRetiringTheEntryCancelsItsPopup(bool mac, string mutation)
    {
        await using var fixture = new TrayPresenterFixture(mac);
        fixture.Service.Set(Options);
        var activations = 0;
        fixture.Service.Activated += (_, _) => { if (++activations == 1) Mutate(fixture.Service, mutation); };

        fixture.Activate();

        Assert.AreEqual(1, activations);
        Assert.AreEqual(0, fixture.PopupCount, "A retired entry must not open its old menu or the replacement's menu.");
        Assert.IsEmpty(fixture.Violations);
        fixture.Activate();
        var replacementSurvived = mutation is "replace" or "recreate";
        Assert.AreEqual(replacementSurvived ? 2 : 1, activations, "Only a live replacement can receive a subsequent activation.");
        Assert.AreEqual(replacementSurvived ? 1 : 0, fixture.PopupCount);
        await fixture.DisposeAsync();
        Assert.AreEqual(0, fixture.LiveResources);
        Assert.IsEmpty(fixture.Violations);
    }

    [TestMethod]
    [DataRow("remove")]
    [DataRow("replace")]
    [DataRow("recreate")]
    [DataRow("dispose")]
    public async Task MacHandlerRetiringTheEntryKeepsTheDispatchingButtonAndTargetAlive(string mutation)
    {
        await using var fixture = new TrayPresenterFixture(mac: true);
        fixture.Service.Set(Options);
        var button = fixture.Button;
        var target = fixture.Target;
        bool? buttonLive = null, targetLive = null;
        fixture.Service.Activated += (_, _) =>
        {
            Mutate(fixture.Service, mutation);
            buttonLive = fixture.IsLive(button);
            targetLive = fixture.IsLive(target);
        };

        fixture.Activate();

        // AppKit is still dispatching the button's action to the target when the handler returns.
        Assert.IsTrue(buttonLive, "A handler that retires the entry cannot free the button that sent the action.");
        Assert.IsTrue(targetLive, "A handler that disposes the service cannot free the action target.");
        Assert.IsEmpty(fixture.Violations);
        await fixture.DisposeAsync();
        Assert.AreEqual(0, fixture.LiveResources);
        Assert.IsEmpty(fixture.Violations);
    }

    [TestMethod]
    [DataRow(true, "remove")]
    [DataRow(true, "replace")]
    [DataRow(true, "recreate")]
    [DataRow(true, "dispose")]
    [DataRow(false, "remove")]
    [DataRow(false, "replace")]
    [DataRow(false, "recreate")]
    [DataRow(false, "dispose")]
    public async Task PopupRetainsItsNativeResourcesUntilNestedMutationsReturn(bool mac, string mutation)
    {
        await using var fixture = new TrayPresenterFixture(mac);
        var commands = 0;
        fixture.Commands.Register("test.run", _ => { commands++; return ValueTask.CompletedTask; });
        fixture.Service.Set(Options);
        var menuItem = fixture.MenuItem;
        fixture.DuringPopup = () =>
        {
            Mutate(fixture.Service, mutation);
            if (mac) fixture.ActivateMenu(menuItem);
        };

        fixture.Activate();

        Assert.AreEqual(1, fixture.PopupCount);
        Assert.IsEmpty(fixture.Violations, "A nested native loop must not lose its menu, item/window, or callback target.");
        Assert.AreEqual(0, commands, "A retired menu cannot dispatch its stale action.");
        await fixture.DisposeAsync();
        Assert.AreEqual(0, fixture.LiveResources, "Temporary ownership must be released when popup tracking returns.");
        Assert.IsEmpty(fixture.Violations);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task NestedPopupDisposalWaitsForTheOutermostPopupToReturn(bool mac)
    {
        await using var fixture = new TrayPresenterFixture(mac);
        fixture.Service.Set(Options);
        fixture.DuringPopup = () =>
        {
            fixture.Service.Set(Options);
            fixture.DuringPopup = () => Mutate(fixture.Service, "dispose");
            fixture.Activate();
        };

        fixture.Activate();

        Assert.AreEqual(2, fixture.PopupCount);
        Assert.IsEmpty(fixture.Violations);
        Assert.AreEqual(0, fixture.LiveResources);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task PopupFailureStillReleasesResourcesRetiredByDisposal(bool mac)
    {
        await using var fixture = new TrayPresenterFixture(mac);
        fixture.Service.Set(Options);
        fixture.DuringPopup = () =>
        {
            Mutate(fixture.Service, "dispose");
            throw new InvalidOperationException("Native popup failed after nested disposal.");
        };

        var exception = Assert.ThrowsExactly<TargetInvocationException>(() => fixture.Activate());

        Assert.IsInstanceOfType<InvalidOperationException>(exception.InnerException);
        Assert.IsEmpty(fixture.Violations);
        Assert.AreEqual(0, fixture.LiveResources);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task MutatingAnotherItemDoesNotCancelTheCurrentPopup(bool mac)
    {
        await using var fixture = new TrayPresenterFixture(mac);
        fixture.Service.Set(Options);
        fixture.Service.Activated += (_, _) => fixture.Service.Set(Options with { Id = "other" });

        fixture.Activate();

        Assert.AreEqual(1, fixture.PopupCount);
        await fixture.DisposeAsync();
        Assert.AreEqual(0, fixture.LiveResources);
        Assert.IsEmpty(fixture.Violations);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    public async Task UnchangedEntryKeepsActivationOrderingAndPopupPolicy(bool mac, bool secondary)
    {
        await using var fixture = new TrayPresenterFixture(mac);
        var commands = 0;
        fixture.Commands.Register("test.run", _ => { commands++; return ValueTask.CompletedTask; });
        var activations = new List<NeoTrayActivation>();
        fixture.Service.Set(Options);
        var menuItem = fixture.MenuItem;
        fixture.DuringPopup = () => { if (mac) fixture.ActivateMenu(menuItem); };
        fixture.Service.Activated += (_, activation) =>
        {
            Assert.AreEqual(0, fixture.PopupCount, "Public activation precedes native popup tracking.");
            activations.Add(activation);
            throw new InvalidOperationException("User handler exceptions remain contained.");
        };

        fixture.Activate(secondary);

        CollectionAssert.AreEqual(new[] { new NeoTrayActivation("main", secondary, 1) }, activations);
        Assert.AreEqual(mac || secondary ? 1 : 0, fixture.PopupCount);
        Assert.AreEqual(mac || secondary ? 1 : 0, commands, "A live menu still dispatches its selected command.");
        Assert.IsEmpty(fixture.Violations);
        await fixture.DisposeAsync();
        Assert.AreEqual(0, fixture.LiveResources);
        Assert.IsEmpty(fixture.Violations);
    }

    private static void Mutate(NeoTrayService service, string mutation)
    {
        switch (mutation)
        {
            case "remove": Assert.IsTrue(service.Remove(Options.Id)); break;
            case "replace": service.Set(Options); break;
            case "recreate": Assert.IsTrue(service.Remove(Options.Id)); service.Set(Options); break;
            case "dispose": Assert.IsTrue(service.DisposeAsync().IsCompletedSuccessfully); break;
            default: Assert.Fail("Unknown mutation: " + mutation); break;
        }
    }
}
