using System.Text.Json.Serialization;
using NeoAstra;
using NeoAstra.Rpc;

[assembly: NeoRpcJsonContext(typeof(AppJsonContext))]

return NeoApplication.Run(new NeoApplicationOptions { ApplicationName = "NeoAstra App" }, async app =>
{
    var developmentUrl = Environment.GetEnvironmentVariable("NEOASTRA_DEV_URL"); var development = developmentUrl is not null;
    var catalog = new NeoPermissionCatalogBuilder().Add(new NeoPermissionDeclaration("greeting:read", 1, ["greeting.hello"], NeoPermissionRisk.Low, NeoScopeFamily.None)).Build();
    var capabilities = NeoCapabilityManifest.Resolve(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "capabilities", "main.json")), catalog, new() { Platform = OperatingSystem.IsWindows() ? NeoCapabilityPlatform.Windows : OperatingSystem.IsMacOS() ? NeoCapabilityPlatform.MacOS : NeoCapabilityPlatform.Linux, Release = !development, Profile = development ? NeoSecurityProfile.DevelopmentLocalApp : NeoSecurityProfile.ProductionLocalApp });
    var rpcBuilder = new NeoRpcBuilder(new NeoRpcOptions { ContractHash = NeoRpcGeneratedContract.Hash, CapabilityManifest = capabilities, AuthorizationService = new NeoCapabilityAuthorizationService(capabilities) }); rpcBuilder.AddGreetingService(new GreetingService()); await using var rpc = rpcBuilder.Build();
    var window = app.CreateWindow(new NeoWindowOptions { Title = "NeoAstra App", Width = 960, Height = 640, IsVisible = true }); var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); window.Closed += (_, _) => closed.TrySetResult();
    NeoAssetManifest? manifest = null; if (!development) manifest = NeoAssetManifest.Load(Path.Combine(AppContext.BaseDirectory, "assets", "neoastra-assets.json"));
    await using var environment = await app.CreateEnvironmentAsync(new NeoEnvironmentOptions { CustomSchemes = manifest is null ? [] : [NeoCustomScheme.Application("app", new NeoManifestResourceProvider(Path.Combine(AppContext.BaseDirectory, "assets"), manifest))] });
    var target = development ? new Uri(developmentUrl!) : new Uri("app://neoastra/index.html"); var wholeView = !development || OperatingSystem.IsLinux();
    await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), new NeoAstraOptions { ViewLabel = "main", BridgePolicy = wholeView ? NeoBridgePolicy.TrustEntireView : NeoBridgePolicy.TrustedOrigins, BridgeOrigins = wholeView ? [] : [target.GetLeftPart(UriPartial.Authority)] });
    await using var binding = NeoRpcViewBinding.Bind(rpc, view); await view.NavigateAsync(target); await closed.Task;
});

[NeoRpcService("greeting", Version = 1)] public sealed class GreetingService { [NeoRpcMethod("hello", Permission = "greeting:read")] public ValueTask<GreetingResponse> HelloAsync(GreetingRequest request, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(new GreetingResponse($"Hello, {request.Name}!")); } }
public sealed record GreetingRequest(string Name);
public sealed record GreetingResponse(string Message);
[JsonSerializable(typeof(GreetingRequest))][JsonSerializable(typeof(GreetingResponse))][JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)] internal sealed partial class AppJsonContext : JsonSerializerContext;
