// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

#if NEOASTRA_TOOL
namespace NeoAstra.Tool.Shared.Interop;
#else
namespace NeoAstra.Interop;
#endif

internal static class NeoNativeAbi
{
    internal const uint Major = 1;
    internal const uint Minor = 0;

    // Pre-release minor labels are not compatibility boundaries until the first release.
    internal static bool IsCompatible(uint major, uint minor)
    {
        _ = minor;
        return major == Major;
    }
}
