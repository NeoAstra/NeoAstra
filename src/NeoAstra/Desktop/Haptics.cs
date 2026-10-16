// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Desktop.Haptics;

/// <summary>Identifies a portable haptic feedback that a device plays, such as a tap or a confirmation.</summary>
public enum NeoHapticFeedback
{
    /// <summary>A light impact, such as a small control that snaps into place.</summary>
    Light,
    /// <summary>A medium impact.</summary>
    Medium,
    /// <summary>A heavy impact, such as a large element that lands.</summary>
    Heavy,
    /// <summary>A change of selection, such as a picker that moves to the next value.</summary>
    Selection,
    /// <summary>A task that succeeded.</summary>
    Success,
    /// <summary>A task that produced a warning.</summary>
    Warning,
    /// <summary>A task that failed.</summary>
    Error,
}

/// <summary>Plays haptic feedback on a device that has it.</summary>
public interface INeoHaptics
{
    /// <summary>Gets truthful platform support.</summary>
    NeoCapabilityInfo Support { get; }

    /// <summary>Plays one feedback.</summary>
    /// <param name="feedback">The feedback to play.</param>
    /// <param name="cancellationToken">Cancels the request before it reaches the device.</param>
    /// <returns><see cref="NeoDesktopStatus.Success"/>, or <see cref="NeoDesktopStatus.Unsupported"/> on a device without haptics.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="feedback"/> is not a defined value.</exception>
    ValueTask<NeoDesktopStatus> PlayAsync(NeoHapticFeedback feedback, CancellationToken cancellationToken = default);
}

/// <summary>Creates the statically selected haptic adapter.</summary>
public static class NeoHaptics
{
    /// <summary>Creates the adapter of the current operating system: UIKit feedback generators on iOS, the vibrator on Android.</summary>
    /// <param name="dispatcher">Optional application UI dispatcher, on which iOS plays feedback.</param>
    /// <returns>The adapter, which answers <see cref="NeoDesktopStatus.Unsupported"/> on a desktop.</returns>
    public static INeoHaptics CreateSystem(NeoDispatcher? dispatcher = null)
    {
        if (OperatingSystem.IsIOS()) return new IosHaptics(dispatcher);
        if (OperatingSystem.IsAndroid()) return new AndroidHaptics();
        return new UnsupportedHaptics("A desktop has no haptic feedback.");
    }

    internal static void Validate(NeoHapticFeedback feedback)
    {
        if (!Enum.IsDefined(feedback)) throw new ArgumentOutOfRangeException(nameof(feedback), feedback, "The haptic feedback is not defined.");
    }
}

internal sealed class UnsupportedHaptics(string details) : INeoHaptics
{
    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.None, 1, 0, details);

    public ValueTask<NeoDesktopStatus> PlayAsync(NeoHapticFeedback feedback, CancellationToken cancellationToken = default)
    {
        NeoHaptics.Validate(feedback);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(NeoDesktopStatus.Unsupported);
    }
}
