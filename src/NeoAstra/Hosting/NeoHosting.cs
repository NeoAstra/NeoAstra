// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NeoAstra.Rpc;

namespace NeoAstra.Hosting;

/// <summary>Runs application-specific startup after the native loop is dispatchable and before NeoAstra becomes ready.</summary>
public interface INeoHostedApplication
{
    /// <summary>Initializes windows, views, and application services on the UI dispatcher before application readiness.</summary>
    /// <param name="application">The owned native application.</param>
    /// <param name="cancellationToken">Cancels host startup.</param>
    /// <returns>A task representing startup.</returns>
    /// <remarks>The injectable <see cref="INeoUiDispatcher"/> is available during this callback. Observe cancellation and preserve the UI context when accessing native objects after an await.</remarks>
    ValueTask StartAsync(NeoApplication application, CancellationToken cancellationToken);
}

/// <summary>Configures optional Generic Host integration.</summary>
public sealed class NeoHostingOptions
{
    /// <summary>Gets mutable native application options bound before native startup.</summary>
    public NeoApplicationOptions Application { get; } = new();

    /// <summary>Gets mutable coordinated quit options.</summary>
    public NeoQuitOptions Quit { get; } = new();

    /// <summary>Gets or sets the process exit code requested when the host stops.</summary>
    public int QuitExitCode { get; set; }

    internal void Bind(IConfiguration configuration)
    {
        var section = configuration.GetSection("NeoAstra");
        Application.ApplicationName = section["ApplicationName"] ?? Application.ApplicationName;
        if (uint.TryParse(section["MaximumPendingDispatches"], out var dispatches)) Application.MaximumPendingDispatches = dispatches;
        if (int.TryParse(section["MaximumPendingLaunchEvents"], out var launches)) Application.MaximumPendingLaunchEvents = launches;
        if (bool.TryParse(section["QueueInitialLaunchEvent"], out var initial)) Application.QueueInitialLaunchEvent = initial;
        if (Enum.TryParse<NeoApplicationShutdownMode>(section["ShutdownMode"], true, out var shutdown)) Application.ShutdownMode = shutdown;
        if (TimeSpan.TryParse(section["QuitTimeout"], System.Globalization.CultureInfo.InvariantCulture, out var quitTimeout)) Quit.Timeout = quitTimeout;
        if (bool.TryParse(section["PreflightAllWindows"], out var preflight)) Quit.PreflightWindows = preflight;
        if (int.TryParse(section["DefaultExitCode"], out var exitCode)) QuitExitCode = exitCode;
    }
}

/// <summary>Injectable native-handle-free UI dispatcher abstraction.</summary>
/// <remarks>Dispatch becomes available before hosted application startup completes; it does not imply that the application is ready.</remarks>
public interface INeoUiDispatcher
{
    /// <summary>Invokes work on the NeoAstra UI thread.</summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="callback">UI callback.</param>
    /// <param name="cancellationToken">Cancels queued work.</param>
    /// <returns>The callback result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="callback"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled or the native loop exits before dispatch becomes available.</exception>
    /// <exception cref="NeoAstraNativeLibraryException">The native host cannot be initialized.</exception>
    /// <exception cref="ObjectDisposedException">Native application shutdown has started.</exception>
    ValueTask<T> InvokeAsync<T>(Func<T> callback, CancellationToken cancellationToken = default);
}

/// <summary>Creates explicit per-view dependency-injection scopes.</summary>
public sealed class NeoViewScopeFactory
{
    private readonly IServiceScopeFactory _scopes;
    /// <summary>Creates a factory over the host scope provider.</summary>
    /// <param name="scopes">The registered scope provider.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scopes"/> is <see langword="null"/>.</exception>
    public NeoViewScopeFactory(IServiceScopeFactory scopes) { ArgumentNullException.ThrowIfNull(scopes); _scopes = scopes; }
    /// <summary>Creates a scope disposed when its view binding is disposed.</summary>
    public AsyncServiceScope CreateScope() => _scopes.CreateAsyncScope();
}

/// <summary>Creates explicit per-document-session dependency-injection scopes.</summary>
public sealed class NeoDocumentSessionScopeFactory
{
    private readonly IServiceScopeFactory _scopes;
    /// <summary>Creates a factory over the host scope provider.</summary>
    /// <param name="scopes">The registered scope provider.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scopes"/> is <see langword="null"/>.</exception>
    public NeoDocumentSessionScopeFactory(IServiceScopeFactory scopes) { ArgumentNullException.ThrowIfNull(scopes); _scopes = scopes; }
    /// <summary>Creates a scope disposed when navigation replaces the document session.</summary>
    public AsyncServiceScope CreateScope() => _scopes.CreateAsyncScope();
}

/// <summary>Creates explicit per-RPC-invocation dependency-injection scopes.</summary>
public sealed class NeoInvocationScopeFactory
{
    private readonly IServiceScopeFactory _scopes;
    /// <summary>Creates a factory over the host scope provider.</summary>
    /// <param name="scopes">The registered scope provider.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scopes"/> is <see langword="null"/>.</exception>
    public NeoInvocationScopeFactory(IServiceScopeFactory scopes) { ArgumentNullException.ThrowIfNull(scopes); _scopes = scopes; }
    /// <summary>Creates a scope disposed after one invocation completes.</summary>
    public AsyncServiceScope CreateScope() => _scopes.CreateAsyncScope();
}

/// <summary>Static, NativeAOT-safe Generic Host registrations.</summary>
public static class NeoHostingExtensions
{
    /// <summary>Registers NeoAstra configuration, logging, dispatcher, scopes, and host lifetime coordination.</summary>
    /// <param name="builder">The Generic Host application builder.</param>
    /// <param name="configure">Optional static options callback applied after configuration binding.</param>
    /// <returns><paramref name="builder"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static IHostApplicationBuilder UseNeoAstra(this IHostApplicationBuilder builder, Action<NeoHostingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new NeoHostingOptions();
        options.Bind(builder.Configuration);
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<NeoHostedService>();
        builder.Services.AddSingleton<IHostedService>(static services => services.GetRequiredService<NeoHostedService>());
        builder.Services.AddSingleton<INeoUiDispatcher>(static services => services.GetRequiredService<NeoHostedService>());
        builder.Services.AddSingleton<NeoViewScopeFactory>();
        builder.Services.AddSingleton<NeoDocumentSessionScopeFactory>();
        builder.Services.AddSingleton<NeoInvocationScopeFactory>();
        return builder;
    }

    /// <summary>Registers the reflection-free RPC builder and one application-scoped RPC host.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional static registry callback.</param>
    /// <returns><paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddNeoAstraRpc(this IServiceCollection services, Action<NeoRpcBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(static _ => new NeoRpcBuilder());
        if (configure is not null) services.AddSingleton(new RpcConfiguration(configure));
        services.AddSingleton(static provider =>
        {
            var builder = provider.GetRequiredService<NeoRpcBuilder>();
            provider.GetService<RpcConfiguration>()?.Configure(builder);
            return builder.Build();
        });
        return services;
    }

    /// <summary>Registers an application singleton using a compile-time constructor rather than runtime activation.</summary>
    /// <typeparam name="TApplication">Static hosted application type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddNeoAstraApplication<TApplication>(this IServiceCollection services)
        where TApplication : class, INeoHostedApplication, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(static _ => new TApplication());
        services.AddSingleton<INeoHostedApplication>(static provider => provider.GetRequiredService<TApplication>());
        return services;
    }

    /// <summary>Registers an application singleton using an explicit NativeAOT-safe factory.</summary>
    /// <typeparam name="TApplication">Static hosted application type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="factory">A compile-time factory that may resolve registered dependencies.</param>
    /// <returns><paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddNeoAstraApplication<TApplication>(this IServiceCollection services,
        Func<IServiceProvider, TApplication> factory) where TApplication : class, INeoHostedApplication
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);
        services.AddSingleton(factory);
        services.AddSingleton<INeoHostedApplication>(static provider => provider.GetRequiredService<TApplication>());
        return services;
    }

    private sealed class RpcConfiguration(Action<NeoRpcBuilder> configure)
    {
        internal void Configure(NeoRpcBuilder builder) => configure(builder);
    }
}

internal sealed class NeoHostedService : IHostedService, INeoUiDispatcher
{
    private readonly NeoHostingOptions _options;
    private readonly INeoHostedApplication _startup;
    private readonly IHostApplicationLifetime _hostLifetime;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TaskCompletionSource<NeoApplication> _dispatchable = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<NeoApplication> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _hostStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _uiThread;
    private NeoApplication? _application;
    private CancellationToken _startupCancellation;
    private int _hostStopRequested;

    public NeoHostedService(NeoHostingOptions options, INeoHostedApplication startup,
        IHostApplicationLifetime hostLifetime, ILoggerFactory loggerFactory)
    {
        _options = options;
        _startup = startup;
        _hostLifetime = hostLifetime;
        _loggerFactory = loggerFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var configuredLog = _options.Application.LogCallback;
        _options.Application.LogCallback = message =>
        {
            try { configuredLog?.Invoke(message); }
            catch { /* User diagnostics cannot unwind through a native callback. */ }
            Log(message);
        };
        _startupCancellation = cancellationToken;
        _hostLifetime.ApplicationStopping.Register(static state => ((NeoHostedService)state!).RequestQuitFromHost(), this);
        _hostLifetime.ApplicationStopped.Register(static state => ((NeoHostedService)state!).OnHostStopped(), this);
        _uiThread = new Thread(Run) { IsBackground = false, Name = "NeoAstra UI" };
        if (OperatingSystem.IsWindows()) _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        try { await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (Volatile.Read(ref _hostStopRequested) != 0)
        {
            // Startup failure enters native stopping first, which cancels the host token before
            // the UI thread publishes the original exception. Preserve that original failure.
            await _ready.Task.WaitAsync(_options.Quit.Timeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RequestQuitFromHost();
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        RequestQuitFromHost();
        return Task.CompletedTask;
    }

    public async ValueTask<T> InvokeAsync<T>(Func<T> callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var application = await _dispatchable.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await application.Dispatcher.InvokeAsync(callback, cancellationToken).ConfigureAwait(false);
    }

    private void Run()
    {
        try
        {
            NeoApplication.Run(_options.Application, async application =>
            {
                application.Stopping += OnNeoAstraStopping;
                application.StoppingAsync += WaitForHostServicesAsync;
                Volatile.Write(ref _application, application);
                _dispatchable.TrySetResult(application);
                // A host stop may have been recorded before the native application existed.
                if (_hostStopped.Task.IsCompleted)
                {
                    application.ForceShutdown(_options.QuitExitCode);
                    return;
                }
                if (Volatile.Read(ref _hostStopRequested) != 0)
                    _ = application.RequestQuitAsync(NeoQuitReason.HostStopping, _options.QuitExitCode, _options.Quit);
                await _startup.StartAsync(application, _startupCancellation).ConfigureAwait(true);
                _startupCancellation.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _hostStopRequested) != 0)
                {
                    _ready.TrySetCanceled();
                    return;
                }
                application.NotifyReady();
                _ready.TrySetResult(application);
            });
            // The native loop can exit during startup without the callback reaching readiness.
            _dispatchable.TrySetCanceled();
            _ready.TrySetCanceled();
            _exited.TrySetResult();
        }
        catch (Exception exception)
        {
            _dispatchable.TrySetException(exception);
            if (!_ready.TrySetException(exception) && _ready.Task.IsCompletedSuccessfully)
            {
                try
                {
                    _loggerFactory.CreateLogger<NeoHostedService>().LogError(exception,
                        "The native application failed after hosted startup completed.");
                }
                catch { /* Logging providers must not unwind through the native thread. */ }
            }
            _exited.TrySetException(exception);
        }
    }

    private void RequestQuitFromHost()
    {
        if (Interlocked.Exchange(ref _hostStopRequested, 1) != 0) return;
        var application = Volatile.Read(ref _application);
        if (application is not null) _ = application.RequestQuitAsync(NeoQuitReason.HostStopping, _options.QuitExitCode, _options.Quit);
    }

    private void OnNeoAstraStopping(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref _hostStopRequested, 1) == 0) _hostLifetime.StopApplication();
    }

    private async ValueTask WaitForHostServicesAsync(CancellationToken cancellationToken)
    {
        await _hostStopped.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnHostStopped()
    {
        _hostStopped.TrySetResult();
        var application = Volatile.Read(ref _application);
        if (application is not null && application.State is not (NeoApplicationState.Stopping or NeoApplicationState.Stopped))
            application.ForceShutdown(_options.QuitExitCode);
        if (application?.Dispatcher.CheckAccess() == true) return;
        var wait = _options.Quit.Timeout + TimeSpan.FromSeconds(5);
        try
        {
            if (!_exited.Task.Wait(wait))
            {
                application?.ForceShutdown(_options.QuitExitCode);
                _ = _exited.Task.Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch (AggregateException) when (_exited.Task.IsFaulted)
        {
            // Exit is complete. Startup failures are already reported through the readiness task,
            // and must not be rethrown through the host's ApplicationStopped callback.
        }
    }

    private void Log(NeoLogMessage message)
    {
        try
        {
            var logger = _loggerFactory.CreateLogger(message.Category);
            logger.Log(Map(message.Level), new EventId(unchecked((int)message.ObjectId), "NeoAstraNative"), message,
                null, static (value, _) => $"{value.Message} [native={value.NativeCode}; object={value.ObjectId}]");
        }
        catch { /* Logging providers cannot unwind through the native callback. */ }
    }

    private static LogLevel Map(NeoLogLevel level) => level switch
    {
        NeoLogLevel.Trace => LogLevel.Trace,
        NeoLogLevel.Debug => LogLevel.Debug,
        NeoLogLevel.Information => LogLevel.Information,
        NeoLogLevel.Warning => LogLevel.Warning,
        NeoLogLevel.Error => LogLevel.Error,
        NeoLogLevel.Critical => LogLevel.Critical,
        _ => LogLevel.None,
    };
}
