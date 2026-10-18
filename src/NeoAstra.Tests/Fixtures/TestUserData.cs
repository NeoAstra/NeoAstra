// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Tests;

/// <summary>The browser-data folders of the tests.</summary>
internal static class TestUserData
{
    /// <summary>The folder that the environments of the tests share when they need no folder of their own.</summary>
    /// <remarks>
    /// Without a folder, WebView2 keeps its data beside the executable of the process. On Windows on Arm the test host
    /// runs in <c>dotnet.exe</c>, whose folder under Program Files cannot be written, and the environment is refused
    /// (E_ACCESSDENIED).
    /// </remarks>
    internal static string Shared { get; } = Path.Combine(Path.GetTempPath(), "neoastra-tests", "shared");
}
