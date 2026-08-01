// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Buffers.Binary;
using System.IO.Pipes;

namespace NeoAstra.Tests;

[TestClass]
public sealed class LifecycleTests
{
    [TestMethod]
    public async Task CloseHandlersRunInOrderAndAnyCancellationWins()
    {
        var window = (NeoWindow)RuntimeHelpers.GetUninitializedObject(typeof(NeoWindow));
        var order = new List<int>();
        window.CloseRequested += request => { order.Add(1); return ValueTask.CompletedTask; };
        window.CloseRequested += request => { order.Add(2); request.Cancel(); return ValueTask.CompletedTask; };
        window.CloseRequested += request => { order.Add(3); return ValueTask.CompletedTask; };

        var allowed = await window.EvaluateCloseAsync(NeoWindowCloseReason.User, true, CancellationToken.None);

        Assert.IsFalse(allowed);
        CollectionAssert.AreEqual(new[] { 1, 2 }, order);
    }

    [TestMethod]
    public async Task CloseHandlerExceptionUsesSafeCancelDefault()
    {
        var window = (NeoWindow)RuntimeHelpers.GetUninitializedObject(typeof(NeoWindow));
        window.CloseRequested += _ => ValueTask.FromException(new InvalidOperationException("save failed"));

        Assert.IsFalse(await window.EvaluateCloseAsync(NeoWindowCloseReason.Programmatic, true, CancellationToken.None));
        Assert.IsTrue(await window.EvaluateCloseAsync(NeoWindowCloseReason.SessionEnd, false, CancellationToken.None));
    }

    [TestMethod]
    public async Task NativeCloseEvaluationFailureUsesSafeDefaultAndReleasesOnce()
    {
        var window = (NeoWindow)RuntimeHelpers.GetUninitializedObject(typeof(NeoWindow));
        bool? completed = null;
        var releases = 0;

        await window.CompleteCloseEvaluationAsync(
            _ => ValueTask.FromException<bool>(new InvalidOperationException("tree evaluation failed")),
            canCancel: true,
            CancellationToken.None,
            allowed => { completed = allowed; return ValueTask.CompletedTask; },
            () => releases++);

        Assert.AreEqual(false, completed);
        Assert.AreEqual(1, releases);
    }

    [TestMethod]
    public async Task CloseHandlerDeadlineUsesSafeDefaultWithoutBlockingCaller()
    {
        var window = (NeoWindow)RuntimeHelpers.GetUninitializedObject(typeof(NeoWindow));
        window.CloseRequested += _ => new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        Assert.IsFalse(await window.EvaluateCloseAsync(NeoWindowCloseReason.User, true, deadline.Token));

        using var forcedDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
        Assert.IsTrue(await window.EvaluateCloseAsync(NeoWindowCloseReason.SessionEnd, false, forcedDeadline.Token));
    }

    [TestMethod]
    public void WindowConvenienceEventsReportDistinctNativeTransitions()
    {
        var window = (NeoWindow)RuntimeHelpers.GetUninitializedObject(typeof(NeoWindow));
        var events = new List<string>();
        window.FocusChanged += (_, _) => events.Add("focus");
        window.Activated += (_, _) => events.Add("activated");
        window.Deactivated += (_, _) => events.Add("deactivated");
        window.StateChanged += (_, args) => events.Add($"state:{args.OldState}-{args.NewState}");
        window.Maximized += (_, _) => events.Add("maximized");
        window.FullscreenEntered += (_, _) => events.Add("fullscreen-entered");
        window.FullscreenExited += (_, _) => events.Add("fullscreen-exited");
        window.Restored += (_, _) => events.Add("restored");

        window.OnFocusChanged(true);
        window.OnFocusChanged(true);
        window.OnFocusChanged(false);
        window.OnStateChanged(NeoWindowState.Maximized);
        window.OnStateChanged(NeoWindowState.Maximized);
        window.OnStateChanged(NeoWindowState.Fullscreen);
        window.OnStateChanged(NeoWindowState.Normal);

        CollectionAssert.AreEqual(new[]
        {
            "focus", "activated", "focus", "deactivated",
            "state:Normal-Maximized", "maximized",
            "state:Maximized-Fullscreen", "fullscreen-entered",
            "state:Fullscreen-Normal", "fullscreen-exited", "restored",
        }, events);
    }

    [TestMethod]
    public void WindowResizeEdgeIsValidatedBeforeNativeDispatch()
    {
        var window = (NeoWindow)RuntimeHelpers.GetUninitializedObject(typeof(NeoWindow));
        Assert.Throws<ArgumentOutOfRangeException>(() => window.BeginResize((NeoWindowResizeEdge)99));
    }

    [TestMethod]
    public void LaunchEventsAreImmutableAndStrictlyValidated()
    {
        var arguments = new[] { "--document", "note.txt" };
        var launch = new NeoLaunchEvent(NeoLaunchReason.SecondInstance, arguments, Path.GetFullPath("."),
            [Path.GetFullPath("note.txt")], [new Uri("myapp://open/note")]);
        arguments[0] = "mutated";

        Assert.AreEqual("--document", launch.Arguments[0]);
        Assert.Throws<ArgumentException>(() => new NeoLaunchEvent(NeoLaunchReason.OpenFiles, files: ["relative.txt"]));
        Assert.Throws<ArgumentException>(() => new NeoLaunchEvent(NeoLaunchReason.OpenUrls, urls: [new Uri("file:///secret")]));
        Assert.Throws<ArgumentException>(() => new NeoLaunchEvent(NeoLaunchReason.Initial, arguments: ["bad\nargument"]));
    }

    [TestMethod]
    public void LifecycleOptionsEnforceResourceBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NeoApplicationOptions { MaximumPendingLaunchEvents = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NeoQuitOptions { Timeout = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentException>(() => new NeoSingleInstanceOptions { ApplicationId = "bad\nid" }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NeoSingleInstanceOptions { ApplicationId = "com.example.editor", MaximumEnvelopeBytes = 1 }.Validate());
    }

    [TestMethod]
    public void QuitRequestExposesTruthfulCancellationAndDeadline()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        var cancelable = new NeoQuitRequest(NeoQuitReason.SessionEnd, 7, true, deadline, CancellationToken.None);
        cancelable.Cancel();
        Assert.IsTrue(cancelable.CanCancel);
        Assert.IsTrue(cancelable.IsCanceled);
        Assert.AreEqual(deadline, cancelable.Deadline);

        var forced = new NeoQuitRequest(NeoQuitReason.SessionEnd, 9, false, deadline, CancellationToken.None);
        forced.Cancel();
        Assert.IsFalse(forced.CanCancel);
        Assert.IsFalse(forced.IsCanceled);
    }

    [TestMethod]
    public void SingleInstanceEnvelopeParserRejectsUnknownDuplicateAndInvalidValues()
    {
        const string valid = "{\"version\":1,\"requestId\":\"00112233445566778899aabbccddeeff\",\"reason\":\"SecondInstance\",\"arguments\":[],\"files\":[],\"urls\":[],\"metadata\":{}}";
        Assert.IsTrue(NeoSingleInstance.TryReadEnvelope(Encoding.UTF8.GetBytes(valid), out var launch));
        Assert.AreEqual(NeoLaunchReason.SecondInstance, launch!.Reason);
        Assert.IsFalse(NeoSingleInstance.TryReadEnvelope(Encoding.UTF8.GetBytes(valid.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal)), out _));
        Assert.IsFalse(NeoSingleInstance.TryReadEnvelope(Encoding.UTF8.GetBytes(valid.Replace("\"metadata\":{}", "\"metadata\":{},\"environment\":{}", StringComparison.Ordinal)), out _));
        Assert.IsFalse(NeoSingleInstance.TryReadEnvelope(Encoding.UTF8.GetBytes(valid.Replace("\"files\":[]", "\"files\":[\"relative.txt\"]", StringComparison.Ordinal)), out _));
        Assert.IsFalse(NeoSingleInstance.TryReadEnvelope(Encoding.UTF8.GetBytes(valid.Replace("\"urls\":[]", "\"urls\":[\"file:///secret\"]", StringComparison.Ordinal)), out _));
    }

    [TestMethod]
    public async Task SecondaryProcessRoutesBeforeReadyAndReceivesAcknowledgement()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var receivedFile = await RunStaAsync(() =>
            {
                NeoSingleInstance? primary = null;
                string? received = null;
                var applicationId = "neoastra.tests." + Guid.NewGuid().ToString("N");
                var file = Path.GetFullPath("second-launch.neoastra");
                var exitCode = NeoApplication.Run(new NeoApplicationOptions
                {
                    ApplicationName = "NeoAstra single-instance test",
                    QueueInitialLaunchEvent = false,
                    ShutdownMode = NeoApplicationShutdownMode.Explicit,
                }, async application =>
                {
                    application.LaunchReceived += launch =>
                    {
                        received = launch.Files.Single();
                        application.Shutdown();
                        return ValueTask.CompletedTask;
                    };
                    primary = await NeoSingleInstance.AcquireAsync(application,
                        new NeoSingleInstanceOptions { ApplicationId = applicationId },
                        new NeoLaunchEvent(NeoLaunchReason.SecondInstance));
                    Assert.IsTrue(primary.IsPrimary);

                    var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
                    var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
                    var helper = Path.Combine(source, "NeoAstra.SingleInstanceHelper", "bin", configuration, "net10.0", "NeoAstra.SingleInstanceHelper.dll");
                    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
                    start.ArgumentList.Add(helper);
                    start.ArgumentList.Add(applicationId);
                    start.ArgumentList.Add(file);
                    using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the secondary-process fixture.");
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.AreEqual(0, process.ExitCode);
                });
                Assert.AreEqual(0, exitCode);
                primary?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                return received;
            });

            Assert.AreEqual(Path.GetFullPath("second-launch.neoastra"), receivedFile);
        }
        catch (NeoAstraNativeLibraryException)
        {
            // Managed CI does not stage native assets; native-enabled Windows validation executes this path.
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task PrimaryAcknowledgesDuplicateRequestWithoutEnqueuingTwice()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var delivered = await RunStaAsync(() =>
            {
                var count = 0;
                NeoSingleInstance? primary = null;
                NeoApplication.Run(new NeoApplicationOptions
                {
                    ApplicationName = "NeoAstra replay test",
                    QueueInitialLaunchEvent = false,
                    ShutdownMode = NeoApplicationShutdownMode.Explicit,
                }, async application =>
                {
                    application.LaunchReceived += _ =>
                    {
                        count++;
                        application.ForceShutdown();
                        return ValueTask.CompletedTask;
                    };
                    var applicationId = "neoastra.tests." + Guid.NewGuid().ToString("N");
                    primary = await NeoSingleInstance.AcquireAsync(application,
                        new NeoSingleInstanceOptions { ApplicationId = applicationId },
                        new NeoLaunchEvent(NeoLaunchReason.SecondInstance));
                    Assert.AreEqual(0, await SendRawEnvelopeAsync(primary.EndpointName, Encoding.UTF8.GetBytes("{}")));
                    Assert.AreEqual(0, await SendRawHeaderAsync(primary.EndpointName, 1024 * 1024));
                    await SendTruncatedEnvelopeAsync(primary.EndpointName);
                    var payload = NeoSingleInstance.WriteEnvelope(Guid.NewGuid(), new NeoLaunchEvent(NeoLaunchReason.SecondInstance));
                    await SendWithoutReadingAcknowledgementAsync(primary.EndpointName, payload);
                    Assert.AreEqual(1, await SendRawEnvelopeAsync(primary.EndpointName, payload));
                    Assert.AreEqual(1, await SendRawEnvelopeAsync(primary.EndpointName, payload));
                    await primary.DisposeAsync();
                    primary = await NeoSingleInstance.AcquireAsync(application,
                        new NeoSingleInstanceOptions { ApplicationId = applicationId },
                        new NeoLaunchEvent(NeoLaunchReason.SecondInstance));
                    Assert.IsTrue(primary.IsPrimary);
                });
                primary?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                return count;
            });
            Assert.AreEqual(1, delivered);
        }
        catch (NeoAstraNativeLibraryException) { }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task ConcurrentProcessesElectOnePrimaryAndRecoverAbandonedLock()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "neoastra-single-instance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var helper = GetSingleInstanceHelperPath();
            var applicationId = "neoastra.tests.race." + Guid.NewGuid().ToString("N");
            var gate = Path.Combine(directory, "gate");
            var processes = Enumerable.Range(0, 4).Select(index => StartRaceHelper(helper, applicationId, gate,
                Path.Combine(directory, "ready-" + index), 500)).ToArray();
            await File.WriteAllTextAsync(gate, "go");
            await Task.WhenAll(processes.Select(process => process.WaitForExitAsync())).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, processes.Count(process => process.ExitCode == 10));
            Assert.AreEqual(3, processes.Count(process => process.ExitCode == 11));
            foreach (var process in processes) process.Dispose();

            File.Delete(gate);
            var abandonedReady = Path.Combine(directory, "abandoned-ready");
            using var abandoned = StartRaceHelper(helper, applicationId, gate, abandonedReady, 10_000);
            await File.WriteAllTextAsync(gate, "go");
            await WaitUntilAsync(() => File.Exists(abandonedReady));
            abandoned.Kill(entireProcessTree: true);
            await abandoned.WaitForExitAsync();

            using var replacement = StartRaceHelper(helper, applicationId, gate, Path.Combine(directory, "replacement-ready"), 10);
            await replacement.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(10, replacement.ExitCode);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task ExplicitRejectionIsNotRetriedBySecondaryProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            await RunStaAsync(() =>
            {
                NeoSingleInstance? primary = null;
                NeoApplication.Run(new NeoApplicationOptions
                {
                    QueueInitialLaunchEvent = false,
                    MaximumPendingLaunchEvents = 1,
                    ShutdownMode = NeoApplicationShutdownMode.Explicit,
                }, async application =>
                {
                    var applicationId = "neoastra.tests.reject." + Guid.NewGuid().ToString("N");
                    primary = await NeoSingleInstance.AcquireAsync(application, new NeoSingleInstanceOptions { ApplicationId = applicationId },
                        new NeoLaunchEvent(NeoLaunchReason.SecondInstance));
                    Assert.IsTrue(application.QueueLaunchEvent(new NeoLaunchEvent(NeoLaunchReason.Activated)));
                    using var process = StartSecondaryHelper(GetSingleInstanceHelperPath(), applicationId, Path.GetFullPath("rejected.neoastra"), retry: true);
                    var elapsed = Stopwatch.StartNew();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.AreEqual(4, process.ExitCode);
                    Assert.IsLessThan(TimeSpan.FromMilliseconds(1500), elapsed.Elapsed);
                    application.ForceShutdown();
                });
                primary?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                return 0;
            });
        }
        catch (NeoAstraNativeLibraryException) { }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task UnreachablePrimaryProcessRouteStopsAtConfiguredDeadline()
    {
        if (!OperatingSystem.IsWindows()) return;
        var exitCode = await RunStaAsync(() =>
        {
            var applicationId = "neoastra.tests.hung." + Guid.NewGuid().ToString("N");
            var endpoint = NeoSingleInstance.CreateEndpoint(applicationId);
            using var primaryLock = new Mutex(true, "neoastra-lock-" + endpoint, out var created);
            Assert.IsTrue(created);
            using var process = StartSecondaryHelper(GetSingleInstanceHelperPath(), applicationId, Path.GetFullPath("hung.neoastra"), retry: true);
            Assert.IsTrue(process.WaitForExit(5_000));
            primaryLock.ReleaseMutex();
            return process.ExitCode;
        });
        Assert.AreEqual(5, exitCode);
    }

    [TestMethod]
    public async Task QuitCoalescesRejectsNewWindowsAndNegotiatesChildrenFirst()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var order = await RunStaAsync(() =>
            {
                var observed = new List<string>();
                NeoApplication.Run(new NeoApplicationOptions
                {
                    ApplicationName = "NeoAstra quit ordering test",
                    QueueInitialLaunchEvent = false,
                    ShutdownMode = NeoApplicationShutdownMode.Explicit,
                }, async application =>
                {
                    var owner = application.CreateWindow(new NeoWindowOptions { Label = "owner", IsVisible = false });
                    var child = application.CreateWindow(new NeoWindowOptions { Label = "child", Owner = owner, IsVisible = false });
                    owner.CloseRequested += _ => { observed.Add("owner"); return ValueTask.CompletedTask; };
                    child.CloseRequested += _ => { observed.Add("child"); return ValueTask.CompletedTask; };
                    var first = application.RequestQuitAsync();
                    var joined = application.RequestQuitAsync();
                    Assert.AreSame(first, joined);
                    Assert.Throws<InvalidOperationException>(() => application.CreateWindow(new NeoWindowOptions { Label = "late" }));
                    Assert.AreEqual(NeoQuitResult.Completed, await first);
                });
                return observed;
            });

            CollectionAssert.AreEqual(new[] { "child", "owner" }, order);
        }
        catch (NeoAstraNativeLibraryException)
        {
            // Managed CI does not stage native assets; native-enabled Windows validation executes this path.
        }
    }

    [TestMethod]
    public async Task ApprovedDirectOwnerCloseClosesSnapshotChildBeforeOwner()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var closed = await RunStaAsync(() =>
            {
                var observed = new List<string>();
                NeoApplication.Run(new NeoApplicationOptions
                {
                    ApplicationName = "NeoAstra owner close test",
                    QueueInitialLaunchEvent = false,
                    ShutdownMode = NeoApplicationShutdownMode.Explicit,
                }, async application =>
                {
                    var owner = application.CreateWindow(new NeoWindowOptions { Label = "direct-owner", IsVisible = false });
                    var child = application.CreateWindow(new NeoWindowOptions { Label = "direct-child", Owner = owner, IsVisible = false });
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    child.CloseRequested += request =>
                    {
                        Assert.AreEqual(NeoWindowCloseReason.Owner, request.Reason);
                        observed.Add("child-request");
                        Assert.Throws<InvalidOperationException>(() => application.CreateWindow(new NeoWindowOptions { Label = "mutation", Owner = owner }));
                        return ValueTask.CompletedTask;
                    };
                    owner.CloseRequested += request => { observed.Add("owner-request"); return ValueTask.CompletedTask; };
                    child.Closed += (_, _) => observed.Add("child-closed");
                    owner.Closed += (_, _) => { observed.Add("owner-closed"); completion.TrySetResult(); };
                    owner.Close();
                    await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.IsFalse(application.TryGetWindow("direct-child", out _));
                    Assert.IsFalse(application.TryGetWindow("direct-owner", out _));
                    application.ForceShutdown();
                });
                return observed;
            });

            CollectionAssert.AreEqual(new[] { "child-request", "owner-request", "child-closed", "owner-closed" }, closed);
        }
        catch (NeoAstraNativeLibraryException) { }
    }

    [TestMethod]
    public async Task OverlappingOwnerAndChildCloseTreesAreRejectedInBothDirections()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            await RunStaAsync(() =>
            {
                NeoApplication.Run(new NeoApplicationOptions
                {
                    QueueInitialLaunchEvent = false,
                    ShutdownMode = NeoApplicationShutdownMode.Explicit,
                }, async application =>
                {
                    var owner = application.CreateWindow(new NeoWindowOptions { Label = "child-first-owner", IsVisible = false });
                    var child = application.CreateWindow(new NeoWindowOptions { Label = "child-first-child", Owner = owner, IsVisible = false });
                    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var childClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var ownerRequests = 0;
                    child.CloseRequested += async _ => { entered.TrySetResult(); await release.Task; };
                    child.Closed += (_, _) => childClosed.TrySetResult();
                    owner.CloseRequested += _ => { ownerRequests++; return ValueTask.CompletedTask; };

                    child.Close();
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    owner.Close();
                    await Task.Delay(25);
                    release.TrySetResult();
                    await childClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.AreEqual(0, ownerRequests);
                    Assert.IsTrue(application.TryGetWindow("child-first-owner", out _));
                    application.ForceShutdown();
                });

                NeoApplication.Run(new NeoApplicationOptions
                {
                    QueueInitialLaunchEvent = false,
                    ShutdownMode = NeoApplicationShutdownMode.Explicit,
                }, async application =>
                {
                    var owner = application.CreateWindow(new NeoWindowOptions { Label = "owner-first-owner", IsVisible = false });
                    var child = application.CreateWindow(new NeoWindowOptions { Label = "owner-first-child", Owner = owner, IsVisible = false });
                    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var ownerClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var childRequests = 0;
                    child.CloseRequested += async _ => { childRequests++; entered.TrySetResult(); await release.Task; };
                    owner.Closed += (_, _) => ownerClosed.TrySetResult();

                    owner.Close();
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    child.Close();
                    await Task.Delay(25);
                    release.TrySetResult();
                    await ownerClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.AreEqual(1, childRequests);
                    Assert.IsFalse(application.TryGetWindow("owner-first-child", out _));
                    application.ForceShutdown();
                });
                return 0;
            });
        }
        catch (NeoAstraNativeLibraryException) { }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task WindowsSessionQueryIsBoundedNonCancelableAndFinalPhaseForcesExit()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var queries = await RunStaAsync(() =>
            {
                var count = 0;
                NeoApplication.Run(new NeoApplicationOptions
                {
                    ApplicationName = "NeoAstra session-end test",
                    QueueInitialLaunchEvent = false,
                    ShutdownMode = NeoApplicationShutdownMode.Explicit,
                }, async application =>
                {
                    var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    application.BeforeQuit += request =>
                    {
                        Assert.AreEqual(NeoQuitReason.SessionEnd, request.Reason);
                        Assert.IsFalse(request.CanCancel);
                        Assert.IsGreaterThan(DateTimeOffset.UtcNow, request.Deadline);
                        Assert.IsLessThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(3), request.Deadline);
                        request.Cancel();
                        if (++count == 2) observed.TrySetResult();
                        return ValueTask.CompletedTask;
                    };
                    var window = application.CreateWindow(new NeoWindowOptions { Label = "session-window", IsVisible = false });
                    var hwnd = window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
                    Assert.AreNotEqual(0, SendMessageW(hwnd, 0x0011, 0, 0)); // WM_QUERYENDSESSION
                    await WaitUntilAsync(() => count == 1);
                    _ = SendMessageW(hwnd, 0x0016, 0, 0); // aborted WM_ENDSESSION
                    Assert.AreNotEqual(0, SendMessageW(hwnd, 0x0011, 0, 0));
                    await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    _ = SendMessageW(hwnd, 0x0016, 1, 0); // final WM_ENDSESSION
                });
                return count;
            });
            Assert.AreEqual(2, queries);
        }
        catch (NeoAstraNativeLibraryException) { }
    }

    [TestMethod]
    public async Task CanceledQuitReturnsReadyWithoutClosingPreflightedWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            await RunStaAsync(() =>
            {
                NeoApplication.Run(new NeoApplicationOptions
                {
                    ApplicationName = "NeoAstra canceled quit test",
                    QueueInitialLaunchEvent = false,
                    ShutdownMode = NeoApplicationShutdownMode.Explicit,
                }, async application =>
                {
                    var window = application.CreateWindow(new NeoWindowOptions { Label = "retained", IsVisible = false });
                    window.CloseRequested += request => { request.Cancel(); return ValueTask.CompletedTask; };
                    Assert.AreEqual(NeoQuitResult.Canceled, await application.RequestQuitAsync());
                    Assert.AreEqual(NeoApplicationState.Starting, application.State);
                    Assert.IsTrue(application.TryGetWindow("retained", out var retained));
                    Assert.AreSame(window, retained);
                    application.NotifyReady();
                    Assert.AreEqual(NeoApplicationState.Ready, application.State);
                    application.ForceShutdown();
                });
                return 0;
            });
        }
        catch (NeoAstraNativeLibraryException)
        {
            // Managed CI does not stage native assets; native-enabled Windows validation executes this path.
        }
    }

    [TestMethod]
    public async Task EarlyLaunchEventsDispatchOnceInArrivalOrderAfterReady()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var launches = await RunStaAsync(() =>
            {
                var observed = new List<(NeoLaunchReason Reason, ulong Order)>();
                NeoApplication.Run(new NeoApplicationOptions
                {
                    ApplicationName = "NeoAstra early launch test",
                    QueueInitialLaunchEvent = false,
                    ShutdownMode = NeoApplicationShutdownMode.Explicit,
                }, application =>
                {
                    application.LaunchReceived += launch =>
                    {
                        observed.Add((launch.Reason, launch.Order));
                        if (observed.Count == 2) application.Shutdown();
                        return ValueTask.CompletedTask;
                    };
                    Assert.IsTrue(application.QueueLaunchEvent(new NeoLaunchEvent(NeoLaunchReason.Activated)));
                    Assert.IsTrue(application.QueueLaunchEvent(new NeoLaunchEvent(NeoLaunchReason.OpenFiles, files: [Path.GetFullPath("early.txt")])));
                    Assert.AreEqual(0, observed.Count);
                    return ValueTask.CompletedTask;
                });
                return observed;
            });

            CollectionAssert.AreEqual(new[] { NeoLaunchReason.Activated, NeoLaunchReason.OpenFiles }, launches.Select(static launch => launch.Reason).ToArray());
            CollectionAssert.AreEqual(new ulong[] { 1, 2 }, launches.Select(static launch => launch.Order).ToArray());
        }
        catch (NeoAstraNativeLibraryException)
        {
            // Managed CI does not stage native assets; native-enabled Windows validation executes this path.
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<byte> SendRawEnvelopeAsync(string endpoint, byte[] payload)
    {
        await using var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await pipe.WriteAsync(header);
        await pipe.WriteAsync(payload);
        await pipe.FlushAsync();
        var acknowledgement = new byte[1];
        await pipe.ReadExactlyAsync(acknowledgement);
        return acknowledgement[0];
    }

    [SupportedOSPlatform("windows")]
    private static async Task<byte> SendRawHeaderAsync(string endpoint, int length)
    {
        await using var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        await pipe.WriteAsync(header);
        var acknowledgement = new byte[1];
        await pipe.ReadExactlyAsync(acknowledgement);
        return acknowledgement[0];
    }

    [SupportedOSPlatform("windows")]
    private static async Task SendWithoutReadingAcknowledgementAsync(string endpoint, byte[] payload)
    {
        await using var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await pipe.WriteAsync(header);
        await pipe.WriteAsync(payload);
        await pipe.FlushAsync();
    }

    [SupportedOSPlatform("windows")]
    private static async Task SendTruncatedEnvelopeAsync(string endpoint)
    {
        await using var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 10);
        await pipe.WriteAsync(header);
        await pipe.WriteAsync(new byte[] { 1 });
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private static string GetSingleInstanceHelperPath()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return Path.Combine(source, "NeoAstra.SingleInstanceHelper", "bin", configuration, "net10.0", "NeoAstra.SingleInstanceHelper.dll");
    }

    private static Process StartRaceHelper(string helper, string applicationId, string gate, string ready, int holdMilliseconds)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(helper);
        start.ArgumentList.Add("--race");
        start.ArgumentList.Add(applicationId);
        start.ArgumentList.Add(gate);
        start.ArgumentList.Add(ready);
        start.ArgumentList.Add(holdMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start the single-instance race fixture.");
    }

    private static Process StartSecondaryHelper(string helper, string applicationId, string file, bool retry)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(helper);
        start.ArgumentList.Add(applicationId);
        start.ArgumentList.Add(file);
        if (retry) start.ArgumentList.Add("retry");
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start the single-instance secondary fixture.");
    }

    [System.Runtime.InteropServices.DllImport("user32", EntryPoint = "SendMessageW")]
    [SupportedOSPlatform("windows")]
    private static extern nint SendMessageW(nint window, uint message, nuint wparam, nint lparam);

    [SupportedOSPlatform("windows")]
    private static Task<T> RunStaAsync<T>(Func<T> callback)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(callback()); }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
