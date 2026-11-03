// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using NeoAstra.Interop;

namespace NeoAstra;

/// <summary>Calls the services that only the native backend reaches: on Android, the Java services of NeoAstra and of its plugins.</summary>
/// <remarks>
/// A plugin that needs Java adds a service with <c>com.neoastra.backend.NeoAstraServices.register</c> under a dotted name of its
/// own, such as <c>neoastra.location</c>, and calls it here. A call blocks the calling thread until the service answers, and a
/// service that waits for the user waits on it: call a service from a background thread, never from the UI thread.
/// </remarks>
public static class NeoPlatformServices
{
    /// <summary>Sends one JSON request to a platform service and returns its JSON response.</summary>
    /// <param name="service">The name of the service: 1 to 64 lowercase ASCII letters, digits, dots, and hyphens.</param>
    /// <param name="request">The request, a JSON object.</param>
    /// <returns>The response, a JSON object; a service that the backend does not know answers <c>{"status":"Unsupported"}</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> or <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The service name is not valid.</exception>
    /// <exception cref="NotSupportedException">The backend of this platform has no platform services.</exception>
    /// <exception cref="NeoAstraException">The service failed or did not accept the request.</exception>
    public static string Call(string service, string request)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(request);
        if (service.Length is < 1 or > 64 || !service.All(static value => value is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-'))
            throw new ArgumentException("A platform service name has 1 to 64 lowercase ASCII letters, digits, dots, and hyphens.", nameof(service));
        return NeoPlatformCall.Invoke(service, request);
    }
}
