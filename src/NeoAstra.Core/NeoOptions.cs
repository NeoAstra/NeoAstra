// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra;

/// <summary>Configures a <see cref="NeoApplication"/>.</summary>
public sealed class NeoApplicationOptions
{
    /// <summary>Gets or sets the application name passed to the native backend.</summary>
    public string ApplicationName { get; set; } = "NeoAstra Application";

    /// <summary>Gets or sets the initial shutdown policy.</summary>
    public NeoApplicationShutdownMode ShutdownMode { get; set; } = NeoApplicationShutdownMode.OnLastWindowClosed;

    /// <summary>Gets or sets the maximum number of queued dispatcher callbacks. The value must be positive.</summary>
    public uint MaximumPendingDispatches { get; set; } = 65_536;

    /// <summary>Gets or sets the maximum number of launch events retained before <see cref="NeoApplicationState.Ready"/>.</summary>
    public int MaximumPendingLaunchEvents { get; set; } = 128;

    /// <summary>Gets or sets whether initial process arguments and working directory are queued as one authoritative initial launch event.</summary>
    public bool QueueInitialLaunchEvent { get; set; } = true;

    /// <summary>Gets or sets the callback that receives native diagnostic log messages.</summary>
    /// <remarks>The callback can run on any native thread. Exceptions are contained and ignored.</remarks>
    public Action<NeoLogMessage>? LogCallback { get; set; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApplicationName))
        {
            throw new ArgumentException("The application name must not be empty.", nameof(ApplicationName));
        }

        if (!Enum.IsDefined(ShutdownMode))
        {
            throw new ArgumentOutOfRangeException(nameof(ShutdownMode));
        }

        if (MaximumPendingDispatches == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumPendingDispatches), "The pending-dispatch limit must be positive.");
        }

        if (MaximumPendingLaunchEvents is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumPendingLaunchEvents), "The launch-event limit must be between 1 and 4096.");
        }
    }
}

/// <summary>Configures a browser environment.</summary>
public sealed class NeoEnvironmentOptions
{
    /// <summary>Gets or sets the persistent browser-data root.</summary>
    public string? UserDataRoot { get; set; }

    /// <summary>Gets or sets an explicit browser runtime directory.</summary>
    public string? BrowserRuntimePath { get; set; }

    /// <summary>Gets or sets backend-specific browser command-line arguments.</summary>
    public string? BrowserArguments { get; set; }

    /// <summary>Gets or sets the preferred languages in priority order.</summary>
    public IReadOnlyList<string> PreferredLanguages { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets whether the environment uses private storage by default.</summary>
    public bool IsPrivate { get; set; }

    /// <summary>Gets or sets custom URI schemes registered before environment creation.</summary>
    public IReadOnlyList<NeoCustomScheme> CustomSchemes { get; set; } = Array.Empty<NeoCustomScheme>();

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(PreferredLanguages);
        ArgumentNullException.ThrowIfNull(CustomSchemes);

        foreach (var language in PreferredLanguages)
        {
            if (string.IsNullOrWhiteSpace(language) || language.Contains(','))
            {
                throw new ArgumentException("Preferred languages must be non-empty language tags without commas.", nameof(PreferredLanguages));
            }
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scheme in CustomSchemes)
        {
            ArgumentNullException.ThrowIfNull(scheme);
            scheme.Validate();
            if (!names.Add(scheme.Name))
            {
                throw new ArgumentException($"The custom scheme '{scheme.Name}' is registered more than once.", nameof(CustomSchemes));
            }
        }
    }
}

/// <summary>Describes a custom URI scheme registered with an environment.</summary>
public sealed class NeoCustomScheme
{
    private NeoCustomScheme(string name)
    {
        Name = name;
    }

    /// <summary>Gets the lower-case URI scheme name.</summary>
    public string Name { get; }

    /// <summary>Gets or sets whether URIs use an authority component.</summary>
    public bool HasAuthority { get; set; } = true;

    /// <summary>Gets or sets whether the browser treats the scheme as secure.</summary>
    public bool IsSecure { get; set; } = true;

    /// <summary>Gets or sets whether cross-origin requests are enabled.</summary>
    public bool IsCorsEnabled { get; set; }

    /// <summary>Gets or sets whether the scheme serves trusted application content.</summary>
    public bool IsApplicationScheme { get; set; }

    /// <summary>Gets or sets whether service-worker behavior is expected for the scheme.</summary>
    public bool SupportsServiceWorkers { get; set; }

    /// <summary>Gets or sets origins permitted to access the scheme.</summary>
    public IReadOnlyList<string> AllowedOrigins { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the provider that resolves resources for this scheme.</summary>
    public INeoResourceProvider? ResourceProvider { get; set; }

    /// <summary>Creates a custom scheme definition.</summary>
    /// <param name="name">A valid URI scheme name.</param>
    /// <returns>The new definition.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is invalid or identifies a built-in browser scheme.</exception>
    public static NeoCustomScheme Create(string name)
    {
        ValidateName(name);
        return new NeoCustomScheme(name.ToLowerInvariant());
    }

    /// <summary>Creates a secure scheme intended for trusted application content.</summary>
    /// <param name="name">A valid URI scheme name.</param>
    /// <returns>The new application scheme definition.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is invalid or identifies a built-in browser scheme.</exception>
    public static NeoCustomScheme Application(string name) => Create(name).WithApplicationDefaults();

    /// <summary>Creates a secure application scheme backed by a resource provider.</summary>
    /// <param name="name">A valid URI scheme name.</param>
    /// <param name="resourceProvider">The provider used to resolve resources.</param>
    /// <returns>The new application scheme definition.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is invalid or identifies a built-in browser scheme.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="resourceProvider"/> is <see langword="null"/>.</exception>
    public static NeoCustomScheme Application(string name, INeoResourceProvider resourceProvider)
    {
        ArgumentNullException.ThrowIfNull(resourceProvider);
        var scheme = Application(name);
        scheme.ResourceProvider = resourceProvider;
        return scheme;
    }

    private NeoCustomScheme WithApplicationDefaults()
    {
        IsApplicationScheme = true;
        IsSecure = true;
        HasAuthority = true;
        return this;
    }

    internal void Validate()
    {
        ValidateName(Name);
        if (ResourceProvider is null)
        {
            throw new ArgumentException($"The custom scheme '{Name}' requires a resource provider.", nameof(ResourceProvider));
        }
        ArgumentNullException.ThrowIfNull(AllowedOrigins);
        foreach (var origin in AllowedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 ||
                !string.Equals(uri.GetLeftPart(UriPartial.Authority).TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"'{origin}' is not an absolute origin.", nameof(AllowedOrigins));
            }
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !Uri.CheckSchemeName(name))
        {
            throw new ArgumentException("A custom scheme must be a valid URI scheme name.", nameof(name));
        }
        if (name.ToLowerInvariant() is "about" or "blob" or "data" or "file" or "ftp" or "http" or "https" or "javascript" or "ws" or "wss")
        {
            throw new ArgumentException("A custom scheme cannot replace a built-in browser scheme.", nameof(name));
        }
    }
}

/// <summary>Configures a browser profile.</summary>
public sealed class NeoProfileOptions
{
    /// <summary>Gets or sets the backend profile name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets whether browser data is ephemeral.</summary>
    public bool IsEphemeral { get; set; }

    internal void Validate()
    {
        if (Name is { Length: > 128 })
        {
            throw new ArgumentException("The profile name must not exceed 128 characters.", nameof(Name));
        }
    }
}

/// <summary>Configures a NeoAstra-owned top-level window.</summary>
public sealed class NeoWindowOptions
{
    /// <summary>Gets or sets the immutable application-local window label.</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets the owner window.</summary>
    public NeoWindow? Owner { get; set; }

    /// <summary>Gets or sets whether this owned window blocks input to its owner without starting a nested application loop.</summary>
    public bool IsModal { get; set; }

    /// <summary>Gets or sets the initial title.</summary>
    public string Title { get; set; } = "NeoAstra";

    /// <summary>Gets or sets the initial horizontal position.</summary>
    public int X { get; set; }

    /// <summary>Gets or sets the initial vertical position.</summary>
    public int Y { get; set; }

    /// <summary>Gets or sets the initial client width.</summary>
    public int Width { get; set; } = 800;

    /// <summary>Gets or sets the initial client height.</summary>
    public int Height { get; set; } = 600;

    /// <summary>Gets or sets the initial placement policy.</summary>
    public NeoWindowStartupLocation StartupLocation { get; set; } = NeoWindowStartupLocation.Default;

    /// <summary>Gets or sets the minimum client size. An empty value means no managed minimum.</summary>
    public NeoSize MinimumClientSize { get; set; }

    /// <summary>Gets or sets the maximum client size. An empty value means no managed maximum.</summary>
    public NeoSize MaximumClientSize { get; set; }

    /// <summary>Gets or sets whether the window has normal platform decorations.</summary>
    public bool HasDecorations { get; set; } = true;

    /// <summary>Gets or sets whether the user can resize the window.</summary>
    public bool IsResizable { get; set; } = true;

    /// <summary>Gets or sets whether the window is initially visible.</summary>
    public bool IsVisible { get; set; }

    /// <summary>Gets or sets the initial window state.</summary>
    public NeoWindowState State { get; set; }

    /// <summary>Gets or sets whether the window remains above ordinary windows.</summary>
    public bool IsAlwaysOnTop { get; set; }

    /// <summary>Gets or sets whether the window appears in the taskbar or dock.</summary>
    public bool ShowInTaskbar { get; set; } = true;

    /// <summary>Gets or sets the initial background color.</summary>
    public NeoColor BackgroundColor { get; set; } = NeoColor.White;

    internal void Validate(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(Title);
        if (Label is not null && (string.IsNullOrWhiteSpace(Label) || Label.Length > 128 || Label.Any(char.IsControl)))
        {
            throw new ArgumentException("A window label must be non-empty, at most 128 characters, and free of controls.", nameof(Label));
        }
        if (Width <= 0 || Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Width), "Window dimensions must be positive.");
        }

        ValidateSize(MinimumClientSize, nameof(MinimumClientSize));
        ValidateSize(MaximumClientSize, nameof(MaximumClientSize));
        if (MaximumClientSize.Width > 0 && MinimumClientSize.Width > MaximumClientSize.Width ||
            MaximumClientSize.Height > 0 && MinimumClientSize.Height > MaximumClientSize.Height)
        {
            throw new ArgumentException("The minimum client size must not exceed the maximum client size.");
        }

        if (!Enum.IsDefined(State) || !Enum.IsDefined(StartupLocation))
        {
            throw new ArgumentOutOfRangeException(nameof(State));
        }

        if (Owner is not null && !ReferenceEquals(Owner.Application, application))
        {
            throw new ArgumentException("A window owner must belong to the same application.", nameof(Owner));
        }
        if (IsModal && Owner is null) throw new ArgumentException("A modal window requires an explicit owner.", nameof(IsModal));
    }

    private static void ValidateSize(NeoSize value, string parameterName)
    {
        if (value.Width < 0 || value.Height < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Window constraints must not be negative.");
        }
    }
}

/// <summary>Configures a browser view.</summary>
public sealed class NeoAstraOptions
{
    /// <summary>Gets or sets the immutable application-assigned label used to identify this view.</summary>
    /// <remarks>When specified, the label must be unique within the application. A label is required when the bridge is enabled.</remarks>
    public string? ViewLabel { get; set; }

    /// <summary>Gets or sets the profile used by the view.</summary>
    public NeoProfile? Profile { get; set; }

    /// <summary>Gets or sets explicit initial bounds when the host is not filled.</summary>
    public NeoRect Bounds { get; set; } = new(0, 0, 800, 600);

    /// <summary>Gets or sets whether the view automatically fills its parent.</summary>
    public bool FillParent { get; set; } = true;

    /// <summary>Gets or sets the maximum accepted web-message size in bytes. The value must be positive.</summary>
    public uint MaximumMessageSize { get; set; } = 1024 * 1024;

    /// <summary>Gets or sets the timeout for asynchronous browser decisions.</summary>
    public TimeSpan DecisionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the trust policy for inbound and outbound web/native messaging.</summary>
    /// <remarks>
    /// <see cref="NeoBridgePolicy.TrustEntireView"/> trusts all scripts executing in the view;
    /// origin metadata, when present, is informational and is not an authorization check.
    /// </remarks>
    public NeoBridgePolicy BridgePolicy { get; set; }

    /// <summary>
    /// Gets or sets the exact origins that may use web/native messaging when <see cref="BridgePolicy"/>
    /// is <see cref="NeoBridgePolicy.TrustedOrigins"/>. An empty collection never enables messaging.
    /// </summary>
    public IReadOnlyList<string> BridgeOrigins { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the portable frontend transport limits and handshake policy.</summary>
    public NeoTransportOptions Transport { get; set; } = new();

    internal void Validate(NeoEnvironment environment)
    {
        if (Profile is not null && !ReferenceEquals(Profile.Environment, environment))
        {
            throw new ArgumentException("The profile must belong to the environment creating the view.", nameof(Profile));
        }

        if (!FillParent && (Bounds.Width <= 0 || Bounds.Height <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(Bounds), "Explicit view bounds must have positive dimensions.");
        }

        if (DecisionTimeout <= TimeSpan.Zero || DecisionTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(DecisionTimeout), "The decision timeout must be between zero and ten minutes.");
        }

        if (MaximumMessageSize == 0 || MaximumMessageSize > NeoTransportOptions.HardMaximumFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumMessageSize), $"The web-message size limit must be between 1 and {NeoTransportOptions.HardMaximumFrameBytes} bytes.");
        }

        if (!Enum.IsDefined(BridgePolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(BridgePolicy));
        }

        ArgumentNullException.ThrowIfNull(BridgeOrigins);
        ArgumentNullException.ThrowIfNull(Transport);
        Transport.Validate();
        if (ViewLabel is not null && (string.IsNullOrWhiteSpace(ViewLabel) || ViewLabel.Length > 128 || ViewLabel.Any(char.IsControl)))
        {
            throw new ArgumentException("A view label must be non-empty and contain at most 128 characters without controls.", nameof(ViewLabel));
        }
        if (BridgePolicy != NeoBridgePolicy.Disabled)
        {
            if (ViewLabel is null)
            {
                throw new ArgumentException("A bridge-enabled view requires a non-empty application label of at most 128 characters.", nameof(ViewLabel));
            }
        }
        if (BridgePolicy == NeoBridgePolicy.TrustedOrigins && BridgeOrigins.Count == 0)
        {
            throw new ArgumentException("TrustedOrigins requires at least one bridge origin.", nameof(BridgeOrigins));
        }

        if (BridgePolicy != NeoBridgePolicy.TrustedOrigins && BridgeOrigins.Count != 0)
        {
            throw new ArgumentException("Bridge origins may be specified only with the TrustedOrigins policy.", nameof(BridgeOrigins));
        }

        foreach (var origin in BridgeOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 ||
                !string.Equals(uri.GetLeftPart(UriPartial.Authority).TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"'{origin}' is not an absolute origin.", nameof(BridgeOrigins));
            }
        }

        if (BridgePolicy == NeoBridgePolicy.TrustedOrigins && OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("WebKitGTK 4.1 does not expose trustworthy script-message sender origins. Use Disabled or explicitly opt into TrustEntireView.");
        }
    }
}

/// <summary>Configures bounded protocol handling for the portable frontend transport.</summary>
public sealed class NeoTransportOptions
{
    internal const uint HardMaximumFrameBytes = 16 * 1024 * 1024;

    /// <summary>Gets or sets the maximum JSON nesting depth accepted by the host.</summary>
    public int MaximumJsonDepth { get; set; } = 32;

    /// <summary>Gets or sets the maximum hello attempts accepted in one document.</summary>
    public int MaximumHandshakeAttempts { get; set; } = 3;

    /// <summary>Gets or sets the maximum number of retained transport diagnostics.</summary>
    public int MaximumDiagnosticQueue { get; set; } = 100;

    /// <summary>Gets or sets the host handshake timeout.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets the number of application frames accepted before a handshake.</summary>
    public int MaximumPreHandshakeFrames => 0;

    internal void Validate()
    {
        if (MaximumJsonDepth is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(MaximumJsonDepth));
        if (MaximumHandshakeAttempts is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(MaximumHandshakeAttempts));
        if (MaximumDiagnosticQueue is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(MaximumDiagnosticQueue));
        if (HandshakeTimeout <= TimeSpan.Zero || HandshakeTimeout > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(HandshakeTimeout));
    }
}

/// <summary>Configures a script injected into each matching document.</summary>
public sealed class NeoScriptOptions
{
    /// <summary>Gets or sets whether the script runs at document end instead of document start.</summary>
    public bool InjectAtDocumentEnd { get; set; }

    /// <summary>Gets or sets whether the script is restricted to the main frame.</summary>
    public bool MainFrameOnly { get; set; }

    /// <summary>Gets or sets whether the script runs in an isolated JavaScript world.</summary>
    public bool IsolatedWorld { get; set; }

    /// <summary>Gets or sets the optional isolated-world name.</summary>
    public string? WorldName { get; set; }

    internal void Validate()
    {
        if (WorldName is { Length: > 128 })
        {
            throw new ArgumentException("The script world name must not exceed 128 characters.", nameof(WorldName));
        }

        if (!IsolatedWorld && !string.IsNullOrEmpty(WorldName))
        {
            throw new ArgumentException("A world name requires isolated-world injection.", nameof(WorldName));
        }
    }
}
