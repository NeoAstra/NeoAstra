// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace NeoAstra.Desktop.Dialogs;

internal sealed partial class WindowsDialogs(NeoDispatcher? dispatcher) : INeoDialogs, INeoApplicationBoundDesktopService
{
    private const uint WmClose = 0x0010;
    private const uint WmUser = 0x0400;
    private const uint BffmInitialized = 1;
    private const uint BffmSetSelectionW = WmUser + 103;
    private const uint OfnAllowMultiSelect = 0x00000200;
    private const uint OfnCreatePrompt = 0x00002000;
    private const uint OfnEnableHook = 0x00000020;
    private const uint OfnEnableSizing = 0x00800000;
    private const uint OfnExplorer = 0x00080000;
    private const uint OfnFileMustExist = 0x00001000;
    private const uint OfnNoChangeDir = 0x00000008;
    private const uint OfnOverwritePrompt = 0x00000002;
    private const uint OfnPathMustExist = 0x00000800;
    private const uint BifReturnOnlyFsDirs = 0x0001;
    private const uint BifNewDialogStyle = 0x0040;
    private const uint TdfAllowDialogCancellation = 0x0008;
    private const uint TdfSizeToContent = 0x01000000;
    private const uint TdnCreated = 0;
    private const uint TdnDestroyed = 5;
    private const uint WmNcDestroy = 0x0082;
    private static long _nextOwnerSubclassId;
    private NeoDispatcher? _dispatcher = dispatcher;

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Limited, 1, 0,
        "Win32 common file/folder dialogs that are resizable by default, and TaskDialog with explicit HWND ownership, cancellation, native standard role labels, filters, and canonical scope checks. Folder multi-select is unavailable in the reviewed Win32 folder presenter.");

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_dispatcher is not null && !ReferenceEquals(_dispatcher, application.Dispatcher)) throw new InvalidOperationException("The dialog presenter is already bound to another dispatcher.");
        _dispatcher = application.Dispatcher;
    }

    public ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFilesAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate(false);
        return Invoke(() => SelectFiles(options, save: false, cancellationToken), cancellationToken);
    }

    public ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFoldersAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate(false);
        if (options.AllowMultiple) return ValueTask.FromResult(NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Unsupported, "folder_multi_select_unavailable"));
        return Invoke(() => SelectFolder(options, cancellationToken), cancellationToken);
    }

    public async ValueTask<NeoDesktopResult<string>> SaveFileAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate(true);
        var result = await Invoke(() => SelectFiles(options, save: true, cancellationToken), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess && result.Value is { Count: > 0 } ? NeoDesktopResult<string>.Success(result.Value[0]) : NeoDesktopResult<string>.Failure(result.Status, result.Code);
    }

    public ValueTask<NeoDesktopResult<NeoDialogButtonRole>> ShowMessageAsync(NeoMessageDialogOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        return Invoke(() => ShowTaskDialog(options, cancellationToken), cancellationToken);
    }

    private ValueTask<T> Invoke<T>(Func<T> callback, CancellationToken cancellationToken)
    {
        var value = _dispatcher ?? throw new InvalidOperationException("The Windows dialog presenter must be bound to the NeoAstra UI dispatcher before use.");
        return value.InvokeAsync(callback, cancellationToken);
    }

    private static unsafe NeoDesktopResult<IReadOnlyList<string>> SelectFiles(NeoFileDialogOptions options, bool save, CancellationToken cancellationToken)
    {
        const int maximumCharacters = 32_768;
        var fileBuffer = (char*)NativeMemory.AllocZeroed((nuint)maximumCharacters, (nuint)sizeof(char));
        char* title = null, initial = null, filter = null;
        var owner = Owner(options.Owner);
        var state = new NativeDialogState(cancellationToken);
        var stateHandle = GCHandle.Alloc(state);
        try
        {
            state.AttachOwner(owner, GCHandle.ToIntPtr(stateHandle));
            if (save && options.SuggestedFileName is { } suggested) suggested.AsSpan().CopyTo(new Span<char>(fileBuffer, maximumCharacters));
            title = Copy(options.Title);
            initial = Copy(options.InitialDirectory);
            filter = BuildFilter(options.Filters);
            var native = new OpenFileName
            {
                Size = (uint)sizeof(OpenFileName),
                Owner = owner,
                Filter = filter,
                File = fileBuffer,
                MaximumFileCharacters = maximumCharacters,
                InitialDirectory = initial,
                Title = title,
                Flags = BuildFileDialogFlags(options, save),
                Hook = &FileDialogHook,
                CustomData = GCHandle.ToIntPtr(stateHandle),
            };
            cancellationToken.ThrowIfCancellationRequested();
            var accepted = save ? WindowsDialogNative.GetSaveFileName(&native) : WindowsDialogNative.GetOpenFileName(&native);
            if (!accepted)
            {
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                var error = WindowsDialogNative.CommDlgExtendedError();
                return NeoDesktopResult<IReadOnlyList<string>>.Failure(error == 0 ? NeoDesktopStatus.Canceled : NeoDesktopStatus.Failed, error == 0 ? null : "common_dialog_failed");
            }
            var paths = ParseFileBuffer(fileBuffer, maximumCharacters);
            var output = new string[paths.Count];
            for (var index = 0; index < paths.Count; index++)
            {
                string? canonical;
                var allowed = save ? options.Scope.TryAuthorizeCreatableFile(paths[index], out canonical) : options.Scope.TryAuthorize(paths[index], requireExisting: true, out canonical);
                if (!allowed) return NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Denied, "path_scope");
                output[index] = canonical!;
            }
            return NeoDesktopResult<IReadOnlyList<string>>.Success(Array.AsReadOnly(output));
        }
        finally
        {
            state.Dispose();
            stateHandle.Free();
            Free(title); Free(initial); Free(filter); NativeMemory.Free(fileBuffer);
        }
    }

    private static unsafe NeoDesktopResult<IReadOnlyList<string>> SelectFolder(NeoFileDialogOptions options, CancellationToken cancellationToken)
    {
        char* title = Copy(options.Title);
        char* initial = Copy(options.InitialDirectory);
        var owner = Owner(options.Owner);
        var state = new NativeDialogState(cancellationToken) { InitialPath = (nint)initial };
        var stateHandle = GCHandle.Alloc(state);
        try
        {
            state.AttachOwner(owner, GCHandle.ToIntPtr(stateHandle));
            var info = new BrowseInfo
            {
                Owner = owner,
                Title = title,
                Flags = BifReturnOnlyFsDirs | BifNewDialogStyle,
                Callback = &FolderDialogCallback,
                CustomData = GCHandle.ToIntPtr(stateHandle),
            };
            cancellationToken.ThrowIfCancellationRequested();
            var item = WindowsDialogNative.BrowseForFolder(&info);
            if (item == 0)
            {
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                return NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Canceled);
            }
            try
            {
                var buffer = stackalloc char[32_768];
                if (!WindowsDialogNative.GetPathFromIdList(item, buffer)) return NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Failed, "folder_path_failed");
                var path = new string(buffer);
                if (!options.Scope.TryAuthorize(path, requireExisting: true, out var canonical) || !Directory.Exists(canonical)) return NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Denied, "path_scope");
                return NeoDesktopResult<IReadOnlyList<string>>.Success(Array.AsReadOnly(new[] { canonical! }));
            }
            finally { WindowsDialogNative.CoTaskMemFree(item); }
        }
        finally
        {
            state.Dispose(); stateHandle.Free(); Free(title); Free(initial);
        }
    }

    private static unsafe NeoDesktopResult<NeoDialogButtonRole> ShowTaskDialog(NeoMessageDialogOptions options, CancellationToken cancellationToken)
    {
        char* title = Copy(options.Title ?? string.Empty);
        char* message = Copy(options.Message);
        char* detail = Copy(options.Detail);
        char* destructive = null;
        TaskDialogButton* customButtons = null;
        var owner = Owner(options.Owner);
        var state = new NativeDialogState(cancellationToken);
        var stateHandle = GCHandle.Alloc(state);
        try
        {
            state.AttachOwner(owner, GCHandle.ToIntPtr(stateHandle));
            uint commonButtons = 0;
            foreach (var role in options.Buttons)
            {
                commonButtons |= role switch
                {
                    NeoDialogButtonRole.Accept => 0x0001u,
                    NeoDialogButtonRole.Yes => 0x0002u,
                    NeoDialogButtonRole.No => 0x0004u,
                    NeoDialogButtonRole.Cancel => 0x0008u,
                    NeoDialogButtonRole.Destructive => 0u,
                    _ => 0u,
                };
            }
            uint customCount = 0;
            if (options.Buttons.Contains(NeoDialogButtonRole.Destructive))
            {
                destructive = Copy("Delete");
                customButtons = (TaskDialogButton*)NativeMemory.Alloc((nuint)sizeof(TaskDialogButton));
                *customButtons = new TaskDialogButton(100, destructive);
                customCount = 1;
            }
            var config = new TaskDialogConfig
            {
                Size = (uint)sizeof(TaskDialogConfig),
                Owner = owner,
                Flags = TdfAllowDialogCancellation | TdfSizeToContent,
                CommonButtons = commonButtons,
                WindowTitle = title,
                MainInstruction = message,
                Content = detail,
                MainIcon = options.Icon switch
                {
                    NeoDialogIcon.Warning => 0xffff,
                    NeoDialogIcon.Error => 0xfffe,
                    NeoDialogIcon.Information => 0xfffd,
                    _ => 0,
                },
                ButtonCount = customCount,
                Buttons = customButtons,
                Callback = &TaskDialogCallback,
                CallbackData = GCHandle.ToIntPtr(stateHandle),
            };
            cancellationToken.ThrowIfCancellationRequested();
            var result = WindowsDialogNative.TaskDialogIndirect(&config, out var selected, null, null);
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            if (result < 0) return NeoDesktopResult<NeoDialogButtonRole>.Failure(NeoDesktopStatus.Failed, "task_dialog_failed");
            var selectedRole = selected switch { 1 => NeoDialogButtonRole.Accept, 2 => NeoDialogButtonRole.Cancel, 6 => NeoDialogButtonRole.Yes, 7 => NeoDialogButtonRole.No, 100 => NeoDialogButtonRole.Destructive, _ => (NeoDialogButtonRole?)null };
            return selectedRole is { } value ? NeoDesktopResult<NeoDialogButtonRole>.Success(value) : NeoDesktopResult<NeoDialogButtonRole>.Failure(NeoDesktopStatus.Canceled);
        }
        finally
        {
            state.Dispose(); stateHandle.Free(); Free(title); Free(message); Free(detail); Free(destructive); NativeMemory.Free(customButtons);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe nuint FileDialogHook(nint dialog, uint message, nuint wParam, nint lParam)
    {
        if (message != 0x0110 || lParam == 0) return 0;
        try
        {
            var native = (OpenFileName*)lParam;
            State(native->CustomData)?.SetHandle(WindowsDialogNative.GetParent(dialog));
        }
        catch { }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FolderDialogCallback(nint dialog, uint message, nint lParam, nint data)
    {
        try
        {
            if (message == BffmInitialized && State(data) is { } state)
            {
                state.SetHandle(dialog);
                if (state.InitialPath != 0) _ = WindowsDialogNative.SendMessage(dialog, BffmSetSelectionW, 1, state.InitialPath);
            }
        }
        catch { }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int TaskDialogCallback(nint dialog, uint notification, nuint wParam, nint lParam, nint data)
    {
        try
        {
            var state = State(data);
            if (notification == TdnCreated) state?.SetHandle(dialog);
            else if (notification == TdnDestroyed) state?.SetHandle(0);
        }
        catch { }
        return 0;
    }

    private static NativeDialogState? State(nint handle) => handle == 0 ? null : GCHandle.FromIntPtr(handle).Target as NativeDialogState;

    internal static uint BuildFileDialogFlags(NeoFileDialogOptions options, bool save)
    {
        // A hook procedure suppresses the common dialog's default resizing unless OFN_ENABLESIZING is explicit.
        return OfnExplorer | OfnEnableHook | OfnEnableSizing | OfnNoChangeDir | OfnPathMustExist |
            (save ? OfnOverwritePrompt | OfnCreatePrompt : OfnFileMustExist) |
            (!save && options.AllowMultiple ? OfnAllowMultiSelect : 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint OwnerSubclass(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        try { if (message == WmNcDestroy) State((nint)data)?.OwnerDestroyed(); }
        catch { }
        return WindowsDialogNative.DefSubclassProc(window, message, wParam, lParam);
    }
    private static nint Owner(NeoWindow? owner) => owner?.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value ?? 0;

    private static unsafe List<string> ParseFileBuffer(char* buffer, int maximum)
    {
        var parts = new List<string>();
        var offset = 0;
        while (offset < maximum && buffer[offset] != '\0')
        {
            var length = 0;
            while (offset + length < maximum && buffer[offset + length] != '\0') length++;
            if (offset + length >= maximum) throw new InvalidDataException("The native file dialog returned an unterminated path.");
            parts.Add(new string(buffer + offset, 0, length));
            offset += length + 1;
        }
        if (parts.Count <= 1) return parts;
        var directory = parts[0];
        return parts.Skip(1).Select(path => Path.Combine(directory, path)).ToList();
    }

    private static unsafe char* BuildFilter(IReadOnlyList<NeoFileDialogFilter> filters)
    {
        if (filters.Count == 0) return null;
        var text = string.Concat(filters.Select(filter => filter.Name + '\0' + string.Join(';', filter.Extensions.Select(static extension => "*." + extension)) + '\0')) + '\0';
        return Copy(text);
    }

    private static unsafe char* Copy(string? value)
    {
        if (value is null) return null;
        var pointer = (char*)NativeMemory.Alloc((nuint)(value.Length + 1), (nuint)sizeof(char));
        value.AsSpan().CopyTo(new Span<char>(pointer, value.Length)); pointer[value.Length] = '\0';
        return pointer;
    }

    private static unsafe void Free(void* pointer) { if (pointer != null) NativeMemory.Free(pointer); }

    private sealed class NativeDialogState : IDisposable
    {
        private readonly CancellationToken _token;
        private readonly CancellationTokenRegistration _registration;
        private nint _handle;
        private nint _owner;
        private nuint _ownerSubclassId;
        internal NativeDialogState(CancellationToken token) { _token = token; _registration = token.Register(static value => ((NativeDialogState)value!).Cancel(), this); }
        internal nint InitialPath { get; init; }
        internal void SetHandle(nint handle) { Volatile.Write(ref _handle, handle); if (handle != 0 && _token.IsCancellationRequested) Cancel(); }
        internal unsafe void AttachOwner(nint owner, nint data)
        {
            if (owner == 0) return;
            var id = checked((nuint)Interlocked.Increment(ref _nextOwnerSubclassId));
            if (!WindowsDialogNative.SetWindowSubclass(owner, &OwnerSubclass, id, (nuint)data)) throw new InvalidOperationException("The explicit dialog owner could not be observed safely.");
            _owner = owner; _ownerSubclassId = id;
        }
        internal void OwnerDestroyed() { _owner = 0; _ownerSubclassId = 0; Cancel(); }
        internal void Cancel() { var handle = Volatile.Read(ref _handle); if (handle != 0) _ = WindowsDialogNative.PostMessage(handle, WmClose, 0, 0); }
        public unsafe void Dispose()
        {
            _registration.Dispose(); Volatile.Write(ref _handle, 0);
            var owner = _owner; var id = _ownerSubclassId; _owner = 0; _ownerSubclassId = 0;
            if (owner != 0) _ = WindowsDialogNative.RemoveWindowSubclass(owner, &OwnerSubclass, id);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct OpenFileName
    {
        internal uint Size; internal nint Owner; internal nint Instance; internal char* Filter; internal char* CustomFilter; internal uint MaximumCustomFilterCharacters; internal uint FilterIndex;
        internal char* File; internal uint MaximumFileCharacters; internal char* FileTitle; internal uint MaximumFileTitleCharacters; internal char* InitialDirectory; internal char* Title; internal uint Flags;
        internal ushort FileOffset; internal ushort FileExtension; internal char* DefaultExtension; internal nint CustomData; internal delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint> Hook; internal char* TemplateName;
        internal nint Reserved; internal uint Reserved2; internal uint FlagsEx;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct BrowseInfo
    {
        internal nint Owner; internal nint Root; internal char* DisplayName; internal char* Title; internal uint Flags;
        internal delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int> Callback; internal nint CustomData; internal int Image;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly unsafe struct TaskDialogButton(int id, char* text) { internal readonly int Id = id; internal readonly char* Text = text; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct TaskDialogConfig
    {
        internal uint Size; internal nint Owner; internal nint Instance; internal uint Flags; internal uint CommonButtons; internal char* WindowTitle; internal nint MainIcon;
        internal char* MainInstruction; internal char* Content; internal uint ButtonCount; internal TaskDialogButton* Buttons; internal int DefaultButton;
        internal uint RadioButtonCount; internal TaskDialogButton* RadioButtons; internal int DefaultRadioButton; internal char* VerificationText; internal char* ExpandedInformation;
        internal char* ExpandedControlText; internal char* CollapsedControlText; internal nint FooterIcon; internal char* Footer;
        internal delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint, int> Callback; internal nint CallbackData; internal uint Width;
    }

    private static unsafe partial class WindowsDialogNative
    {
        [LibraryImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GetOpenFileName(OpenFileName* value);
        [LibraryImport("comdlg32.dll", EntryPoint = "GetSaveFileNameW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GetSaveFileName(OpenFileName* value);
        [LibraryImport("comdlg32.dll")] internal static partial uint CommDlgExtendedError();
        [LibraryImport("shell32.dll", EntryPoint = "SHBrowseForFolderW")] internal static partial nint BrowseForFolder(BrowseInfo* value);
        [LibraryImport("shell32.dll", EntryPoint = "SHGetPathFromIDListW")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GetPathFromIdList(nint item, char* path);
        [LibraryImport("ole32.dll")] internal static partial void CoTaskMemFree(nint memory);
        [LibraryImport("comctl32.dll")] internal static partial int TaskDialogIndirect(TaskDialogConfig* config, out int button, int* radioButton, int* verification);
        [LibraryImport("user32.dll")] internal static partial nint GetParent(nint window);
        [LibraryImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
        [LibraryImport("user32.dll", EntryPoint = "SendMessageW")] internal static partial nint SendMessage(nint window, uint message, nuint wParam, nint lParam);
        [LibraryImport("comctl32.dll", EntryPoint = "SetWindowSubclass")] [return: MarshalAs(UnmanagedType.Bool)] internal static unsafe partial bool SetWindowSubclass(nint window, delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> callback, nuint id, nuint data);
        [LibraryImport("comctl32.dll", EntryPoint = "RemoveWindowSubclass")] [return: MarshalAs(UnmanagedType.Bool)] internal static unsafe partial bool RemoveWindowSubclass(nint window, delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> callback, nuint id);
        [LibraryImport("comctl32.dll", EntryPoint = "DefSubclassProc")] internal static partial nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    }
}
