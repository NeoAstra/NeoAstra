// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Desktop.Haptics;

// The vibrator of the device through the Java services; the application declares android.permission.VIBRATE.
internal sealed class AndroidHaptics : INeoHaptics
{
    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Native, 1, 0,
        "Predefined vibration effects of the device vibrator (android.permission.VIBRATE); a device without a vibrator answers Unsupported.");

    public ValueTask<NeoDesktopStatus> PlayAsync(NeoHapticFeedback feedback, CancellationToken cancellationToken = default)
    {
        NeoHaptics.Validate(feedback);
        cancellationToken.ThrowIfCancellationRequested();
        using var response = AndroidPlatform.Call("haptics", ("feedback", feedback.ToString()));
        return ValueTask.FromResult(AndroidPlatform.Status(response));
    }
}
