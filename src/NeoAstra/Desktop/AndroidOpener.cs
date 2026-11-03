// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Desktop.Opener;

/// <summary>Hands a web address to Android, which opens it in the application that the user chose for it.</summary>
internal static class AndroidOpener
{
    internal static NeoDesktopStatus OpenUrl(string url)
    {
        using var response = AndroidPlatform.Call("opener", ("url", url));
        return AndroidPlatform.Status(response);
    }
}
