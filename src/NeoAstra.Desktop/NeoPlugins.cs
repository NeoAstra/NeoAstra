// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.ObjectModel;
using NeoAstra.Rpc;

namespace NeoAstra.Desktop;

/// <summary>Identifies the trusted owner of plugin resources.</summary>
public readonly record struct NeoPluginOwner
{
    private NeoPluginOwner(NeoPluginOwnerKind kind, string id) { Kind = kind; Id = id; }

    /// <summary>Gets the owner kind.</summary>
    public NeoPluginOwnerKind Kind { get; }

    /// <summary>Gets the bounded opaque owner identifier.</summary>
    public string Id { get; }

    /// <summary>Creates an application owner.</summary>
    /// <returns>The application owner.</returns>
    public static NeoPluginOwner Application() => new(NeoPluginOwnerKind.Application, "application");

    /// <summary>Creates a view owner from a trusted immutable view label.</summary>
    /// <param name="viewLabel">The view label.</param>
    /// <returns>The view owner.</returns>
    public static NeoPluginOwner View(string viewLabel) => Create(NeoPluginOwnerKind.View, viewLabel);

    /// <summary>Creates a document-session owner from a trusted session identifier.</summary>
    /// <param name="sessionId">The document-session identifier.</param>
    /// <returns>The document-session owner.</returns>
    public static NeoPluginOwner DocumentSession(string sessionId) => Create(NeoPluginOwnerKind.DocumentSession, sessionId);

    /// <summary>Creates an opaque resource owner.</summary>
    /// <param name="resourceId">The resource identifier.</param>
    /// <returns>The resource owner.</returns>
    public static NeoPluginOwner Resource(string resourceId) => Create(NeoPluginOwnerKind.Resource, resourceId);

    private static NeoPluginOwner Create(NeoPluginOwnerKind kind, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 || value.Any(char.IsControl)) throw new ArgumentException("An owner ID must be at most 128 characters and contain no controls.", nameof(value));
        return new(kind, value);
    }

    internal void Validate()
    {
        if (!Enum.IsDefined(Kind) || string.IsNullOrEmpty(Id) || Id.Length > 128 || Id.Any(char.IsControl)) throw new ArgumentException("A plugin owner is uninitialized or malformed.");
    }
}

/// <summary>Classifies plugin resource ownership.</summary>
public enum NeoPluginOwnerKind
{
    /// <summary>The application owns the resource.</summary>
    Application,
    /// <summary>A view owns the resource.</summary>
    View,
    /// <summary>A document session owns the resource.</summary>
    DocumentSession,
    /// <summary>Another opaque resource owns the child resource.</summary>
    Resource,
}

/// <summary>Declares a statically resolved plugin dependency.</summary>
/// <param name="Id">Stable dependency plugin ID.</param>
/// <param name="MinimumVersion">Inclusive semantic minimum version.</param>
/// <param name="MaximumVersion">Optional exclusive semantic maximum version.</param>
public sealed record NeoPluginDependency(string Id, Version MinimumVersion, Version? MaximumVersion = null);

/// <summary>Declares one plugin RPC command without registering or granting it.</summary>
/// <param name="Name">Stable wire command name.</param>
/// <param name="Permission">Exact required permission.</param>
/// <param name="ScopeSchema">Bounded JSON Schema resource name, or <see langword="null"/>.</param>
/// <param name="Risk">Permission risk.</param>
/// <param name="Audited">Whether invocations produce redacted audit records.</param>
public sealed record NeoPluginCommandDeclaration(string Name, string Permission, string? ScopeSchema, NeoPermissionRisk Risk, bool Audited);

/// <summary>Declares one plugin renderer event without registering or granting it.</summary>
/// <param name="Name">Stable wire event name.</param>
/// <param name="Permission">Exact subscription permission.</param>
/// <param name="Risk">Permission risk.</param>
/// <param name="Audited">Whether subscription lifecycle produces redacted audit records.</param>
public sealed record NeoPluginEventDeclaration(string Name, string Permission, NeoPermissionRisk Risk, bool Audited);

/// <summary>Contains immutable, AOT-safe plugin metadata.</summary>
public sealed class NeoPluginMetadata
{
    /// <summary>Initializes plugin metadata.</summary>
    /// <param name="id">Stable reverse-DNS or package-aligned ID.</param>
    /// <param name="managedApiVersion">Managed semantic API version.</param>
    /// <param name="frontendProtocolVersion">Positive frontend protocol version.</param>
    /// <param name="minimumNeoAstraVersion">Minimum compatible NeoAstra version.</param>
    /// <param name="dependencies">Static dependencies.</param>
    /// <param name="commands">Declared renderer commands.</param>
    /// <param name="events">Declared renderer events.</param>
    /// <param name="permissionCatalog">Static permission catalog, when renderer commands exist.</param>
    /// <param name="hasStaticJsonMetadata">Whether every declared command has source-generated JSON metadata.</param>
    /// <exception cref="ArgumentException">Metadata is malformed or inconsistent.</exception>
    public NeoPluginMetadata(string id, Version managedApiVersion, int frontendProtocolVersion, Version minimumNeoAstraVersion,
        IEnumerable<NeoPluginDependency>? dependencies = null, IEnumerable<NeoPluginCommandDeclaration>? commands = null, IEnumerable<NeoPluginEventDeclaration>? events = null,
        NeoPluginPermissionCatalog? permissionCatalog = null, bool hasStaticJsonMetadata = true)
    {
        ValidateId(id, nameof(id));
        ArgumentNullException.ThrowIfNull(managedApiVersion);
        ArgumentNullException.ThrowIfNull(minimumNeoAstraVersion);
        if (frontendProtocolVersion is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(frontendProtocolVersion));
        var dependencyArray = (dependencies ?? []).Take(257).ToArray();
        var commandArray = (commands ?? []).Take(513).ToArray();
        var eventArray = (events ?? []).Take(257).ToArray();
        if (dependencyArray.Length > 256) throw new ArgumentException("A plugin may declare at most 256 dependencies.", nameof(dependencies));
        if (commandArray.Length > 512) throw new ArgumentException("A plugin may declare at most 512 commands.", nameof(commands));
        if (eventArray.Length > 256) throw new ArgumentException("A plugin may declare at most 256 events.", nameof(events));
        foreach (var dependency in dependencyArray)
        {
            ArgumentNullException.ThrowIfNull(dependency);
            ValidateId(dependency.Id, nameof(dependencies));
            ArgumentNullException.ThrowIfNull(dependency.MinimumVersion);
            if (dependency.MaximumVersion is not null && dependency.MaximumVersion <= dependency.MinimumVersion) throw new ArgumentException("A dependency maximum must exceed its minimum.", nameof(dependencies));
        }
        if (dependencyArray.Select(static value => value.Id).Distinct(StringComparer.Ordinal).Count() != dependencyArray.Length) throw new ArgumentException("A plugin dependency is duplicated.", nameof(dependencies));
        foreach (var command in commandArray)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (!IsWireName(command.Name) || !IsPermission(command.Permission)) throw new ArgumentException("A plugin command or permission is malformed.", nameof(commands));
            if (command.ScopeSchema is { } schema && (schema.Length > 256 || schema.Any(char.IsControl))) throw new ArgumentException("A scope schema name is malformed.", nameof(commands));
            if (!Enum.IsDefined(command.Risk)) throw new ArgumentException("A plugin command risk is invalid.", nameof(commands));
        }
        if (commandArray.Select(static value => value.Name).Distinct(StringComparer.Ordinal).Count() != commandArray.Length) throw new ArgumentException("A plugin command is duplicated.", nameof(commands));
        foreach (var pluginEvent in eventArray)
        {
            ArgumentNullException.ThrowIfNull(pluginEvent);
            if (!IsWireName(pluginEvent.Name) || !IsPermission(pluginEvent.Permission) || !Enum.IsDefined(pluginEvent.Risk)) throw new ArgumentException("A plugin event declaration is malformed.", nameof(events));
        }
        if (eventArray.Select(static value => value.Name).Distinct(StringComparer.Ordinal).Count() != eventArray.Length) throw new ArgumentException("A plugin event is duplicated.", nameof(events));
        if (commandArray.Select(static value => value.Name).Intersect(eventArray.Select(static value => value.Name), StringComparer.Ordinal).Any()) throw new ArgumentException("A renderer operation cannot be both a command and an event.");
        if (commandArray.Length + eventArray.Length != 0 && permissionCatalog is null) throw new ArgumentException("Renderer operations require a permission catalog.", nameof(permissionCatalog));
        if (commandArray.Length + eventArray.Length != 0 && !hasStaticJsonMetadata) throw new ArgumentException("Renderer operations require source-generated JSON metadata.", nameof(hasStaticJsonMetadata));
        if (permissionCatalog is not null && !string.Equals(permissionCatalog.Id, id, StringComparison.Ordinal)) throw new ArgumentException("The permission catalog plugin ID does not match metadata.", nameof(permissionCatalog));
        if (commandArray.Any(command => !permissionCatalog!.Permissions.Any(permission => permission.Id == command.Permission && permission.Commands.Contains(command.Name, StringComparer.Ordinal)))) throw new ArgumentException("Every command must be covered by its exact declared permission.", nameof(commands));
        if (eventArray.Any(pluginEvent => !permissionCatalog!.Permissions.Any(permission => permission.Id == pluginEvent.Permission && permission.Commands.Contains(pluginEvent.Name, StringComparer.Ordinal)))) throw new ArgumentException("Every event must be covered by its exact declared permission.", nameof(events));

        Id = id;
        ManagedApiVersion = managedApiVersion;
        FrontendProtocolVersion = frontendProtocolVersion;
        MinimumNeoAstraVersion = minimumNeoAstraVersion;
        Dependencies = Array.AsReadOnly(dependencyArray);
        Commands = Array.AsReadOnly(commandArray);
        Events = Array.AsReadOnly(eventArray);
        PermissionCatalog = permissionCatalog;
        HasStaticJsonMetadata = hasStaticJsonMetadata;
    }

    /// <summary>Gets the stable plugin ID.</summary>
    public string Id { get; }
    /// <summary>Gets the managed API version.</summary>
    public Version ManagedApiVersion { get; }
    /// <summary>Gets the frontend protocol version.</summary>
    public int FrontendProtocolVersion { get; }
    /// <summary>Gets the minimum NeoAstra version.</summary>
    public Version MinimumNeoAstraVersion { get; }
    /// <summary>Gets ordered static dependencies.</summary>
    public IReadOnlyList<NeoPluginDependency> Dependencies { get; }
    /// <summary>Gets renderer commands declared but not automatically registered or granted.</summary>
    public IReadOnlyList<NeoPluginCommandDeclaration> Commands { get; }
    /// <summary>Gets renderer events declared but not automatically registered or granted.</summary>
    public IReadOnlyList<NeoPluginEventDeclaration> Events { get; }
    /// <summary>Gets the permission catalog, when present.</summary>
    public NeoPluginPermissionCatalog? PermissionCatalog { get; }
    /// <summary>Gets whether source-generated JSON metadata covers command DTOs.</summary>
    public bool HasStaticJsonMetadata { get; }

    internal static void ValidateId(string value, string parameterName)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value[0] is '.' or '-' || value[^1] is '.' or '-' || value.Any(static character => !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-')))
            throw new ArgumentException("A plugin ID must be a bounded lowercase reverse-DNS or package identifier.", parameterName);
    }

    private static bool IsWireName(string value) => !string.IsNullOrEmpty(value) && value.Length <= 128 && value.All(static c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_' or ':');
    private static bool IsPermission(string value) => IsWireName(value) && value.Contains(':', StringComparison.Ordinal);
}

/// <summary>Provides the statically selected platform adapter for one plugin.</summary>
public interface INeoPluginAdapter : IAsyncDisposable
{
    /// <summary>Gets truthful platform support and limitations.</summary>
    NeoCapabilityInfo Support { get; }

    /// <summary>Attaches native state on the application dispatcher.</summary>
    /// <param name="application">The owning application.</param>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <returns>A task representing attachment.</returns>
    ValueTask AttachAsync(NeoApplication application, CancellationToken cancellationToken);
}

/// <summary>Defines one statically composed plugin and its deterministic lifecycle.</summary>
public interface INeoAstraPlugin : IAsyncDisposable
{
    /// <summary>Gets immutable plugin metadata.</summary>
    NeoPluginMetadata Metadata { get; }

    /// <summary>Creates the platform adapter without reflection or dynamic plugin loading.</summary>
    /// <returns>The selected adapter.</returns>
    INeoPluginAdapter CreateAdapter();

    /// <summary>Configures application-scoped services before native attachment.</summary>
    /// <param name="context">The startup context.</param>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <returns>A task representing configuration.</returns>
    ValueTask ConfigureAsync(NeoPluginContext context, CancellationToken cancellationToken);

    /// <summary>Runs after the application becomes ready.</summary>
    /// <param name="context">The plugin context.</param>
    /// <param name="cancellationToken">Cancels readiness work.</param>
    /// <returns>A task representing readiness.</returns>
    ValueTask ReadyAsync(NeoPluginContext context, CancellationToken cancellationToken);

    /// <summary>Cancels operations and revokes owned state during bounded stopping.</summary>
    /// <param name="context">The plugin context.</param>
    /// <param name="cancellationToken">The bounded shutdown token.</param>
    /// <returns>A task representing stopping.</returns>
    ValueTask StoppingAsync(NeoPluginContext context, CancellationToken cancellationToken);
}

/// <summary>Supplies application state and bounded ownership to plugin callbacks.</summary>
public sealed class NeoPluginContext
{
    private readonly NeoPluginHost _host;
    internal NeoPluginContext(NeoPluginHost host, string pluginId, NeoApplication application) { _host = host; PluginId = pluginId; Application = application; }

    /// <summary>Gets the owning application.</summary>
    public NeoApplication Application { get; }

    /// <summary>Gets the active plugin ID.</summary>
    public string PluginId { get; }

    /// <summary>Tracks a resource for deterministic reverse-order owner teardown.</summary>
    /// <param name="owner">The trusted owner.</param>
    /// <param name="resource">The disposable resource.</param>
    /// <exception cref="InvalidOperationException">The host is stopping or its resource limit is reached.</exception>
    public void Track(NeoPluginOwner owner, IAsyncDisposable resource) => _host.Track(PluginId, owner, resource);
}

/// <summary>Configures static plugins and bounded lifecycle behavior.</summary>
public sealed class NeoPluginBuilder
{
    private readonly List<Func<INeoAstraPlugin>> _factories = [];
    private bool _built;

    /// <summary>Gets or sets the maximum statically registered plugin count.</summary>
    public int MaximumPlugins { get; set; } = 64;

    /// <summary>Gets or sets the maximum tracked resources across plugins.</summary>
    public int MaximumOwnedResources { get; set; } = 4096;

    /// <summary>Gets or sets the total bounded stopping timeout.</summary>
    public TimeSpan StoppingTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Adds a statically constructible plugin without runtime reflection.</summary>
    /// <typeparam name="TPlugin">The plugin type.</typeparam>
    /// <returns>This builder.</returns>
    public NeoPluginBuilder AddNeoAstraPlugin<TPlugin>() where TPlugin : INeoAstraPlugin, new() => AddNeoAstraPlugin(static () => new TPlugin());

    /// <summary>Adds an explicit static plugin factory.</summary>
    /// <param name="factory">The AOT-safe factory.</param>
    /// <returns>This builder.</returns>
    public NeoPluginBuilder AddNeoAstraPlugin(Func<INeoAstraPlugin> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (_built) throw new InvalidOperationException("The plugin builder was already built.");
        _factories.Add(factory);
        return this;
    }

    /// <summary>Validates metadata and resolves a deterministic dependency graph.</summary>
    /// <returns>An immutable plugin host.</returns>
    /// <exception cref="InvalidOperationException">A factory, dependency, adapter, permission, or graph is invalid.</exception>
    public NeoPluginHost Build()
    {
        if (_built) throw new InvalidOperationException("The plugin builder was already built.");
        _built = true;
        if (MaximumPlugins is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(MaximumPlugins));
        if (MaximumOwnedResources is < 1 or > 65_536) throw new ArgumentOutOfRangeException(nameof(MaximumOwnedResources));
        if (StoppingTimeout <= TimeSpan.Zero || StoppingTimeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(StoppingTimeout));
        if (_factories.Count > MaximumPlugins) throw new InvalidOperationException($"At most {MaximumPlugins} plugins may be registered.");
        var plugins = new List<INeoAstraPlugin>(_factories.Count);
        try
        {
            foreach (var factory in _factories) plugins.Add(factory() ?? throw new InvalidOperationException("A plugin factory returned null."));
            return new NeoPluginHost(Resolve(plugins), MaximumOwnedResources, StoppingTimeout);
        }
        catch
        {
            foreach (var plugin in plugins) try { plugin.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            throw;
        }
    }

    private static IReadOnlyList<INeoAstraPlugin> Resolve(IReadOnlyList<INeoAstraPlugin> plugins)
    {
        var byId = new Dictionary<string, INeoAstraPlugin>(StringComparer.Ordinal);
        var permissionBuilder = new NeoPermissionCatalogBuilder();
        foreach (var plugin in plugins)
        {
            if (plugin.Metadata is null) throw new InvalidOperationException("A plugin returned null metadata.");
            if (!byId.TryAdd(plugin.Metadata.Id, plugin)) throw new InvalidOperationException($"Plugin ID '{plugin.Metadata.Id}' is duplicated.");
            if (plugin.Metadata.PermissionCatalog is { } catalog) permissionBuilder.AddPlugin(catalog);
        }
        if (plugins.Any(static value => value.Metadata.PermissionCatalog is not null)) _ = permissionBuilder.Build();
        foreach (var plugin in plugins)
        foreach (var dependency in plugin.Metadata.Dependencies)
        {
            if (!byId.TryGetValue(dependency.Id, out var resolved)) throw new InvalidOperationException($"Plugin '{plugin.Metadata.Id}' requires missing plugin '{dependency.Id}'.");
            var version = resolved.Metadata.ManagedApiVersion;
            if (version < dependency.MinimumVersion || dependency.MaximumVersion is not null && version >= dependency.MaximumVersion) throw new InvalidOperationException($"Plugin '{plugin.Metadata.Id}' requires '{dependency.Id}' in the declared version range; found {version}.");
        }
        var output = new List<INeoAstraPlugin>(plugins.Count);
        var state = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (var plugin in plugins.OrderBy(static value => value.Metadata.Id, StringComparer.Ordinal)) Visit(plugin);
        return new ReadOnlyCollection<INeoAstraPlugin>(output);

        void Visit(INeoAstraPlugin plugin)
        {
            if (state.TryGetValue(plugin.Metadata.Id, out var value))
            {
                if (value == 1) throw new InvalidOperationException($"Plugin dependency cycle contains '{plugin.Metadata.Id}'.");
                return;
            }
            state[plugin.Metadata.Id] = 1;
            foreach (var dependency in plugin.Metadata.Dependencies.OrderBy(static value => value.Id, StringComparer.Ordinal)) Visit(byId[dependency.Id]);
            state[plugin.Metadata.Id] = 2;
            output.Add(plugin);
        }
    }
}

/// <summary>Owns a resolved plugin graph and integrates it with application readiness and stopping.</summary>
public sealed class NeoPluginHost : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly IReadOnlyList<INeoAstraPlugin> _plugins;
    private readonly List<(string PluginId, NeoPluginOwner Owner, IAsyncDisposable Resource)> _resources = [];
    private readonly Dictionary<string, NeoPluginContext> _contexts = new(StringComparer.Ordinal);
    private readonly List<INeoPluginAdapter> _adapters = [];
    private readonly int _maximumOwnedResources;
    private readonly TimeSpan _stoppingTimeout;
    private NeoApplication? _application;
    private Task? _readyTask;
    private Task? _stopTask;
    private bool _started;
    private bool _stopping;

    internal NeoPluginHost(IReadOnlyList<INeoAstraPlugin> plugins, int maximumOwnedResources, TimeSpan stoppingTimeout)
    {
        _plugins = plugins;
        _maximumOwnedResources = maximumOwnedResources;
        _stoppingTimeout = stoppingTimeout;
    }

    /// <summary>Gets resolved metadata in deterministic dependency order.</summary>
    public IReadOnlyList<NeoPluginMetadata> Plugins => Array.AsReadOnly(_plugins.Select(static plugin => plugin.Metadata).ToArray());

    /// <summary>Gets a deterministic diagnostic snapshot without grants or user data.</summary>
    public IReadOnlyList<string> Diagnostics
    {
        get
        {
            lock (_sync)
            {
                return Array.AsReadOnly(_plugins.Select((plugin, index) => $"{plugin.Metadata.Id}@{plugin.Metadata.ManagedApiVersion};protocol={plugin.Metadata.FrontendProtocolVersion};support={(_adapters.Count > index ? _adapters[index].Support.SupportLevel : NeoSupportLevel.None)}").ToArray());
            }
        }
    }

    /// <summary>Configures and attaches the resolved graph before application ready.</summary>
    /// <param name="application">The starting application.</param>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <returns>A task representing startup.</returns>
    /// <exception cref="InvalidOperationException">The host already started or the application is not starting.</exception>
    public async ValueTask StartAsync(NeoApplication application, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        lock (_sync)
        {
            if (_started) throw new InvalidOperationException("A plugin host can start only once.");
            if (application.State != NeoApplicationState.Starting) throw new InvalidOperationException("Plugins must attach while the application is starting.");
            _started = true;
            _application = application;
            foreach (var plugin in _plugins) _contexts.Add(plugin.Metadata.Id, new(this, plugin.Metadata.Id, application));
        }
        try
        {
            foreach (var plugin in _plugins) await plugin.ConfigureAsync(_contexts[plugin.Metadata.Id], cancellationToken).ConfigureAwait(false);
            foreach (var plugin in _plugins)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var adapter = plugin.CreateAdapter() ?? throw new InvalidOperationException($"Plugin '{plugin.Metadata.Id}' returned no platform adapter.");
                lock (_sync) _adapters.Add(adapter);
                var attach = await application.Dispatcher.InvokeAsync(() => adapter.AttachAsync(application, cancellationToken), cancellationToken).ConfigureAwait(false);
                await attach.ConfigureAwait(false);
            }
            application.StateChanged += OnApplicationStateChanged;
            application.StoppingAsync += StopFromApplicationAsync;
            if (application.State == NeoApplicationState.Ready) ScheduleReady();
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Disposes every resource owned by one view/session/resource in reverse registration order.</summary>
    /// <param name="owner">The owner to release.</param>
    /// <param name="cancellationToken">Cancels the bounded wait; disposal still continues best effort.</param>
    /// <returns>A task representing release.</returns>
    public async ValueTask ReleaseOwnerAsync(NeoPluginOwner owner, CancellationToken cancellationToken = default)
    {
        owner.Validate();
        List<IAsyncDisposable> resources;
        lock (_sync)
        {
            resources = _resources.Where(value => value.Owner == owner).Select(static value => value.Resource).Reverse().ToList();
            _resources.RemoveAll(value => value.Owner == owner);
        }
        foreach (var resource in resources)
        {
            var disposal = resource.DisposeAsync().AsTask();
            try { await disposal.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { _ = ObserveDisposalAsync(disposal, _application, "plugin.owner-dispose"); }
            catch (Exception exception) { _application?.ReportLifecycleFailure("plugin.owner-dispose", exception, 0); }
        }
    }

    /// <summary>Stops child resources, adapters, and plugins in deterministic reverse order.</summary>
    /// <param name="cancellationToken">Optional external shutdown deadline.</param>
    /// <returns>A task representing stopping.</returns>
    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync) return new ValueTask(_stopTask ??= StopCoreAsync(cancellationToken));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    internal void Track(string pluginId, NeoPluginOwner owner, IAsyncDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        owner.Validate();
        lock (_sync)
        {
            if (_stopping) throw new InvalidOperationException("Plugin resources cannot be added while stopping.");
            if (!_contexts.ContainsKey(pluginId)) throw new InvalidOperationException("The plugin is not active in this host.");
            if (_resources.Count >= _maximumOwnedResources) throw new InvalidOperationException($"The plugin resource limit of {_maximumOwnedResources} was reached.");
            _resources.Add((pluginId, owner, resource));
        }
    }

    private void OnApplicationStateChanged(object? sender, NeoApplicationStateChangedEventArgs args)
    {
        if (args.Current == NeoApplicationState.Ready) ScheduleReady();
    }

    private void ScheduleReady()
    {
        lock (_sync)
        {
            if (_stopping || _readyTask is not null) return;
            _readyTask = ReadyCoreAsync();
        }
    }

    private async Task ReadyCoreAsync()
    {
        var application = _application!;
        foreach (var plugin in _plugins)
        {
            try { await plugin.ReadyAsync(_contexts[plugin.Metadata.Id], CancellationToken.None).ConfigureAwait(true); }
            catch (Exception exception) { application.ReportLifecycleFailure("plugin.ready", exception, 0); }
        }
    }

    private async ValueTask StopFromApplicationAsync(CancellationToken cancellationToken) => await StopAsync(cancellationToken).ConfigureAwait(false);

    private async Task StopCoreAsync(CancellationToken externalToken)
    {
        NeoApplication? application;
        lock (_sync) { _stopping = true; application = _application; }
        if (application is not null)
        {
            application.StateChanged -= OnApplicationStateChanged;
            application.StoppingAsync -= StopFromApplicationAsync;
        }
        using var timeout = new CancellationTokenSource(_stoppingTimeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, externalToken);
        var token = deadline.Token;
        for (var index = _plugins.Count - 1; _started && index >= 0; index--)
        {
            try { await _plugins[index].StoppingAsync(_contexts[_plugins[index].Metadata.Id], token).AsTask().WaitAsync(token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not StackOverflowException) { application?.ReportLifecycleFailure("plugin.stopping", exception, 0); }
        }
        List<IAsyncDisposable> resources;
        lock (_sync) { resources = _resources.Select(static value => value.Resource).Reverse().ToList(); _resources.Clear(); }
        await DisposeGroupsContainedAsync(
        [
            (resources, "plugin.resource"),
            (_adapters.Reverse<IAsyncDisposable>(), "plugin.adapter"),
            (_plugins.Reverse<IAsyncDisposable>(), "plugin.dispose"),
        ], application, token).ConfigureAwait(false);
    }

    internal static async ValueTask DisposeGroupsContainedAsync(IEnumerable<(IEnumerable<IAsyncDisposable> Resources, string Category)> groups, NeoApplication? application, CancellationToken token)
    {
        foreach (var group in groups) await DisposeAllContainedAsync(group.Resources, application, group.Category, token).ConfigureAwait(false);
    }

    private static async ValueTask DisposeAllContainedAsync(IEnumerable<IAsyncDisposable> resources, NeoApplication? application, string category, CancellationToken token)
    {
        foreach (var resource in resources)
        {
            if (token.IsCancellationRequested)
            {
                BeginContainedDisposal(resource, application, category);
                continue;
            }

            await DisposeContainedAsync(resource, application, category, token).ConfigureAwait(false);
        }
    }

    private static void BeginContainedDisposal(IAsyncDisposable resource, NeoApplication? application, string category)
    {
        try
        {
            var disposal = resource.DisposeAsync().AsTask();
            _ = ObserveDisposalAsync(disposal, application, category);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            application?.ReportLifecycleFailure(category, exception, 0);
        }
    }

    private static async ValueTask DisposeContainedAsync(IAsyncDisposable resource, NeoApplication? application, string category, CancellationToken token)
    {
        var disposal = resource.DisposeAsync().AsTask();
        try { await disposal.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { _ = ObserveDisposalAsync(disposal, application, category); }
        catch (Exception exception) when (exception is not StackOverflowException) { application?.ReportLifecycleFailure(category, exception, 0); }
    }

    private static async Task ObserveDisposalAsync(Task disposal, NeoApplication? application, string category)
    {
        try { await disposal.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not StackOverflowException) { application?.ReportLifecycleFailure(category, exception, 0); }
    }
}
