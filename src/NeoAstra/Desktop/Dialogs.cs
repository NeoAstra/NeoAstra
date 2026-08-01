// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text;

namespace NeoAstra.Desktop.Dialogs;

/// <summary>Identifies a portable native message icon.</summary>
public enum NeoDialogIcon
{
    /// <summary>No icon.</summary>
    None,
    /// <summary>Informational icon.</summary>
    Information,
    /// <summary>Warning icon.</summary>
    Warning,
    /// <summary>Error icon.</summary>
    Error,
    /// <summary>Question icon.</summary>
    Question,
}

/// <summary>Identifies a portable message button role.</summary>
public enum NeoDialogButtonRole
{
    /// <summary>Accept the operation.</summary>
    Accept,
    /// <summary>Cancel the operation.</summary>
    Cancel,
    /// <summary>Answer yes.</summary>
    Yes,
    /// <summary>Answer no.</summary>
    No,
    /// <summary>Delete/destructive action.</summary>
    Destructive,
}

/// <summary>Declares one validated file filter.</summary>
public sealed class NeoFileDialogFilter
{
    /// <summary>Initializes a filter.</summary>
    /// <param name="name">Localized display name.</param>
    /// <param name="extensions">Extensions without wildcards or dots.</param>
    /// <param name="mimeTypes">Optional exact MIME types.</param>
    public NeoFileDialogFilter(string name, IEnumerable<string> extensions, IEnumerable<string>? mimeTypes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 128 || name.Any(char.IsControl)) throw new ArgumentException("A filter name is malformed.", nameof(name));
        ArgumentNullException.ThrowIfNull(extensions);
        var extensionArray = extensions.Take(65).ToArray();
        var mimeArray = (mimeTypes ?? []).Take(65).ToArray();
        if (extensionArray.Length is < 1 or > 64 || extensionArray.Any(static value => string.IsNullOrEmpty(value) || value.Length > 32 || value[0] == '.' || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))) throw new ArgumentException("Filters require 1 to 64 literal extensions without dots or wildcards.", nameof(extensions));
        if (extensionArray.Distinct(StringComparer.OrdinalIgnoreCase).Count() != extensionArray.Length) throw new ArgumentException("Filter extensions must be unique.", nameof(extensions));
        if (mimeArray.Length > 64 || mimeArray.Any(static value => value.Length > 128 || value.Count(static character => character == '/') != 1 || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '/' or '+' or '.' or '-')))) throw new ArgumentException("MIME filters must be exact bounded MIME types.", nameof(mimeTypes));
        Name = name;
        Extensions = Array.AsReadOnly(extensionArray);
        MimeTypes = Array.AsReadOnly(mimeArray);
    }

    /// <summary>Gets the display name.</summary>
    public string Name { get; }
    /// <summary>Gets literal extensions.</summary>
    public IReadOnlyList<string> Extensions { get; }
    /// <summary>Gets exact MIME types.</summary>
    public IReadOnlyList<string> MimeTypes { get; }
}

/// <summary>Configures an open/save/folder native dialog.</summary>
public sealed class NeoFileDialogOptions
{
    /// <summary>Gets the explicit modal owner, when available.</summary>
    public NeoWindow? Owner { get; init; }
    /// <summary>Gets a bounded title.</summary>
    public string? Title { get; init; }
    /// <summary>Gets an initial absolute directory authorized by <see cref="Scope"/>.</summary>
    public string? InitialDirectory { get; init; }
    /// <summary>Gets a suggested leaf filename for save.</summary>
    public string? SuggestedFileName { get; init; }
    /// <summary>Gets validated extension/MIME filters.</summary>
    public IReadOnlyList<NeoFileDialogFilter> Filters { get; init; } = Array.Empty<NeoFileDialogFilter>();
    /// <summary>Gets whether multiple selections are allowed.</summary>
    public bool AllowMultiple { get; init; }
    /// <summary>Gets the mandatory backend file scope applied after native selection.</summary>
    public required NeoFileScope Scope { get; init; }

    internal void Validate(bool save)
    {
        ArgumentNullException.ThrowIfNull(Scope);
        if (Title is { } title && (title.Length > 256 || title.Any(char.IsControl))) throw new ArgumentException("The dialog title is malformed.", nameof(Title));
        if (InitialDirectory is { } initial && !Scope.TryAuthorize(initial, requireExisting: true, out _)) throw new ArgumentException("The initial directory is outside the configured scope or unavailable.", nameof(InitialDirectory));
        if (SuggestedFileName is { } filename && (filename.Length > 255 || filename != Path.GetFileName(filename) || filename.Any(char.IsControl))) throw new ArgumentException("The suggested filename must be a bounded leaf name.", nameof(SuggestedFileName));
        if (!save && SuggestedFileName is not null) throw new ArgumentException("A suggested filename is valid only for save dialogs.", nameof(SuggestedFileName));
        if (save && AllowMultiple) throw new ArgumentException("Save dialogs cannot select multiple files.", nameof(AllowMultiple));
        if (Filters.Count > 64 || Filters.Any(static value => value is null)) throw new ArgumentException("A dialog supports at most 64 filters.", nameof(Filters));
    }
}

/// <summary>Configures a native message or confirmation dialog.</summary>
public sealed class NeoMessageDialogOptions
{
    /// <summary>Gets the explicit modal owner, when available.</summary>
    public NeoWindow? Owner { get; init; }
    /// <summary>Gets the bounded title.</summary>
    public string? Title { get; init; }
    /// <summary>Gets the bounded message.</summary>
    public required string Message { get; init; }
    /// <summary>Gets the optional bounded detail.</summary>
    public string? Detail { get; init; }
    /// <summary>Gets the native icon role.</summary>
    public NeoDialogIcon Icon { get; init; }
    /// <summary>Gets 1 to 4 ordered native button roles.</summary>
    public IReadOnlyList<NeoDialogButtonRole> Buttons { get; init; } = [NeoDialogButtonRole.Accept];

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Message);
        if (Message.Length > 8192 || Message.Any(static c => c == '\0') || Title is { Length: > 256 } || Detail is { Length: > 8192 }) throw new ArgumentException("Dialog text exceeds a safe bound.");
        if (!Enum.IsDefined(Icon) || Buttons.Count is < 1 or > 4 || Buttons.Any(static value => !Enum.IsDefined(value)) || Buttons.Distinct().Count() != Buttons.Count) throw new ArgumentException("Dialog roles are invalid.");
    }
}

/// <summary>Provides capability-aware native dialogs.</summary>
public interface INeoDialogs
{
    /// <summary>Gets platform support details.</summary>
    NeoCapabilityInfo Support { get; }
    /// <summary>Opens one or more existing files.</summary>
    ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFilesAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default);
    /// <summary>Selects one or more existing folders.</summary>
    ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFoldersAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default);
    /// <summary>Selects an absolute save destination without opening it.</summary>
    ValueTask<NeoDesktopResult<string>> SaveFileAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default);
    /// <summary>Displays a native message and returns the selected portable role.</summary>
    ValueTask<NeoDesktopResult<NeoDialogButtonRole>> ShowMessageAsync(NeoMessageDialogOptions options, CancellationToken cancellationToken = default);
}

/// <summary>Creates statically selected system dialog adapters.</summary>
public static class NeoDialogs
{
    /// <summary>Creates the adapter selected by trusted process platform state.</summary>
    /// <returns>A truthful platform adapter.</returns>
    public static INeoDialogs CreateSystem(NeoDispatcher? dispatcher = null)
    {
        INeoDialogs presenter = OperatingSystem.IsWindows()
            ? new WindowsDialogs(dispatcher)
            : OperatingSystem.IsMacOS()
                ? new ProcessDialogs("/usr/bin/osascript", true)
                : OperatingSystem.IsLinux()
                    ? new ProcessDialogs(DesktopProcess.FindTrustedExecutable("/usr/bin/zenity", "/usr/local/bin/zenity"), false)
                    : new UnsupportedDialogs("No supported native dialog API is available.");
        return new OwnerBoundDialogs(presenter);
    }
}

internal sealed class OwnerBoundDialogs(INeoDialogs presenter) : INeoDialogs, INeoApplicationBoundDesktopService
{
    public NeoCapabilityInfo Support => presenter.Support;

    public ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFilesAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default)
        => InvokeAsync(options.Owner, token => presenter.OpenFilesAsync(options, token), NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Canceled), cancellationToken);

    public ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFoldersAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default)
        => InvokeAsync(options.Owner, token => presenter.OpenFoldersAsync(options, token), NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Canceled), cancellationToken);

    public ValueTask<NeoDesktopResult<string>> SaveFileAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default)
        => InvokeAsync(options.Owner, token => presenter.SaveFileAsync(options, token), NeoDesktopResult<string>.Failure(NeoDesktopStatus.Canceled), cancellationToken);

    public ValueTask<NeoDesktopResult<NeoDialogButtonRole>> ShowMessageAsync(NeoMessageDialogOptions options, CancellationToken cancellationToken = default)
        => InvokeAsync(options.Owner, token => presenter.ShowMessageAsync(options, token), NeoDesktopResult<NeoDialogButtonRole>.Failure(NeoDesktopStatus.Canceled), cancellationToken);

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        if (presenter is INeoApplicationBoundDesktopService bound) bound.BindApplication(application);
    }

    private static async ValueTask<T> InvokeAsync<T>(NeoWindow? owner, Func<CancellationToken, ValueTask<T>> operation, T ownerCanceled, CancellationToken cancellationToken)
    {
        if (owner is null) return await operation(cancellationToken).ConfigureAwait(false);
        using var ownerSource = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ownerSource.Token);
        void Closed(object? sender, EventArgs args) => ownerSource.Cancel();
        owner.Closed += Closed;
        try
        {
            if (owner.IsClosed) ownerSource.Cancel();
            return await operation(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ownerSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { return ownerCanceled; }
        finally { owner.Closed -= Closed; }
    }
}

/// <summary>Deterministic fake dialog adapter for contract and application tests.</summary>
public sealed class NeoFakeDialogs : INeoDialogs
{
    private readonly Queue<object> _results = new();
    private readonly object _sync = new();

    /// <inheritdoc />
    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Emulated, 1, 0, "Deterministic test adapter; no native UX.");

    /// <summary>Enqueues exactly one result consumed by the next operation.</summary>
    /// <typeparam name="T">The operation result type.</typeparam>
    /// <param name="result">The result.</param>
    public void Enqueue<T>(NeoDesktopResult<T> result) { lock (_sync) { if (_results.Count >= 256) throw new InvalidOperationException("The fake dialog queue is full."); _results.Enqueue(result); } }

    /// <inheritdoc />
    public ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFilesAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default) { options.Validate(false); return Take<IReadOnlyList<string>>(cancellationToken); }
    /// <inheritdoc />
    public ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFoldersAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default) { options.Validate(false); return Take<IReadOnlyList<string>>(cancellationToken); }
    /// <inheritdoc />
    public ValueTask<NeoDesktopResult<string>> SaveFileAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default) { options.Validate(true); return Take<string>(cancellationToken); }
    /// <inheritdoc />
    public ValueTask<NeoDesktopResult<NeoDialogButtonRole>> ShowMessageAsync(NeoMessageDialogOptions options, CancellationToken cancellationToken = default) { options.Validate(); return Take<NeoDialogButtonRole>(cancellationToken); }

    private ValueTask<NeoDesktopResult<T>> Take<T>(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_results.Count == 0) throw new InvalidOperationException("No fake dialog result was queued.");
            if (_results.Dequeue() is not NeoDesktopResult<T> value) throw new InvalidOperationException("The next fake result has the wrong operation type.");
            return ValueTask.FromResult(value);
        }
    }
}

internal sealed class UnsupportedDialogs(string details) : INeoDialogs
{
    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.None, 1, 0, details);
    public ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFilesAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default) { options.Validate(false); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Unsupported)); }
    public ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFoldersAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default) { options.Validate(false); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Unsupported)); }
    public ValueTask<NeoDesktopResult<string>> SaveFileAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default) { options.Validate(true); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoDesktopResult<string>.Failure(NeoDesktopStatus.Unsupported)); }
    public ValueTask<NeoDesktopResult<NeoDialogButtonRole>> ShowMessageAsync(NeoMessageDialogOptions options, CancellationToken cancellationToken = default) { options.Validate(); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoDesktopResult<NeoDialogButtonRole>.Failure(NeoDesktopStatus.Unsupported)); }
}

internal sealed class ProcessDialogs : INeoDialogs
{
    private readonly string _executable;
    private readonly bool _macOS;
    internal ProcessDialogs(string executable, bool macOS) { _executable = executable; _macOS = macOS; Support = string.IsNullOrEmpty(executable) ? new(NeoSupportLevel.None, 1, 0, macOS ? "osascript is unavailable." : "zenity is unavailable; Wayland/desktop portal support varies.") : new(NeoSupportLevel.Limited, 1, 0, macOS ? "Native Apple dialogs through fixed osascript; owner attachment is unavailable." : "Native GTK dialogs through zenity; owner attachment and desktop availability vary."); }
    public NeoCapabilityInfo Support { get; }

    public ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFilesAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default) { options.Validate(false); return SelectAsync(options, folders: false, save: false, cancellationToken); }
    public ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFoldersAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default) { options.Validate(false); return SelectAsync(options, folders: true, save: false, cancellationToken); }
    public async ValueTask<NeoDesktopResult<string>> SaveFileAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate(true);
        var result = await SelectAsync(options, folders: false, save: true, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess && result.Value is { Count: > 0 } ? NeoDesktopResult<string>.Success(result.Value[0]) : NeoDesktopResult<string>.Failure(result.Status, result.Code);
    }

    public async ValueTask<NeoDesktopResult<NeoDialogButtonRole>> ShowMessageAsync(NeoMessageDialogOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate();
        if (string.IsNullOrEmpty(_executable)) return NeoDesktopResult<NeoDialogButtonRole>.Failure(NeoDesktopStatus.Unsupported);
        if (!_macOS && options.Buttons.Count > 2) return NeoDesktopResult<NeoDialogButtonRole>.Failure(NeoDesktopStatus.Unsupported);
        var roles = options.Buttons.Select(RoleText).ToArray();
        IReadOnlyList<string> args;
        if (_macOS)
        {
            const string script = "on run argv\nset bs to items 4 thru -1 of argv\ndisplay dialog (item 2 of argv) with title (item 1 of argv) buttons bs default button 1 with icon note\nreturn button returned of result\nend run";
            args = ["-e", script, "--", options.Title ?? string.Empty, options.Message + (options.Detail is null ? string.Empty : "\n\n" + options.Detail), ((int)options.Icon).ToString(), .. roles];
        }
        else
        {
            var list = new List<string> { options.Buttons.Count > 1 ? "--question" : "--info", "--no-wrap", "--text", options.Message + (options.Detail is null ? string.Empty : "\n\n" + options.Detail) };
            if (options.Title is not null) { list.Add("--title"); list.Add(options.Title); }
            if (roles.Length > 0) { list.Add("--ok-label"); list.Add(roles[0]); }
            if (roles.Length > 1) { list.Add("--cancel-label"); list.Add(roles[1]); }
            args = list;
        }
        try
        {
            var result = await DesktopProcess.RunAsync(_executable, args, default, TimeSpan.FromMinutes(2), captureOutput: _macOS, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0) return NeoDesktopResult<NeoDialogButtonRole>.Failure(result.ExitCode == 1 ? NeoDesktopStatus.Canceled : NeoDesktopStatus.Failed, "dialog_failed");
            if (!_macOS) return NeoDesktopResult<NeoDialogButtonRole>.Success(options.Buttons[0]);
            var selected = Encoding.UTF8.GetString(result.Output).TrimEnd('\r', '\n');
            var index = Array.IndexOf(roles, selected);
            return index >= 0 ? NeoDesktopResult<NeoDialogButtonRole>.Success(options.Buttons[index]) : NeoDesktopResult<NeoDialogButtonRole>.Failure(NeoDesktopStatus.Failed, "invalid_native_result");
        }
        catch (OperationCanceledException) { throw; }
        catch { return NeoDesktopResult<NeoDialogButtonRole>.Failure(NeoDesktopStatus.Failed, "dialog_failed"); }
    }

    private async ValueTask<NeoDesktopResult<IReadOnlyList<string>>> SelectAsync(NeoFileDialogOptions options, bool folders, bool save, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_executable)) return NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Unsupported);
        var args = _macOS ? MacArguments(options, folders, save) : LinuxArguments(options, folders, save);
        try
        {
            var result = await DesktopProcess.RunAsync(_executable, args, default, TimeSpan.FromMinutes(2), captureOutput: true, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0) return NeoDesktopResult<IReadOnlyList<string>>.Failure(result.ExitCode == 1 ? NeoDesktopStatus.Canceled : NeoDesktopStatus.Failed, "dialog_failed");
            var values = Encoding.UTF8.GetString(result.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (values.Length == 0) return NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Canceled);
            var canonical = new List<string>(values.Length);
            foreach (var path in values)
            {
                var candidate = save ? path : Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : path;
                var checkExisting = !save;
                string? authorized;
                var allowed = save ? options.Scope.TryAuthorizeCreatableFile(candidate, out authorized) : options.Scope.TryAuthorize(candidate, checkExisting, out authorized);
                if (!allowed) return NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Denied, "path_scope");
                canonical.Add(authorized!);
            }
            return NeoDesktopResult<IReadOnlyList<string>>.Success(Array.AsReadOnly(canonical.ToArray()));
        }
        catch (OperationCanceledException) { throw; }
        catch { return NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Failed, "dialog_failed"); }
    }

    internal static IReadOnlyList<string> MacArguments(NeoFileDialogOptions options, bool folders, bool save)
    {
        var multipleSelection = options.AllowMultiple ? " with multiple selections allowed" : string.Empty;
        var script = save
            ? "on run argv\nset p to choose file name with prompt (item 1 of argv) default name (item 2 of argv)\nreturn POSIX path of p\nend run"
            : folders
                ? $"on run argv\nset p to choose folder with prompt (item 1 of argv){multipleSelection}\nif class of p is list then\nset o to \"\"\nrepeat with x in p\nset o to o & POSIX path of x & linefeed\nend repeat\nreturn o\nend if\nreturn POSIX path of p\nend run"
                : $"on run argv\nset p to choose file with prompt (item 1 of argv){multipleSelection}\nif class of p is list then\nset o to \"\"\nrepeat with x in p\nset o to o & POSIX path of x & linefeed\nend repeat\nreturn o\nend if\nreturn POSIX path of p\nend run";
        return save ? ["-e", script, "--", options.Title ?? string.Empty, options.SuggestedFileName ?? string.Empty] : ["-e", script, "--", options.Title ?? string.Empty];
    }

    private static IReadOnlyList<string> LinuxArguments(NeoFileDialogOptions options, bool folders, bool save)
    {
        var args = new List<string> { "--file-selection", "--separator=\n" };
        if (folders) args.Add("--directory");
        if (save) { args.Add("--save"); args.Add("--confirm-overwrite"); }
        if (options.AllowMultiple) args.Add("--multiple");
        if (options.Title is not null) { args.Add("--title"); args.Add(options.Title); }
        if (options.InitialDirectory is not null) { args.Add("--filename"); args.Add(Path.EndsInDirectorySeparator(options.InitialDirectory) ? options.InitialDirectory : options.InitialDirectory + Path.DirectorySeparatorChar); }
        if (save && options.SuggestedFileName is not null) { args.Add("--filename"); args.Add(Path.Combine(options.InitialDirectory ?? options.Scope.Roots[0], options.SuggestedFileName)); }
        foreach (var filter in options.Filters) { args.Add("--file-filter"); args.Add($"{filter.Name} | {string.Join(' ', filter.Extensions.Select(static value => "*." + value))}"); }
        return args;
    }

    private static string RoleText(NeoDialogButtonRole role) => role switch { NeoDialogButtonRole.Accept => "OK", NeoDialogButtonRole.Cancel => "Cancel", NeoDialogButtonRole.Yes => "Yes", NeoDialogButtonRole.No => "No", NeoDialogButtonRole.Destructive => "Delete", _ => throw new ArgumentOutOfRangeException(nameof(role)) };
}
