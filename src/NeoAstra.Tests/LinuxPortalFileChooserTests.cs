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
    public async Task ARequestThePortalNamesOtherwiseIsClosedAndTheDialogFails()
    {
        var bus = new FakeBus { Version = 4 };
        var chooser = new LinuxPortalFileChooser(bus);
        const string other = "/org/freedesktop/portal/desktop/request/1_42/other";

        var pick = chooser.PickAsync(InlineThread.Instance, Options, folders: false, save: true, CancellationToken.None).AsTask();
        Assert.AreEqual("SaveFile", bus.Opens.Single().Method);
        var predicted = bus.Requests.Single();
        bus.Opens.Single().Completed(new(LinuxPortalReplyKind.Ok, other));

        // A portal that does not honour handle_token is not followed: a stop could not close a request it has not named yet.
        Assert.IsNull(await pick, "Another presenter may show the dialog.");
        CollectionAssert.AreEqual(new[] { other }, bus.ClosedRequests);
        Assert.IsTrue(predicted.Disposed);
        Assert.AreEqual(1, bus.Requests.Count, "Nothing listens at the other path.");
        Assert.AreEqual(0, chooser.PendingCount);
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
        Assert.IsFalse(bus.Opens.Single().Operation.Canceled, "A caller's cancel keeps the call, whose answer names the request to close.");
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
        const string portal = "org.freedesktop.portal.Desktop";
        Assert.IsTrue(LinuxPortalFileChooserProtocol.PortalOwnerLost(portal, ":1.5", ""), "The portal left the bus.");
        Assert.IsTrue(LinuxPortalFileChooserProtocol.PortalOwnerLost(portal, ":1.5", null));
        Assert.IsTrue(LinuxPortalFileChooserProtocol.PortalOwnerLost(portal, ":1.5", ":1.9"), "Another portal replaced it directly.");
        Assert.IsFalse(LinuxPortalFileChooserProtocol.PortalOwnerLost(portal, "", ":1.9"), "The bus starts a first portal.");
        Assert.IsFalse(LinuxPortalFileChooserProtocol.PortalOwnerLost(portal, null, ":1.9"));
        Assert.IsFalse(LinuxPortalFileChooserProtocol.PortalOwnerLost(portal, ":1.5", ":1.5"));
        Assert.IsFalse(LinuxPortalFileChooserProtocol.PortalOwnerLost("org.example.Other", ":1.5", ""));
    }

    [TestMethod]
    public async Task ALostOwnerEndsAPendingVersionQueryAndDropsALateReply()
    {
        var bus = new FakeBus();
        var chooser = new LinuxPortalFileChooser(bus);

        var pick = chooser.PickAsync(InlineThread.Instance, Options, folders: false, save: false, CancellationToken.None).AsTask();
        bus.Watch!.Raise();
        var result = (await pick)!.Value;
        Assert.AreEqual(NeoDesktopStatus.Failed, result.Status);
        Assert.AreEqual("portal_vanished", result.Code);
        Assert.IsTrue(bus.VersionCalls.Single().Operation.Canceled);
        // The old owner answers late: its version is not kept for the new one.
        bus.VersionCalls.Single().Completed(new(LinuxPortalReplyKind.Ok, 4));
        _ = chooser.PickAsync(InlineThread.Instance, Options, folders: false, save: false, CancellationToken.None).AsTask();
        Assert.AreEqual(2, bus.VersionCalls.Count);
        Assert.AreEqual(0, bus.Opens.Count);
    }

    [TestMethod]
    public async Task DisposingWhileTheFirstConnectionIsMadeReleasesItOnceMade()
    {
        var bus = new FakeBus { Version = 4 };
        var chooser = new LinuxPortalFileChooser(bus);
        bus.Connecting = () =>
        {
            Assert.IsTrue(chooser.DisposeAsync().IsCompletedSuccessfully);
            Assert.AreEqual(0, bus.Releases, "Nothing to release before the bus gave the connection.");
        };

        var result = await chooser.PickAsync(InlineThread.Instance, Options, folders: false, save: false, CancellationToken.None);

        Assert.AreEqual(NeoDesktopStatus.Canceled, result!.Value.Status);
        Assert.AreEqual(1, bus.Releases, "The connection made after the disposal is released at once.");
        Assert.AreEqual(0, bus.VersionCalls.Count, "No version query starts on it.");
        Assert.IsNull(bus.Watch);
        await chooser.DisposeAsync();
        Assert.AreEqual(1, bus.Releases);
    }

    [TestMethod]
    public async Task StoppingWhileTheFirstConnectionIsMadeKeepsItForTheDisposal()
    {
        var bus = new FakeBus { Version = 4 };
        var chooser = new LinuxPortalFileChooser(bus);
        bus.Connecting = chooser.Stop;

        var result = await chooser.PickAsync(InlineThread.Instance, Options, folders: false, save: false, CancellationToken.None);

        Assert.AreEqual(NeoDesktopStatus.Canceled, result!.Value.Status);
        Assert.AreEqual(0, bus.VersionCalls.Count);
        Assert.IsNull(bus.Watch);
        Assert.AreEqual(0, bus.Releases);
        await chooser.DisposeAsync();
        Assert.AreEqual(1, bus.Releases);
    }

    [TestMethod]
    public async Task AStopBeforeTheCallIsSentOpensNothing()
    {
        var bus = new FakeBus { Version = 4 };
        var chooser = new LinuxPortalFileChooser(bus);
        // The application stops while the request starts to listen, before it calls the portal.
        bus.Subscribing = _ => chooser.Stop();

        var result = await chooser.PickAsync(InlineThread.Instance, Options, folders: false, save: false, CancellationToken.None);

        Assert.AreEqual(NeoDesktopStatus.Canceled, result!.Value.Status);
        Assert.AreEqual(0, bus.Opens.Count, "No request is made that nobody would close.");
        Assert.AreEqual(0, bus.ClosedRequests.Count);
        Assert.IsTrue(bus.Requests.Single().Disposed);
        Assert.AreEqual(0, chooser.PendingCount);
    }

    [TestMethod]
    public async Task ACancelWhileTheRequestStartsClosesItAfterTheCall()
    {
        var bus = new FakeBus { Version = 4 };
        var chooser = new LinuxPortalFileChooser(bus);
        using var cancellation = new CancellationTokenSource();
        var opensAtClose = -1;
        bus.Subscribing = _ => cancellation.Cancel();
        bus.Closing = _ => opensAtClose = bus.Opens.Count;

        var pick = chooser.PickAsync(InlineThread.Instance, Options, folders: false, save: false, cancellation.Token).AsTask();

        await Assert.ThrowsAsync<OperationCanceledException>(() => pick);
        CollectionAssert.AreEqual(new[] { bus.Requests.Single().Path }, bus.ClosedRequests);
        Assert.AreEqual(1, opensAtClose, "The close of the predicted path follows the call that makes the request.");
        Assert.AreEqual(0, chooser.PendingCount);
    }

    [TestMethod]
    public async Task StoppingCancelsACallThatHasNotReturnedAndDrainsTheCalls()
    {
        var bus = new FakeBus { Version = 4 };
        var chooser = new LinuxPortalFileChooser(bus);

        var pick = chooser.PickAsync(InlineThread.Instance, Options, folders: false, save: false, CancellationToken.None).AsTask();
        await chooser.StopAsync(CancellationToken.None);

        Assert.AreEqual(NeoDesktopStatus.Canceled, (await pick)!.Value.Status);
        Assert.IsTrue(bus.Opens.Single().Operation.Canceled, "Its answer could not be delivered any more.");
        CollectionAssert.AreEqual(new[] { bus.Requests.Single().Path }, bus.ClosedRequests, "The request is closed at the path it is predicted to have.");
        Assert.AreEqual(1, bus.Drains, "The main loop reports the end of the native calls before the application quits.");
        // A late answer of a portal that named the request otherwise still closes that request.
        bus.Opens.Single().Completed(new(LinuxPortalReplyKind.Ok, "/org/freedesktop/portal/desktop/request/1_42/late"));
        Assert.AreEqual("/org/freedesktop/portal/desktop/request/1_42/late", bus.ClosedRequests.Last());
        await chooser.DisposeAsync();
        Assert.AreEqual(1, bus.Abandons);
        Assert.AreEqual(1, bus.Releases);

        var unused = new FakeBus();
        var idle = new LinuxPortalFileChooser(unused);
        await idle.StopAsync(CancellationToken.None);
        await idle.DisposeAsync();
        Assert.AreEqual(0, unused.Drains + unused.Abandons + unused.Releases, "A chooser that never connected has nothing to drain.");
    }

    [TestMethod]
    public async Task TheCallTrackerDrainsWithoutBlockingAndAbandonsWhatOutlivesTheDeadline()
    {
        var tracker = new LinuxPortalCallTracker();
        var call = new FakeTrackedCall(tracker);
        var subscription = new FakeTrackedCall(tracker);

        var drain = tracker.DrainAsync(CancellationToken.None).AsTask();
        Assert.IsTrue(call.Canceled && subscription.Canceled);
        Assert.IsFalse(drain.IsCompleted, "The drain waits for the main loop to report the end of the call and the release of the subscription.");
        call.End();
        Assert.IsFalse(drain.IsCompleted, "A subscription whose queued signals were not delivered yet is still held.");
        subscription.End();
        await drain;
        Assert.AreEqual(0, tracker.Count);
        Assert.IsFalse(call.Abandoned || subscription.Abandoned);
        Assert.IsFalse(tracker.TryAdd(new FakeTrackedCall()), "After the drain, nothing starts.");

        var stuckTracker = new LinuxPortalCallTracker();
        var stuck = new FakeTrackedCall(stuckTracker);
        using var deadline = new CancellationTokenSource();
        var late = stuckTracker.DrainAsync(deadline.Token).AsTask();
        deadline.Cancel();
        await late;
        Assert.IsTrue(stuck.Canceled && stuck.Abandoned, "What never ends is released at the deadline.");
        Assert.AreEqual(0, stuckTracker.Count);

        var abandoned = new LinuxPortalCallTracker();
        var forgotten = new FakeTrackedCall(abandoned);
        abandoned.Abandon();
        Assert.IsTrue(forgotten.Abandoned);
        Assert.AreEqual(0, abandoned.Count);
        Assert.IsFalse(abandoned.TryAdd(new FakeTrackedCall()), "The abandonment is final too.");
        Assert.AreEqual(0, abandoned.Count);
    }

    private sealed class FakeTrackedCall : ILinuxPortalTrackedCall
    {
        private readonly LinuxPortalCallTracker? _tracker;
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal FakeTrackedCall() { }
        internal FakeTrackedCall(LinuxPortalCallTracker tracker) { _tracker = tracker; Assert.IsTrue(tracker.TryAdd(this)); }

        internal bool Canceled { get; private set; }
        internal bool Abandoned { get; private set; }
        public Task Ended => _ended.Task;
        public void Cancel() => Canceled = true;
        public void Abandon() { Canceled = true; Abandoned = true; _ended.TrySetResult(); }
        // The main loop reports the end of the call.
        internal void End() { _tracker?.Remove(this); _ended.TrySetResult(); }
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

        // Runs inside Connect and inside Subscribe, as another thread would between two steps of the chooser.
        internal Action? Connecting { get; set; }
        internal Action<string>? Subscribing { get; set; }
        internal int Drains { get; private set; }
        internal int Abandons { get; private set; }

        public string? Connect() { Connects++; Connecting?.Invoke(); return UniqueName; }

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
            Subscribing?.Invoke(path);
            return subscription;
        }

        public IDisposable WatchPortal(Action vanished) => Watch = new FakeSubscription("NameOwnerChanged", null, vanished);

        internal Action<string>? Closing { get; set; }

        public void CloseRequest(string path) { Closing?.Invoke(path); ClosedRequests.Add(path); }

        public ValueTask DrainAsync(CancellationToken cancellationToken) { Drains++; return ValueTask.CompletedTask; }

        public void Abandon() => Abandons++;

        public void Release() => Releases++;
    }
}
