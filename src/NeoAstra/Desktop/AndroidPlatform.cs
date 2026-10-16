// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text;
using System.Text.Json;
using NeoAstra.Interop;

namespace NeoAstra.Desktop;

/// <summary>Reaches the Android services of the native backend, which run in Java, with one JSON request per call.</summary>
internal static class AndroidPlatform
{
    internal static JsonDocument Call(string service, params (string Name, string Value)[] fields)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in fields) writer.WriteString(name, value);
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(NeoPlatformCall.Invoke(service, Encoding.UTF8.GetString(stream.ToArray())));
    }

    internal static NeoDesktopStatus Status(JsonDocument response) =>
        response.RootElement.TryGetProperty("status", out var status) && Enum.TryParse<NeoDesktopStatus>(status.GetString(), out var value) ? value : NeoDesktopStatus.Failed;

    internal static string? Text(JsonDocument response, string name) =>
        response.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
