using System.Text.Json.Serialization;
using NeoAstra;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Dialogs;
using NeoAstra.Desktop.Opener;
using NeoAstra.Location;
using NeoAstra.Passkeys;
using NeoAstra.Purchases;
using NeoAstra.Push;
using NeoAstra.Rpc;

[assembly: NeoRpcJsonContext(typeof(NeoAstra.Samples.Mobile.AppJsonContext))]

namespace NeoAstra.Samples.Mobile;

/// <summary>Hosts the sample page in a window of NeoAstra, the same way on iOS and Android.</summary>
/// <remarks>
/// A window of NeoAstra is the UIWindow of the active scene on iOS and the content of the current activity on Android; the
/// page in it reaches the window commands too. The page reaches the desktop services of NeoAstra (clipboard, theme, safe storage, opener, notifications, dialogs) through
/// the official renderer handlers, the location of the device, push messages, passkeys, and purchases through their plugins, and the capability document grants
/// it exactly the permissions it uses on iOS and Android.
/// A service that a platform does not offer answers Unsupported instead of failing.
/// </remarks>
internal sealed class MobileHost
{
    private const string ApplicationId = "dev.neoastra.sample.mobile";

    private static MobileHost? s_current;

    private readonly NeoApplication _application;
    private readonly NeoEnvironment _environment;
    private readonly NeoRpcHost _rpc;
    private readonly NeoDesktopServices _desktop;
    private NeoWindow? _window;
    private global::NeoAstra.NeoAstra? _view;
    private NeoRpcViewBinding? _binding;

    private MobileHost(NeoApplication application, NeoEnvironment environment, NeoRpcHost rpc, NeoDesktopServices desktop)
    {
        _application = application;
        _environment = environment;
        _rpc = rpc;
        _desktop = desktop;
    }

    /// <summary>Shows the page in a new window; the first call attaches to the run loop of the platform.</summary>
    /// <remarks>
    /// Call it on the main thread, which the platform owns, once a scene is connected on iOS or in the creation of an activity
    /// on Android. A later activity, after the user left the previous one, takes a new window in the same application.
    /// </remarks>
    public static async Task ShowAsync(string assetsDirectory)
    {
        if (s_current is not null)
        {
            await s_current.OpenWindowAsync();
            return;
        }
        s_current = await StartAsync(assetsDirectory);
    }

    private static async Task<MobileHost> StartAsync(string assetsDirectory)
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
        var location = NeoLocation.CreateSystem(application.Dispatcher);
        // The push service of iOS observes the notification service of NeoAstra, which owns the delegate of the notification center.
        var push = NeoPush.CreateSystem(desktop.Notifications, application.Dispatcher);
        var passkeys = NeoPasskeys.CreateSystem(application.Dispatcher);
        var purchases = NeoPurchases.CreateSystem(application.Dispatcher);
        var plugins = new NeoPluginBuilder().AddNeoAstraDesktop(desktop).AddNeoAstraLocation(location).AddNeoAstraPush(push).AddNeoAstraPasskeys(passkeys).AddNeoAstraPurchases(purchases).Build();

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
        builder.AddNeoAstraLocationHandlers(location);
        // The page decides whether a push that arrives while it is shown is shown too.
        builder.AddNeoAstraPushHandlers(push, new NeoPushRendererOptions { PagesDecideForegroundPresentation = true });
        builder.AddNeoAstraPasskeysHandlers(passkeys);
        builder.AddNeoAstraPurchasesHandlers(purchases);
        var rpc = builder.Build();
        // The plugins start once the handlers listen: starting binds the notifications, which hand over the tap of a scheduled
        // notification that launched the application, and the handlers keep it for the page.
        await plugins.StartAsync(application);
        var mobile = new MobileHost(application, environment, rpc, desktop);
        await mobile.OpenWindowAsync();
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
            new NeoPermissionCatalogBuilder().AddNeoAstraDesktopPermissions().AddNeoAstraLocationPermissions().AddNeoAstraPushPermissions().AddNeoAstraPasskeysPermissions().AddNeoAstraPurchasesPermissions().Build(),
            new NeoCapabilityResolutionOptions
            {
                Platform = OperatingSystem.IsIOS() ? NeoCapabilityPlatform.IOS : NeoCapabilityPlatform.Android,
                Profile = NeoSecurityProfile.ProductionLocalApp,
            });
    }

    private async Task OpenWindowAsync()
    {
        var window = _application.CreateWindow(new NeoWindowOptions { Title = "NeoAstra Mobile", IsVisible = true });
        _window = window;
        // The window ends with its scene or activity; the view in it goes with it.
        window.Closed += async (_, _) => { if (ReferenceEquals(_window, window)) await CloseViewAsync(); };
        await ShowViewAsync();
    }

    private async Task ShowViewAsync()
    {
        if (_window is null) return;
        _view = await _environment.CreateWebViewAsync(NeoAstraHost.FillWindow(_window), new NeoAstraOptions
        {
            ViewLabel = "main",
            BridgePolicy = NeoBridgePolicy.TrustedOrigins,
            BridgeOrigins = ["app://neoastra"],
        });
        _binding = NeoRpcViewBinding.Bind(_rpc, _view);
        // What the page asks of the browser: its dialogs show as a native dialog, a file input opens the picker of the system,
        // and a camera or a microphone is what the user allows the application.
        _view.ScriptDialogRequested = ShowScriptDialogAsync;
        _view.FileChooserRequested = _ => ValueTask.FromResult(new NeoFileChooserDecision(NeoDecisionAction.Default));
        _view.PermissionRequested = request => ValueTask.FromResult(request.Kind is NeoPermissionKind.Camera or NeoPermissionKind.Microphone ? NeoPermissionDecision.Default : NeoPermissionDecision.DenyOnce);
        // iOS and Android end the web content process of a view under memory pressure; a lost view is recreated.
        _view.ProcessFailed += async (_, _) => await RecreateAsync();
        await _view.NavigateAsync(new Uri("app://neoastra/index.html"));
    }

    // The dialog service shows a message with buttons; a prompt, which needs a text field, is canceled.
    private async ValueTask<NeoScriptDialogDecision> ShowScriptDialogAsync(NeoScriptDialogRequest request)
    {
        if (request.Kind is not (NeoScriptDialogKind.Alert or NeoScriptDialogKind.Confirm)) return NeoScriptDialogDecision.Cancel;
        var result = await _desktop.Dialogs.ShowMessageAsync(new NeoMessageDialogOptions
        {
            Message = request.Message,
            Buttons = request.Kind == NeoScriptDialogKind.Alert ? [NeoDialogButtonRole.Accept] : [NeoDialogButtonRole.Accept, NeoDialogButtonRole.Cancel],
        });
        return result.IsSuccess && result.Value == NeoDialogButtonRole.Accept ? NeoScriptDialogDecision.Accept : NeoScriptDialogDecision.Cancel;
    }

    private async Task RecreateAsync()
    {
        await CloseViewAsync();
        await ShowViewAsync();
    }

    private async Task CloseViewAsync()
    {
        if (_binding is not null) await _binding.DisposeAsync();
        if (_view is not null) await _view.DisposeAsync();
        _binding = null;
        _view = null;
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
