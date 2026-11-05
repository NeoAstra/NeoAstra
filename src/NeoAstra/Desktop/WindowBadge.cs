// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace NeoAstra.Desktop.WindowState;

// What a badge becomes on the backends that have no text badge of their own: the overlay icon of a Windows taskbar button and the
// count of a Unity launcher entry on Linux.
internal static class NeoWindowBadge
{
    private const int MaximumOverlayLength = 2;

    // A 16-pixel overlay holds two characters: a number of one or two digits stays, a larger one (or one that already ends with a
    // plus, as "99+") becomes "9+", and other text keeps its first two text elements. Null for no badge.
    internal static string? OverlayText(string? badge)
    {
        if (string.IsNullOrWhiteSpace(badge)) return null;
        var text = badge.Trim();
        if (TryNumber(text, out var digits, out var plus))
            return digits.Length == 1 ? (plus ? digits + "+" : digits) : digits.Length == 2 && !plus ? digits : "9+";
        var length = 0;
        for (var count = 0; count < MaximumOverlayLength && length < text.Length; count++) length += StringInfo.GetNextTextElementLength(text, length);
        return text[..length];
    }

    // A launcher entry shows a number only: "99+" shows 99, and text that is not a number has no count.
    internal static bool TryLauncherCount(string badge, out long count)
    {
        count = 0;
        return TryNumber(badge.Trim(), out var digits, out _) && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out count);
    }

    private static bool TryNumber(string text, out string digits, out bool plus)
    {
        plus = text.EndsWith('+');
        digits = plus ? text[..^1] : text;
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) return false;
        digits = digits.TrimStart('0');
        if (digits.Length == 0) digits = "0";
        return true;
    }

    // The picture of an overlay icon, top-down BGRA with straight alpha as a 32-bit icon wants it: a red disc with a smoothed edge,
    // white where the coverage of the text (one byte a pixel, 0 to 255, drawn by the font renderer) says.
    internal static byte[] RenderOverlay(int size, ReadOnlySpan<byte> textCoverage)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 8);
        if (textCoverage.Length != size * size) throw new ArgumentException("The text coverage must have one byte a pixel.", nameof(textCoverage));
        const int samples = 4; const byte red = 0xD1, green = 0x34, blue = 0x38; // #D13438
        var pixels = new byte[size * size * 4];
        var center = size / 2.0; var radius = size / 2.0;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var inside = 0;
                for (var sy = 0; sy < samples; sy++)
                    for (var sx = 0; sx < samples; sx++)
                    {
                        var dx = x + (sx + 0.5) / samples - center; var dy = y + (sy + 0.5) / samples - center;
                        if (dx * dx + dy * dy <= radius * radius) inside++;
                    }
                var index = (y * size + x) * 4; var text = textCoverage[y * size + x];
                pixels[index] = Blend(blue, text); pixels[index + 1] = Blend(green, text); pixels[index + 2] = Blend(red, text);
                pixels[index + 3] = (byte)((inside * 255 + samples * samples / 2) / (samples * samples));
            }
        return pixels;

        static byte Blend(byte color, byte white) => (byte)((color * (255 - white) + 255 * white + 127) / 255);
    }
}

// A Unity launcher entry (com.canonical.Unity.LauncherEntry) of the desktop file of the application: an Update signal on the
// session bus that the docks which read it (KDE Plasma, Ubuntu Dock and Dash to Dock, Plank, Latte) show on the icon of the
// application that has this desktop file (see LinuxDesktopEntry). A signal changes only the properties it carries.
internal sealed unsafe partial class LinuxLauncherEntry : IDisposable
{
    private const string Interface = "com.canonical.Unity.LauncherEntry";
    private readonly object _sync = new();
    private nint _connection;
    private bool _connected, _disposed;

    // The desktop entry is the name of the desktop file without .desktop: codealta gives application://codealta.desktop.
    internal LinuxLauncherEntry(string desktopEntry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desktopEntry);
        ApplicationUri = "application://" + desktopEntry + ".desktop";
        ObjectPath = "/com/canonical/unity/launcherentry/" + Fnv1a(ApplicationUri).ToString(CultureInfo.InvariantCulture);
    }

    internal string ApplicationUri { get; }
    internal string ObjectPath { get; }

    internal static string CountProperties(long? count) => count is { } value
        ? "{'count': <int64 " + value.ToString(CultureInfo.InvariantCulture) + ">, 'count-visible': <true>}"
        : "{'count-visible': <false>}";

    internal static string ProgressProperties(double? progress) => progress is { } value
        ? "{'progress': <double " + Math.Clamp(value, 0, 1).ToString("0.0###", CultureInfo.InvariantCulture) + ">, 'progress-visible': <true>}"
        : "{'progress-visible': <false>}";

    internal static string UrgentProperties(bool urgent) => urgent ? "{'urgent': <true>}" : "{'urgent': <false>}";

    // The text of the (sa{sv}) parameters of an Update signal, in the GVariant text format.
    internal string UpdateParameters(string properties) => "(" + Quote(ApplicationUri) + ", " + properties + ")";

    internal NeoDesktopStatus Update(string properties)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(properties);
        lock (_sync)
        {
            if (_disposed) return NeoDesktopStatus.Failed;
            if (!Connect()) return NeoDesktopStatus.Unsupported;
            nint error = 0, value = 0; var type = Native.g_variant_type_new("(sa{sv})");
            try
            {
                value = Native.g_variant_parse(type, UpdateParameters(properties), 0, 0, &error);
                if (value == 0) { FreeError(error); return NeoDesktopStatus.Failed; }
                // A parsed value is not floating, so the signal does not take it: it is released here.
                value = Native.g_variant_take_ref(value);
                var sent = Native.g_dbus_connection_emit_signal(_connection, 0, ObjectPath, Interface, "Update", value, &error);
                FreeError(error);
                return sent ? NeoDesktopStatus.Success : NeoDesktopStatus.Failed;
            }
            finally { if (value != 0) Native.g_variant_unref(value); Native.g_variant_type_free(type); }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return; _disposed = true;
            if (_connection == 0) return;
            // The signals still queued leave before the connection goes.
            try { Native.g_dbus_connection_flush_sync(_connection, 0, 0); Native.g_object_unref(_connection); } catch { }
            _connection = 0;
        }
    }

    private bool Connect()
    {
        if (_connected) return _connection != 0;
        _connected = true;
        try
        {
            nint error = 0; _connection = Native.g_bus_get_sync(2, 0, &error); FreeError(error); // G_BUS_TYPE_SESSION
        }
        catch (DllNotFoundException) { _connection = 0; }
        catch (EntryPointNotFoundException) { _connection = 0; }
        return _connection != 0;
    }

    private static void FreeError(nint error) { if (error != 0) Native.g_error_free(error); }

    private static uint Fnv1a(string value)
    {
        var hash = 2166136261u;
        foreach (var item in Encoding.UTF8.GetBytes(value)) hash = (hash ^ item) * 16777619u;
        return hash;
    }

    internal static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var character in value)
        {
            if (character is '\\' or '\'') builder.Append('\\').Append(character);
            else if (char.IsControl(character)) builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            else builder.Append(character);
        }
        return builder.Append('\'').ToString();
    }

    private static partial class Native
    {
        private const string Gio = "libgio-2.0.so.0";
        private const string Glib = "libglib-2.0.so.0";
        private const string GObject = "libgobject-2.0.so.0";
        [LibraryImport(Gio)] internal static partial nint g_bus_get_sync(int busType, nint cancellable, nint* error);
        [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool g_dbus_connection_emit_signal(nint connection, nint destination, string objectPath, string interfaceName, string signalName, nint parameters, nint* error);
        [LibraryImport(Gio)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool g_dbus_connection_flush_sync(nint connection, nint cancellable, nint error);
        [LibraryImport(Glib, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint g_variant_type_new(string typeString);
        [LibraryImport(Glib)] internal static partial void g_variant_type_free(nint type);
        [LibraryImport(Glib, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint g_variant_parse(nint type, string text, nint limit, nint endPointer, nint* error);
        [LibraryImport(Glib)] internal static partial nint g_variant_take_ref(nint value);
        [LibraryImport(Glib)] internal static partial void g_variant_unref(nint value);
        [LibraryImport(Glib)] internal static partial void g_error_free(nint error);
        [LibraryImport(GObject)] internal static partial void g_object_unref(nint value);
    }
}
