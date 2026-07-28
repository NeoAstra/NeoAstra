// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using NeoAstra;
using NeoAstra.Hosting;
using NeoAstra.Rpc;

[assembly: NeoRpcJsonContext(typeof(FixtureJsonContext))]

internal static class RpcFixture
{
    internal static async Task<int> RunAsync()
    {
        var frames = new List<string>();
        var secondFrames = new List<string>();
        var hostedServices = new ServiceCollection();
        hostedServices.AddNeoAstraRpc();
        hostedServices.AddNeoAstraApplication<HostedFixtureApplication>();
        if (!hostedServices.Any(static descriptor => descriptor.ServiceType == typeof(INeoHostedApplication))) return 4;
        var catalog = new NeoPermissionCatalogBuilder()
            .Add(new NeoPermissionDeclaration("documents:open", 1, ["documents.open"], NeoPermissionRisk.Sensitive, NeoScopeFamily.None))
            .Add(new NeoPermissionDeclaration("documents:read", 1, ["documents.changed"], NeoPermissionRisk.Low, NeoScopeFamily.None))
            .Build();
        var capabilityJson = """
        {"$schema":"neoastra-capabilities-v1.schema.json","version":1,"capabilities":[{"id":"fixture","views":["fixture","fixture-secondary"],"permissions":["documents:open","documents:read"]}]}
        """u8;
        var capabilityManifest = NeoCapabilityManifest.Resolve(capabilityJson, catalog, new() { Platform = CurrentPlatform(), Release = true, Profile = NeoSecurityProfile.ProductionLocalApp });
        var builder = new NeoRpcBuilder(new NeoRpcOptions { ContractHash = NeoRpcGeneratedContract.Hash, AuthorizationService = new NeoCapabilityAuthorizationService(capabilityManifest), CapabilityManifest = capabilityManifest });
        builder.AddDocumentsService(new DocumentsService());
        _ = builder.AddDocumentEventsChangedEvent();
        await using var host = builder.Build();
        await using var session = host.OpenSession(new NeoRpcSessionIdentity("fixture", "native-aot-document") { Platform = CurrentPlatform(), WholeViewTrust = CurrentPlatform() == NeoCapabilityPlatform.Linux, IsMainFrame = true }, (json, _) =>
        {
            frames.Add(json);
            return ValueTask.CompletedTask;
        });
        await using var secondSession = host.OpenSession(new NeoRpcSessionIdentity("fixture-secondary", "native-aot-document-2") { Platform = CurrentPlatform(), WholeViewTrust = CurrentPlatform() == NeoCapabilityPlatform.Linux, IsMainFrame = true }, (json, _) =>
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

        static NeoCapabilityPlatform CurrentPlatform() => OperatingSystem.IsWindows() ? NeoCapabilityPlatform.Windows : OperatingSystem.IsMacOS() ? NeoCapabilityPlatform.MacOS : NeoCapabilityPlatform.Linux;
    }
}

public sealed class HostedFixtureApplication : INeoHostedApplication
{
    public ValueTask StartAsync(NeoApplication application, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

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
