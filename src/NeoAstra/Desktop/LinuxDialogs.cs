// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Desktop.Dialogs;

internal sealed class LinuxDialogs : INeoDialogs, INeoApplicationBoundDesktopService
{
    private readonly ProcessDialogs _presenter;
    private NeoDispatcher? _dispatcher;

    internal LinuxDialogs(NeoDispatcher? dispatcher)
    {
        _dispatcher = dispatcher;
        _presenter = new ProcessDialogs(DesktopProcess.FindTrustedExecutable("/usr/bin/zenity", "/usr/local/bin/zenity"), macOS: false);
        Support = _presenter.Support.SupportLevel == NeoSupportLevel.None
            ? _presenter.Support
            : new(NeoSupportLevel.Limited, 1, 0, "GTK4-compatible dialogs through the trusted zenity helper process, with cancellation, filters, multiple selection, and canonical scope checks. A separate process avoids loading dialog-toolkit symbols into the WebKitGTK process; transient owner attachment is unavailable.");
    }

    public NeoCapabilityInfo Support { get; }

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_dispatcher is not null && !ReferenceEquals(_dispatcher, application.Dispatcher)) throw new InvalidOperationException("The dialog presenter is already bound to another dispatcher.");
        _dispatcher = application.Dispatcher;
    }

    public ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFilesAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default)
        => _presenter.OpenFilesAsync(options, cancellationToken);

    public ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFoldersAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default)
        => _presenter.OpenFoldersAsync(options, cancellationToken);

    public ValueTask<NeoDesktopResult<string>> SaveFileAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default)
        => _presenter.SaveFileAsync(options, cancellationToken);

    public ValueTask<NeoDesktopResult<NeoDialogButtonRole>> ShowMessageAsync(NeoMessageDialogOptions options, CancellationToken cancellationToken = default)
        => _presenter.ShowMessageAsync(options, cancellationToken);

    internal static NeoDesktopResult<IReadOnlyList<string>> AuthorizeSelections(NeoFileDialogOptions options, IReadOnlyList<string> paths, bool save, bool folders)
    {
        if (paths.Count == 0) return NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Canceled);
        if (paths.Count > 256 || save && paths.Count != 1) return NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Failed, "invalid_native_result");
        var output = new string[paths.Count];
        for (var index = 0; index < paths.Count; index++)
        {
            string? canonical;
            var allowed = save
                ? options.Scope.TryAuthorizeCreatableFile(paths[index], out canonical)
                : options.Scope.TryAuthorize(paths[index], requireExisting: true, out canonical);
            if (!allowed || folders && !Directory.Exists(canonical)) return NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Denied, "path_scope");
            output[index] = canonical!;
        }
        return NeoDesktopResult<IReadOnlyList<string>>.Success(Array.AsReadOnly(output));
    }
}
