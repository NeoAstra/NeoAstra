// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text;
using System.Text.Json;
using NeoAstra.Desktop;

namespace NeoAstra.Location;

// The location manager of Android through the Java service `neoastra.location` of this package, which the platform services of
// NeoAstra reach. The Java service blocks until the user answers or a fix arrives, so every call leaves the calling thread.
internal sealed class AndroidLocation : INeoLocation
{
    internal const string Service = "neoastra.location";

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Native, 1, 0,
        "The location manager of Android while the application is in use: the precise or approximate location that the user allows (android.permission.ACCESS_FINE_LOCATION, android.permission.ACCESS_COARSE_LOCATION).");

    public ValueTask<NeoLocationAuthorization> GetAuthorizationAsync(CancellationToken cancellationToken = default)
        => new(Task.Run(() => Authorization(Call("authorization", null)), cancellationToken));

    public ValueTask<NeoLocationAuthorization> RequestAuthorizationAsync(CancellationToken cancellationToken = default)
        => new(Task.Run(() => Authorization(Call("authorize", null)), cancellationToken).WaitAsync(cancellationToken));

    public ValueTask<NeoDesktopResult<NeoLocationPosition>> GetCurrentPositionAsync(NeoLocationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var accuracy = request.Accuracy;
        var timeout = (long)request.Timeout.TotalMilliseconds;
        var maximumAge = (long)request.MaximumAge.TotalMilliseconds;
        return new(Task.Run(() => Position(Call("current", writer =>
        {
            writer.WriteString("accuracy", accuracy.ToString());
            writer.WriteNumber("timeout", timeout);
            writer.WriteNumber("maximumAge", maximumAge);
        })), cancellationToken).WaitAsync(cancellationToken));
    }

    internal static JsonDocument Call(string operation, Action<Utf8JsonWriter>? write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", operation);
            write?.Invoke(writer);
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(NeoPlatformServices.Call(Service, Encoding.UTF8.GetString(stream.ToArray())));
    }

    internal static NeoLocationAuthorization Authorization(JsonDocument response)
    {
        using (response)
        {
            var root = response.RootElement;
            if (Status(root) != NeoDesktopStatus.Success) return new(NeoLocationPermission.Denied, false, false);
            var permission = root.TryGetProperty("permission", out var value) && Enum.TryParse<NeoLocationPermission>(value.GetString(), out var parsed) ? parsed : NeoLocationPermission.Denied;
            return new(permission, Flag(root, "precise"), Flag(root, "enabled"));
        }
    }

    internal static NeoDesktopResult<NeoLocationPosition> Position(JsonDocument response)
    {
        using (response)
        {
            var root = response.RootElement;
            var status = Status(root);
            var code = root.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (status != NeoDesktopStatus.Success) return NeoLocation.Failure(status, code);
            return NeoDesktopResult<NeoLocationPosition>.Success(new(root.GetProperty("latitude").GetDouble(), root.GetProperty("longitude").GetDouble(), root.GetProperty("accuracy").GetDouble(),
                DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("timestamp").GetInt64()))
            {
                Altitude = Number(root, "altitude"),
                AltitudeAccuracyMeters = Number(root, "altitudeAccuracy"),
                SpeedMetersPerSecond = Number(root, "speed"),
                CourseDegrees = Number(root, "course"),
            });
        }
    }

    private static NeoDesktopStatus Status(JsonElement root) =>
        root.TryGetProperty("status", out var status) && Enum.TryParse<NeoDesktopStatus>(status.GetString(), out var value) ? value : NeoDesktopStatus.Failed;

    private static bool Flag(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static double? Number(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}
