// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.Json;

namespace NeoAstra.Desktop.SystemInfo;

internal static partial class NeoSystemInfoPlatform
{
    // The night mode of the configuration and the animator duration scale, read when the services are created.
    private static NeoThemeSnapshot ReadAndroidTheme()
    {
        using var response = AndroidPlatform.Call("theme");
        var theme = AndroidPlatform.Text(response, "theme") switch { "Light" => NeoSystemTheme.Light, "Dark" => NeoSystemTheme.Dark, _ => NeoSystemTheme.Unknown };
        bool? reducedMotion = response.RootElement.TryGetProperty("reducedMotion", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
        return new(theme, null, reducedMotion, null);
    }
}
