// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.Json;
using NeoAstra.Rpc;

namespace NeoAstra;

/// <summary>Runs a conventional secure one-window NeoAstra application.</summary>
public static class NeoApp
{
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
}

/// <summary>Configures a conventional secure one-window NeoAstra application.</summary>
public sealed class NeoAppBuilder
{
    private readonly HashSet<string> _mainViewPermissions = new(StringComparer.Ordinal);
    private IReadOnlyList<NeoPermissionDeclaration>? _permissionDeclarations;
    private Action<NeoRpcBuilder>? _configureRpc;
    private string? _contractHash;
    private Session? _session;

    /// <summary>Gets or sets the main window title.</summary>
    public string Title { get; set; } = AppDomain.CurrentDomain.FriendlyName;

    /// <summary>Gets or sets the main window width in device-independent pixels.</summary>
    public int Width { get; set; } = 960;

    /// <summary>Gets or sets the main window height in device-independent pixels.</summary>
    public int Height { get; set; } = 640;

    /// <summary>Gets or sets the production asset directory.</summary>
    public string AssetsDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "assets");

    /// <summary>Explicitly grants generated application permissions to the main view.</summary>
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

        NeoRpcHost? rpc = null;
        NeoEnvironment? environment = null;
        global::NeoAstra.NeoAstra? view = null;
        NeoRpcViewBinding? binding = null;
        try
        {
            var developmentUrl = Environment.GetEnvironmentVariable("NEOASTRA_DEV_URL");
            var developmentOrigin = developmentUrl is null ? null : ValidateDevelopmentUrl(developmentUrl);
            var release = developmentOrigin is null;
            var profile = release ? NeoSecurityProfile.ProductionLocalApp : NeoSecurityProfile.DevelopmentLocalApp;
            var manifest = CreateCapabilityManifest(profile, release);
            var rpcBuilder = new NeoRpcBuilder(new NeoRpcOptions
            {
                ContractHash = _contractHash!,
                CapabilityManifest = manifest,
                AuthorizationService = new NeoCapabilityAuthorizationService(manifest),
                SecurityProfile = profile,
                Release = release,
                DevelopmentOrigin = developmentOrigin,
            });
            _configureRpc!(rpcBuilder);
            rpc = rpcBuilder.Build();

            Uri target;
            NeoAstraOptions viewOptions;
            if (developmentOrigin is not null)
            {
                target = new Uri(developmentOrigin, "/");
                var trustEntireView = OperatingSystem.IsLinux();
                viewOptions = new NeoAstraOptions
                {
                    ViewLabel = "main",
                    BridgePolicy = trustEntireView ? NeoBridgePolicy.TrustEntireView : NeoBridgePolicy.TrustedOrigins,
                    BridgeOrigins = trustEntireView ? [] : [developmentOrigin.GetLeftPart(UriPartial.Authority)],
                };
                environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions(), cancellationToken).ConfigureAwait(false);
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
                    cancellationToken).ConfigureAwait(false);
                target = new Uri("app://neoastra/index.html");
                viewOptions = new NeoAstraOptions
                {
                    ViewLabel = "main",
                    BridgePolicy = NeoBridgePolicy.TrustEntireView,
                };
            }

            window.Show();
            window.Activate();
            view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), viewOptions, cancellationToken).ConfigureAwait(false);
            binding = NeoRpcViewBinding.Bind(rpc, view);
            await view.NavigateAsync(target, cancellationToken).ConfigureAwait(false);
            _session = new Session(rpc, environment, view, binding);
            rpc = null;
            environment = null;
            view = null;
            binding = null;
        }
        finally
        {
            if (binding is not null) await binding.DisposeAsync().ConfigureAwait(false);
            if (view is not null) await view.DisposeAsync().ConfigureAwait(false);
            if (environment is not null) await environment.DisposeAsync().ConfigureAwait(false);
            if (rpc is not null) await rpc.DisposeAsync().ConfigureAwait(false);
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
        if (declarations.Count != 0 && _mainViewPermissions.Count == 0)
            throw new InvalidOperationException("Generated RPC permissions remain denied until GrantMainView is called explicitly.");
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
                Platform = OperatingSystem.IsWindows() ? NeoCapabilityPlatform.Windows : OperatingSystem.IsMacOS() ? NeoCapabilityPlatform.MacOS : NeoCapabilityPlatform.Linux,
                Release = release,
                Profile = profile,
            });
    }

    private static Uri ValidateDevelopmentUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort && uri.Port is < 1 or > 65535)
            throw new InvalidOperationException("NEOASTRA_DEV_URL must be an absolute HTTP(S) loopback URL.");
        if (!string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal) && !string.Equals(uri.Host, "[::1]", StringComparison.Ordinal) && !string.Equals(uri.Host, "::1", StringComparison.Ordinal))
            throw new InvalidOperationException("NEOASTRA_DEV_URL must use the exact 127.0.0.1 or ::1 loopback address.");
        return uri;
    }

    private sealed class Session(NeoRpcHost rpc, NeoEnvironment environment, global::NeoAstra.NeoAstra view, NeoRpcViewBinding binding) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await binding.DisposeAsync().ConfigureAwait(false);
            await view.DisposeAsync().ConfigureAwait(false);
            await environment.DisposeAsync().ConfigureAwait(false);
            await rpc.DisposeAsync().ConfigureAwait(false);
        }
    }
}
