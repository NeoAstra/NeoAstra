// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Desktop.Dialogs;

// A message is an AlertDialog on the resumed activity; the platform call waits for the answer on a thread of its own.
internal sealed class AndroidDialogs : INeoDialogs
{
    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Limited, 1, 0,
        "Message dialogs as an AlertDialog of the resumed activity with up to three buttons (one that cancels, one that accepts, and a third); file and folder dialogs are unavailable.");

    public ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFilesAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default) { options.Validate(false); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Unsupported)); }
    public ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFoldersAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default) { options.Validate(false); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Unsupported)); }
    public ValueTask<NeoDesktopResult<string>> SaveFileAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default) { options.Validate(true); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoDesktopResult<string>.Failure(NeoDesktopStatus.Unsupported)); }

    public async ValueTask<NeoDesktopResult<NeoDialogButtonRole>> ShowMessageAsync(NeoMessageDialogOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var answer = Task.Factory.StartNew(() =>
        {
            using var response = AndroidPlatform.Call("dialog", writer =>
            {
                if (options.Title is not null) writer.WriteString("title", options.Title);
                writer.WriteString("message", options.Message);
                if (options.Detail is not null) writer.WriteString("detail", options.Detail);
                writer.WriteString("icon", options.Icon.ToString());
                writer.WriteStartArray("buttons");
                foreach (var role in options.Buttons) writer.WriteStringValue(role.ToString());
                writer.WriteEndArray();
            });
            var status = AndroidPlatform.Status(response);
            return status == NeoDesktopStatus.Success && Enum.TryParse<NeoDialogButtonRole>(AndroidPlatform.Text(response, "value"), out var value)
                ? NeoDesktopResult<NeoDialogButtonRole>.Success(value)
                : NeoDesktopResult<NeoDialogButtonRole>.Failure(status == NeoDesktopStatus.Success ? NeoDesktopStatus.Failed : status);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return await answer.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
