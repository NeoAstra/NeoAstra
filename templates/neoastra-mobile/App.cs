using System.Text.Json.Serialization;
using NeoAstra;
using NeoAstra.Rpc;
#if DEBUG
using NeoAstra.Mcp;
#endif

[assembly: NeoRpcJsonContext(typeof(NeoAstraApp.AppJsonContext))]

namespace NeoAstraApp;

/// <summary>The application, the same on iOS and Android: its page calls the generated RPC services of .NET.</summary>
internal static class App
{
    public static void Configure(NeoAppBuilder app)
    {
        app.Title = "NeoAstra App";
        app.UseRpc(rpc => rpc.AddGreetingService(new GreetingService()));
#if DEBUG
        // In a `dotnet neoastra dev` session, an agent reaches the page through `dotnet neoastra mcp`; the plugin does nothing
        // outside a session. Add further plugins in this same call.
        app.UsePlugins((_, plugins) => plugins.AddNeoAstraMcp());
#endif
    }
}

[NeoRpcService("greeting", Version = 1)]
public sealed class GreetingService
{
    [NeoRpcMethod("hello")]
    public ValueTask<GreetingResponse> HelloAsync(GreetingRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new GreetingResponse($"Hello, {request.Name}!"));
    }
}

public sealed record GreetingRequest(string Name);
public sealed record GreetingResponse(string Message);

[JsonSerializable(typeof(GreetingRequest))]
[JsonSerializable(typeof(GreetingResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class AppJsonContext : JsonSerializerContext;
