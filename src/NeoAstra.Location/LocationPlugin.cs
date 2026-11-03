// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.Json.Serialization;
using NeoAstra.Desktop;
using NeoAstra.Rpc;

namespace NeoAstra.Location;

/// <summary>The location plugin of NeoAstra: it owns the location service and declares its renderer commands, which it grants to no one.</summary>
public sealed class NeoLocationPlugin : INeoAstraPlugin
{
    /// <summary>The stable plugin ID.</summary>
    public const string Id = "neoastra.location";

    private readonly INeoLocation _location;
    private int _disposed;

    /// <summary>Initializes the plugin, which takes ownership of the location service.</summary>
    /// <param name="location">The location service.</param>
    /// <exception cref="ArgumentNullException"><paramref name="location"/> is <see langword="null"/>.</exception>
    public NeoLocationPlugin(INeoLocation location) => _location = location ?? throw new ArgumentNullException(nameof(location));

    /// <summary>Gets the permission declarations of the renderer commands; they grant nothing until a capability document names them.</summary>
    public static NeoPluginPermissionCatalog PermissionCatalog { get; } = CreatePermissionCatalog();

    /// <inheritdoc />
    public NeoPluginMetadata Metadata { get; } = CreateMetadata();

    /// <inheritdoc />
    public INeoPluginAdapter CreateAdapter() => new LocationAdapter(_location);

    /// <inheritdoc />
    public ValueTask ConfigureAsync(NeoPluginContext context, CancellationToken cancellationToken) { ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }

    /// <inheritdoc />
    public ValueTask ReadyAsync(NeoPluginContext context, CancellationToken cancellationToken) { ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }

    /// <inheritdoc />
    public ValueTask StoppingAsync(NeoPluginContext context, CancellationToken cancellationToken) { ArgumentNullException.ThrowIfNull(context); return ValueTask.CompletedTask; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _location is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(false);
    }

    internal static readonly string[] Commands = ["location.authorization", "location.request-authorization", "location.current-position"];

    private static NeoPluginMetadata CreateMetadata()
    {
        var commands = Commands.Select(static command =>
        {
            var permission = PermissionCatalog.Permissions.Single(permission => permission.Commands.Contains(command, StringComparer.Ordinal));
            return new NeoPluginCommandDeclaration(command, permission.Id, null, permission.Risk, Audited: true);
        }).ToArray();
        return new(Id, new Version(1, 0, 0), 1, new Version(0, 1, 0), commands: commands, permissionCatalog: PermissionCatalog, hasStaticJsonMetadata: true);
    }

    private static NeoPluginPermissionCatalog CreatePermissionCatalog() => new(Id, "1.0.0", "0.1.0",
    [
        Permission("location:status", ["location.authorization"], NeoPermissionRisk.Low, TimeSpan.FromSeconds(30)),
        // Reading the location asks the user first; the question of the system waits for the answer.
        Permission("location:read", ["location.request-authorization", "location.current-position"], NeoPermissionRisk.Sensitive, LocationRendererRegistration.ReadTimeout),
    ]);

    private static NeoPermissionDeclaration Permission(string id, string[] commands, NeoPermissionRisk risk, TimeSpan timeout) => new(id, 1, commands, risk, NeoScopeFamily.None)
    {
        MaximumConcurrency = 4,
        DefaultTimeout = timeout,
        Redaction = NeoAuditRedaction.Full,
        Documentation = "Official NeoAstra location renderer permission. Registration and grants remain explicit.",
    };

    private sealed class LocationAdapter(INeoLocation location) : INeoPluginAdapter
    {
        public NeoCapabilityInfo Support => location.Support;

        public ValueTask AttachAsync(NeoApplication application, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(application);
            cancellationToken.ThrowIfCancellationRequested();
            if (!application.Dispatcher.CheckAccess()) throw new InvalidOperationException("The location adapter must attach on the UI dispatcher.");
            if (location is INeoLocationDispatcherBinding binding) binding.Bind(application.Dispatcher);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Contains the static registration helpers of the location plugin.</summary>
public static class NeoLocationPluginExtensions
{
    /// <summary>Adds the location plugin through an explicit AOT-safe factory and transfers the ownership of the service to the plugin host. This does not register renderer handlers or grants.</summary>
    /// <param name="builder">The plugin builder.</param>
    /// <param name="location">The location service.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static NeoPluginBuilder AddNeoAstraLocation(this NeoPluginBuilder builder, INeoLocation location)
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentNullException.ThrowIfNull(location);
        return builder.AddNeoAstraPlugin(() => new NeoLocationPlugin(location));
    }

    /// <summary>Adds the permission declarations of the location plugin to application capability tooling. This grants no renderer authority.</summary>
    /// <param name="builder">The permission catalog builder.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static NeoPermissionCatalogBuilder AddNeoAstraLocationPermissions(this NeoPermissionCatalogBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddPlugin(NeoLocationPlugin.PermissionCatalog);
    }

    /// <summary>Adds the location renderer handlers with source-generated JSON metadata. Calling this method grants nothing.</summary>
    /// <param name="builder">The explicit RPC builder.</param>
    /// <param name="location">The location service.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static NeoRpcBuilder AddNeoAstraLocationHandlers(this NeoRpcBuilder builder, INeoLocation location)
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentNullException.ThrowIfNull(location);
        new LocationRendererRegistration(location).Register(builder);
        return builder;
    }
}

internal sealed class LocationRendererRegistration(INeoLocation location)
{
    // The longest request, and the question of the system, which the user may leave open for a while.
    internal static readonly TimeSpan ReadTimeout = NeoLocationRequest.MaximumTimeout + TimeSpan.FromSeconds(30);

    public void Register(NeoRpcBuilder builder)
    {
        var json = LocationRendererJsonContext.Default;
        builder.AddCommand("location.authorization", AuthorizationAsync, json.LocationEmptyRequest, json.LocationAuthorizationResult, Options("location:status", TimeSpan.FromSeconds(30)));
        builder.AddCommand("location.request-authorization", RequestAuthorizationAsync, json.LocationEmptyRequest, json.LocationAuthorizationResult, Options("location:read", ReadTimeout));
        builder.AddCommand("location.current-position", CurrentPositionAsync, json.LocationPositionRequest, json.LocationPositionResult, Options("location:read", ReadTimeout));
    }

    private static NeoRpcCommandOptions Options(string permission, TimeSpan timeout) => new() { Permission = permission, Dispatch = NeoRpcDispatchMode.Background, MaximumConcurrency = 4, Timeout = timeout };

    private async ValueTask<LocationAuthorizationResult> AuthorizationAsync(LocationEmptyRequest request, NeoRpcContext context, CancellationToken token)
        => LocationAuthorizationResult.From(await location.GetAuthorizationAsync(token).ConfigureAwait(false));

    private async ValueTask<LocationAuthorizationResult> RequestAuthorizationAsync(LocationEmptyRequest request, NeoRpcContext context, CancellationToken token)
        => LocationAuthorizationResult.From(await location.RequestAuthorizationAsync(token).ConfigureAwait(false));

    private async ValueTask<LocationPositionResult> CurrentPositionAsync(LocationPositionRequest request, NeoRpcContext context, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Accuracy) || request.TimeoutMilliseconds is < 1000 or > 120_000 || request.MaximumAgeMilliseconds is < 0 or > 86_400_000)
            throw new NeoRpcException("location_invalid", "The location request is out of its range.");
        var result = await location.GetCurrentPositionAsync(new NeoLocationRequest
        {
            Accuracy = request.Accuracy,
            Timeout = TimeSpan.FromMilliseconds(request.TimeoutMilliseconds),
            MaximumAge = TimeSpan.FromMilliseconds(request.MaximumAgeMilliseconds),
        }, token).ConfigureAwait(false);
        return new(result.Status, result.Value is { } position ? LocationPositionDto.From(position) : null, result.Code);
    }
}

internal sealed record LocationEmptyRequest;
internal sealed record LocationPositionRequest(NeoLocationAccuracy Accuracy = NeoLocationAccuracy.Balanced, int TimeoutMilliseconds = 30_000, int MaximumAgeMilliseconds = 0);
internal sealed record LocationAuthorizationResult(NeoLocationPermission Permission, bool IsPrecise, bool IsServiceEnabled)
{
    internal static LocationAuthorizationResult From(NeoLocationAuthorization value) => new(value.Permission, value.IsPrecise, value.IsServiceEnabled);
}
internal sealed record LocationPositionDto(double Latitude, double Longitude, double Accuracy, long Timestamp, double? Altitude, double? AltitudeAccuracy, double? Speed, double? Course)
{
    // The fields and units of the coordinates of a web page: meters, degrees, and the milliseconds of the Unix epoch.
    internal static LocationPositionDto From(NeoLocationPosition value) => new(value.Latitude, value.Longitude, value.AccuracyMeters, value.Timestamp.ToUnixTimeMilliseconds(), value.Altitude, value.AltitudeAccuracyMeters, value.SpeedMetersPerSecond, value.CourseDegrees);
}
internal sealed record LocationPositionResult(NeoDesktopStatus Status, LocationPositionDto? Value, string? Code);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(LocationEmptyRequest))]
[JsonSerializable(typeof(LocationPositionRequest))]
[JsonSerializable(typeof(LocationAuthorizationResult))]
[JsonSerializable(typeof(LocationPositionResult))]
internal sealed partial class LocationRendererJsonContext : JsonSerializerContext;
