// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text;

namespace NeoAstra.Desktop.Clipboard;

internal sealed class AndroidClipboard(NeoDispatcher? dispatcher) : INeoClipboard
{
    private readonly NeoDispatcher? _dispatcher = dispatcher;

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Limited, 1, 0, "The Android ClipboardManager with text and HTML. Android lets an application read the clipboard only while it has the focus; images and file lists are not exposed.");

    public ValueTask<NeoDesktopResult<byte[]>> ReadAsync(NeoClipboardFormat format, CancellationToken cancellationToken = default)
    {
        NeoFakeClipboard.ValidateFormat(format);
        if (format is not (NeoClipboardFormat.Text or NeoClipboardFormat.Html)) return ValueTask.FromResult(NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Unsupported));
        return Invoke(() =>
        {
            using var response = AndroidPlatform.Call("clipboard", ("operation", "read"), ("format", Format(format)));
            var status = AndroidPlatform.Status(response);
            var text = AndroidPlatform.Text(response, "text");
            return status == NeoDesktopStatus.Success && text is not null ? NeoDesktopResult<byte[]>.Success(Encoding.UTF8.GetBytes(text)) : NeoDesktopResult<byte[]>.Failure(status == NeoDesktopStatus.Success ? NeoDesktopStatus.NotFound : status);
        }, cancellationToken);
    }

    public ValueTask<NeoDesktopStatus> WriteAsync(NeoClipboardFormat format, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        NeoFakeClipboard.Validate(format, content);
        if (format is not (NeoClipboardFormat.Text or NeoClipboardFormat.Html)) return ValueTask.FromResult(NeoDesktopStatus.Unsupported);
        var text = Encoding.UTF8.GetString(content.Span);
        return Invoke(() => { using var response = AndroidPlatform.Call("clipboard", ("operation", "write"), ("format", Format(format)), ("text", text)); return AndroidPlatform.Status(response); }, cancellationToken);
    }

    public ValueTask<NeoDesktopStatus> ClearAsync(CancellationToken cancellationToken = default)
        => Invoke(() => { using var response = AndroidPlatform.Call("clipboard", ("operation", "clear")); return AndroidPlatform.Status(response); }, cancellationToken);

    private static string Format(NeoClipboardFormat format) => format == NeoClipboardFormat.Html ? "html" : "text";

    // The clipboard of Android answers on the main thread, which has the focus of the application.
    private ValueTask<T> Invoke<T>(Func<T> operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_dispatcher is null || _dispatcher.CheckAccess()) return ValueTask.FromResult(operation());
        return _dispatcher.InvokeAsync(operation, token);
    }
}
