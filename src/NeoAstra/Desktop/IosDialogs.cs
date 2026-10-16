// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NeoAstra.Desktop.Dialogs;

// A message is a UIAlertController presented by the topmost view controller of the key window. Each button has a block whose
// context names the pending dialog and the role of the button; UIKit copies the blocks, and a dialog answers once.
internal sealed partial class IosDialogs(NeoDispatcher? dispatcher) : INeoDialogs, INeoApplicationBoundDesktopService
{
    private static readonly ConcurrentDictionary<long, TaskCompletionSource<NeoDialogButtonRole>> Pending = new();
    private static long s_nextId;
    private static nint s_stackBlock;
    private static unsafe BlockDescriptor* s_descriptor;
    private NeoDispatcher? _dispatcher = dispatcher;

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Limited, 1, 0,
        "Message dialogs as a UIAlertController of the key window, without icons; file and folder dialogs are unavailable.");

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application) { ArgumentNullException.ThrowIfNull(application); _dispatcher ??= application.Dispatcher; }

    public ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFilesAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default) { options.Validate(false); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Unsupported)); }
    public ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFoldersAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default) { options.Validate(false); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Unsupported)); }
    public ValueTask<NeoDesktopResult<string>> SaveFileAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default) { options.Validate(true); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoDesktopResult<string>.Failure(NeoDesktopStatus.Unsupported)); }

    public async ValueTask<NeoDesktopResult<NeoDialogButtonRole>> ShowMessageAsync(NeoMessageDialogOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate();
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("The iOS dialog presenter must be bound to the UI dispatcher before use.");
        var id = Interlocked.Increment(ref s_nextId);
        var completion = new TaskCompletionSource<NeoDialogButtonRole>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[id] = completion;
        try
        {
            nint alert = 0;
            var presented = dispatcher.CheckAccess() ? Present(options, id, out alert) : await dispatcher.InvokeAsync(() => { var shown = Present(options, id, out var value); alert = value; return shown; }, cancellationToken).ConfigureAwait(false);
            if (!presented) return NeoDesktopResult<NeoDialogButtonRole>.Failure(NeoDesktopStatus.Failed);
            try { return NeoDesktopResult<NeoDialogButtonRole>.Success(await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false)); }
            catch (OperationCanceledException)
            {
                // The caller gave up: take the alert away so that the user is not left with a question nobody waits for.
                _ = dispatcher.InvokeAsync(() => IosObjC.Send(alert, IosObjC.Selector("dismissViewControllerAnimated:completion:"), 1, 0));
                throw;
            }
        }
        finally { Pending.TryRemove(id, out _); }
    }

    private static unsafe bool Present(NeoMessageDialogOptions options, long id, out nint alert)
    {
        alert = 0;
        var presenter = TopViewController();
        if (presenter == 0) return false;
        EnsureBlockRuntime();
        var message = string.IsNullOrEmpty(options.Detail) ? options.Message : options.Message + "\n\n" + options.Detail;
        alert = IosObjC.Send(IosObjC.Class("UIAlertController"), IosObjC.Selector("alertControllerWithTitle:message:preferredStyle:"), options.Title is null ? 0 : IosObjC.String(options.Title), IosObjC.String(message), 1);
        var hasCancel = false;
        foreach (var role in options.Buttons)
        {
            // An alert has at most one cancel action.
            var style = role == NeoDialogButtonRole.Destructive ? 2 : role == NeoDialogButtonRole.Cancel && !hasCancel ? 1 : 0;
            hasCancel |= style == 1;
            var block = new Block { Isa = s_stackBlock, Invoke = (nint)(delegate* unmanaged[Cdecl]<Block*, nint, void>)&Chosen, Descriptor = s_descriptor, Id = id, Role = (int)role };
            var action = IosObjC.Send(IosObjC.Class("UIAlertAction"), IosObjC.Selector("actionWithTitle:style:handler:"), IosObjC.String(RoleText(role)), style, (nint)(&block));
            IosObjC.Send(alert, "addAction:", action);
        }
        IosObjC.Send(presenter, IosObjC.Selector("presentViewController:animated:completion:"), alert, 1, 0);
        return true;
    }

    private static nint TopViewController()
    {
        var application = IosObjC.Send(IosObjC.Class("UIApplication"), "sharedApplication");
        var scenes = IosObjC.Send(IosObjC.Send(application, "connectedScenes"), "allObjects");
        var windowScene = IosObjC.Class("UIWindowScene");
        nint window = 0;
        var count = (long)IosObjC.Send(scenes, "count");
        for (var index = 0L; index < count && window == 0; index++)
        {
            var scene = IosObjC.Send(scenes, IosObjC.Selector("objectAtIndex:"), (nint)index);
            if (IosObjC.Send(scene, IosObjC.Selector("isKindOfClass:"), windowScene) == 0) continue;
            window = IosObjC.Send(scene, "keyWindow");
            if (window == 0) window = IosObjC.Send(IosObjC.Send(scene, "windows"), "firstObject");
        }
        var controller = window == 0 ? 0 : IosObjC.Send(window, "rootViewController");
        for (var next = controller; next != 0; next = IosObjC.Send(controller, "presentedViewController")) controller = next;
        return controller;
    }

    private static string RoleText(NeoDialogButtonRole role) => role switch { NeoDialogButtonRole.Accept => "OK", NeoDialogButtonRole.Cancel => "Cancel", NeoDialogButtonRole.Yes => "Yes", NeoDialogButtonRole.No => "No", NeoDialogButtonRole.Destructive => "Delete", _ => throw new ArgumentOutOfRangeException(nameof(role)) };

    private static unsafe void EnsureBlockRuntime()
    {
        if (s_stackBlock != 0) return;
        s_stackBlock = NativeLibrary.GetExport(NativeLibrary.GetMainProgramHandle(), "_NSConcreteStackBlock");
        var descriptor = (BlockDescriptor*)NativeMemory.Alloc((nuint)sizeof(BlockDescriptor));
        *descriptor = new() { Size = (nuint)sizeof(Block) };
        s_descriptor = descriptor;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void Chosen(Block* block, nint action)
    {
        try { if (Pending.TryGetValue(block->Id, out var completion)) completion.TrySetResult((NeoDialogButtonRole)block->Role); } catch { }
    }

    // A block without copy or dispose helpers: UIKit copies its bytes, the id and the role included.
    [StructLayout(LayoutKind.Sequential)] private unsafe struct Block { internal nint Isa; internal int Flags, Reserved; internal nint Invoke; internal BlockDescriptor* Descriptor; internal long Id; internal int Role; }
    [StructLayout(LayoutKind.Sequential)] private struct BlockDescriptor { internal nuint Reserved, Size; }
}
