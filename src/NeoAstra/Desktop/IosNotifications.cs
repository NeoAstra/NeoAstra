// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;

namespace NeoAstra.Desktop.Notifications;

/// <summary>iOS has the UNUserNotificationCenter of macOS: the same presenter serves it once the framework is loaded.</summary>
internal static class IosNotifications
{
    private const string Framework = "/System/Library/Frameworks/UserNotifications.framework/UserNotifications";

    internal static INeoNotifications Create(string applicationId)
        => NativeLibrary.TryLoad(Framework, out _) ? new MacNotifications(applicationId) : new UnsupportedNotifications("The UserNotifications framework is unavailable.");
}
