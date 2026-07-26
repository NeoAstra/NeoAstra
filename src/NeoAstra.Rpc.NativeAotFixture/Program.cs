// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.Json;
using System.Text.Json.Serialization;
using NeoAstra.Rpc;

[assembly: NeoRpcJsonContext(typeof(FixtureJsonContext))]

var frames = new List<string>();
var secondFrames = new List<string>();
var builder = new NeoRpcBuilder(new NeoRpcOptions { ContractHash = NeoRpcGeneratedContract.Hash });
builder.AddDocumentsService(new DocumentsService());
_ = builder.AddDocumentEventsChangedEvent();
await using var host = builder.Build();
await using var session = host.OpenSession(new NeoRpcSessionIdentity("fixture", "native-aot-document"), (json, _) =>
{
    frames.Add(json);
    return ValueTask.CompletedTask;
});
await using var secondSession = host.OpenSession(new NeoRpcSessionIdentity("fixture-secondary", "native-aot-document-2"), (json, _) =>
{
    secondFrames.Add(json);
    return ValueTask.CompletedTask;
});
await session.ReceiveAsync("{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"fixture-1\",\"command\":\"documents.open\",\"contract\":\"" + NeoRpcGeneratedContract.Hash + "\",\"args\":{\"id\":\"readme\"}}");
await secondSession.ReceiveAsync("{\"neoastra\":1,\"kind\":\"invoke\",\"id\":\"fixture-2\",\"command\":\"documents.open\",\"contract\":\"" + NeoRpcGeneratedContract.Hash + "\",\"args\":{\"id\":\"secondary\"}}");
if (frames.Count != 1 || secondFrames.Count != 1)
{
    Console.Error.WriteLine("The NativeAOT RPC fixture did not produce exactly one terminal result.");
    return 2;
}
using var result = JsonDocument.Parse(frames[0]);
using var secondResult = JsonDocument.Parse(secondFrames[0]);
if (!result.RootElement.GetProperty("ok").GetBoolean() || result.RootElement.GetProperty("value").GetProperty("title").GetString() != "README" ||
    secondResult.RootElement.GetProperty("value").GetProperty("viewLabel").GetString() != "fixture-secondary")
{
    Console.Error.WriteLine("The NativeAOT RPC fixture result did not match its generated contract.");
    return 3;
}
Console.WriteLine($"NeoAstra RPC NativeAOT fixture passed ({NeoRpcGeneratedContract.Hash}).");
return 0;

[NeoRpcService("documents", Version = 1)]
public sealed class DocumentsService
{
    [NeoRpcMethod("open", Permission = "documents:open")]
    public ValueTask<OpenDocumentResponse> OpenAsync(OpenDocumentRequest request, NeoRpcContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new OpenDocumentResponse(request.Id.ToUpperInvariant(), context.ViewLabel));
    }
}

public sealed record OpenDocumentRequest(string Id);
public sealed record OpenDocumentResponse(string Title, string ViewLabel);

public sealed class DocumentEvents
{
    [NeoRpcEvent("documents.changed", Permission = "documents:read", OverflowBehavior = NeoRpcOverflowBehavior.Coalesce)]
    public OpenDocumentResponse Changed { get; } = new(string.Empty, string.Empty);
}

[JsonSerializable(typeof(OpenDocumentRequest))]
[JsonSerializable(typeof(OpenDocumentResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class FixtureJsonContext : JsonSerializerContext;
