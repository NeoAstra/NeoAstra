// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Desktop.Haptics;

// The feedback generators of UIKit, which belong to the main thread. A device without a Taptic Engine plays nothing and reports
// no error, which UIKit does not tell an application either.
internal sealed class IosHaptics(NeoDispatcher? dispatcher) : INeoHaptics, INeoApplicationBoundDesktopService
{
    private NeoDispatcher? _dispatcher = dispatcher;

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Native, 1, 0,
        "The impact, selection, and notification feedback generators of UIKit; a device without a Taptic Engine plays nothing.");

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application) { ArgumentNullException.ThrowIfNull(application); _dispatcher ??= application.Dispatcher; }

    public ValueTask<NeoDesktopStatus> PlayAsync(NeoHapticFeedback feedback, CancellationToken cancellationToken = default)
    {
        NeoHaptics.Validate(feedback);
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("The iOS haptics must be bound to the UI dispatcher before use.");
        return dispatcher.CheckAccess() ? ValueTask.FromResult(Play(feedback)) : dispatcher.InvokeAsync(() => Play(feedback), cancellationToken);
    }

    private static NeoDesktopStatus Play(NeoHapticFeedback feedback)
    {
        switch (feedback)
        {
            case NeoHapticFeedback.Selection:
                Send(IosObjC.Send(IosObjC.Send(IosObjC.Class("UISelectionFeedbackGenerator"), "alloc"), "init"), "selectionChanged", null);
                break;
            case NeoHapticFeedback.Success or NeoHapticFeedback.Warning or NeoHapticFeedback.Error:
                // UINotificationFeedbackType: success 0, warning 1, error 2.
                var type = feedback == NeoHapticFeedback.Success ? 0 : feedback == NeoHapticFeedback.Warning ? 1 : 2;
                Send(IosObjC.Send(IosObjC.Send(IosObjC.Class("UINotificationFeedbackGenerator"), "alloc"), "init"), "notificationOccurred:", type);
                break;
            default:
                // UIImpactFeedbackStyle: light 0, medium 1, heavy 2.
                var style = feedback == NeoHapticFeedback.Light ? 0 : feedback == NeoHapticFeedback.Medium ? 1 : 2;
                var generator = IosObjC.Send(IosObjC.Send(IosObjC.Class("UIImpactFeedbackGenerator"), "alloc"), IosObjC.Selector("initWithStyle:"), style);
                Send(generator, "impactOccurred", null);
                break;
        }
        return NeoDesktopStatus.Success;
    }

    private static void Send(nint generator, string selector, nint? argument)
    {
        if (generator == 0) return;
        try
        {
            if (argument is { } value) IosObjC.Send(generator, selector, value);
            else IosObjC.Send(generator, selector);
        }
        finally { IosObjC.Send(generator, "release"); }
    }
}
