// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.Json;
using NeoAstra.Rpc;
using NeoExternalUrlScope = NeoAstra.Desktop.Opener.NeoUrlScope;

namespace NeoAstra;

/// <summary>Runs a conventional secure one-window NeoAstra application.</summary>
public static class NeoApp
{
    private static Task<(NeoAppBuilder Builder, NeoApplication Application)>? s_attached;

    /// <summary>Runs an application until its main window closes.</summary>
    /// <param name="args">The process arguments reserved for application launch handling.</param>
    /// <param name="configure">Configures the application and its generated RPC services.</param>
    /// <returns>The process exit code.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static int Run(string[] args, Action<NeoAppBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new NeoAppBuilder();
        configure(builder);
        try
        {
            return NeoApplication.Run(
                new NeoApplicationOptions
                {
                    ApplicationName = builder.Title,
                    ShutdownMode = NeoApplicationShutdownMode.OnMainWindowClosed,
                },
                application => builder.StartAsync(application, CancellationToken.None));
        }
        finally
        {
            builder.StopAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Shows a mobile application in a new main window, attached to the run loop of the platform.</summary>
    /// <param name="configure">Configures the application and its generated RPC services; only the first call runs it.</param>
    /// <returns>A task that completes once the main window shows the application.</returns>
    /// <remarks>
    /// iOS and Android own the run loop of their main thread, so a mobile application attaches to it instead of running
    /// one. Call this method on the main thread once a window scene is connected on iOS, or in <c>OnCreate</c> of an activity
    /// on Android. The first call attaches NeoAstra, runs <paramref name="configure"/>, and shows the main window; a later
    /// call, such as for a new activity after the user closed the previous one, shows the same application in a new main
    /// window. The application lives as long as the process: a closed main window releases only its own view.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="PlatformNotSupportedException">The operating system is not iOS or Android; a desktop application calls <see cref="Run"/>.</exception>
    public static async Task AttachAsync(Action<NeoAppBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (!OperatingSystem.IsIOS() && !OperatingSystem.IsAndroid())
            throw new PlatformNotSupportedException("Only an iOS or Android application attaches to the run loop of its platform; a desktop application calls NeoApp.Run.");
        if (s_attached is { } attached)
        {
            var (builder, application) = await attached.ConfigureAwait(true);
            await builder.ShowMainWindowAsync(application, CancellationToken.None).ConfigureAwait(true);
            return;
        }
        if (OperatingSystem.IsIOS()) global::NeoAstra.Desktop.Notifications.IosNotifications.CatchLaunchResponses();
        s_attached = StartAttachedAsync(configure);
        try { await s_attached.ConfigureAwait(true); }
        catch { s_attached = null; throw; }
    }

    private static async Task<(NeoAppBuilder, NeoApplication)> StartAttachedAsync(Action<NeoAppBuilder> configure)
    {
        var builder = new NeoAppBuilder();
        configure(builder);
        var application = NeoApplication.AttachToCurrentThread(new NeoApplicationOptions
        {
            ApplicationName = builder.Title,
            ShutdownMode = NeoApplicationShutdownMode.Explicit,
        });
        try
        {
            builder.ValidateConfiguration();
            await builder.PrepareAsync(application, CancellationToken.None).ConfigureAwait(true);
            await builder.ShowMainWindowAsync(application, CancellationToken.None).ConfigureAwait(true);
        }
        catch
        {
            await builder.StopAsync().ConfigureAwait(true);
            await application.DisposeAsync().ConfigureAwait(true);
            throw;
        }
        // An application that attaches to a run loop of its own reports when its startup is complete.
        application.NotifyReady();
        return (builder, application);
    }
}

/// <summary>Configures a conventional secure one-window NeoAstra application.</summary>
public sealed class NeoAppBuilder
{
    private readonly HashSet<string> _mainViewPermissions = new(StringComparer.Ordinal);
    private IReadOnlyList<NeoPermissionDeclaration>? _permissionDeclarations;
    private Action<NeoRpcBuilder>? _configureRpc;
    private Action<NeoApplication, NeoWindow>? _configureMainWindow;
    private string? _contractHash;
    private NeoExternalUrlScope? _externalLinkScope;
    private Action<NeoExternalOpenCompletedEventArgs>? _externalLinkCompleted;
    private Func<NeoFileChooserRequest, ValueTask<NeoFileChooserDecision>>? _fileChooser;
    private Session? _session;
    private Uri? _target;
    private string? _assetsDirectory;

    /// <summary>Gets or sets the main window title.</summary>
    public string Title { get; set; } = AppDomain.CurrentDomain.FriendlyName;

    /// <summary>Gets or sets the main window width in device-independent pixels.</summary>
    public int Width { get; set; } = 960;

    /// <summary>Gets or sets the main window height in device-independent pixels.</summary>
    public int Height { get; set; } = 640;

    /// <summary>Gets or sets the production asset directory.</summary>
    /// <remarks>
    /// The default is the <c>assets</c> folder next to the application, which is inside the app bundle on iOS. Android keeps
    /// the assets in the package, so the default there is the folder into which NeoAstra copies them once per installed
    /// version.
    /// </remarks>
    public string AssetsDirectory
    {
        get => _assetsDirectory ??= DefaultAssetsDirectory();
        set => _assetsDirectory = value;
    }

    /// <summary>Gets or sets which engine-provided browser features the main view keeps.</summary>
    public NeoBrowserFeatures BrowserFeatures { get; set; } = new();

    /// <summary>Registers backend configuration for the hidden main window and its application.</summary>
    /// <param name="configure">The synchronous callback used to attach lifecycle handlers or configure window properties.</param>
    /// <returns>This builder.</returns>
    /// <remarks>The callback runs once during startup on the native UI thread, after assigning <see cref="NeoApplication.MainWindow"/> and before RPC/browser creation, showing, or navigation; a mobile application runs it again for each new main window, before its view is created. It does not change bridge or navigation policy. Attach asynchronous close/quit/launch handlers here rather than using an async-void configuration callback. A thrown exception fails startup and propagates from <see cref="NeoApp.Run"/> after application teardown.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Main-window configuration was already registered.</exception>
    public NeoAppBuilder ConfigureMainWindow(Action<NeoApplication, NeoWindow> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (_configureMainWindow is not null) throw new InvalidOperationException("Main-window configuration can be registered only once.");
        _configureMainWindow = configure;
        return this;
    }

    /// <summary>Opts the main view into capability authorization and grants exact generated application permissions.</summary>
    /// <param name="permissions">Exact generated permission identifiers.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">A permission is empty or malformed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="permissions"/> is <see langword="null"/>.</exception>
    public NeoAppBuilder GrantMainView(params string[] permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        foreach (var permission in permissions)
        {
            if (string.IsNullOrWhiteSpace(permission) || permission.Length > 192)
                throw new ArgumentException("Permission identifiers must be non-empty and bounded.", nameof(permissions));
            _mainViewPermissions.Add(permission);
        }
        return this;
    }

    /// <summary>Allows user-initiated links from exact HTTP(S) origins to open in the system browser.</summary>
    /// <param name="allowedOrigins">Exact HTTP(S) origins, such as <c>https://neoastra.dev</c>.</param>
    /// <returns>This builder.</returns>
    /// <remarks>External links are blocked unless this method is called. Redirects and other non-user-initiated navigation remain blocked.</remarks>
    /// <exception cref="ArgumentException">The collection is empty, contains duplicates, or contains a malformed or non-HTTP(S) origin.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="allowedOrigins"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">External-link policy was already configured.</exception>
    public NeoAppBuilder OpenExternalLinksInSystemBrowser(params string[] allowedOrigins)
    {
        ArgumentNullException.ThrowIfNull(allowedOrigins);
        if (_externalLinkScope is not null) throw new InvalidOperationException("External-link policy can be configured only once.");
        foreach (var origin in allowedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new ArgumentException("External-link origins must use HTTP or HTTPS.", nameof(allowedOrigins));
        }
        _externalLinkScope = new NeoExternalUrlScope(allowedOrigins);
        return this;
    }

    /// <summary>Allows user-initiated links to any web address to open in the system browser.</summary>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// For an application whose content links anywhere, such as a chat or a document viewer. It is
    /// <see cref="OpenExternalLinksInSystemBrowser(NeoExternalUrlScope)"/> with
    /// <see cref="NeoExternalUrlScope.AnyWebAddress"/>: an absolute HTTP or HTTPS address with a host, without
    /// credentials, control or white-space characters, and of at most 4096 characters.
    /// </remarks>
    /// <exception cref="InvalidOperationException">External-link policy was already configured.</exception>
    public NeoAppBuilder OpenAnyWebLinkInSystemBrowser() => OpenExternalLinksInSystemBrowser(NeoExternalUrlScope.AnyWebAddress);

    /// <summary>Allows user-initiated HTTP(S) links that a URL scope accepts to open in the system browser.</summary>
    /// <param name="scope">
    /// The scope that decides which addresses open, such as <see cref="NeoExternalUrlScope.AnyWebAddress"/> for an
    /// application whose content links anywhere.
    /// </param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// External links are blocked unless one of the overloads of this method is called. Redirects and other
    /// non-user-initiated navigation remain blocked, and so do the addresses of the scope that are not HTTP(S),
    /// such as <c>mailto:</c>.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">External-link policy was already configured.</exception>
    public NeoAppBuilder OpenExternalLinksInSystemBrowser(NeoExternalUrlScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (_externalLinkScope is not null) throw new InvalidOperationException("External-link policy can be configured only once.");
        _externalLinkScope = scope;
        return this;
    }

    /// <summary>Registers a callback that is told what became of each link sent to the system browser.</summary>
    /// <param name="handler">The callback, which runs on the native UI thread.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// An application uses it to tell its user that a link could not be opened: the status is
    /// <see cref="NeoExternalOpenStatus.Failed"/> when the system did not open the address, and
    /// <see cref="NeoExternalOpenStatus.Refused"/> when the native library did not take it for a web address.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A callback was already registered.</exception>
    public NeoAppBuilder OnExternalLinkOpenCompleted(Action<NeoExternalOpenCompletedEventArgs> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (_externalLinkCompleted is not null) throw new InvalidOperationException("The external-link callback can be registered only once.");
        _externalLinkCompleted = handler;
        return this;
    }

    /// <summary>Answers the file inputs of the main view with the picker of the system.</summary>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// Without a file-chooser answer the view cancels every file input. On Android the default answer shows the picker of the
    /// system, and the camera for an image input with the <c>capture</c> attribute; WKWebView on iOS shows its own menu in any
    /// case. This is <see cref="OnFileChooser"/> with a handler that answers <see cref="NeoFileChooserDecision.Default"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A file-chooser answer was already registered.</exception>
    public NeoAppBuilder UseSystemFileChooser() => OnFileChooser(static _ => ValueTask.FromResult(NeoFileChooserDecision.Default));

    /// <summary>Registers how the main view answers its file inputs.</summary>
    /// <param name="handler">The callback, which runs on the native UI thread and decides each request.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// The handler can cancel, select local paths, or answer <see cref="NeoFileChooserDecision.Default"/> to let the system
    /// show its picker. Without a handler the view cancels every file input.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A file-chooser answer was already registered.</exception>
    public NeoAppBuilder OnFileChooser(Func<NeoFileChooserRequest, ValueTask<NeoFileChooserDecision>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (_fileChooser is not null) throw new InvalidOperationException("The file-chooser answer can be registered only once.");
        _fileChooser = handler;
        return this;
    }

    internal Func<NeoFileChooserRequest, ValueTask<NeoFileChooserDecision>>? FileChooserHandler => _fileChooser;

    /// <summary>Connects generated contract metadata to the conventional application host.</summary>
    /// <param name="contractHash">The deterministic generated contract hash.</param>
    /// <param name="permissions">Generated application permission declarations.</param>
    /// <param name="configure">Registers generated RPC services and events.</param>
    /// <returns>This builder.</returns>
    /// <remarks>This method supports generated code. Applications normally call the generated <c>UseRpc</c> extension.</remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">RPC was already configured.</exception>
    public NeoAppBuilder ConfigureGeneratedRpc(
        string contractHash,
        IReadOnlyList<NeoPermissionDeclaration> permissions,
        Action<NeoRpcBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(contractHash);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(configure);
        if (_configureRpc is not null) throw new InvalidOperationException("Generated RPC can be configured only once.");
        _contractHash = contractHash;
        _permissionDeclarations = permissions;
        _configureRpc = configure;
        return this;
    }

    internal async ValueTask StartAsync(NeoApplication application, CancellationToken cancellationToken)
    {
        ValidateConfiguration();
        var window = application.CreateWindow(new NeoWindowOptions
        {
            Label = "main",
            Title = Title,
            Width = Width,
            Height = Height,
            IsVisible = false,
        });
        application.MainWindow = window;
        _configureMainWindow?.Invoke(application, window);
        await PrepareAsync(application, cancellationToken).ConfigureAwait(true);
        await OpenViewAsync(window, cancellationToken).ConfigureAwait(true);
    }

    // Creates what every main window of the application shares: the RPC host and the browser environment.
    internal async ValueTask PrepareAsync(NeoApplication application, CancellationToken cancellationToken)
    {
        NeoRpcHost? rpc = null;
        NeoEnvironment? environment = null;
        try
        {
            var developmentUrl = DevelopmentUrl();
            var developmentOrigin = developmentUrl is null ? null : ValidateDevelopmentUrl(developmentUrl);
            var release = developmentOrigin is null;
            var profile = release ? NeoSecurityProfile.ProductionLocalApp : NeoSecurityProfile.DevelopmentLocalApp;
            var manifest = _mainViewPermissions.Count == 0 ? null : CreateCapabilityManifest(profile, release);
            var rpcBuilder = new NeoRpcBuilder(new NeoRpcOptions
            {
                ContractHash = _contractHash!,
                CapabilityManifest = manifest,
                AuthorizationService = manifest is null ? null : new NeoCapabilityAuthorizationService(manifest),
                SecurityProfile = profile,
                Release = release,
                DevelopmentOrigin = developmentOrigin,
            });
            _configureRpc!(rpcBuilder);
            rpc = rpcBuilder.Build();

            // Native window and browser setup must resume on the application's UI dispatcher.
            if (developmentOrigin is not null)
            {
                _target = new Uri(developmentOrigin, "/");
                environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions(), cancellationToken).ConfigureAwait(true);
            }
            else
            {
                var assetRoot = Path.GetFullPath(AssetsDirectory);
                var assetManifest = NeoAssetManifest.Load(Path.Combine(assetRoot, "neoastra-assets.json"));
                environment = await application.CreateEnvironmentAsync(
                    new NeoEnvironmentOptions
                    {
                        CustomSchemes = [NeoCustomScheme.Application("app", new NeoManifestResourceProvider(assetRoot, assetManifest))],
                    },
                    cancellationToken).ConfigureAwait(true);
                _target = new Uri("app://neoastra/index.html");
            }
            _session = new Session(rpc, environment);
            rpc = null;
            environment = null;
        }
        finally
        {
            if (environment is not null) await environment.DisposeAsync().ConfigureAwait(true);
            if (rpc is not null) await rpc.DisposeAsync().ConfigureAwait(true);
        }
    }

    // A mobile application shows each scene or activity in a main window of its own; a closed one releases its view.
    internal async ValueTask ShowMainWindowAsync(NeoApplication application, CancellationToken cancellationToken)
    {
        var window = application.CreateWindow(new NeoWindowOptions
        {
            Label = application.Windows.Any(static value => value.Label == "main") ? null : "main",
            Title = Title,
            Width = Width,
            Height = Height,
            IsVisible = false,
        });
        application.MainWindow = window;
        _configureMainWindow?.Invoke(application, window);
        await OpenViewAsync(window, cancellationToken).ConfigureAwait(true);
    }

    private async ValueTask OpenViewAsync(NeoWindow window, CancellationToken cancellationToken)
    {
        var session = _session ?? throw new InvalidOperationException("The application is not prepared.");
        var target = _target!;
        global::NeoAstra.NeoAstra? view = null;
        NeoRpcViewBinding? binding = null;
        try
        {
            var viewOptions = CreateLocalViewOptions(target);
            window.Show();
            window.Activate();
            viewOptions.BrowserFeatures = BrowserFeatures;
            view = await session.Environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), viewOptions, cancellationToken).ConfigureAwait(true);
            var trustedOrigin = new Uri(target.GetLeftPart(UriPartial.Authority));
            view.NavigationRequested = request => ValueTask.FromResult(new NeoNavigationDecision(DecideNavigation(request.Uri, request.IsMainFrame, request.IsUserInitiated, trustedOrigin)));
            view.NewWindowRequested = request => ValueTask.FromResult(new NeoNewWindowDecision(DecideNewWindow(request.TargetUri, request.IsUserInitiated)));
            if (_externalLinkCompleted is { } externalLinkCompleted) view.ExternalOpenCompleted += (_, outcome) => externalLinkCompleted(outcome);
            if (_fileChooser is { } fileChooser) view.FileChooserRequested = fileChooser;
            binding = NeoRpcViewBinding.Bind(session.Rpc, view);
            await view.NavigateAsync(target, cancellationToken).ConfigureAwait(true);
            session.Add(window, view, binding);
            view = null;
            binding = null;
        }
        finally
        {
            if (binding is not null) await binding.DisposeAsync().ConfigureAwait(true);
            if (view is not null) await view.DisposeAsync().ConfigureAwait(true);
        }
    }

    internal async ValueTask StopAsync()
    {
        if (_session is { } session)
        {
            _session = null;
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(Title)) throw new InvalidOperationException("The application title must not be empty.");
        if (Width is < 320 or > 16384 || Height is < 240 or > 16384) throw new InvalidOperationException("The main window size is outside the supported bounds.");
        if (_configureRpc is null || _permissionDeclarations is null || string.IsNullOrEmpty(_contractHash))
            throw new InvalidOperationException("Register generated RPC services by calling the generated UseRpc extension.");

        var declarations = _permissionDeclarations.ToDictionary(static permission => permission.Id, StringComparer.Ordinal);
        foreach (var permission in _mainViewPermissions)
        {
            if (!declarations.TryGetValue(permission, out var declaration))
                throw new InvalidOperationException($"Permission '{permission}' is not declared by the generated RPC contract.");
            if (declaration.ScopeRequired || declaration.ScopeFamily != NeoScopeFamily.None)
                throw new InvalidOperationException($"Permission '{permission}' requires an explicit scoped capability manifest.");
        }
    }

    internal NeoCapabilityManifest CreateCapabilityManifest(NeoSecurityProfile profile, bool release)
    {
        var catalog = new NeoPermissionCatalogBuilder();
        foreach (var declaration in _permissionDeclarations!) catalog.Add(declaration);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("$schema", "neoastra-capabilities-v1.schema.json");
            writer.WriteNumber("version", 1);
            writer.WriteStartArray("capabilities");
            writer.WriteStartObject();
            writer.WriteString("id", "main");
            writer.WriteStartArray("views");
            writer.WriteStringValue("main");
            writer.WriteEndArray();
            writer.WriteStartArray("permissions");
            foreach (var permission in _mainViewPermissions.Order(StringComparer.Ordinal)) writer.WriteStringValue(permission);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return NeoCapabilityManifest.Resolve(
            stream.ToArray(),
            catalog.Build(),
            new NeoCapabilityResolutionOptions
            {
                Platform = CurrentPlatform(),
                Release = release,
                Profile = profile,
            });
    }

    // The development server of `dotnet neoastra dev`: an environment variable on the desktop and in the iOS simulator, which
    // inherits it from its launch, and on Android a file that adb gives a debuggable application.
    private static string? DevelopmentUrl()
    {
        var value = Environment.GetEnvironmentVariable("NEOASTRA_DEV_URL");
        if (value is not null || !OperatingSystem.IsAndroid()) return value;
        using var response = Desktop.AndroidPlatform.Call("development");
        return Desktop.AndroidPlatform.Status(response) == Desktop.NeoDesktopStatus.Success ? Desktop.AndroidPlatform.Text(response, "url") : null;
    }

    private static string DefaultAssetsDirectory()
    {
        // An iOS device installs an application under /var, a link to /private/var, and the resource provider refuses a root
        // reached through a link.
        if (OperatingSystem.IsIOS()) return ResolveLinks(Path.Combine(AppContext.BaseDirectory, "assets"));
        if (!OperatingSystem.IsAndroid()) return Path.Combine(AppContext.BaseDirectory, "assets");
        // The build packages the assets of the frontend in the neoastra folder of the APK assets.
        using var response = Desktop.AndroidPlatform.Call("assets", ("source", "neoastra"));
        return Desktop.AndroidPlatform.Status(response) == Desktop.NeoDesktopStatus.Success && Desktop.AndroidPlatform.Text(response, "path") is { } path
            ? path
            : throw new InvalidOperationException("The assets of the application are missing from its package.");
    }

    // The same path with every link among its directories replaced by its target, whose own links are resolved too.
    internal static string ResolveLinks(string path) => ResolveLinks(path, 0);

    private static string ResolveLinks(string path, int depth)
    {
        if (depth > 32) throw new IOException("The asset directory is reached through too many links.");
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (NeoFileLinks.IsLink(current) && Directory.ResolveLinkTarget(current, returnFinalTarget: true) is { } target)
                current = ResolveLinks(target.FullName, depth + 1);
        }
        return current;
    }

    internal static NeoCapabilityPlatform CurrentPlatform() =>
        OperatingSystem.IsWindows() ? NeoCapabilityPlatform.Windows
        : OperatingSystem.IsMacOS() ? NeoCapabilityPlatform.MacOS
        : OperatingSystem.IsIOS() ? NeoCapabilityPlatform.IOS
        : OperatingSystem.IsAndroid() ? NeoCapabilityPlatform.Android
        : NeoCapabilityPlatform.Linux;

    internal NeoDecisionAction DecideNavigation(Uri target, bool isMainFrame, bool isUserInitiated, Uri trustedOrigin)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(trustedOrigin);
        if (HasSameOrigin(target, trustedOrigin)) return NeoDecisionAction.Allow;
        return isMainFrame && IsAllowedExternalLink(target, isUserInitiated) ? NeoDecisionAction.OpenExternal : NeoDecisionAction.Cancel;
    }

    internal NeoDecisionAction DecideNewWindow(Uri? target, bool isUserInitiated) =>
        target is not null && IsAllowedExternalLink(target, isUserInitiated) ? NeoDecisionAction.OpenExternal : NeoDecisionAction.Cancel;

    internal static NeoAstraOptions CreateLocalViewOptions(Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var trustEntireView = OperatingSystem.IsLinux();
        return new NeoAstraOptions
        {
            ViewLabel = "main",
            BridgePolicy = trustEntireView ? NeoBridgePolicy.TrustEntireView : NeoBridgePolicy.TrustedOrigins,
            BridgeOrigins = trustEntireView ? [] : [target.GetLeftPart(UriPartial.Authority)],
        };
    }

    // The native opener is for web addresses: a scope that also lists mailto: does not send those to it.
    private bool IsAllowedExternalLink(Uri target, bool isUserInitiated) =>
        isUserInitiated && target.IsAbsoluteUri && target.Scheme is ("http" or "https") && _externalLinkScope?.TryAuthorize(target, out _) == true;

    private static bool HasSameOrigin(Uri target, Uri trustedOrigin) =>
        target.IsAbsoluteUri && string.IsNullOrEmpty(target.UserInfo) &&
        string.Equals(target.Scheme, trustedOrigin.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(target.IdnHost, trustedOrigin.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        target.Port == trustedOrigin.Port;

    internal static Uri ValidateDevelopmentUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || !uri.IsDefaultPort && uri.Port is < 1 or > 65535)
            throw new InvalidOperationException("NEOASTRA_DEV_URL must be an absolute HTTP(S) loopback URL.");
        if (!string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal) && !string.Equals(uri.Host, "[::1]", StringComparison.Ordinal) && !string.Equals(uri.Host, "::1", StringComparison.Ordinal))
            throw new InvalidOperationException("NEOASTRA_DEV_URL must use the exact 127.0.0.1 or ::1 loopback address.");
        return uri;
    }

    // The view and its transport belong to the UI thread, while the teardown before it may end on the thread pool. Once
    // the application loop has ended, no thread runs them any more, and the view is disposed where this runs.
    internal static async ValueTask DisposeOnDispatcherAsync(NeoDispatcher dispatcher, IAsyncDisposable value)
    {
        if (dispatcher.CheckAccess())
        {
            await value.DisposeAsync().ConfigureAwait(false);
            return;
        }

        Task disposal;
        try { disposal = await dispatcher.InvokeAsync(() => value.DisposeAsync().AsTask()).ConfigureAwait(false); }
        catch (Exception exception) when (exception is ObjectDisposedException or OperationCanceledException) { disposal = value.DisposeAsync().AsTask(); }
        await disposal.ConfigureAwait(false);
    }

    private sealed class Session(NeoRpcHost rpc, NeoEnvironment environment) : IAsyncDisposable
    {
        private readonly List<(global::NeoAstra.NeoAstra View, NeoRpcViewBinding Binding)> _views = [];

        public NeoRpcHost Rpc { get; } = rpc;

        public NeoEnvironment Environment { get; } = environment;

        public void Add(NeoWindow window, global::NeoAstra.NeoAstra view, NeoRpcViewBinding binding)
        {
            var entry = (view, binding);
            _views.Add(entry);
            window.Closed += async (_, _) =>
            {
                if (!_views.Remove(entry)) return;
                await binding.DisposeAsync().ConfigureAwait(false);
                await DisposeOnDispatcherAsync(Environment.Application.Dispatcher, view).ConfigureAwait(false);
            };
        }

        public async ValueTask DisposeAsync()
        {
            var views = _views.ToArray();
            _views.Clear();
            foreach (var (view, binding) in views)
            {
                await binding.DisposeAsync().ConfigureAwait(false);
                await DisposeOnDispatcherAsync(Environment.Application.Dispatcher, view).ConfigureAwait(false);
            }
            await Environment.DisposeAsync().ConfigureAwait(false);
            await Rpc.DisposeAsync().ConfigureAwait(false);
        }
    }
}
