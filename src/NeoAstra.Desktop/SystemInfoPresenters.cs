// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace NeoAstra.Desktop.SystemInfo;

internal readonly record struct NeoSystemPlatformSnapshot(NeoThemeSnapshot Theme, IReadOnlyList<NeoDisplaySnapshot> Displays);

internal static partial class NeoSystemInfoPlatform
{
    internal static NeoCapabilityInfo ThemeSupport => OperatingSystem.IsWindows()
        ? new(NeoSupportLevel.Native, 1, 0, "Win32 high contrast, application theme, accent, animation, and transparency preferences are polled and changes are coalesced on the UI dispatcher.")
        : OperatingSystem.IsMacOS()
            ? new(NeoSupportLevel.Limited, 1, 0, "macOS appearance and accessibility preferences are read through fixed system preference queries; accent color is unavailable from this presenter.")
            : OperatingSystem.IsLinux()
                ? new(NeoSupportLevel.Limited, 1, 0, "Freedesktop/GNOME color-scheme and accessibility preferences are queried when gsettings is available; desktop environments may not expose every preference.")
                : new(NeoSupportLevel.None, 1, 0, "No supported system theme presenter.");

    internal static NeoCapabilityInfo DisplaySupport => OperatingSystem.IsWindows()
        ? new(NeoSupportLevel.Native, 1, 0, "Win32 monitor topology, work areas, primary display, stable device IDs, and per-monitor DPI in logical desktop coordinates.")
        : OperatingSystem.IsMacOS()
            ? new(NeoSupportLevel.Limited, 1, 0, "CoreGraphics active-display topology, rotation, refresh, scale, and primary display; work area equals bounds because Cocoa visible-frame integration is unavailable.")
            : OperatingSystem.IsLinux()
                ? new(NeoSupportLevel.Limited, 1, 0, "XRandR topology when an X11 desktop helper is available; Wayland compositors without XWayland do not expose portable monitor topology through this presenter.")
                : new(NeoSupportLevel.None, 1, 0, "No supported display presenter.");

    internal static NeoThemeSnapshot ReadInitialTheme()
    {
        try { return OperatingSystem.IsWindows() ? ReadWindowsTheme() : new(NeoSystemTheme.Unknown, null, null, null); }
        catch { return new(NeoSystemTheme.Unknown, null, null, null); }
    }

    internal static IReadOnlyList<NeoDisplaySnapshot> ReadInitialDisplays()
    {
        try { return OperatingSystem.IsWindows() ? ReadWindowsDisplays() : Array.Empty<NeoDisplaySnapshot>(); }
        catch { return Array.Empty<NeoDisplaySnapshot>(); }
    }

    internal static async ValueTask<NeoSystemPlatformSnapshot> ReadAsync()
    {
        if (OperatingSystem.IsWindows()) return new(ReadWindowsTheme(), ReadWindowsDisplays());
        if (OperatingSystem.IsMacOS()) return new(await ReadMacThemeAsync().ConfigureAwait(false), ReadMacDisplays());
        if (OperatingSystem.IsLinux()) return await ReadLinuxAsync().ConfigureAwait(false);
        return new(new(NeoSystemTheme.Unknown, null, null, null), Array.Empty<NeoDisplaySnapshot>());
    }

    [SupportedOSPlatform("windows")]
    private static unsafe NeoThemeSnapshot ReadWindowsTheme()
    {
        var highContrast = new HighContrast { Size = (uint)sizeof(HighContrast) };
        var isHighContrast = SystemParametersInfo(0x0042, highContrast.Size, &highContrast, 0) && (highContrast.Flags & 1) != 0;
        var light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is not int value || value != 0;
        var transparency = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1) is not int transparent || transparent == 0;
        int animation = 1;
        bool? reducedMotion = SystemParametersInfo(0x1042, 0, &animation, 0) ? animation == 0 : null;
        string? accent = null;
        if (DwmGetColorizationColor(out var color, out _) == 0) accent = $"#{(color >> 16) & 0xff:X2}{(color >> 8) & 0xff:X2}{color & 0xff:X2}";
        return new(isHighContrast ? NeoSystemTheme.HighContrast : light ? NeoSystemTheme.Light : NeoSystemTheme.Dark, accent, reducedMotion, transparency);
    }

    private static unsafe IReadOnlyList<NeoDisplaySnapshot> ReadWindowsDisplays()
    {
        var values = new List<NeoDisplaySnapshot>();
        var handle = GCHandle.Alloc(values);
        try
        {
            if (!EnumDisplayMonitors(0, 0, &MonitorCallback, GCHandle.ToIntPtr(handle)) || values.Count == 0) return Array.Empty<NeoDisplaySnapshot>();
            return Array.AsReadOnly(values.OrderByDescending(static display => display.IsPrimary).ThenBy(static display => display.Id, StringComparer.Ordinal).ToArray());
        }
        finally { handle.Free(); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe int MonitorCallback(nint monitor, nint deviceContext, NativeRect* bounds, nint data)
    {
        try
        {
            var info = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) };
            if (!GetMonitorInfo(monitor, &info)) return 1;
            uint dpiX = 96, dpiY = 96;
            if (GetDpiForMonitor(monitor, 0, out dpiX, out dpiY) != 0 || dpiX is < 24 or > 1536) dpiX = 96;
            var scale = dpiX / 96d;
            var snapshot = new NeoDisplaySnapshot(
                new string(info.Device).TrimEnd('\0'),
                Logical(info.Monitor, scale),
                Logical(info.Work, scale),
                scale,
                (info.Flags & 1) != 0,
                null,
                null);
            ((List<NeoDisplaySnapshot>)GCHandle.FromIntPtr(data).Target!).Add(snapshot);
        }
        catch { }
        return 1;
    }

    private static NeoRect Logical(NativeRect value, double scale) => new(
        checked((int)Math.Round(value.Left / scale)), checked((int)Math.Round(value.Top / scale)),
        checked((int)Math.Round((value.Right - value.Left) / scale)), checked((int)Math.Round((value.Bottom - value.Top) / scale)));

    private static async ValueTask<NeoThemeSnapshot> ReadMacThemeAsync()
    {
        var executable = DesktopProcess.FindTrustedExecutable("/usr/bin/defaults");
        if (string.IsNullOrEmpty(executable)) return new(NeoSystemTheme.Unknown, null, null, null);
        var dark = await ReadDefaultsBoolAsync(executable, ["read", "-g", "AppleInterfaceStyle"], expectedText: "Dark").ConfigureAwait(false);
        var reducedMotion = await ReadDefaultsBoolAsync(executable, ["read", "com.apple.universalaccess", "reduceMotion"]).ConfigureAwait(false);
        var reducedTransparency = await ReadDefaultsBoolAsync(executable, ["read", "com.apple.universalaccess", "reduceTransparency"]).ConfigureAwait(false);
        return new(dark == true ? NeoSystemTheme.Dark : NeoSystemTheme.Light, null, reducedMotion, reducedTransparency);
    }

    private static unsafe IReadOnlyList<NeoDisplaySnapshot> ReadMacDisplays()
    {
        uint count = 0;
        if (CGGetActiveDisplayList(0, null, &count) != 0 || count is 0 or > 64) return Array.Empty<NeoDisplaySnapshot>();
        var ids = stackalloc uint[(int)count];
        if (CGGetActiveDisplayList(count, ids, &count) != 0) return Array.Empty<NeoDisplaySnapshot>();
        var main = CGMainDisplayID();
        var values = new NeoDisplaySnapshot[count];
        for (var index = 0; index < count; index++)
        {
            var id = ids[index];
            var bounds = CGDisplayBounds(id);
            var mode = CGDisplayCopyDisplayMode(id);
            double refresh = 0; double scale = 1;
            if (mode != 0)
            {
                try
                {
                    refresh = CGDisplayModeGetRefreshRate(mode);
                    var width = CGDisplayModeGetWidth(mode);
                    var pixels = CGDisplayModeGetPixelWidth(mode);
                    if (width != 0 && pixels >= width) scale = pixels / (double)width;
                }
                finally { CFRelease(mode); }
            }
            var logical = new NeoRect(checked((int)Math.Round(bounds.X)), checked((int)Math.Round(bounds.Y)), checked((int)Math.Round(bounds.Width)), checked((int)Math.Round(bounds.Height)));
            values[index] = new($"cg:{id}", logical, logical, scale is >= 0.25 and <= 16 ? scale : 1, id == main, NormalizeRotation(CGDisplayRotation(id)), refresh is >= 1 and <= 1000 ? refresh : null);
        }
        return Array.AsReadOnly(values.OrderByDescending(static display => display.IsPrimary).ThenBy(static display => display.Id, StringComparer.Ordinal).ToArray());
    }

    private static int? NormalizeRotation(double rotation)
    {
        var normalized = ((int)Math.Round(rotation) % 360 + 360) % 360;
        return normalized is 0 or 90 or 180 or 270 ? normalized : null;
    }

    private static async ValueTask<NeoSystemPlatformSnapshot> ReadLinuxAsync()
    {
        var gsettings = DesktopProcess.FindTrustedExecutable("/usr/bin/gsettings", "/usr/local/bin/gsettings");
        var theme = NeoSystemTheme.Unknown;
        bool? reducedMotion = null;
        if (!string.IsNullOrEmpty(gsettings))
        {
            var scheme = await RunTextAsync(gsettings, ["get", "org.gnome.desktop.interface", "color-scheme"]).ConfigureAwait(false);
            if (scheme.Contains("dark", StringComparison.OrdinalIgnoreCase)) theme = NeoSystemTheme.Dark;
            else if (scheme.Length != 0) theme = NeoSystemTheme.Light;
            reducedMotion = await ReadGSettingsBoolAsync(gsettings, "org.gnome.desktop.interface", "enable-animations", invert: true).ConfigureAwait(false);
            if (await ReadGSettingsBoolAsync(gsettings, "org.gnome.desktop.a11y.interface", "high-contrast", invert: false).ConfigureAwait(false) == true) theme = NeoSystemTheme.HighContrast;
        }
        var displays = await ReadXrandrAsync().ConfigureAwait(false);
        return new(new(theme, null, reducedMotion, null), displays);
    }

    private static async ValueTask<IReadOnlyList<NeoDisplaySnapshot>> ReadXrandrAsync()
    {
        var executable = DesktopProcess.FindTrustedExecutable("/usr/bin/xrandr", "/usr/local/bin/xrandr");
        if (string.IsNullOrEmpty(executable) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return Array.Empty<NeoDisplaySnapshot>();
        var text = await RunTextAsync(executable, ["--query"]).ConfigureAwait(false);
        var values = new List<NeoDisplaySnapshot>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains(" connected ", StringComparison.Ordinal) || !TryXrandrGeometry(line, out var id, out var bounds, out var primary)) continue;
            values.Add(new(id, bounds, bounds, 1, primary, null, null));
        }
        if (values.Count == 0) return Array.Empty<NeoDisplaySnapshot>();
        if (values.Count(static value => value.IsPrimary) == 0) values[0] = values[0] with { IsPrimary = true };
        return Array.AsReadOnly(values.ToArray());
    }

    private static bool TryXrandrGeometry(string line, out string id, out NeoRect bounds, out bool primary)
    {
        id = line.Split(' ', 2)[0]; primary = line.Contains(" connected primary ", StringComparison.Ordinal); bounds = default;
        foreach (var token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var x = token.IndexOf('x');
            var plus1 = x < 1 ? -1 : token.IndexOf('+', x + 1);
            var plus2 = plus1 < 0 ? -1 : token.IndexOf('+', plus1 + 1);
            if (plus2 < 0 || !int.TryParse(token.AsSpan(0, x), NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
                !int.TryParse(token.AsSpan(x + 1, plus1 - x - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var height) ||
                !int.TryParse(token.AsSpan(plus1 + 1, plus2 - plus1 - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var left) ||
                !int.TryParse(token.AsSpan(plus2 + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var top) || width <= 0 || height <= 0) continue;
            bounds = new(left, top, width, height); return true;
        }
        return false;
    }

    private static async ValueTask<bool?> ReadDefaultsBoolAsync(string executable, IReadOnlyList<string> arguments, string? expectedText = null)
    {
        var text = await RunTextAsync(executable, arguments).ConfigureAwait(false);
        if (expectedText is not null) return string.Equals(text.Trim(), expectedText, StringComparison.OrdinalIgnoreCase);
        return text.Trim() switch { "1" or "true" or "TRUE" => true, "0" or "false" or "FALSE" => false, _ => null };
    }

    private static async ValueTask<bool?> ReadGSettingsBoolAsync(string executable, string schema, string key, bool invert)
    {
        var text = (await RunTextAsync(executable, ["get", schema, key]).ConfigureAwait(false)).Trim();
        return text switch { "true" => !invert, "false" => invert, _ => null };
    }

    private static async ValueTask<string> RunTextAsync(string executable, IReadOnlyList<string> arguments)
    {
        try
        {
            var result = await DesktopProcess.RunAsync(executable, arguments, default, TimeSpan.FromSeconds(10), true, CancellationToken.None).ConfigureAwait(false);
            if (result.ExitCode != 0) { Array.Clear(result.Output); return string.Empty; }
            try { return new UTF8Encoding(false, true).GetString(result.Output).Trim(); }
            finally { Array.Clear(result.Output); }
        }
        catch { return string.Empty; }
    }

    [StructLayout(LayoutKind.Sequential)] private unsafe struct HighContrast { internal uint Size; internal uint Flags; internal char* DefaultScheme; }
    [StructLayout(LayoutKind.Sequential)] private readonly struct NativeRect { internal readonly int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private unsafe struct MonitorInfo
    {
        internal uint Size; internal NativeRect Monitor; internal NativeRect Work; internal uint Flags; internal fixed char Device[32];
    }
    [StructLayout(LayoutKind.Sequential)] private readonly struct CgPoint { internal readonly double X, Y; }
    [StructLayout(LayoutKind.Sequential)] private readonly struct CgSize { internal readonly double Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private readonly struct CgRect { internal readonly double X, Y, Width, Height; }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static unsafe partial bool SystemParametersInfo(uint action, uint parameter, void* value, uint flags);
    [LibraryImport("dwmapi.dll")] private static partial int DwmGetColorizationColor(out uint color, [MarshalAs(UnmanagedType.Bool)] out bool opaque);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static unsafe partial bool EnumDisplayMonitors(nint deviceContext, nint clip, delegate* unmanaged[Stdcall]<nint, nint, NativeRect*, nint, int> callback, nint data);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static unsafe partial bool GetMonitorInfo(nint monitor, MonitorInfo* info);
    [LibraryImport("shcore.dll")] private static partial int GetDpiForMonitor(nint monitor, int kind, out uint x, out uint y);

    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static unsafe partial int CGGetActiveDisplayList(uint maximum, uint* displays, uint* count);
    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static partial uint CGMainDisplayID();
    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static partial CgRect CGDisplayBounds(uint display);
    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static partial double CGDisplayRotation(uint display);
    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static partial nint CGDisplayCopyDisplayMode(uint display);
    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static partial nuint CGDisplayModeGetWidth(nint mode);
    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static partial nuint CGDisplayModeGetPixelWidth(nint mode);
    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static partial double CGDisplayModeGetRefreshRate(nint mode);
    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static partial void CFRelease(nint value);
}
