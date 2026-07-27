using Microsoft.Extensions.Logging;
using NeoAstra;
using NeoAstra.Desktop;
using NeoAstra.Desktop.DragDrop;
using NeoAstra.Desktop.Menus;
using NeoAstra.Desktop.Opener;
using NeoAstra.Desktop.WindowState;
using NeoAstra.Hosting;
using NeoAstra.Rpc;

internal sealed class ReferenceApplication(
    TourEventHub events,
    TourState state,
    ILogger<ReferenceApplication> logger) : INeoHostedApplication
{
    internal const string ApplicationId = "org.neoastra.v2-reference";
    internal const string DisplayName = "NeoAstra v2 Feature Tour";
    internal const string Version = "2.0.0";

    private ReferenceSession? _session;

    public async ValueTask StartAsync(
        NeoApplication application,
        CancellationToken cancellationToken)
    {
        // Establish the main-window lifetime before the first asynchronous yield. This prevents
        // OnMainWindowClosed policy from observing an empty startup window set.
        var mainWindow = application.CreateWindow(new NeoWindowOptions
        {
            Label = "main",
            Title = DisplayName,
            Width = 1180,
            Height = 820,
            IsVisible = false,
        });
        application.MainWindow = mainWindow;

        var launch = new NeoLaunchEvent(
            NeoLaunchReason.SecondInstance,
            Environment.GetCommandLineArgs(),
            Environment.CurrentDirectory);
        var singleInstance = await NeoSingleInstance.AcquireAsync(
            application,
            new NeoSingleInstanceOptions
            {
                ApplicationId = ApplicationId,
                HungPrimaryPolicy = NeoSingleInstanceHungPrimaryPolicy.Retry,
            },
            launch,
            cancellationToken);

        if (!singleInstance.IsPrimary)
        {
            await singleInstance.DisposeAsync();
            // Let the hosting adapter publish its ready transition before the queued secondary
            // process shutdown runs. The launch payload has already reached the primary process.
            application.Dispatcher.Post(() => application.ForceShutdown());
            return;
        }

        try
        {
            _session = await CreateSessionAsync(
                application,
                mainWindow,
                singleInstance,
                cancellationToken);
        }
        catch
        {
            await singleInstance.DisposeAsync();
            throw;
        }
    }

    private async ValueTask<ReferenceSession> CreateSessionAsync(
        NeoApplication application,
        NeoWindow mainWindow,
        NeoSingleInstance singleInstance,
        CancellationToken cancellationToken)
    {
        var developmentUrl = Environment.GetEnvironmentVariable("NEOASTRA_DEV_URL");
        var development = developmentUrl is not null;
        var assetRoot = Path.Combine(AppContext.BaseDirectory, "assets");
        var privateData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NeoAstra",
            "v2-reference");

        var desktop = NeoDesktopServices.CreateSystem(
            ApplicationId,
            DisplayName,
            Version,
            privateData,
            ["https://neoastra.dev"],
            [assetRoot],
            [assetRoot],
            [NeoOpenFileIntent.TextDocument, NeoOpenFileIntent.PdfDocument, NeoOpenFileIntent.Image],
            application.Dispatcher);
        var pluginHost = new NeoPluginBuilder()
            .AddNeoAstraDesktop(desktop)
            .Build();
        await pluginHost.StartAsync(application, cancellationToken);

        var capabilityManifest = ReferenceCapabilities.Load(development);
        var rpcBuilder = new NeoRpcBuilder(new NeoRpcOptions
        {
            ContractHash = NeoRpcGeneratedContract.Hash,
            CapabilityManifest = capabilityManifest,
            AuthorizationService = new NeoCapabilityAuthorizationService(capabilityManifest),
        });
        rpcBuilder.AddTourService(new TourService(state));
        events.Attach(rpcBuilder.AddTourEventsActivityEvent());
        rpcBuilder.AddNeoAstraDesktopHandlers(
            desktop,
            CreateRendererOptions(assetRoot));
        var rpc = rpcBuilder.Build();

        var previewWindow = application.CreateWindow(new NeoWindowOptions
        {
            Label = "preview",
            Owner = mainWindow,
            Title = "Restricted preview — read-only grant",
            Width = 620,
            Height = 560,
            IsVisible = false,
        });
        state.ConfigurePreview(() =>
        {
            previewWindow.Show();
            previewWindow.Activate();
        });

        ConfigureLaunchRouting(application, mainWindow);
        await ConfigureNativeMenuAsync(
            application,
            desktop,
            previewWindow,
            cancellationToken);
        _ = await desktop.WindowPolish.SetTitleBarThemeAsync(
            mainWindow,
            NeoWindowTitleBarTheme.System,
            cancellationToken);

        var manifest = development
            ? null
            : NeoAssetManifest.Load(Path.Combine(assetRoot, "neoastra-assets.json"));
        var environment = await application.CreateEnvironmentAsync(
            new NeoEnvironmentOptions
            {
                CustomSchemes = manifest is null
                    ? []
                    : [NeoCustomScheme.Application(
                        "app",
                        new NeoManifestResourceProvider(assetRoot, manifest))],
            },
            cancellationToken);

        var target = development
            ? new Uri(developmentUrl!)
            : new Uri("app://reference/index.html");
        var trustEntireView = !development || OperatingSystem.IsLinux();

        NeoAstraOptions ViewOptions(string label) => new()
        {
            ViewLabel = label,
            BridgePolicy = trustEntireView
                ? NeoBridgePolicy.TrustEntireView
                : NeoBridgePolicy.TrustedOrigins,
            BridgeOrigins = trustEntireView
                ? []
                : [target.GetLeftPart(UriPartial.Authority)],
        };

        var windowState = new NeoWindowStateController(
            mainWindow,
            new NeoJsonWindowStateStore(Path.Combine(privateData, "window-state")),
            "main");
        _ = await windowState.RestoreAsync(
            desktop.SystemInfo.Displays,
            cancellationToken: cancellationToken);

        // Realize the restored native client area before WebView2 creates its child controller.
        // Creating the fill-parent controller under the hidden window can leave it visually blank
        // until a later WM_SIZE even though the document has loaded successfully.
        mainWindow.Show();
        mainWindow.Activate();

        var mainView = await environment.CreateWebViewAsync(
            NeoAstraHost.FillWindow(mainWindow),
            ViewOptions("main"),
            cancellationToken);
        var previewView = await environment.CreateWebViewAsync(
            NeoAstraHost.FillWindow(previewWindow),
            ViewOptions("preview"),
            cancellationToken);
        var mainBinding = NeoRpcViewBinding.Bind(rpc, mainView);
        var previewBinding = NeoRpcViewBinding.Bind(rpc, previewView);

        mainWindow.CloseRequested += request =>
            ConfirmUnsavedCloseAsync(request, mainView);

        await mainView.NavigateAsync(target, cancellationToken);
        await previewView.NavigateAsync(target, cancellationToken);

        logger.LogInformation(
            "Started {ApplicationName} with {SecurityProfile} assets at {Target}.",
            DisplayName,
            development ? "development" : "release",
            target);

        return new ReferenceSession(
            singleInstance,
            pluginHost,
            rpc,
            environment,
            mainView,
            previewView,
            mainBinding,
            previewBinding,
            windowState);
    }

    private void ConfigureLaunchRouting(
        NeoApplication application,
        NeoWindow mainWindow)
    {
        application.LaunchReceived += async launch =>
        {
            mainWindow.Show();
            mainWindow.Activate();
            logger.LogInformation(
                "Received launch reason {Reason} with {ArgumentCount} arguments.",
                launch.Reason,
                launch.Arguments.Count);
            await events.PublishAsync(
                "application-lifecycle",
                $"Received {launch.Reason} launch routing.");
        };
    }

    private async ValueTask ConfigureNativeMenuAsync(
        NeoApplication application,
        NeoDesktopServices desktop,
        NeoWindow previewWindow,
        CancellationToken cancellationToken)
    {
        desktop.Menus.Commands.Register(
            "tour.say-hello",
            token => events.PublishAsync(
                "native-menu",
                "A native menu command reached managed code.",
                token));
        desktop.Menus.Commands.Register(
            "tour.show-preview",
            async token =>
            {
                await application.Dispatcher.InvokeAsync(() =>
                {
                    previewWindow.Show();
                    previewWindow.Activate();
                }, token);
                await events.PublishAsync(
                    "native-menu",
                    "Opened the differently authorized preview view.",
                    token);
            });

        var menu = new[]
        {
            NeoMenuItem.Command(
                "hello",
                "Send managed greeting",
                "tour.say-hello",
                "Ctrl+Shift+H"),
            NeoMenuItem.Command(
                "preview",
                "Open restricted preview",
                "tour.show-preview",
                "Ctrl+Shift+P"),
            NeoMenuItem.Separator("edit-separator"),
            NeoMenuItem.RoleItem("copy", NeoMenuRole.Copy, "Copy"),
            NeoMenuItem.RoleItem("paste", NeoMenuRole.Paste, "Paste"),
        };
        await desktop.Menus.SetMenuAsync(
            "window:main",
            menu,
            cancellationToken);
    }

    private async ValueTask ConfirmUnsavedCloseAsync(
        NeoWindowCloseRequest request,
        NeoAstra.NeoAstra mainView)
    {
        if (!state.HasUnsavedChanges || !request.CanCancel)
        {
            return;
        }

        try
        {
            var answer = await mainView.EvaluateScriptAsync(
                "globalThis.confirm('Discard the unsaved feature-tour changes?')",
                request.DeadlineToken);
            if (!string.Equals(answer, "true", StringComparison.OrdinalIgnoreCase))
            {
                request.Cancel();
                return;
            }

            state.SetUnsavedChanges(false);
        }
        catch
        {
            request.Cancel();
        }
    }

    private static NeoDesktopRendererOptions CreateRendererOptions(string assetRoot) => new()
    {
        FileRoots = new Dictionary<string, string>
        {
            ["assets"] = assetRoot,
        },
        AllowedMenuCommands = new HashSet<string>(StringComparer.Ordinal)
        {
            "tour.say-hello",
            "tour.show-preview",
        },
        AllowedTrayIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "feature-tour",
        },
        AllowedGlobalShortcuts = new HashSet<string>(StringComparer.Ordinal)
        {
            "Ctrl+Shift+R",
        },
        AllowedSafeStorageKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "feature-tour-secret",
        },
    };

    internal async ValueTask StopAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null)
        {
            return;
        }

        logger.LogInformation("Stopping {ApplicationName}.", DisplayName);
        await session.DisposeAsync();
    }

    private sealed class ReferenceSession(
        NeoSingleInstance singleInstance,
        NeoPluginHost pluginHost,
        NeoRpcHost rpc,
        NeoEnvironment environment,
        NeoAstra.NeoAstra mainView,
        NeoAstra.NeoAstra previewView,
        NeoRpcViewBinding mainBinding,
        NeoRpcViewBinding previewBinding,
        NeoWindowStateController windowState) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await windowState.DisposeAsync();
            await previewBinding.DisposeAsync();
            await mainBinding.DisposeAsync();
            await previewView.DisposeAsync();
            await mainView.DisposeAsync();
            await environment.DisposeAsync();
            await rpc.DisposeAsync();
            await pluginHost.DisposeAsync();
            await singleInstance.DisposeAsync();
        }
    }
}
