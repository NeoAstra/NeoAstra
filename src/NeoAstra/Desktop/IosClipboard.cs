// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text;

namespace NeoAstra.Desktop.Clipboard;

internal sealed class IosClipboard(NeoDispatcher? dispatcher) : INeoClipboard
{
    private const string HtmlType = "public.html";
    private readonly NeoDispatcher? _dispatcher = dispatcher;

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Limited, 1, 0, "The general UIPasteboard with UTF-8 text and HTML. iOS asks the user before an application reads what another application copied; images and file lists are not exposed.");

    public ValueTask<NeoDesktopResult<byte[]>> ReadAsync(NeoClipboardFormat format, CancellationToken cancellationToken = default)
    {
        NeoFakeClipboard.ValidateFormat(format);
        return Invoke(() =>
        {
            if (format is not (NeoClipboardFormat.Text or NeoClipboardFormat.Html)) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Unsupported);
            var board = IosObjC.Send(IosObjC.Class("UIPasteboard"), "generalPasteboard");
            var value = format == NeoClipboardFormat.Text ? IosObjC.Send(board, "string") : IosObjC.Send(board, "valueForPasteboardType:", IosObjC.String(HtmlType));
            var text = IosObjC.Text(value);
            return text is null ? NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.NotFound) : NeoDesktopResult<byte[]>.Success(Encoding.UTF8.GetBytes(text));
        }, cancellationToken);
    }

    public ValueTask<NeoDesktopStatus> WriteAsync(NeoClipboardFormat format, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        NeoFakeClipboard.Validate(format, content);
        var text = Encoding.UTF8.GetString(content.Span);
        return Invoke(() =>
        {
            if (format is not (NeoClipboardFormat.Text or NeoClipboardFormat.Html)) return NeoDesktopStatus.Unsupported;
            var board = IosObjC.Send(IosObjC.Class("UIPasteboard"), "generalPasteboard");
            if (format == NeoClipboardFormat.Text) IosObjC.Send(board, "setString:", IosObjC.String(text));
            else IosObjC.Send(board, IosObjC.Selector("setValue:forPasteboardType:"), IosObjC.String(text), IosObjC.String(HtmlType));
            return NeoDesktopStatus.Success;
        }, cancellationToken);
    }

    public ValueTask<NeoDesktopStatus> ClearAsync(CancellationToken cancellationToken = default)
        => Invoke(() => { IosObjC.Send(IosObjC.Send(IosObjC.Class("UIPasteboard"), "generalPasteboard"), "setItems:", IosObjC.Send(IosObjC.Class("NSArray"), "array")); return NeoDesktopStatus.Success; }, cancellationToken);

    // UIKit objects belong to the main thread.
    private ValueTask<T> Invoke<T>(Func<T> operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_dispatcher is null || _dispatcher.CheckAccess()) return ValueTask.FromResult(operation());
        return _dispatcher.InvokeAsync(operation, token);
    }
}
