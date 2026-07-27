using System.Text.Json.Serialization;
using NeoAstra;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Clipboard;
using NeoAstra.Desktop.Dialogs;
using NeoAstra.Desktop.DragDrop;
using NeoAstra.Desktop.GlobalShortcuts;
using NeoAstra.Desktop.Menus;
using NeoAstra.Desktop.Notifications;
using NeoAstra.Desktop.Opener;
using NeoAstra.Desktop.Tray;
using NeoAstra.Desktop.WindowState;
using NeoAstra.Rpc;

[assembly: NeoRpcJsonContext(typeof(ReferenceJsonContext))]

if (args is ["--validate-reference"])
{
    var root = Path.Combine(AppContext.BaseDirectory, "assets"); var manifest = NeoAssetManifest.Load(Path.Combine(root, "neoastra-assets.json")); var provider = new NeoManifestResourceProvider(root, manifest);
    var response = provider.GetResponse(new(new Uri("app://reference/route"), "HEAD", new Dictionary<string, string> { ["Accept"] = "text/html" }, null, NeoResourceKind.Document, true, default));
    if (response?.StatusCode != 200 || response.MimeType != "text/html; charset=utf-8") return 2;
    foreach (var asset in manifest.Assets)
        if (provider.GetResponse(new(new Uri("app://reference/" + asset.Path), "HEAD", new Dictionary<string, string>(), null, NeoResourceKind.Other, false, default))?.StatusCode != 200) return 3;
    var scripts = manifest.Assets.Where(static asset => asset.Path.EndsWith(".js", StringComparison.Ordinal)).ToArray();
    var mainScript = scripts.SingleOrDefault(static asset => Path.GetFileName(asset.Path).StartsWith("index-", StringComparison.Ordinal));
    var dynamicScript = scripts.SingleOrDefault(static asset => Path.GetFileName(asset.Path).StartsWith("details-", StringComparison.Ordinal));
    var workerScript = scripts.SingleOrDefault(static asset => Path.GetFileName(asset.Path).StartsWith("reference.worker-", StringComparison.Ordinal));
    var style = manifest.Assets.SingleOrDefault(static asset => asset.Path.EndsWith(".css", StringComparison.Ordinal));
    var font = manifest.Assets.SingleOrDefault(static asset => asset.Path.EndsWith(".ttf", StringComparison.Ordinal));
    var image = manifest.Assets.SingleOrDefault(static asset => asset.Path.EndsWith(".svg", StringComparison.Ordinal));
    if (mainScript is null || dynamicScript is null || workerScript is null || style is null || font is null || image is null || manifest.Assets.Where(asset => asset.Path != manifest.EntryDocument).Any(static asset => asset.CacheControl != "public,max-age=31536000,immutable")) return 4;
    var html = File.ReadAllText(Path.Combine(root, manifest.EntryDocument)); var main = File.ReadAllText(Path.Combine(root, mainScript.Path)); var css = File.ReadAllText(Path.Combine(root, style.Path));
    if (!html.Contains(mainScript.Path, StringComparison.Ordinal) || !html.Contains(style.Path, StringComparison.Ordinal) || !main.Contains(Path.GetFileName(dynamicScript.Path), StringComparison.Ordinal) || !main.Contains(Path.GetFileName(workerScript.Path), StringComparison.Ordinal) || !main.Contains(Path.GetFileName(image.Path), StringComparison.Ordinal) || !css.Contains(Path.GetFileName(font.Path), StringComparison.Ordinal)) return 5;
    if (!html.Contains("<html lang=\"en\">", StringComparison.Ordinal) || !html.Contains("charset=\"UTF-8\"", StringComparison.Ordinal) || !html.Contains("tabindex=\"-1\"", StringComparison.Ordinal) || !main.Contains("aria-live", StringComparison.Ordinal) || !main.Contains("polite", StringComparison.Ordinal) || !main.Contains("Typed RPC", StringComparison.Ordinal) || !main.Contains("alt:", StringComparison.Ordinal)) return 7;
    var catalog = new NeoPermissionCatalogBuilder().Add(new NeoPermissionDeclaration("notes:read", 1, ["notes.hello"], NeoPermissionRisk.Low, NeoScopeFamily.None)).AddNeoAstraDesktopPermissions().Build();
    var platform = OperatingSystem.IsWindows() ? NeoCapabilityPlatform.Windows : OperatingSystem.IsMacOS() ? NeoCapabilityPlatform.MacOS : NeoCapabilityPlatform.Linux;
    var capabilities = NeoCapabilityManifest.Resolve(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "capabilities", "main.json")), catalog, new() { Platform = platform, Release = true, Profile = NeoSecurityProfile.ProductionLocalApp });
    using var capabilityJson = System.Text.Json.JsonDocument.Parse(capabilities.Json); var grants = capabilityJson.RootElement.GetProperty("capabilities");
    if (grants.GetArrayLength() != 1 || grants[0].GetProperty("views").GetArrayLength() != 1 || grants[0].GetProperty("views")[0].GetString() != "main" || grants[0].GetProperty("permissions").GetArrayLength() != 1 || grants[0].GetProperty("permissions")[0].GetProperty("id").GetString() != "notes:read") return 6;
    Console.WriteLine($"NeoAstra v2 reference module graph, release main grant/preview denial, and generated contract {NeoRpcGeneratedContract.Hash} validated."); return 0;
}

if (OperatingSystem.IsWindows() && Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
{
    var result = -1; Exception? failure = null;
    var thread = new Thread(() => { try { result = RunApplication(args); } catch (Exception exception) { failure = exception; } });
    thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    return result;
}

return RunApplication(args);

static int RunApplication(string[] args) => NeoApplication.Run(new NeoApplicationOptions { ApplicationName = "NeoAstra v2 Reference", ShutdownMode = NeoApplicationShutdownMode.OnMainWindowClosed }, async app =>
{
    var routedLaunch = new NeoLaunchEvent(NeoLaunchReason.SecondInstance, args, Environment.CurrentDirectory);
    await using var singleInstance = await NeoSingleInstance.AcquireAsync(app, new NeoSingleInstanceOptions
    {
        ApplicationId = "org.neoastra.v2-reference",
        HungPrimaryPolicy = NeoSingleInstanceHungPrimaryPolicy.Retry,
    }, routedLaunch);
    if (!singleInstance.IsPrimary) { app.ForceShutdown(); return; }
    var developmentUrl = Environment.GetEnvironmentVariable("NEOASTRA_DEV_URL"); var development = developmentUrl is not null; var assetRoot = Path.Combine(AppContext.BaseDirectory, "assets");
    var privateData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeoAstra", "v2-reference");
    var desktop = NeoDesktopServices.CreateSystem("org.neoastra.v2-reference", "NeoAstra v2 Reference", "2.0.0", privateData, ["https://neoastra.local"], [assetRoot], [assetRoot], [NeoOpenFileIntent.TextDocument, NeoOpenFileIntent.PdfDocument, NeoOpenFileIntent.Image], app.Dispatcher);
    await using var pluginHost = new NeoPluginBuilder().AddNeoAstraDesktop(desktop).Build(); await pluginHost.StartAsync(app);
    var catalog = new NeoPermissionCatalogBuilder().Add(new NeoPermissionDeclaration("notes:read", 1, ["notes.hello"], NeoPermissionRisk.Low, NeoScopeFamily.None)).AddNeoAstraDesktopPermissions().Build();
    var capabilities = NeoCapabilityManifest.Resolve(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "capabilities", "main.json")), catalog, new() { Platform = OperatingSystem.IsWindows() ? NeoCapabilityPlatform.Windows : OperatingSystem.IsMacOS() ? NeoCapabilityPlatform.MacOS : NeoCapabilityPlatform.Linux, Release = !development, Profile = development ? NeoSecurityProfile.DevelopmentLocalApp : NeoSecurityProfile.ProductionLocalApp });
    var rpcBuilder = new NeoRpcBuilder(new NeoRpcOptions { ContractHash = NeoRpcGeneratedContract.Hash, CapabilityManifest = capabilities, AuthorizationService = new NeoCapabilityAuthorizationService(capabilities) });
    rpcBuilder.AddNotesService(new NotesService()).AddNeoAstraDesktopHandlers(desktop, new NeoDesktopRendererOptions { FileRoots = new Dictionary<string, string> { ["assets"] = assetRoot } }); await using var rpc = rpcBuilder.Build();
    var window = app.CreateWindow(new NeoWindowOptions { Label = "main", Title = "NeoAstra v2 Reference", Width = 960, Height = 640, IsVisible = true });
    app.MainWindow = window;
    _ = await desktop.WindowPolish.SetTitleBarThemeAsync(window, NeoWindowTitleBarTheme.System);
    var previewWindow = app.CreateWindow(new NeoWindowOptions { Label = "preview", Owner = window, Title = "NeoAstra v2 Preview (no grants)", Width = 480, Height = 320, IsVisible = false });
    app.LaunchReceived += launch => { if (launch.Reason == NeoLaunchReason.SecondInstance) { window.Show(); window.Activate(); } return ValueTask.CompletedTask; };
    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); window.Closed += (_, _) => closed.TrySetResult();
    NeoAssetManifest? manifest = development ? null : NeoAssetManifest.Load(Path.Combine(assetRoot, "neoastra-assets.json"));
    await using var environment = await app.CreateEnvironmentAsync(new NeoEnvironmentOptions { CustomSchemes = manifest is null ? [] : [NeoCustomScheme.Application("app", new NeoManifestResourceProvider(assetRoot, manifest))] });
    var target = development ? new Uri(developmentUrl!) : new Uri("app://reference/index.html"); var wholeView = !development || OperatingSystem.IsLinux();
    NeoAstraOptions Options(string label) => new() { ViewLabel = label, BridgePolicy = wholeView ? NeoBridgePolicy.TrustEntireView : NeoBridgePolicy.TrustedOrigins, BridgeOrigins = wholeView ? [] : [target.GetLeftPart(UriPartial.Authority)] };
    await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), Options("main"));
    await using var preview = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(previewWindow), Options("preview"));
    await using var binding = NeoRpcViewBinding.Bind(rpc, view); await using var previewBinding = NeoRpcViewBinding.Bind(rpc, preview);
    var unsaved = string.Equals(Environment.GetEnvironmentVariable("NEOASTRA_REFERENCE_UNSAVED"), "1", StringComparison.Ordinal);
    window.CloseRequested += async request =>
    {
        if (!unsaved || !request.CanCancel) return;
        try
        {
            var save = await view.EvaluateScriptAsync("globalThis.confirm('Save unsaved reference work before closing?')", request.DeadlineToken);
            if (!string.Equals(save, "true", StringComparison.OrdinalIgnoreCase)) request.Cancel(); else unsaved = false;
        }
        catch { request.Cancel(); } // Renderer failure must preserve unsaved work.
    };
    await view.NavigateAsync(target); await preview.NavigateAsync(target); app.NotifyReady();
    if (string.Equals(Environment.GetEnvironmentVariable("NEOASTRA_REFERENCE_EXERCISE_DESKTOP"), "1", StringComparison.Ordinal)) await ExerciseDesktopAsync(desktop, window, assetRoot, privateData);
    await closed.Task;
});

static async ValueTask ExerciseDesktopAsync(NeoDesktopServices desktop, NeoWindow window, string assetRoot, string privateData)
{
    // Explicit opt-in keeps automated/headless validation non-interactive while compiling and
    // demonstrating every backend desktop-service family through one platform-neutral path.
    var scope = new NeoFileScope([assetRoot]); var dialog = new NeoFileDialogOptions { Owner = window, Title = "NeoAstra reference", InitialDirectory = assetRoot, Scope = scope };
    _ = await desktop.Dialogs.ShowMessageAsync(new NeoMessageDialogOptions { Owner = window, Message = "NeoAstra desktop services", Buttons = [NeoDialogButtonRole.Accept] });
    _ = await desktop.Dialogs.OpenFilesAsync(dialog); _ = await desktop.Dialogs.OpenFoldersAsync(dialog); _ = await desktop.Dialogs.SaveFileAsync(new NeoFileDialogOptions { Owner = window, Title = "NeoAstra reference", InitialDirectory = assetRoot, SuggestedFileName = "reference.txt", Scope = scope });
    desktop.Menus.Commands.Register("reference.activate", _ => ValueTask.CompletedTask);
    var referenceMenu = new[] { NeoMenuItem.Command("activate", "Activate", "reference.activate", "Ctrl+Shift+R"), NeoMenuItem.RoleItem("copy", NeoMenuRole.Copy, "Copy") };
    await desktop.Menus.SetMenuAsync("application", referenceMenu); await desktop.Menus.RemoveMenuAsync("application");
    await desktop.Menus.SetMenuAsync("window:main", referenceMenu); await desktop.Menus.RemoveMenuAsync("window:main");
    await desktop.Menus.SetMenuAsync("context:main", referenceMenu); _ = await desktop.Menus.ShowContextMenuAsync("context:main", new NeoPoint(16, 16));
    desktop.Tray.Set(new NeoTrayItemOptions { Id = "reference", ToolTip = "NeoAstra reference", Menu = [NeoMenuItem.Command("activate", "Activate", "reference.activate")] });
    desktop.Tray.Set(new NeoTrayItemOptions { Id = "reference", ToolTip = "NeoAstra reference (updated)", Menu = [NeoMenuItem.RoleItem("quit", NeoMenuRole.Quit, "Quit")] });
    var unicode = "NeoAstra 🌍"u8.ToArray(); _ = await desktop.Clipboard.WriteAsync(NeoClipboardFormat.Text, unicode); _ = await desktop.Clipboard.ReadAsync(NeoClipboardFormat.Text); _ = await desktop.Clipboard.ClearAsync();
    _ = await desktop.Notifications.GetPermissionStatusAsync(); _ = await desktop.Notifications.ShowAsync(new NeoNotificationRequest { Id = "reference", Title = "NeoAstra", Body = "Desktop notification", Actions = [new("open", "Open")] }); _ = await desktop.Notifications.RemoveAsync("reference");
    _ = await desktop.GlobalShortcuts.RegisterAsync("reference", NeoAccelerator.Normalize("Ctrl+Shift+R")); _ = await desktop.GlobalShortcuts.UnregisterAsync("reference");
    _ = desktop.SystemInfo.Theme; _ = desktop.SystemInfo.Displays; _ = desktop.SystemInfo.Metadata;
    var marker = Path.Combine(assetRoot, "reference-mark.svg"); _ = await desktop.Opener.OpenUrlAsync(new Uri("https://neoastra.local")); _ = await desktop.Opener.OpenFileAsync(marker, NeoOpenFileIntent.Image); _ = await desktop.Opener.RevealAsync(marker);
    var owner = NeoPluginOwner.Application(); var gesture = desktop.DragDrop.IssueUserGesture(owner, TimeSpan.FromSeconds(2)); _ = await desktop.DragDrop.StartOutboundAsync(gesture, owner, new NeoOutboundDragRequest { ViewLabel = "main", Items = [new(NeoDragDataKind.Text, "NeoAstra")] }); desktop.DragDrop.ReleaseOwner(owner);
    var secret = "reference secret"u8.ToArray(); try { _ = await desktop.SafeStorage.StoreAsync("reference", secret); _ = await desktop.SafeStorage.ContainsAsync("reference"); var stored = await desktop.SafeStorage.RetrieveAsync("reference"); if (stored.Value is not null) Array.Clear(stored.Value); _ = await desktop.SafeStorage.DeleteAsync("reference"); } finally { Array.Clear(secret); }
    await using (var state = new NeoWindowStateController(window, new NeoJsonWindowStateStore(Path.Combine(privateData, "window-state")), "main")) _ = await state.RestoreAsync(desktop.SystemInfo.Displays); window.State = NeoWindowState.Normal;
    _ = await desktop.WindowPolish.SetIconAsync(window, marker); _ = await desktop.WindowPolish.SetRepresentedFileAsync(window, marker); _ = await desktop.WindowPolish.RequestAttentionAsync(window, true); _ = await desktop.WindowPolish.SetProgressAsync(window, NeoWindowProgressState.Normal, 0.5); _ = await desktop.WindowPolish.SetBadgeAsync(window, "1"); _ = await desktop.WindowPolish.SetDocumentEditedAsync(window, true); _ = await desktop.WindowPolish.SetContentProtectionAsync(window, true); _ = await desktop.WindowPolish.SetTitleBarThemeAsync(window, NeoWindowTitleBarTheme.System);
    desktop.Tray.Remove("reference"); await desktop.Menus.RemoveMenuAsync("context:main");
}

[NeoRpcService("notes", Version = 1)] public sealed class NotesService { [NeoRpcMethod("hello", Permission = "notes:read")] public ValueTask<HelloResponse> HelloAsync(HelloRequest request, NeoRpcContext context, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(new HelloResponse($"Hello, {request.Name}!", context.ViewLabel)); } }
public sealed record HelloRequest(string Name);
public sealed record HelloResponse(string Message, string ViewLabel);
[JsonSerializable(typeof(HelloRequest))][JsonSerializable(typeof(HelloResponse))][JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)] internal sealed partial class ReferenceJsonContext : JsonSerializerContext;
