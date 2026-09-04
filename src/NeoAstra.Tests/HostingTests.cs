// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NeoAstra.Hosting;

namespace NeoAstra.Tests;

[TestClass]
public sealed class HostingTests
{
    [TestMethod]
    public async Task ExplicitBoundaryScopesDisposeExactlyOnce()
    {
        var source = new TrackingScopeFactory();
        await VerifyAsync(new NeoViewScopeFactory(source).CreateScope(), source);
        await VerifyAsync(new NeoDocumentSessionScopeFactory(source).CreateScope(), source);
        await VerifyAsync(new NeoInvocationScopeFactory(source).CreateScope(), source);
        Assert.AreEqual(3, source.Created);
        Assert.AreEqual(3, source.Disposed);
    }

    [TestMethod]
    public async Task HostedStartupCanAwaitInjectedDispatcherBeforeBecomingReady()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Native hosting is currently qualified by these tests only on Windows.");
        using var cancellation = new CancellationTokenSource();
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NeoApplication? application = null;
        var builder = Host.CreateApplicationBuilder();
        builder.UseNeoAstra(options =>
        {
            options.Application.QueueInitialLaunchEvent = false;
            options.Application.ShutdownMode = NeoApplicationShutdownMode.Explicit;
            options.Quit.Timeout = TimeSpan.FromSeconds(1);
        });
        builder.Services.AddNeoAstraApplication(services => new CallbackStartup(async (app, token) =>
        {
            application = app;
            await Task.Yield();
            var state = await services.GetRequiredService<INeoUiDispatcher>().InvokeAsync(() =>
            {
                Assert.IsTrue(app.Dispatcher.CheckAccess());
                return app.State;
            }, token);
            Assert.AreEqual(NeoApplicationState.Starting, state);
            dispatched.TrySetResult();
            await release.Task.WaitAsync(token);
        }));
        using var host = builder.Build();
        var start = host.StartAsync(cancellation.Token);
        try
        {
            // Observe native-load/startup failures instead of mistaking them for dispatcher timeouts.
            var first = await Task.WhenAny(start, dispatched.Task).WaitAsync(TimeSpan.FromSeconds(5));
            if (first == start) await start;
            await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(start.IsCompleted, "Dispatchability must not publish host readiness.");
            Assert.IsNotNull(application);
            Assert.AreEqual(NeoApplicationState.Starting, application.State);
            release.TrySetResult();
            await start.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(NeoApplicationState.Ready, application.State);
        }
        catch (NeoAstraNativeLibraryException exception)
        {
            Assert.Inconclusive($"Native hosting assets are unavailable: {exception.Message}");
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            application?.ForceShutdown();
            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            try { await start.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            catch (NeoAstraNativeLibraryException) { }
        }
    }

    [TestMethod]
    public async Task NativeCreationFailureCompletesPendingDispatcherAndStartup()
    {
        var options = new NeoHostingOptions();
        options.Application.MaximumPendingDispatches = 0; // Rejected before native loading on every OS.
        options.Quit.Timeout = TimeSpan.FromSeconds(1);
        var lifetime = new TrackingHostLifetime();
        var service = new NeoHostedService(options, new TrackingStartup(), lifetime, NullLoggerFactory.Instance);
        var dispatch = service.InvokeAsync(() => true).AsTask();
        var start = service.StartAsync(CancellationToken.None);
        try
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => start.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => dispatch.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await FinishHostedServiceTestAsync(service, lifetime, null, start);
        }
    }

    [TestMethod]
    public async Task HostStopRecordedBeforeStartupIsReplayedAndRetainsStopOrdering()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Native hosting is currently qualified by these tests only on Windows.");
        var options = new NeoHostingOptions();
        options.Application.QueueInitialLaunchEvent = false;
        options.Application.ShutdownMode = NeoApplicationShutdownMode.Explicit;
        options.Quit.Timeout = TimeSpan.FromSeconds(2);
        var lifetime = new TrackingHostLifetime();
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NeoApplication? application = null;
        var startup = new CallbackStartup((app, _) =>
        {
            application = app;
            app.Stopping += (_, _) => stopping.TrySetResult();
            app.Stopped += (_, _) => stopped.TrySetResult();
            return ValueTask.CompletedTask;
        });
        var service = new NeoHostedService(options, startup, lifetime, NullLoggerFactory.Instance);
        lifetime.StopApplication(); // Registration observes this before the native application exists.
        var start = service.StartAsync(CancellationToken.None);
        try
        {
            var exception = await Assert.ThrowsAsync<Exception>(() => start.WaitAsync(TimeSpan.FromSeconds(5)));
            if (exception is NeoAstraNativeLibraryException native) Assert.Inconclusive($"Native hosting assets are unavailable: {native.Message}");
            Assert.IsInstanceOfType<OperationCanceledException>(exception);
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(stopped.Task.IsCompleted, "Native teardown must still wait for the host's stopped signal.");
            await Task.Run(lifetime.NotifyStopped).WaitAsync(TimeSpan.FromSeconds(15));
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await FinishHostedServiceTestAsync(service, lifetime, application, start);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HostedStartupFailureOrCancellationAfterDispatchNeverBecomesReady(bool cancel)
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Native hosting is currently qualified by these tests only on Windows.");
        using var cancellation = new CancellationTokenSource();
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NeoApplication? application = null;
        var becameReady = false;
        var lifetime = new TrackingHostLifetime();
        var options = new NeoHostingOptions();
        options.Application.QueueInitialLaunchEvent = false;
        options.Application.ShutdownMode = NeoApplicationShutdownMode.Explicit;
        options.Quit.Timeout = TimeSpan.FromSeconds(1);
        NeoHostedService? service = null;
        var startup = new CallbackStartup(async (app, token) =>
        {
            application = app;
            app.StateChanged += (_, args) => becameReady |= args.Current == NeoApplicationState.Ready;
            app.Stopped += (_, _) => stopped.TrySetResult();
            await Task.Yield();
            Assert.IsTrue(await service!.InvokeAsync(() => app.Dispatcher.CheckAccess(), token));
            dispatched.TrySetResult();
            try { await release.Task.WaitAsync(token); }
            catch (OperationCanceledException) when (cancel && token.IsCancellationRequested)
            {
                // Startup may finish its own cancellation cleanup successfully. The host must
                // still observe the canceled token rather than publish readiness afterward.
                return;
            }
            throw new InvalidOperationException("startup after dispatch failed");
        });
        service = new NeoHostedService(options, startup, lifetime, NullLoggerFactory.Instance);
        var start = service.StartAsync(cancellation.Token);
        try
        {
            var first = await Task.WhenAny(start, dispatched.Task).WaitAsync(TimeSpan.FromSeconds(5));
            if (first == start) await start;
            await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancel) cancellation.Cancel();
            else release.TrySetResult();
            var exception = await Assert.ThrowsAsync<Exception>(() => start.WaitAsync(TimeSpan.FromSeconds(5)));
            if (cancel) Assert.IsInstanceOfType<OperationCanceledException>(exception);
            else
            {
                Assert.IsInstanceOfType<InvalidOperationException>(exception);
                Assert.AreEqual("startup after dispatch failed", exception.Message);
            }
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(becameReady);
            // In particular, a faulted native exit must not escape this host-lifetime callback.
            await Task.Run(lifetime.NotifyStopped).WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (NeoAstraNativeLibraryException exception)
        {
            Assert.Inconclusive($"Native hosting assets are unavailable: {exception.Message}");
        }
        finally
        {
            cancellation.Cancel();
            await FinishHostedServiceTestAsync(service, lifetime, application, start);
        }
    }

    [TestMethod]
    public async Task NeoAstraQuitStopsHostExactlyOnceAndJoinsNativeExit()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Native hosting is currently qualified by these tests only on Windows.");
        try
        {
            var lifetime = new TrackingHostLifetime();
            var startup = new TrackingStartup();
            var service = new NeoHostedService(new NeoHostingOptions(), startup, lifetime, NullLoggerFactory.Instance);
            await service.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsNotNull(startup.Application);
            Assert.AreEqual(NeoApplicationState.Ready, await service.InvokeAsync(() => startup.Application!.State));
            var quit = startup.Application.RequestQuitAsync();
            await lifetime.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stop = service.StopAsync(CancellationToken.None);
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            lifetime.NotifyStopped();
            Assert.AreEqual(NeoQuitResult.Completed, await quit.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(1, lifetime.StopCalls);
        }
        catch (NeoAstraNativeLibraryException exception)
        {
            Assert.Inconclusive($"Native hosting assets are unavailable: {exception.Message}");
        }
    }

    [TestMethod]
    public async Task ExternalHostStopFinishesServicesOnBothSidesBeforeNativeExit()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Native hosting is currently qualified by these tests only on Windows.");
        try
        {
            var events = new List<string>();
            var before = new RecordingHostedService("before", events);
            var after = new RecordingHostedService("after", events);
            var startup = new TrackingStartup();
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSingleton<IHostedService>(before);
            builder.UseNeoAstra(options =>
            {
                options.Application.ApplicationName = "NeoAstra host ordering test";
                options.Application.QueueInitialLaunchEvent = false;
                options.Application.ShutdownMode = NeoApplicationShutdownMode.Explicit;
                options.Quit.Timeout = TimeSpan.FromSeconds(5);
            });
            builder.Services.AddSingleton<INeoHostedApplication>(startup);
            builder.Services.AddSingleton<IHostedService>(after);
            using var host = builder.Build();
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsNotNull(startup.Application);
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            startup.Application.Stopped += (_, _) =>
            {
                CollectionAssert.Contains(events, "before-stop-end");
                CollectionAssert.Contains(events, "after-stop-end");
                exited.TrySetResult();
            };

            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
            CollectionAssert.AreEqual(new[]
            {
                "before-start", "after-start", "after-stop-start", "after-stop-end", "before-stop-start", "before-stop-end",
            }, events);
        }
        catch (NeoAstraNativeLibraryException exception)
        {
            Assert.Inconclusive($"Native hosting assets are unavailable: {exception.Message}");
        }
    }

    [TestMethod]
    public async Task HostedServiceStopExceptionDoesNotDeadlockNativeTeardown()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Native hosting is currently qualified by these tests only on Windows.");
        try
        {
            var startup = new TrackingStartup();
            var builder = Host.CreateApplicationBuilder();
            builder.UseNeoAstra(options =>
            {
                options.Application.QueueInitialLaunchEvent = false;
                options.Application.ShutdownMode = NeoApplicationShutdownMode.Explicit;
                options.Quit.Timeout = TimeSpan.FromSeconds(2);
            });
            builder.Services.AddSingleton<INeoHostedApplication>(startup);
            builder.Services.AddSingleton<IHostedService>(new ThrowingStopHostedService());
            using var host = builder.Build();
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            startup.Application!.Stopped += (_, _) => exited.TrySetResult();

            await Assert.ThrowsAsync<Exception>(async () => await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (NeoAstraNativeLibraryException exception)
        {
            Assert.Inconclusive($"Native hosting assets are unavailable: {exception.Message}");
        }
    }

    [TestMethod]
    public async Task HostedApplicationStartupExceptionDoesNotLeakNativeLoop()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Native hosting is currently qualified by these tests only on Windows.");
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.UseNeoAstra(options => options.Application.QueueInitialLaunchEvent = false);
            builder.Services.AddSingleton<INeoHostedApplication>(new ThrowingStartup());
            using var host = builder.Build();
            var exception = await Assert.ThrowsAsync<Exception>(async () => await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            if (exception is NeoAstraNativeLibraryException native) Assert.Inconclusive($"Native hosting assets are unavailable: {native.Message}");
            Assert.IsInstanceOfType<InvalidOperationException>(exception);
        }
        catch (NeoAstraNativeLibraryException exception)
        {
            Assert.Inconclusive($"Native hosting assets are unavailable: {exception.Message}");
        }
    }

    [TestMethod]
    public async Task CanceledHostStopDoesNotDeadlockNativeTeardown()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Native hosting is currently qualified by these tests only on Windows.");
        try
        {
            var startup = new TrackingStartup();
            var builder = Host.CreateApplicationBuilder();
            builder.UseNeoAstra(options =>
            {
                options.Application.QueueInitialLaunchEvent = false;
                options.Quit.Timeout = TimeSpan.FromMilliseconds(250);
            });
            builder.Services.AddSingleton<INeoHostedApplication>(startup);
            builder.Services.AddSingleton<IHostedService>(new CancelingStopHostedService());
            using var host = builder.Build();
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            startup.Application!.Stopped += (_, _) => exited.TrySetResult();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            await Assert.ThrowsAsync<OperationCanceledException>(async () => await host.StopAsync(cancellation.Token));
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (NeoAstraNativeLibraryException exception)
        {
            Assert.Inconclusive($"Native hosting assets are unavailable: {exception.Message}");
        }
    }

    private static async Task VerifyAsync(AsyncServiceScope scope, TrackingScopeFactory source)
    {
        var before = source.Disposed;
        await scope.DisposeAsync();
        await scope.DisposeAsync();
        Assert.AreEqual(before + 1, source.Disposed);
    }

    private static async Task FinishHostedServiceTestAsync(NeoHostedService service, TrackingHostLifetime lifetime,
        NeoApplication? application, Task start)
    {
        // Even assertion failures must release the foreground native thread. This is test cleanup,
        // not the normal host stop path whose ordering is asserted by the tests above.
        application?.ForceShutdown();
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Run(lifetime.NotifyStopped).WaitAsync(TimeSpan.FromSeconds(15));
        try { await start.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) when (start.IsFaulted || start.IsCanceled) { /* The test observes startup failures. */ }
    }

    private sealed class TrackingScopeFactory : IServiceScopeFactory
    {
        internal int Created;
        internal int Disposed;

        public IServiceScope CreateScope()
        {
            Created++;
            return new Scope(this);
        }

        private sealed class Scope(TrackingScopeFactory owner) : IServiceScope, IAsyncDisposable
        {
            private int _disposed;
            public IServiceProvider ServiceProvider { get; } = new EmptyServices();
            public void Dispose() => DisposeCore();
            public ValueTask DisposeAsync() { DisposeCore(); return ValueTask.CompletedTask; }
            private void DisposeCore() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Disposed++; }
        }

        private sealed class EmptyServices : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }
    }

    private sealed class TrackingStartup : INeoHostedApplication
    {
        internal NeoApplication? Application;
        public ValueTask StartAsync(NeoApplication application, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Application = application;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CallbackStartup(Func<NeoApplication, CancellationToken, ValueTask> callback) : INeoHostedApplication
    {
        public ValueTask StartAsync(NeoApplication application, CancellationToken cancellationToken)
            => callback(application, cancellationToken);
    }

    private sealed class RecordingHostedService(string name, List<string> events) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            events.Add(name + "-start");
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            events.Add(name + "-stop-start");
            await Task.Yield();
            events.Add(name + "-stop-end");
        }
    }

    private sealed class ThrowingStopHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.FromException(new InvalidOperationException("stop failure"));
    }

    private sealed class ThrowingStartup : INeoHostedApplication
    {
        public ValueTask StartAsync(NeoApplication application, CancellationToken cancellationToken)
            => ValueTask.FromException(new InvalidOperationException("startup failure"));
    }

    private sealed class CancelingStopHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class TrackingHostLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();
        internal readonly TaskCompletionSource Stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int StopCalls;
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;
        public void StopApplication()
        {
            Interlocked.Increment(ref StopCalls);
            Stopping.TrySetResult();
            _stopping.Cancel();
        }

        internal void NotifyStopped() => _stopped.Cancel();
    }
}
