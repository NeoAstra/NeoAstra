// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoWebView;

/// <summary>Configures a <see cref="NeoApplication"/>.</summary>
public sealed class NeoApplicationOptions
{
    /// <summary>Gets or sets the application name passed to the native backend.</summary>
    public string ApplicationName { get; set; } = "NeoWebView Application";

    /// <summary>Gets or sets the initial shutdown policy.</summary>
    public NeoApplicationShutdownMode ShutdownMode { get; set; } = NeoApplicationShutdownMode.OnLastWindowClosed;

    /// <summary>Gets or sets the maximum number of queued dispatcher callbacks.</summary>
    public uint MaximumPendingDispatches { get; set; } = 65_536;

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

    /// <summary>Gets or sets origins permitted to access the scheme.</summary>
    public IReadOnlyList<string> AllowedOrigins { get; set; } = Array.Empty<string>();

    /// <summary>Creates a custom scheme definition.</summary>
    /// <param name="name">A valid URI scheme name.</param>
    /// <returns>The new definition.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid URI scheme name.</exception>
    public static NeoCustomScheme Create(string name)
    {
        ValidateName(name);
        return new NeoCustomScheme(name.ToLowerInvariant());
    }

    /// <summary>Creates a secure scheme intended for trusted application content.</summary>
    /// <param name="name">A valid URI scheme name.</param>
    /// <returns>The new application scheme definition.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid URI scheme name.</exception>
    public static NeoCustomScheme Application(string name) => Create(name).WithApplicationDefaults();

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
        ArgumentNullException.ThrowIfNull(AllowedOrigins);
        foreach (var origin in AllowedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
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

/// <summary>Configures a NeoWebView-owned top-level window.</summary>
public sealed class NeoWindowOptions
{
    /// <summary>Gets or sets the owner window.</summary>
    public NeoWindow? Owner { get; set; }

    /// <summary>Gets or sets the initial title.</summary>
    public string Title { get; set; } = "NeoWebView";

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
public sealed class NeoWebViewOptions
{
    /// <summary>Gets or sets the profile used by the view.</summary>
    public NeoProfile? Profile { get; set; }

    /// <summary>Gets or sets explicit initial bounds when the host is not filled.</summary>
    public NeoRect Bounds { get; set; } = new(0, 0, 800, 600);

    /// <summary>Gets or sets whether the view automatically fills its parent.</summary>
    public bool FillParent { get; set; } = true;

    /// <summary>Gets or sets the maximum accepted web-message size in bytes.</summary>
    public uint MaximumMessageSize { get; set; } = 1024 * 1024;

    /// <summary>Gets or sets the timeout for asynchronous browser decisions.</summary>
    public TimeSpan DecisionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets origins that may use the managed web-message bridge.</summary>
    public IReadOnlyList<string> BridgeOrigins { get; set; } = Array.Empty<string>();

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

        ArgumentNullException.ThrowIfNull(BridgeOrigins);
        foreach (var origin in BridgeOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            {
                throw new ArgumentException($"'{origin}' is not an absolute origin.", nameof(BridgeOrigins));
            }
        }
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
