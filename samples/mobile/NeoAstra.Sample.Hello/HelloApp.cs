using System.Text.Json.Serialization;
using NeoAstra;
using NeoAstra.Rpc;

[assembly: NeoRpcJsonContext(typeof(NeoAstra.Samples.Mobile.Hello.AppJsonContext))]

namespace NeoAstra.Samples.Mobile.Hello;

/// <summary>The application, the same on iOS and Android: one page that calls one generated RPC service.</summary>
internal static class HelloApp
{
    public static void Configure(NeoAppBuilder app)
    {
        app.Title = "NeoAstra Hello";
        app.UseRpc(rpc => rpc.AddGreetingService(new GreetingService()));
    }
}

[NeoRpcService("greeting")]
internal sealed class GreetingService
{
    [NeoRpcMethod("hello")]
    public ValueTask<GreetingResponse> HelloAsync(GreetingRequest request) =>
        ValueTask.FromResult(new GreetingResponse($"Hello, {request.Name}!"));
}

internal sealed record GreetingRequest(string Name);
internal sealed record GreetingResponse(string Message);

[JsonSerializable(typeof(GreetingRequest))]
[JsonSerializable(typeof(GreetingResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class AppJsonContext : JsonSerializerContext;
