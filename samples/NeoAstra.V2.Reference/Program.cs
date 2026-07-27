using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NeoAstra;
using NeoAstra.Hosting;
using NeoAstra.Rpc;

[assembly: NeoRpcJsonContext(typeof(ReferenceJsonContext))]

internal static class Program
{
    [STAThread]
    internal static async Task<int> Main(string[] args)
    {
        if (args is ["--validate-reference"])
        {
            return ReferenceValidation.Run();
        }

        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.UseNeoAstra(options =>
        {
            options.Application.ApplicationName = ReferenceApplication.DisplayName;
            options.Application.ShutdownMode = NeoApplicationShutdownMode.OnMainWindowClosed;
        });

        builder.Services.AddSingleton<TourEventHub>();
        builder.Services.AddSingleton<TourState>();
        builder.Services.AddNeoAstraApplication<ReferenceApplication>(services => new(
            services.GetRequiredService<TourEventHub>(),
            services.GetRequiredService<TourState>(),
            services.GetRequiredService<ILogger<ReferenceApplication>>()));
        builder.Services.AddHostedService<TourPulseService>();
        builder.Services.AddHostedService<ReferenceLifetimeService>();

        using var host = builder.Build();
        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }
}

[JsonSerializable(typeof(TourActivity))]
[JsonSerializable(typeof(TourDelayRequest))]
[JsonSerializable(typeof(TourDelayResponse))]
[JsonSerializable(typeof(TourDirtyRequest))]
[JsonSerializable(typeof(TourEmptyRequest))]
[JsonSerializable(typeof(TourHelloRequest))]
[JsonSerializable(typeof(TourHelloResponse))]
[JsonSerializable(typeof(TourStateResponse))]
[JsonSerializable(typeof(TourStreamItem))]
[JsonSerializable(typeof(TourStreamRequest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ReferenceJsonContext : JsonSerializerContext;
