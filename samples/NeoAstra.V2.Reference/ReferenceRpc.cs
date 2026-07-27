using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;
using NeoAstra.Rpc;

internal sealed class TourState
{
    private Action? _showPreview;
    private int _hasUnsavedChanges;

    internal bool HasUnsavedChanges => Volatile.Read(ref _hasUnsavedChanges) != 0;

    internal void ConfigurePreview(Action showPreview)
    {
        ArgumentNullException.ThrowIfNull(showPreview);
        Volatile.Write(ref _showPreview, showPreview);
    }

    internal void SetUnsavedChanges(bool value) =>
        Interlocked.Exchange(ref _hasUnsavedChanges, value ? 1 : 0);

    internal void ShowPreview() =>
        Volatile.Read(ref _showPreview)?.Invoke();
}

internal sealed class TourEventHub
{
    private NeoRpcEvent<TourActivity>? _publisher;

    internal void Attach(NeoRpcEvent<TourActivity> publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        if (Interlocked.CompareExchange(ref _publisher, publisher, null) is not null)
        {
            throw new InvalidOperationException("The tour event publisher is already attached.");
        }
    }

    internal async ValueTask PublishAsync(
        string source,
        string message,
        CancellationToken cancellationToken = default)
    {
        var publisher = Volatile.Read(ref _publisher);
        if (publisher is null)
        {
            return;
        }

        var activity = new TourActivity(
            source,
            message,
            DateTimeOffset.UtcNow.ToString("O"));
        _ = await publisher.PublishAsync(activity, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class TourPulseService(TourEventHub events) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await events.PublishAsync(
                "hosted-service",
                "The .NET background service is healthy.",
                stoppingToken).ConfigureAwait(false);
        }
    }
}

internal sealed class ReferenceLifetimeService(ReferenceApplication application) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken) =>
        await application.StopAsync();
}

[NeoRpcService("tour", Version = 1)]
internal sealed class TourService(TourState state)
{
    [NeoRpcMethod("hello", Permission = "tour:read")]
    public ValueTask<TourHelloResponse> HelloAsync(
        TourHelloRequest request,
        NeoRpcContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = string.IsNullOrWhiteSpace(request.Name) ? "NeoAstra developer" : request.Name.Trim();
        return ValueTask.FromResult(new TourHelloResponse(
            $"Hello, {name}! This response came from C#.",
            context.ViewLabel));
    }

    [NeoRpcMethod("delay", Permission = "tour:control", TimeoutMilliseconds = 15_000)]
    public async ValueTask<TourDelayResponse> DelayAsync(
        TourDelayRequest request,
        CancellationToken cancellationToken)
    {
        var milliseconds = Math.Clamp(request.Milliseconds, 250, 10_000);
        await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);
        return new TourDelayResponse(milliseconds, "The cancelable C# operation completed.");
    }

    [NeoRpcMethod("stream", Permission = "tour:control")]
    public NeoRpcChannel<TourStreamItem> Stream(
        TourStreamRequest request,
        CancellationToken cancellationToken)
    {
        var count = Math.Clamp(request.Count, 1, 12);
        return new NeoRpcChannel<TourStreamItem>(
            StreamItemsAsync(count, cancellationToken),
            ReferenceJsonContext.Default.TourStreamItem);
    }

    [NeoRpcMethod("setDirty", Permission = "tour:control")]
    public TourStateResponse SetDirty(TourDirtyRequest request)
    {
        state.SetUnsavedChanges(request.Value);
        return new TourStateResponse(request.Value);
    }

    [NeoRpcMethod("showPreview", Permission = "tour:control", Dispatch = NeoRpcDispatchMode.UiThread)]
    public TourStateResponse ShowPreview(TourEmptyRequest request)
    {
        state.ShowPreview();
        return new TourStateResponse(state.HasUnsavedChanges);
    }

    private static async IAsyncEnumerable<TourStreamItem> StreamItemsAsync(
        int count,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var index = 1; index <= count; index++)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            yield return new TourStreamItem(index, $"Ordered item {index} of {count}");
        }
    }
}

public sealed class TourEvents
{
    [NeoRpcEvent(
        "tour.activity",
        Permission = "tour:events",
        OverflowBehavior = NeoRpcOverflowBehavior.DropOldest)]
    public TourActivity Activity { get; } = new(string.Empty, string.Empty, string.Empty);
}

public sealed record TourActivity(string Source, string Message, string Timestamp);
public sealed record TourDelayRequest(int Milliseconds);
public sealed record TourDelayResponse(int Milliseconds, string Message);
public sealed record TourDirtyRequest(bool Value);
public sealed record TourEmptyRequest;
public sealed record TourHelloRequest(string Name);
public sealed record TourHelloResponse(string Message, string ViewLabel);
public sealed record TourStateResponse(bool HasUnsavedChanges);
public sealed record TourStreamItem(int Index, string Message);
public sealed record TourStreamRequest(int Count);
