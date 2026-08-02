// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;

namespace NeoAstra.Desktop.Dialogs;

internal sealed partial class LinuxDialogs(NeoDispatcher? dispatcher) : INeoDialogs, INeoApplicationBoundDesktopService
{
    private const int GtkDialogModal = 1;
    private const int GtkDialogDestroyWithParent = 2;
    private const int GtkResponseAccept = -3;
    private const int GtkResponseCancel = -6;
    private const int FirstButtonResponse = 1000;
    private NeoDispatcher? _dispatcher = dispatcher;

    public NeoCapabilityInfo Support { get; } = new(
        NeoSupportLevel.Limited,
        1,
        0,
        "Native GTK3 message and file/folder dialogs with transient ownership, cancellation, filters, multiple selection, and canonical scope checks. Portable message-button labels are application localized because GTK3 does not expose reliable role-only dialog construction without deprecated stock APIs.");

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_dispatcher is not null && !ReferenceEquals(_dispatcher, application.Dispatcher)) throw new InvalidOperationException("The dialog presenter is already bound to another dispatcher.");
        _dispatcher = application.Dispatcher;
    }

    public ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFilesAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate(false);
        return Invoke(() => Select(options, folders: false, save: false, cancellationToken), cancellationToken);
    }

    public ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFoldersAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate(false);
        return Invoke(() => Select(options, folders: true, save: false, cancellationToken), cancellationToken);
    }

    public async ValueTask<NeoDesktopResult<string>> SaveFileAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate(true);
        var result = await Invoke(() => Select(options, folders: false, save: true, cancellationToken), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess && result.Value is { Count: > 0 }
            ? NeoDesktopResult<string>.Success(result.Value[0])
            : NeoDesktopResult<string>.Failure(result.Status, result.Code);
    }

    public ValueTask<NeoDesktopResult<NeoDialogButtonRole>> ShowMessageAsync(NeoMessageDialogOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        return Invoke(() => ShowMessage(options, cancellationToken), cancellationToken);
    }

    private ValueTask<T> Invoke<T>(Func<T> callback, CancellationToken cancellationToken)
    {
        var value = _dispatcher ?? throw new InvalidOperationException("The Linux dialog presenter must be bound to the NeoAstra UI dispatcher before use.");
        return value.InvokeAsync(callback, cancellationToken);
    }

    private NeoDesktopResult<IReadOnlyList<string>> Select(NeoFileDialogOptions options, bool folders, bool save, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var action = save ? 1 : folders ? 2 : 0;
        var dialog = Native.gtk_file_chooser_native_new(options.Title, Owner(options.Owner), action, null, null);
        if (dialog == 0) return NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Failed, "dialog_create_failed");
        try
        {
            Native.gtk_native_dialog_set_modal(dialog, true);
            Native.gtk_file_chooser_set_select_multiple(dialog, options.AllowMultiple);
            if (options.InitialDirectory is not null) _ = Native.gtk_file_chooser_set_current_folder(dialog, options.InitialDirectory);
            if (save)
            {
                Native.gtk_file_chooser_set_do_overwrite_confirmation(dialog, true);
                if (options.SuggestedFileName is not null) Native.gtk_file_chooser_set_current_name(dialog, options.SuggestedFileName);
            }
            AddFilters(dialog, options.Filters);

            var cancellation = new DialogCancellation(_dispatcher!, dialog, nativeDialog: true);
            using var registration = cancellationToken.Register(static state => ((DialogCancellation)state!).Queue(), cancellation);
            var response = Native.gtk_native_dialog_run(dialog);
            cancellation.Deactivate();
            cancellationToken.ThrowIfCancellationRequested();
            if (response != GtkResponseAccept) return NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Canceled);
            return AuthorizeSelections(options, ReadSelections(dialog), save, folders);
        }
        finally
        {
            Native.gtk_native_dialog_hide(dialog);
            Native.g_object_unref(dialog);
        }
    }

    private NeoDesktopResult<NeoDialogButtonRole> ShowMessage(NeoMessageDialogOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = Native.gtk_message_dialog_new(
            Owner(options.Owner),
            GtkDialogModal | GtkDialogDestroyWithParent,
            MessageType(options.Icon),
            0,
            "%s",
            options.Message);
        if (dialog == 0) return NeoDesktopResult<NeoDialogButtonRole>.Failure(NeoDesktopStatus.Failed, "dialog_create_failed");
        try
        {
            if (options.Title is not null) Native.gtk_window_set_title(dialog, options.Title);
            if (options.Detail is not null) Native.gtk_message_dialog_format_secondary_text(dialog, "%s", options.Detail);
            for (var index = 0; index < options.Buttons.Count; index++)
                _ = Native.gtk_dialog_add_button(dialog, RoleText(options.Buttons[index]), FirstButtonResponse + index);

            var cancellation = new DialogCancellation(_dispatcher!, dialog, nativeDialog: false);
            using var registration = cancellationToken.Register(static state => ((DialogCancellation)state!).Queue(), cancellation);
            var response = Native.gtk_dialog_run(dialog);
            cancellation.Deactivate();
            cancellationToken.ThrowIfCancellationRequested();
            var selected = response - FirstButtonResponse;
            return selected >= 0 && selected < options.Buttons.Count
                ? NeoDesktopResult<NeoDialogButtonRole>.Success(options.Buttons[selected])
                : NeoDesktopResult<NeoDialogButtonRole>.Failure(NeoDesktopStatus.Canceled);
        }
        finally { Native.gtk_widget_destroy(dialog); }
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

    private static IReadOnlyList<string> ReadSelections(nint chooser)
    {
        var head = Native.gtk_file_chooser_get_filenames(chooser);
        var output = new List<string>();
        try
        {
            for (var current = head; current != 0;)
            {
                var node = Marshal.PtrToStructure<SList>(current);
                var path = Marshal.PtrToStringUTF8(node.Data);
                if (!string.IsNullOrEmpty(path) && output.Count <= 256) output.Add(path);
                if (node.Data != 0) Native.g_free(node.Data);
                current = node.Next;
            }
        }
        finally { if (head != 0) Native.g_slist_free(head); }
        return output;
    }

    private static void AddFilters(nint chooser, IReadOnlyList<NeoFileDialogFilter> filters)
    {
        foreach (var filter in filters)
        {
            var native = Native.gtk_file_filter_new();
            if (native == 0) continue;
            Native.gtk_file_filter_set_name(native, filter.Name);
            foreach (var extension in filter.Extensions) Native.gtk_file_filter_add_pattern(native, "*." + extension);
            foreach (var mimeType in filter.MimeTypes) Native.gtk_file_filter_add_mime_type(native, mimeType);
            Native.gtk_file_chooser_add_filter(chooser, native);
        }
    }

    private static nint Owner(NeoWindow? owner)
    {
        if (owner is null) return 0;
        try { return owner.GetNativeHandle(NeoNativeHandleKind.GtkWindow).Value; }
        catch (ObjectDisposedException) { return 0; }
        catch (InvalidOperationException) { return 0; }
    }

    private static int MessageType(NeoDialogIcon icon) => icon switch
    {
        NeoDialogIcon.Information => 0,
        NeoDialogIcon.Warning => 1,
        NeoDialogIcon.Question => 2,
        NeoDialogIcon.Error => 3,
        _ => 4,
    };

    private static string RoleText(NeoDialogButtonRole role) => role switch
    {
        NeoDialogButtonRole.Accept => "OK",
        NeoDialogButtonRole.Cancel => "Cancel",
        NeoDialogButtonRole.Yes => "Yes",
        NeoDialogButtonRole.No => "No",
        NeoDialogButtonRole.Destructive => "Delete",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private sealed class DialogCancellation(NeoDispatcher dispatcher, nint dialog, bool nativeDialog)
    {
        private int _active = 1;

        internal void Queue()
        {
            try { dispatcher.Post(CancelOnDispatcher); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        internal void Deactivate() => Volatile.Write(ref _active, 0);

        private void CancelOnDispatcher()
        {
            if (Volatile.Read(ref _active) == 0) return;
            if (nativeDialog) Native.gtk_native_dialog_hide(dialog);
            else Native.gtk_dialog_response(dialog, GtkResponseCancel);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SList
    {
        internal readonly nint Data;
        internal readonly nint Next;
    }

    private static partial class Native
    {
        [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gtk_file_chooser_native_new(string? title, nint parent, int action, string? acceptLabel, string? cancelLabel);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_native_dialog_set_modal(nint dialog, [MarshalAs(UnmanagedType.Bool)] bool modal);
        [LibraryImport("libgtk-3.so.0")] internal static partial int gtk_native_dialog_run(nint dialog);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_native_dialog_hide(nint dialog);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_file_chooser_set_select_multiple(nint chooser, [MarshalAs(UnmanagedType.Bool)] bool selectMultiple);
        [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial int gtk_file_chooser_set_current_folder(nint chooser, string folder);
        [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial void gtk_file_chooser_set_current_name(nint chooser, string name);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_file_chooser_set_do_overwrite_confirmation(nint chooser, [MarshalAs(UnmanagedType.Bool)] bool overwriteConfirmation);
        [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_file_chooser_get_filenames(nint chooser);
        [LibraryImport("libgtk-3.so.0")] internal static partial nint gtk_file_filter_new();
        [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial void gtk_file_filter_set_name(nint filter, string name);
        [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial void gtk_file_filter_add_pattern(nint filter, string pattern);
        [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial void gtk_file_filter_add_mime_type(nint filter, string mimeType);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_file_chooser_add_filter(nint chooser, nint filter);
        [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gtk_message_dialog_new(nint parent, int flags, int type, int buttons, string format, string message);
        [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial void gtk_message_dialog_format_secondary_text(nint dialog, string format, string message);
        [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial void gtk_window_set_title(nint window, string title);
        [LibraryImport("libgtk-3.so.0", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gtk_dialog_add_button(nint dialog, string buttonText, int responseId);
        [LibraryImport("libgtk-3.so.0")] internal static partial int gtk_dialog_run(nint dialog);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_dialog_response(nint dialog, int responseId);
        [LibraryImport("libgtk-3.so.0")] internal static partial void gtk_widget_destroy(nint widget);
        [LibraryImport("libgobject-2.0.so.0")] internal static partial void g_object_unref(nint value);
        [LibraryImport("libglib-2.0.so.0")] internal static partial void g_free(nint value);
        [LibraryImport("libglib-2.0.so.0")] internal static partial void g_slist_free(nint list);
    }
}
