// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using NeoAstra.Desktop;

namespace NeoAstra.Location;

/// <summary>Describes whether the user lets the application read the location of the device while it is in use.</summary>
public enum NeoLocationPermission
{
    /// <summary>The application has not asked yet; <see cref="INeoLocation.RequestAuthorizationAsync"/> asks the user.</summary>
    NotRequested,
    /// <summary>The user refused, or the application does not declare the permission; only the settings of the system change it.</summary>
    Denied,
    /// <summary>A policy of the device, such as parental controls, forbids it.</summary>
    Restricted,
    /// <summary>The user allows it.</summary>
    Granted,
}

/// <summary>Chooses between an accurate fix and one that costs less energy and time.</summary>
public enum NeoLocationAccuracy
{
    /// <summary>A fix within about a hundred meters, from Wi-Fi and cell networks where they suffice.</summary>
    Balanced,
    /// <summary>The best fix that the device has, from satellites where they are in view.</summary>
    High,
}

/// <summary>Contains what the application may read of the location, and whether the location of the device is switched on.</summary>
/// <param name="Permission">Whether the user lets the application read the location.</param>
/// <param name="IsPrecise">Whether a granted permission is for the precise location rather than an approximate one.</param>
/// <param name="IsServiceEnabled">Whether the location of the device is switched on in the settings of the system.</param>
public sealed record NeoLocationAuthorization(NeoLocationPermission Permission, bool IsPrecise, bool IsServiceEnabled);

/// <summary>Contains one fix of the location of the device.</summary>
/// <param name="Latitude">The latitude in degrees, positive north of the equator.</param>
/// <param name="Longitude">The longitude in degrees, positive east of Greenwich.</param>
/// <param name="AccuracyMeters">The radius in meters around the coordinates in which the device is, with a confidence of about 68%.</param>
/// <param name="Timestamp">When the device determined the fix.</param>
public sealed record NeoLocationPosition(double Latitude, double Longitude, double AccuracyMeters, DateTimeOffset Timestamp)
{
    /// <summary>Gets the altitude in meters above sea level, when the fix has one.</summary>
    public double? Altitude { get; init; }

    /// <summary>Gets the accuracy of <see cref="Altitude"/> in meters, when the fix has one.</summary>
    public double? AltitudeAccuracyMeters { get; init; }

    /// <summary>Gets the speed of the device in meters per second, when the fix has one.</summary>
    public double? SpeedMetersPerSecond { get; init; }

    /// <summary>Gets the direction in which the device moves, in degrees clockwise from true north, when the fix has one.</summary>
    public double? CourseDegrees { get; init; }
}

/// <summary>Configures one request for the current location.</summary>
public sealed class NeoLocationRequest
{
    /// <summary>The longest time that a request may wait for a fix.</summary>
    public static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The oldest fix that a request may accept.</summary>
    public static readonly TimeSpan MaximumAgeLimit = TimeSpan.FromDays(1);

    /// <summary>Gets or sets the accuracy to ask the device for.</summary>
    public NeoLocationAccuracy Accuracy { get; set; } = NeoLocationAccuracy.Balanced;

    /// <summary>Gets or sets how long to wait for a fix, from one second to <see cref="MaximumTimeout"/>.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the age of a fix that the device already has and that the request accepts instead of a new one, up to <see cref="MaximumAgeLimit"/>; zero asks for a new fix.</summary>
    public TimeSpan MaximumAge { get; set; } = TimeSpan.Zero;

    internal void Validate()
    {
        if (!Enum.IsDefined(Accuracy)) throw new ArgumentOutOfRangeException(nameof(Accuracy), Accuracy, "The location accuracy is not defined.");
        if (Timeout < TimeSpan.FromSeconds(1) || Timeout > MaximumTimeout) throw new ArgumentOutOfRangeException(nameof(Timeout), Timeout, "A location request waits from one second to two minutes.");
        if (MaximumAge < TimeSpan.Zero || MaximumAge > MaximumAgeLimit) throw new ArgumentOutOfRangeException(nameof(MaximumAge), MaximumAge, "A location request accepts a fix of at most one day.");
    }
}

/// <summary>Reads the location of the device, after the user allows it.</summary>
/// <remarks>
/// The application declares what the system shows the user when it asks: <c>NSLocationWhenInUseUsageDescription</c> in the
/// <c>Info.plist</c> of an iOS application, and <c>android.permission.ACCESS_FINE_LOCATION</c> and
/// <c>android.permission.ACCESS_COARSE_LOCATION</c> (or only the latter, for an approximate location) in the manifest of an
/// Android application. The location is read while the application is in use, never in the background.
/// </remarks>
public interface INeoLocation
{
    /// <summary>Gets truthful platform support.</summary>
    NeoCapabilityInfo Support { get; }

    /// <summary>Reads what the application may read of the location, without asking the user.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The authorization; a platform without location answers <see cref="NeoLocationPermission.Denied"/> with the service off.</returns>
    ValueTask<NeoLocationAuthorization> GetAuthorizationAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks the user to let the application read the location while it is in use, when the application has not asked yet.</summary>
    /// <param name="cancellationToken">Stops waiting for the answer; the question of the system stays until the user answers it.</param>
    /// <returns>The authorization after the answer of the user, or the current one when the application already asked.</returns>
    ValueTask<NeoLocationAuthorization> RequestAuthorizationAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the current location of the device.</summary>
    /// <param name="request">The accuracy, the timeout, and the age of a fix that the request accepts.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The fix; <see cref="NeoDesktopStatus.Denied"/> with the code <c>permission</c> without the permission of the user, or with
    /// the code <c>service-disabled</c> when the location of the device is off; <see cref="NeoDesktopStatus.NotFound"/> with the
    /// code <c>timeout</c> when the device has no fix in time; <see cref="NeoDesktopStatus.Unsupported"/> on a platform without location.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A value of <paramref name="request"/> is out of its range.</exception>
    ValueTask<NeoDesktopResult<NeoLocationPosition>> GetCurrentPositionAsync(NeoLocationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Creates the statically selected location adapter.</summary>
public static class NeoLocation
{
    /// <summary>The code of a result that the user did not allow.</summary>
    public const string PermissionCode = "permission";

    /// <summary>The code of a result for a device whose location is switched off.</summary>
    public const string ServiceDisabledCode = "service-disabled";

    /// <summary>The code of a result for a device that had no fix in time.</summary>
    public const string TimeoutCode = "timeout";

    /// <summary>Creates the adapter of the current operating system: Core Location on iOS, the location manager on Android.</summary>
    /// <param name="dispatcher">Optional application UI dispatcher, on which iOS reads the location; the plugin binds it otherwise.</param>
    /// <returns>The adapter, which answers <see cref="NeoDesktopStatus.Unsupported"/> on a desktop.</returns>
    public static INeoLocation CreateSystem(NeoDispatcher? dispatcher = null)
    {
        if (OperatingSystem.IsIOS()) return IosLocation.Create(dispatcher);
        if (OperatingSystem.IsAndroid()) return new AndroidLocation();
        return new UnsupportedLocation("The location of a desktop is not available yet.");
    }

    internal static NeoDesktopResult<NeoLocationPosition> Failure(NeoDesktopStatus status, string? code) => new(status, null, code);
}

/// <summary>A deterministic location for tests, whose authorization and position the test sets.</summary>
public sealed class NeoFakeLocation : INeoLocation
{
    private readonly object _sync = new();
    private NeoLocationAuthorization _authorization = new(NeoLocationPermission.NotRequested, false, true);
    private NeoLocationPosition? _position;

    /// <inheritdoc />
    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Emulated, 1, 0, "In-memory test location.");

    /// <summary>Gets or sets the authorization that the fake answers.</summary>
    public NeoLocationAuthorization Authorization { get { lock (_sync) return _authorization; } set { ArgumentNullException.ThrowIfNull(value); lock (_sync) _authorization = value; } }

    /// <summary>Gets or sets the authorization that a request of the user gives, or <see langword="null"/> to keep the current one.</summary>
    public NeoLocationAuthorization? AnswerOfTheUser { get; set; }

    /// <summary>Gets or sets the position that the fake answers, or <see langword="null"/> for a device without a fix.</summary>
    public NeoLocationPosition? Position { get { lock (_sync) return _position; } set { lock (_sync) _position = value; } }

    /// <inheritdoc />
    public ValueTask<NeoLocationAuthorization> GetAuthorizationAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Authorization); }

    /// <inheritdoc />
    public ValueTask<NeoLocationAuthorization> RequestAuthorizationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_authorization.Permission == NeoLocationPermission.NotRequested) _authorization = AnswerOfTheUser ?? _authorization with { Permission = NeoLocationPermission.Denied };
            return ValueTask.FromResult(_authorization);
        }
    }

    /// <inheritdoc />
    public ValueTask<NeoDesktopResult<NeoLocationPosition>> GetCurrentPositionAsync(NeoLocationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_authorization.IsServiceEnabled) return ValueTask.FromResult(NeoLocation.Failure(NeoDesktopStatus.Denied, NeoLocation.ServiceDisabledCode));
            if (_authorization.Permission != NeoLocationPermission.Granted) return ValueTask.FromResult(NeoLocation.Failure(NeoDesktopStatus.Denied, NeoLocation.PermissionCode));
            return ValueTask.FromResult(_position is { } position ? NeoDesktopResult<NeoLocationPosition>.Success(position) : NeoLocation.Failure(NeoDesktopStatus.NotFound, NeoLocation.TimeoutCode));
        }
    }
}

internal sealed class UnsupportedLocation(string details) : INeoLocation
{
    private static readonly NeoLocationAuthorization Unavailable = new(NeoLocationPermission.Denied, false, false);

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.None, 1, 0, details);

    public ValueTask<NeoLocationAuthorization> GetAuthorizationAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Unavailable); }

    public ValueTask<NeoLocationAuthorization> RequestAuthorizationAsync(CancellationToken cancellationToken = default) => GetAuthorizationAsync(cancellationToken);

    public ValueTask<NeoDesktopResult<NeoLocationPosition>> GetCurrentPositionAsync(NeoLocationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(NeoLocation.Failure(NeoDesktopStatus.Unsupported, null));
    }
}

// What iOS binds to the UI dispatcher of the application when the plugin attaches.
internal interface INeoLocationDispatcherBinding
{
    void Bind(NeoDispatcher dispatcher);
}
