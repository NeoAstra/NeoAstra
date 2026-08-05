using System.Text.Json.Serialization;
using NeoAstra;
using NeoAstra.Rpc;

[assembly: NeoRpcJsonContext(typeof(AdvancedJsonContext))]

internal static class Program
{
    [STAThread]
    internal static int Main(string[] args)
    {
        if (args is ["--validate-advanced"])
        {
            return AdvancedValidation.Run();
        }

        var advanced = new AdvancedApplication(new TourEventHub(), new TourState());
        try
        {
            return NeoApplication.Run(
                new NeoApplicationOptions
                {
                    ApplicationName = AdvancedApplication.DisplayName,
                    ShutdownMode = NeoApplicationShutdownMode.OnMainWindowClosed,
                },
                application => advanced.StartAsync(application, CancellationToken.None));
        }
        finally
        {
            advanced.StopAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

[JsonSerializable(typeof(TourActivity))]
[JsonSerializable(typeof(TourDelayRequest))]
[JsonSerializable(typeof(TourDelayResponse))]
[JsonSerializable(typeof(TourDirtyRequest))]
[JsonSerializable(typeof(TourEmptyRequest))]
[JsonSerializable(typeof(TourHelloRequest))]
[JsonSerializable(typeof(TourHelloResponse))]
[JsonSerializable(typeof(TourNativeMenuRequest))]
[JsonSerializable(typeof(TourNativeMenuResponse))]
[JsonSerializable(typeof(TourStateResponse))]
[JsonSerializable(typeof(TourStreamItem))]
[JsonSerializable(typeof(TourStreamRequest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class AdvancedJsonContext : JsonSerializerContext;
