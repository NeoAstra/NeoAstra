// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Tests;

/// <summary>Checks that a desktop backend refuses a call to a platform service of iOS or Android.</summary>
internal static class DesktopPlatformCall
{
    // A desktop backend answers with NotSupportedException. Where the native library cannot load at all, as on Linux without GTK,
    // no backend answers; a library that loads but lacks the export of the call still fails the check.
    public static void AssertNotSupported(Action call)
    {
        try { call(); }
        catch (NotSupportedException) { return; }
        catch (NeoAstraNativeLibraryException exception) when (!exception.Probes.Any(static probe => probe.Result == NeoNativeLibraryProbeResult.Loaded)) { return; }
        Assert.Fail("A desktop backend must refuse a platform call with NotSupportedException.");
    }

    // What a desktop backend answers for a value of a platform service, under the same rule for a library that cannot load.
    public static void AssertAnswers<T>(Func<T> read, T expected)
    {
        T actual;
        try { actual = read(); }
        catch (NeoAstraNativeLibraryException exception) when (!exception.Probes.Any(static probe => probe.Result == NeoNativeLibraryProbeResult.Loaded)) { return; }
        Assert.AreEqual(expected, actual);
    }
}
