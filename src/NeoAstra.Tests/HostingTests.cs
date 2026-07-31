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
    public async Task NeoAstraQuitStopsHostExactlyOnceAndJoinsNativeExit()
    {
        if (!OperatingSystem.IsWindows()) return;
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
        catch (NeoAstraNativeLibraryException)
        {
            // Managed CI does not stage native assets; native-enabled Windows validation executes this path.
        }
    }

    [TestMethod]
    public async Task ExternalHostStopFinishesServicesOnBothSidesBeforeNativeExit()
    {
        if (!OperatingSystem.IsWindows()) return;
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
        catch (NeoAstraNativeLibraryException) { }
    }

    [TestMethod]
    public async Task HostedServiceStopExceptionDoesNotDeadlockNativeTeardown()
    {
        if (!OperatingSystem.IsWindows()) return;
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
        catch (NeoAstraNativeLibraryException) { }
    }

    [TestMethod]
    public async Task HostedApplicationStartupExceptionDoesNotLeakNativeLoop()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.UseNeoAstra(options => options.Application.QueueInitialLaunchEvent = false);
            builder.Services.AddSingleton<INeoHostedApplication>(new ThrowingStartup());
            using var host = builder.Build();
            var exception = await Assert.ThrowsAsync<Exception>(async () => await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            if (exception is NeoAstraNativeLibraryException) return;
            Assert.IsInstanceOfType<InvalidOperationException>(exception);
        }
        catch (NeoAstraNativeLibraryException) { }
    }

    [TestMethod]
    public async Task CanceledHostStopDoesNotDeadlockNativeTeardown()
    {
        if (!OperatingSystem.IsWindows()) return;
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
        catch (NeoAstraNativeLibraryException) { }
    }

    private static async Task VerifyAsync(AsyncServiceScope scope, TrackingScopeFactory source)
    {
        var before = source.Disposed;
        await scope.DisposeAsync();
        await scope.DisposeAsync();
        Assert.AreEqual(before + 1, source.Disposed);
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
