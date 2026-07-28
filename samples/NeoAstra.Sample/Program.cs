using System.Threading.Tasks;
using System.Text.Json.Serialization;
using NeoAstra;
using NeoAstra.Rpc;

[assembly: NeoRpcJsonContext(typeof(AppJsonContext))]

return NeoApp.Run(args, app =>
{
    app.Title = "NeoAstra HelloWorld";
    app.UseRpc(rpc => rpc.AddGreetingService(new GreetingService()));
    app.GrantMainView("greeting:read");
});

[NeoRpcService("greeting")]
internal sealed class GreetingService
{
    [NeoRpcMethod("hello", Permission = "greeting:read")]
    public ValueTask<GreetingResponse> HelloAsync(GreetingRequest request) =>
        ValueTask.FromResult(new GreetingResponse($"Hello, {request.Name}!"));
}

internal sealed record GreetingRequest(string Name);
internal sealed record GreetingResponse(string Message);

[JsonSerializable(typeof(GreetingRequest))]
[JsonSerializable(typeof(GreetingResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class AppJsonContext : JsonSerializerContext;
