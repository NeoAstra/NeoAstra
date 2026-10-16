using System.Text.Json.Serialization;
using NeoAstra;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Opener;
using NeoAstra.Rpc;

[assembly: NeoRpcJsonContext(typeof(NeoAstra.Samples.Mobile.AppJsonContext))]

namespace NeoAstra.Samples.Mobile;

/// <summary>Hosts the sample page in a view of the application, the same way on iOS and Android.</summary>
/// <remarks>
/// The page reaches the desktop services of NeoAstra (clipboard, theme, safe storage, opener, notifications, dialogs) through
/// the official renderer handlers, and the capability document grants it exactly the permissions it uses on iOS and Android.
/// A service that a platform does not offer answers Unsupported instead of failing.
/// </remarks>
internal sealed class MobileHost
{
    private const string ApplicationId = "dev.neoastra.sample.mobile";

    private readonly NeoApplication _application;
    private readonly NeoEnvironment _environment;
    private readonly NeoRpcHost _rpc;
    private readonly NeoAstraHost _host;
    private global::NeoAstra.NeoAstra? _view;
    private NeoRpcViewBinding? _binding;

    private MobileHost(NeoApplication application, NeoEnvironment environment, NeoRpcHost rpc, NeoAstraHost host)
    {
        _application = application;
        _environment = environment;
        _rpc = rpc;
        _host = host;
    }

    /// <summary>Attaches to the run loop of the platform and shows the page in the given host.</summary>
    /// <remarks>Call it on the main thread, which the platform owns.</remarks>
    public static async Task<MobileHost> StartAsync(NeoAstraHost host, string assetsDirectory)
    {
        var application = NeoApplication.AttachToCurrentThread(new NeoApplicationOptions
        {
            ApplicationName = "NeoAstra Mobile Sample",
            ShutdownMode = NeoApplicationShutdownMode.Explicit,
        });
        var environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions
        {
            CustomSchemes = [NeoCustomScheme.Application("app", new NeoDirectoryResourceProvider(assetsDirectory))],
        });
        var desktop = NeoDesktopServices.CreateSystem(
            ApplicationId,
            "NeoAstra Mobile",
            "1.0",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "neoastra"),
            ["https://github.com"],
            [assetsDirectory],
            [assetsDirectory],
            [NeoOpenFileIntent.TextDocument],
            application.Dispatcher);
        var plugins = new NeoPluginBuilder().AddNeoAstraDesktop(desktop).Build();
        await plugins.StartAsync(application);

        var capabilities = LoadCapabilities();
        var builder = new NeoRpcBuilder(new NeoRpcOptions
        {
            CapabilityManifest = capabilities,
            SecurityProfile = capabilities.Profile,
            AuthorizationService = new NeoCapabilityAuthorizationService(capabilities),
        });
        builder.AddGreetingService(new GreetingService());
        builder.AddNeoAstraDesktopHandlers(desktop, new NeoDesktopRendererOptions
        {
            AllowedSafeStorageKeys = new HashSet<string>(StringComparer.Ordinal) { "hello-secret" },
            AllowedTrayIds = new HashSet<string>(StringComparer.Ordinal) { "hello" },
        });
        var mobile = new MobileHost(application, environment, builder.Build(), host);
        await mobile.ShowAsync();
        // An application that attaches to a run loop of its own reports when its startup is complete.
        application.NotifyReady();
        return mobile;
    }

    // The capability document is resolved for the platform that runs it, so the same file serves iOS and Android.
    private static NeoCapabilityManifest LoadCapabilities()
    {
        using var stream = typeof(MobileHost).Assembly.GetManifestResourceStream("capabilities.json") ?? throw new InvalidOperationException("The capability document is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return NeoCapabilityManifest.Resolve(
            memory.ToArray(),
            new NeoPermissionCatalogBuilder().AddNeoAstraDesktopPermissions().Build(),
            new NeoCapabilityResolutionOptions
            {
                Platform = OperatingSystem.IsIOS() ? NeoCapabilityPlatform.IOS : NeoCapabilityPlatform.Android,
                Profile = NeoSecurityProfile.ProductionLocalApp,
            });
    }

    private async Task ShowAsync()
    {
        _view = await _environment.CreateWebViewAsync(_host, new NeoAstraOptions
        {
            ViewLabel = "main",
            BridgePolicy = NeoBridgePolicy.TrustedOrigins,
            BridgeOrigins = ["app://neoastra"],
        });
        _binding = NeoRpcViewBinding.Bind(_rpc, _view);
        // iOS and Android end the web content process of a view under memory pressure; a lost view is recreated.
        _view.ProcessFailed += async (_, _) => await RecreateAsync();
        await _view.NavigateAsync(new Uri("app://neoastra/index.html"));
    }

    private async Task RecreateAsync()
    {
        if (_binding is not null) await _binding.DisposeAsync();
        if (_view is not null) await _view.DisposeAsync();
        await ShowAsync();
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
