using System.Text.Json.Serialization;
using NeoAstra;
using NeoAstra.Rpc;

[assembly: NeoRpcJsonContext(typeof(ReferenceJsonContext))]

internal static class Program
{
    [STAThread]
    internal static int Main(string[] args)
    {
        if (args is ["--validate-reference"])
        {
            return ReferenceValidation.Run();
        }

        var reference = new ReferenceApplication(new TourEventHub(), new TourState());
        try
        {
            return NeoApplication.Run(
                new NeoApplicationOptions
                {
                    ApplicationName = ReferenceApplication.DisplayName,
                    ShutdownMode = NeoApplicationShutdownMode.OnMainWindowClosed,
                },
                application => reference.StartAsync(application, CancellationToken.None));
        }
        finally
        {
            reference.StopAsync().AsTask().GetAwaiter().GetResult();
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
[JsonSerializable(typeof(TourStateResponse))]
[JsonSerializable(typeof(TourStreamItem))]
[JsonSerializable(typeof(TourStreamRequest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ReferenceJsonContext : JsonSerializerContext;
