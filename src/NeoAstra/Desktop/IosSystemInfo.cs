// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;

namespace NeoAstra.Desktop.SystemInfo;

internal static partial class NeoSystemInfoPlatform
{
    private const string UIKit = "/System/Library/Frameworks/UIKit.framework/UIKit";

    // UIKit reads traits on the main thread, where the services are created; a later change is not observed yet.
    private static NeoThemeSnapshot ReadIosTheme()
    {
        var traits = IosObjC.Send(IosObjC.Send(IosObjC.Class("UIScreen"), "mainScreen"), "traitCollection");
        var style = (long)IosObjC.Send(traits, "userInterfaceStyle");
        var theme = style switch { 1 => NeoSystemTheme.Light, 2 => NeoSystemTheme.Dark, _ => NeoSystemTheme.Unknown };
        return new(theme, null, IsReduceMotionEnabled(), IsReduceTransparencyEnabled());
    }

    [LibraryImport(UIKit, EntryPoint = "UIAccessibilityIsReduceMotionEnabled")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool IsReduceMotionEnabled();

    [LibraryImport(UIKit, EntryPoint = "UIAccessibilityIsReduceTransparencyEnabled")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool IsReduceTransparencyEnabled();
}
