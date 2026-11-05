// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;

namespace NeoAstra.Desktop;

// The desktop entry of the application on Linux, the name of its desktop file without .desktop, as a notification names it in its
// desktop-entry hint and the launcher entry of a dock in its application:// URI. GTK names its windows after the GLib program
// name (the app_id of Wayland, the class of X11), which docks match with a desktop file: an application that sets it with
// g_set_prgname has the desktop file of that name, so the program name wins over the application ID.
internal static partial class LinuxDesktopEntry
{
    // The program name read now, or the application ID: null when neither can name a desktop file.
    internal static string? Resolve(string? applicationId) => Name(OperatingSystem.IsLinux() ? ProgramName() : null, applicationId);

    internal static string? Name(string? programName, string? applicationId) => Valid(programName) ?? Valid(applicationId);

    private static string? Valid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var name = value.Trim();
        return name.Length > 240 || name.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character) || character is '/' or '\\') ? null : name;
    }

    // The name that g_set_prgname gave, or null: GLib sets none by itself, and GTK then calls its windows "GTK Application".
    private static string? ProgramName()
    {
        try { var value = g_get_prgname(); return value == 0 ? null : Marshal.PtrToStringUTF8(value); }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    [LibraryImport("libglib-2.0.so.0")] private static partial nint g_get_prgname();
}
