// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using global::NeoAstra.Desktop;
using global::NeoAstra.Desktop.Dialogs;

namespace NeoAstra.Tests;

/// <summary>
/// The lifetime of the dialogs of the desktop portal, over a bus that the tests answer: every call and dialog ends, with its
/// answer, its cancellation, the portal leaving the bus, or the application stopping, and leaves no listener behind.
/// </summary>
[TestClass]
public sealed class LinuxPortalFileChooserTests
{
    private const string ExpectedPath = "/org/freedesktop/portal/desktop/request/1_42/";

    private static readonly NeoFileDialogOptions Options = new() { Scope = new NeoFileScope([Path.GetTempPath()]) };

    [TestMethod]
    public async Task CancelingDuringTheVersionQueryCancelsTheCallAtOnce()
    {
        var bus = new FakeBus();
        var chooser = new LinuxPortalFileChooser(bus);
        using var cancellation = new CancellationTokenSource();

        var pick = chooser.PickAsync(InlineThread.Instance, Options, folders: false, save: false, cancellation.Token).AsTask();
        Assert.IsFalse(pick.IsCompleted, "The portal has not answered its version.");
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => pick);
        Assert.IsTrue(bus.VersionCalls.Single().Operation.Canceled, "The D-Bus call is canceled, not left to its timeout.");
        Assert.AreEqual(0, chooser.PendingCount);
        // GIO still reports the end of the canceled call later: nothing is left to answer.
        bus.VersionCalls.Single().Completed(new(LinuxPortalReplyKind.Canceled));
        Assert.AreEqual(0, chooser.PendingCount);
        Assert.AreEqual(0, bus.Opens.Count);
    }

    [TestMethod]
    public async Task AResponseAnswersTheDialogAndRemovesItsListener()
    {
        var bus = new FakeBus { Version = 4 };
        var chooser = new LinuxPortalFileChooser(bus);

        var pick = chooser.PickAsync(InlineThread.Instance, Options, folders: true, save: false, CancellationToken.None).AsTask();
        var open = bus.Opens.Single();
        StringAssert.Contains(open.Parameters, "'directory': <true>");
        var subscription = bus.Requests.Single();
        StringAssert.StartsWith(subscription.Path, ExpectedPath);
        open.Completed(new(LinuxPortalReplyKind.Ok, subscription.Path));
        subscription.Raise(0, ["file:///tmp/chosen"]);

        var result = await pick;
        Assert.AreEqual(NeoDesktopStatus.Success, result!.Value.Status);
        CollectionAssert.AreEqual(new[] { "/tmp/chosen" }, result.Value.Value!.ToArray());
        Assert.IsTrue(subscription.Disposed);
        Assert.AreEqual(0, chooser.PendingCount);
    }

    [TestMethod]
    public async Task ARequestThePortalNamesOtherwiseIsFollowedAtItsPath()
    {
        var bus = new FakeBus { Version = 4 };
        var chooser = new LinuxPortalFileChooser(bus);

        var pick = chooser.PickAsync(InlineThread.Instance, Options, folders: false, save: true, CancellationToken.None).AsTask();
        Assert.AreEqual("SaveFile", bus.Opens.Single().Method);
        var predicted = bus.Requests.Single();
        bus.Opens.Single().Completed(new(LinuxPortalReplyKind.Ok, "/org/freedesktop/portal/desktop/request/1_42/other"));
        Assert.IsTrue(predicted.Disposed);
        var followed = bus.Requests.Last();
        Assert.AreEqual("/org/freedesktop/portal/desktop/request/1_42/other", followed.Path);
        followed.Raise(1, null);

        Assert.AreEqual(NeoDesktopStatus.Canceled, (await pick)!.Value.Status);
        Assert.IsTrue(followed.Disposed);
    }

    [TestMethod]
    public async Task CancelingAnOpenDialogClosesItAndRemovesItsListener()
    {
        var bus = new FakeBus { Version = 4 };
        var chooser = new LinuxPortalFileChooser(bus);
        using var cancellation = new CancellationTokenSource();

        var pick = chooser.PickAsync(InlineThread.Instance, Options, folders: false, save: false, cancellation.Token).AsTask();
        var subscription = bus.Requests.Single();
        bus.Opens.Single().Completed(new(LinuxPortalReplyKind.Ok, subscription.Path));
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => pick);
        CollectionAssert.AreEqual(new[] { subscription.Path }, bus.ClosedRequests);
        Assert.IsTrue(subscription.Disposed);
        Assert.AreEqual(0, chooser.PendingCount);
    }

    [TestMethod]
    public async Task ADialogCanceledBeforeThePortalNamedItsRequestIsClosedOnceNamed()
    {
        var bus = new FakeBus { Version = 4 };
        var chooser = new LinuxPortalFileChooser(bus);
        using var cancellation = new CancellationTokenSource();

        var pick = chooser.PickAsync(InlineThread.Instance, Options, folders: false, save: false, cancellation.Token).AsTask();
        var predicted = bus.Requests.Single().Path;
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pick);
        bus.Opens.Single().Completed(new(LinuxPortalReplyKind.Ok, "/org/freedesktop/portal/desktop/request/1_42/late"));

        CollectionAssert.AreEqual(new[] { predicted, "/org/freedesktop/portal/desktop/request/1_42/late" }, bus.ClosedRequests);
        Assert.AreEqual(1, bus.Requests.Count, "No listener is added for a dialog that already ended.");
    }

    [TestMethod]
    public async Task APortalThatLeavesTheBusEndsItsDialogsAndIsAskedItsVersionAgain()
    {
        var bus = new FakeBus { Version = 4 };
        var chooser = new LinuxPortalFileChooser(bus);

        var pick = chooser.PickAsync(InlineThread.Instance, Options, folders: false, save: false, CancellationToken.None).AsTask();
        var subscription = bus.Requests.Single();
        bus.Opens.Single().Completed(new(LinuxPortalReplyKind.Ok, subscription.Path));
        bus.Watch!.Raise();

        var result = (await pick)!.Value;
        Assert.AreEqual(NeoDesktopStatus.Failed, result.Status);
        Assert.AreEqual("portal_vanished", result.Code);
        Assert.IsTrue(subscription.Disposed);
        Assert.AreEqual(0, chooser.PendingCount);

        _ = chooser.PickAsync(InlineThread.Instance, Options, folders: false, save: false, CancellationToken.None).AsTask();
        Assert.AreEqual(2, bus.VersionCalls.Count, "A portal that comes back is asked its version again.");
        Assert.AreEqual(1, bus.Connects, "The connection is kept.");
    }

    [TestMethod]
    public async Task StoppingTheApplicationClosesTheDialogsAndShowsNoMore()
    {
        var bus = new FakeBus { Version = 4 };
        var chooser = new LinuxPortalFileChooser(bus);

        var shown = chooser.PickAsync(InlineThread.Instance, Options, folders: false, save: false, CancellationToken.None).AsTask();
        var first = bus.Requests.Single();
        bus.Opens.Single().Completed(new(LinuxPortalReplyKind.Ok, first.Path));
        // A second dialog whose request the portal has not named yet.
        var starting = chooser.PickAsync(InlineThread.Instance, Options, folders: false, save: false, CancellationToken.None).AsTask();
        var second = bus.Requests.Last();
        chooser.Stop();

        Assert.AreEqual(NeoDesktopStatus.Canceled, (await shown)!.Value.Status);
        Assert.AreEqual(NeoDesktopStatus.Canceled, (await starting)!.Value.Status);
        CollectionAssert.AreEqual(new[] { first.Path, second.Path }, bus.ClosedRequests);
        Assert.IsTrue(first.Disposed && second.Disposed);
        Assert.IsTrue(bus.Watch!.Disposed);
        Assert.AreEqual(0, chooser.PendingCount);

        var calls = bus.Calls;
        Assert.AreEqual(NeoDesktopStatus.Canceled, (await chooser.PickAsync(InlineThread.Instance, Options, folders: false, save: false, CancellationToken.None))!.Value.Status);
        Assert.AreEqual(calls, bus.Calls, "Nothing is asked of the portal after the application stopped.");
    }

    [TestMethod]
    public async Task StoppingTheApplicationDuringTheVersionQueryCancelsIt()
    {
        var bus = new FakeBus();
        var chooser = new LinuxPortalFileChooser(bus);

        var pick = chooser.PickAsync(InlineThread.Instance, Options, folders: false, save: false, CancellationToken.None).AsTask();
        Assert.IsFalse(pick.IsCompleted);
        chooser.Stop();

        Assert.AreEqual(NeoDesktopStatus.Canceled, (await pick)!.Value.Status, "Stopping is not a missing portal: no other presenter is started.");
        Assert.IsTrue(bus.VersionCalls.Single().Operation.Canceled);
        Assert.AreEqual(0, chooser.PendingCount);
        Assert.AreEqual(0, bus.Opens.Count);
    }

    [TestMethod]
    public async Task DisposingReleasesTheConnectionOnce()
    {
        var unused = new FakeBus();
        await new LinuxPortalFileChooser(unused).DisposeAsync();
        Assert.AreEqual(0, unused.Releases, "A chooser that never connected has nothing to release.");

        var bus = new FakeBus { Version = 4 };
        var chooser = new LinuxPortalFileChooser(bus);
        var pick = chooser.PickAsync(InlineThread.Instance, Options, folders: false, save: false, CancellationToken.None).AsTask();
        await chooser.DisposeAsync();
        await chooser.DisposeAsync();

        Assert.AreEqual(1, bus.Releases);
        Assert.AreEqual(NeoDesktopStatus.Canceled, (await pick)!.Value.Status);
        Assert.AreEqual(0, chooser.PendingCount);
    }

    [TestMethod]
    public async Task NoPortalOnTheBusLeavesTheDialogToAnotherPresenter()
    {
        var bus = new FakeBus();
        var chooser = new LinuxPortalFileChooser(bus);

        var pick = chooser.PickAsync(InlineThread.Instance, Options, folders: false, save: false, CancellationToken.None).AsTask();
        bus.VersionCalls.Single().Completed(new(LinuxPortalReplyKind.Missing));

        Assert.IsNull(await pick);
        Assert.IsNull(await chooser.PickAsync(InlineThread.Instance, Options, folders: false, save: false, CancellationToken.None));
        Assert.AreEqual(1, bus.VersionCalls.Count, "A missing portal is not asked again.");

        var noBus = new FakeBus { UniqueName = null };
        Assert.IsNull(await new LinuxPortalFileChooser(noBus).PickAsync(InlineThread.Instance, Options, folders: false, save: false, CancellationToken.None));
        Assert.AreEqual(0, noBus.VersionCalls.Count);
    }

    [TestMethod]
    public async Task DisposingTheSystemDialogsDisposesTheirPresenter()
    {
        var presenter = new DisposablePresenter();
        await using (var dialogs = new OwnerBoundDialogs(presenter)) { }
        Assert.AreEqual(1, presenter.Disposals);
        await new OwnerBoundDialogs(new NeoFakeDialogs()).DisposeAsync();
        await new LinuxDialogs(dispatcher: null, zenity: string.Empty, sessionBus: true).DisposeAsync();
    }

    [TestMethod]
    public void CallbackEntriesAreFoundByIdUntilRemoved()
    {
        var value = new object();
        var id = LinuxPortalCallbacks.Register(value);

        Assert.AreNotEqual((nint)0, id);
        Assert.AreNotEqual(id, LinuxPortalCallbacks.Register(new object()));
        Assert.IsTrue(LinuxPortalCallbacks.TryGet<object>(id, out var found));
        Assert.AreSame(value, found);
        Assert.IsFalse(LinuxPortalCallbacks.TryGet<string>(id, out _), "An entry of another type is not found.");
        Assert.IsTrue(LinuxPortalCallbacks.Remove(id));
        Assert.IsFalse(LinuxPortalCallbacks.TryGet<object>(id, out _), "A late callback finds nothing.");
        Assert.IsFalse(LinuxPortalCallbacks.Remove(id));
    }

    [TestMethod]
    public void OnlyTheLossOfItsOwnerIsTheLossOfThePortal()
    {
        Assert.IsTrue(LinuxPortalFileChooserProtocol.PortalVanished("org.freedesktop.portal.Desktop", ""));
        Assert.IsTrue(LinuxPortalFileChooserProtocol.PortalVanished("org.freedesktop.portal.Desktop", null));
        Assert.IsFalse(LinuxPortalFileChooserProtocol.PortalVanished("org.freedesktop.portal.Desktop", ":1.7"), "A portal that starts or restarts did not leave.");
        Assert.IsFalse(LinuxPortalFileChooserProtocol.PortalVanished("org.example.Other", ""));
    }

    private sealed class DisposablePresenter : INeoDialogs, IAsyncDisposable
    {
        internal int Disposals { get; private set; }
        public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Emulated, 1, 0, "Test presenter.");
        public ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFilesAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFoldersAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NeoDesktopResult<string>> SaveFileAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NeoDesktopResult<NeoDialogButtonRole>> ShowMessageAsync(NeoMessageDialogOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class InlineThread : ILinuxPortalThread
    {
        internal static readonly InlineThread Instance = new();

        public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeOperation : ILinuxPortalOperation
    {
        internal bool Canceled { get; private set; }
        public void Cancel() => Canceled = true;
    }

    private sealed class FakeSubscription(string path, Action<uint, IReadOnlyList<string>?>? responded, Action? vanished) : IDisposable
    {
        internal string Path { get; } = path;
        internal bool Disposed { get; private set; }
        internal void Raise(uint response, IReadOnlyList<string>? uris) { if (!Disposed) responded!(response, uris); }
        internal void Raise() { if (!Disposed) vanished!(); }
        public void Dispose() => Disposed = true;
    }

    private sealed record VersionCall(FakeOperation Operation, Action<LinuxPortalReply<uint>> Completed);

    private sealed record OpenCall(string Method, string Parameters, FakeOperation Operation, Action<LinuxPortalReply<string>> Completed);

    private sealed class FakeBus : ILinuxPortalBus
    {
        internal string? UniqueName { get; set; } = ":1.42";
        // A version that the portal answers at once; null leaves the query to the test.
        internal uint? Version { get; set; }
        internal int Connects { get; private set; }
        internal int Releases { get; private set; }
        internal List<VersionCall> VersionCalls { get; } = [];
        internal List<OpenCall> Opens { get; } = [];
        internal List<FakeSubscription> Requests { get; } = [];
        internal List<string> ClosedRequests { get; } = [];
        internal FakeSubscription? Watch { get; private set; }
        internal int Calls => VersionCalls.Count + Opens.Count + Requests.Count + ClosedRequests.Count + Connects;

        public string? Connect() { Connects++; return UniqueName; }

        public ILinuxPortalOperation GetVersion(Action<LinuxPortalReply<uint>> completed)
        {
            var operation = new FakeOperation();
            VersionCalls.Add(new(operation, completed));
            if (Version is { } version) completed(new(LinuxPortalReplyKind.Ok, version));
            return operation;
        }

        public ILinuxPortalOperation Open(string method, string parameters, Action<LinuxPortalReply<string>> completed)
        {
            var operation = new FakeOperation();
            Opens.Add(new(method, parameters, operation, completed));
            return operation;
        }

        public IDisposable Subscribe(string path, Action<uint, IReadOnlyList<string>?> responded)
        {
            var subscription = new FakeSubscription(path, responded, null);
            Requests.Add(subscription);
            return subscription;
        }

        public IDisposable WatchPortal(Action vanished) => Watch = new FakeSubscription("NameOwnerChanged", null, vanished);

        public void CloseRequest(string path) => ClosedRequests.Add(path);

        public void Release() => Releases++;
    }
}
