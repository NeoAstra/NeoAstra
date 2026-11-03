// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text;
using NeoAstra.Interop.Generated;

namespace NeoAstra.Interop;

/// <summary>Calls a platform service that only the native backend can reach; the Android backend answers through Java.</summary>
internal static unsafe class NeoPlatformCall
{
    /// <summary>Sends one JSON request to a service and returns its JSON response.</summary>
    /// <exception cref="NotSupportedException">The backend of this platform has no platform services.</exception>
    /// <exception cref="ArgumentException">The backend has no such service or the request is not valid.</exception>
    /// <exception cref="NeoAstraException">The service failed.</exception>
    internal static string Invoke(string service, string request)
    {
        NativeLibraryLoader.EnsureLoaded();
        using var serviceText = new Utf8String(service);
        using var requestText = new Utf8String(request);
        NativeMethods.neoastra_buffer_t buffer = default;
        NativeMethods.neoastra_error_t error = default;
        var result = NativeMethods.neoastra_platform_call(serviceText.View, requestText.View, &buffer, &error);
        NativeError.ThrowIfFailed(result, error, $"Platform service '{service}'");
        using var owned = new SafeBufferHandle(buffer.Handle);
        if (buffer.Handle == 0) return "{}";
        var length = NativeMethods.neoastra_buffer_get_length(buffer);
        if (length > int.MaxValue) throw new InvalidDataException("The platform service response is too large.");
        var data = NativeMethods.neoastra_buffer_get_data(buffer);
        return length == 0 || data is null ? "{}" : Encoding.UTF8.GetString(data, (int)length);
    }
}
