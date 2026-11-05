// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Desktop.Dialogs;

/// <summary>
/// The dialogs of a Linux desktop: the file and folder dialogs of its portal (<c>xdg-desktop-portal</c>), which every desktop
/// has (KDE, GNOME, and the others), and the trusted <c>zenity</c> helper where no portal answers and for message dialogs.
/// </summary>
internal sealed class LinuxDialogs : INeoDialogs, INeoApplicationBoundDesktopService
{
    private readonly ProcessDialogs _presenter;
    private readonly LinuxPortalFileChooser? _portal;
    private NeoDispatcher? _dispatcher;

    internal LinuxDialogs(NeoDispatcher? dispatcher)
        : this(dispatcher, DesktopProcess.FindTrustedExecutable("/usr/bin/zenity", "/usr/local/bin/zenity"), LinuxPortalFileChooser.SessionBusConfigured())
    {
    }

    internal LinuxDialogs(NeoDispatcher? dispatcher, string zenity, bool sessionBus)
    {
        _dispatcher = dispatcher;
        _presenter = new ProcessDialogs(zenity, macOS: false);
        _portal = sessionBus ? new LinuxPortalFileChooser() : null;
        Support = Describe(sessionBus, _presenter.Support.SupportLevel != NeoSupportLevel.None);
    }

    public NeoCapabilityInfo Support { get; }

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_dispatcher is not null && !ReferenceEquals(_dispatcher, application.Dispatcher)) throw new InvalidOperationException("The dialog presenter is already bound to another dispatcher.");
        _dispatcher = application.Dispatcher;
    }

    public async ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFilesAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate(false);
        return await PortalAsync(options, folders: false, save: false, cancellationToken).ConfigureAwait(false)
            ?? await _presenter.OpenFilesAsync(options, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFoldersAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate(false);
        return await PortalAsync(options, folders: true, save: false, cancellationToken).ConfigureAwait(false)
            ?? await _presenter.OpenFoldersAsync(options, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<NeoDesktopResult<string>> SaveFileAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate(true);
        if (await PortalAsync(options, folders: false, save: true, cancellationToken).ConfigureAwait(false) is not { } result)
            return await _presenter.SaveFileAsync(options, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess && result.Value is [var path] ? NeoDesktopResult<string>.Success(path) : NeoDesktopResult<string>.Failure(result.Status, result.Code);
    }

    // The portal has no message dialogs.
    public ValueTask<NeoDesktopResult<NeoDialogButtonRole>> ShowMessageAsync(NeoMessageDialogOptions options, CancellationToken cancellationToken = default)
        => _presenter.ShowMessageAsync(options, cancellationToken);

    /// <summary>The support of the dialogs, from what this desktop session has.</summary>
    internal static NeoCapabilityInfo Describe(bool sessionBus, bool zenity) => (sessionBus, zenity) switch
    {
        (false, false) => new(NeoSupportLevel.None, 1, 0, "Neither a session D-Bus, where the desktop portal answers, nor the trusted zenity helper is available."),
        (true, true) => new(NeoSupportLevel.Limited, 1, 0, "File and folder dialogs of the desktop through the FileChooser of its portal (xdg-desktop-portal: KDE, GNOME, and other desktops, sandboxes included), with filters, multiple selection, and canonical scope checks; the trusted zenity helper process where no portal answers, and for message dialogs. Transient owner attachment is unavailable."),
        (true, false) => new(NeoSupportLevel.Limited, 1, 0, "File and folder dialogs of the desktop through the FileChooser of its portal (xdg-desktop-portal: KDE, GNOME, and other desktops, sandboxes included), with filters, multiple selection, and canonical scope checks. Message dialogs need the zenity helper, which is unavailable. Transient owner attachment is unavailable."),
        _ => new(NeoSupportLevel.Limited, 1, 0, "GTK4-compatible dialogs through the trusted zenity helper process, with cancellation, filters, multiple selection, and canonical scope checks; no session D-Bus for the desktop portal. A separate process avoids loading dialog-toolkit symbols into the WebKitGTK process; transient owner attachment is unavailable."),
    };

    // The answer of the portal with its paths checked against the scope, or null for zenity: no portal, or one that failed.
    private async ValueTask<NeoDesktopResult<IReadOnlyList<string>>?> PortalAsync(NeoFileDialogOptions options, bool folders, bool save, CancellationToken cancellationToken)
    {
        if (_portal is null || _dispatcher is not { } dispatcher || !OperatingSystem.IsLinux()) return null;
        NeoDesktopResult<IReadOnlyList<string>>? result;
        try { result = await _portal.PickAsync(dispatcher, options, folders, save, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        if (result is not { } answer) return null;
        return answer.IsSuccess ? AuthorizeSelections(options, answer.Value!, save, folders) : answer;
    }

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
