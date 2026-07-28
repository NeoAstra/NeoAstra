// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using global::NeoAstra;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Clipboard;
using NeoAstra.Desktop.Dialogs;
using NeoAstra.Desktop.DragDrop;
using NeoAstra.Desktop.Menus;
using NeoAstra.Desktop.Opener;
using NeoAstra.Desktop.Tray;
using NeoAstra.Desktop.WindowState;
using NeoAstra.Rpc;
using System.Runtime.InteropServices;

if (args is ["--native-smoke"])
{
    if (!OperatingSystem.IsWindows()) return RunNativeSmoke();
    var result = -1; Exception? failure = null; var thread = new Thread(() => { try { result = RunNativeSmoke(); } catch (Exception exception) { failure = exception; } }); thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    return result;
}

static int RunNativeSmoke()
{
    return NeoApplication.Run(new NeoApplicationOptions { ApplicationName = "NeoAstra Desktop Smoke", ShutdownMode = NeoApplicationShutdownMode.Explicit }, async application =>
    {
        var smokeRoot = Path.Combine(Path.GetTempPath(), "neoastra-desktop-smoke-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(smokeRoot);
        try
        {
            await using var services = NeoDesktopServices.CreateSystem("neoastra.desktop.smoke", "NeoAstra Desktop Smoke", "1.0", smokeRoot, ["https://example.com"], [smokeRoot], [smokeRoot], [NeoOpenFileIntent.TextDocument], application.Dispatcher);
            await using var plugins = new NeoPluginBuilder().AddNeoAstraDesktop(services).Build(); await plugins.StartAsync(application);
            var owner = application.CreateWindow(new NeoWindowOptions { Label = "owner", Title = "NeoAstra Héllo", Width = 480, Height = 320, StartupLocation = NeoWindowStartupLocation.Center, IsVisible = true }); application.MainWindow = owner;
            var modal = application.CreateWindow(new NeoWindowOptions { Label = "modal", Owner = owner, IsModal = true, Title = "Modal 更新", Width = 240, Height = 160, StartupLocation = NeoWindowStartupLocation.Center });
            if (!modal.IsModal || !ReferenceEquals(modal.Owner, owner)) throw new InvalidOperationException("Modal ownership was not preserved.");
            await using var environment = await application.CreateEnvironmentAsync(); await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(owner), new NeoAstraOptions { ViewLabel = "owner-view", BridgePolicy = NeoBridgePolicy.Disabled }); await using var secondaryView = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(owner), new NeoAstraOptions { ViewLabel = "a-owner-secondary", BridgePolicy = NeoBridgePolicy.Disabled });
            var droppedPath = Path.Combine(smokeRoot, "dropped.txt"); await File.WriteAllTextAsync(droppedPath, "drop"); var nativeInbound = 0; EventHandler<NeoOwnedDropEvent> nativeHandler = (_, _) => nativeInbound++; services.DragDrop.Inbound += nativeHandler; view.DispatchNativeFileDrop([droppedPath], new NeoPoint(12, 18)); services.DragDrop.Inbound -= nativeHandler; if (nativeInbound != 0) throw new InvalidOperationException("A bridge-disabled WebView received renderer drop authority.");
            var viewOwner = NeoPluginOwner.View("owner-view"); var ownedDrop = services.DragDrop.BrokerInbound("owner-view", "owner", new NeoPoint(12, 18), [(NeoDragDataKind.File, droppedPath)], viewOwner); var fileToken = ownedDrop.Value!.Items[0].FileToken!; if (!services.DragDrop.TryResolveFile(fileToken, viewOwner, out var resolvedDrop) || resolvedDrop != Path.GetFullPath(droppedPath)) throw new InvalidOperationException("A trusted brokered drop did not preserve scoped path authority."); view.NotifyNativeNavigationStarted(); if (services.DragDrop.TryResolveFile(fileToken, viewOwner, out _)) throw new InvalidOperationException("Navigation did not release trusted view drop authority.");
            var typedDrop = services.DragDrop.BrokerInbound("owner-view", "owner", new NeoPoint(4, 5), [(NeoDragDataKind.Url, "https://example.com/drop")], viewOwner); if (typedDrop.Value?.Items[0].Text != "https://example.com/drop") throw new InvalidOperationException("Typed trusted drop metadata was not preserved.");
            var dialogOwner = application.CreateWindow(new NeoWindowOptions { Label = "dialog-owner", Title = "Dialog owner", Width = 200, Height = 120 }); var ownerDialogs = new OwnerBoundDialogs(new CancelableDialogs()); var dialog = ownerDialogs.ShowMessageAsync(new NeoMessageDialogOptions { Owner = dialogOwner, Message = "Owner cancellation", Buttons = [NeoDialogButtonRole.Accept] }); dialogOwner.Close(); var dialogResult = await dialog.AsTask().WaitAsync(TimeSpan.FromSeconds(2)); if (dialogResult.Status != NeoDesktopStatus.Canceled) throw new InvalidOperationException("An owner-bound dialog outlived its owner.");
            var commandActivated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); services.Menus.Commands.Register("smoke.activate", _ => { commandActivated.TrySetResult(); return ValueTask.CompletedTask; });
            var menu = new[] { NeoMenuItem.Command("run", "Run", "smoke.activate", "Ctrl+R"), NeoMenuItem.RoleItem("quit", NeoMenuRole.Quit, "Quitter 更新") };
            await services.Menus.SetMenuAsync("context:owner-view", menu); await services.Menus.SetMenuAsync("context:owner-view", [NeoMenuItem.Command("run", "Run 更新", "smoke.activate", "Ctrl+R", isChecked: true), NeoMenuItem.RoleItem("copy", NeoMenuRole.Copy, "複製 Héllo")]); await Task.Run(async () => await services.Menus.SetMenuAsync("application", menu));
            if (OperatingSystem.IsWindows()) { _ = SmokeNative.SendMessage(owner.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value, 0x0111, 100, 0); await commandActivated.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
            await services.Menus.RemoveMenuAsync("application"); await services.Menus.SetMenuAsync("window:owner", OperatingSystem.IsLinux() ? [NeoMenuItem.RoleItem("copy", NeoMenuRole.Copy)] : menu); await services.Menus.SetMenuAsync("window:modal", [NeoMenuItem.RoleItem("copy", NeoMenuRole.Copy, "複製")]); await services.Menus.SetMenuAsync("application", menu);
            await Task.Run(async () => await services.Tray.SetAsync(new NeoTrayItemOptions { Id = "smoke", ToolTip = "NeoAstra Héllo", Menu = [NeoMenuItem.Command("run", "Run", "smoke.activate")] }));
            services.Tray.Set(new NeoTrayItemOptions { Id = "smoke", ToolTip = "NeoAstra Héllo 更新", Menu = [NeoMenuItem.Command("run", "Run", "smoke.activate"), NeoMenuItem.RoleItem("quit", NeoMenuRole.Quit, "Quitter 更新")] });
            var stateStore = new NeoJsonWindowStateStore(Path.Combine(smokeRoot, "window-state")); var stateController = new NeoWindowStateController(owner, stateStore, "owner", TimeSpan.FromMilliseconds(50));
            _ = await services.WindowPolish.SetEnabledAsync(owner, false); _ = await services.WindowPolish.SetEnabledAsync(owner, true); _ = await services.WindowPolish.RequestAttentionAsync(owner); _ = await services.WindowPolish.SetProgressAsync(owner, NeoWindowProgressState.Normal, 0.25); _ = await services.WindowPolish.SetBadgeAsync(owner, "1"); _ = await services.WindowPolish.SetContentProtectionAsync(owner, true); _ = await services.WindowPolish.SetContentProtectionAsync(owner, false); _ = await services.WindowPolish.SetTitleBarThemeAsync(owner, NeoWindowTitleBarTheme.System);
            var ownerClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var modalClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var effectiveState = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); owner.Closed += (_, _) => ownerClosed.TrySetResult(); modal.Closed += (_, _) => modalClosed.TrySetResult(); owner.StateChanged += (_, _) => effectiveState.TrySetResult();
            modal.Show(); owner.State = NeoWindowState.Minimized; owner.State = NeoWindowState.Normal; await effectiveState.Task.WaitAsync(TimeSpan.FromSeconds(2)); await stateController.DisposeAsync(); if (await stateStore.LoadAsync("owner") is null) throw new InvalidOperationException("Debounced atomic window-state persistence did not flush during teardown."); owner.Close();
            await Task.WhenAll(ownerClosed.Task, modalClosed.Task).WaitAsync(TimeSpan.FromSeconds(5));
            if (services.Menus.GetMenu("context:owner-view").Count != 0 || services.Menus.GetMenu("window:owner").Count != 0 || services.Menus.GetMenu("window:modal").Count != 0) throw new InvalidOperationException("A native menu outlived its destroyed window owner.");
            if (!services.Tray.Remove("smoke")) throw new InvalidOperationException("The application-owned tray item did not survive window closure.");
            await services.Menus.RemoveMenuAsync("application"); await services.Menus.RemoveMenuAsync("context:owner-view");
        }
        finally { application.ForceShutdown(); try { Directory.Delete(smokeRoot, recursive: true); } catch { } }
    });
}

var rpcResult = await RpcFixture.RunAsync();
if (rpcResult != 0) return rpcResult;

var root = Path.Combine(Path.GetTempPath(), "neoastra-native-aot-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var services = NeoDesktopServices.CreateSystem("neoastra.desktop.fixture", "NeoAstra Desktop Fixture", "1.0.0", root, ["https://example.com"], [root], [root], [NeoAstra.Desktop.Opener.NeoOpenFileIntent.TextDocument]);
    await using var host = new NeoPluginBuilder().AddNeoAstraDesktop(services).Build();
    var catalog = new NeoPermissionCatalogBuilder().AddNeoAstraDesktopPermissions().Build();
    var clipboard = new NeoFakeClipboard();
    var clipboardResult = await clipboard.WriteAsync(NeoClipboardFormat.FileList, System.Text.Encoding.UTF8.GetBytes($"[\"{root.Replace("\\", "\\\\", StringComparison.Ordinal)}\"]"));
    if (host.Plugins.Count != 1 || catalog.Plugins.Count != 1 || clipboardResult != NeoDesktopStatus.Success || NeoAccelerator.Normalize("shift+ctrl+p") != "Ctrl+Shift+P") return 1;
    return 0;
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

internal static class SmokeNative
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    internal static extern nint SendMessage(nint window, uint message, nuint wParam, nint lParam);
}

internal sealed class CancelableDialogs : INeoDialogs
{
    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Emulated, 1, 0, "Cancelable owner-close fixture.");
    public ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFilesAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default) => Wait<IReadOnlyList<string>>(cancellationToken);
    public ValueTask<NeoDesktopResult<IReadOnlyList<string>>> OpenFoldersAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default) => Wait<IReadOnlyList<string>>(cancellationToken);
    public ValueTask<NeoDesktopResult<string>> SaveFileAsync(NeoFileDialogOptions options, CancellationToken cancellationToken = default) => Wait<string>(cancellationToken);
    public ValueTask<NeoDesktopResult<NeoDialogButtonRole>> ShowMessageAsync(NeoMessageDialogOptions options, CancellationToken cancellationToken = default) => Wait<NeoDialogButtonRole>(cancellationToken);
    private static async ValueTask<NeoDesktopResult<T>> Wait<T>(CancellationToken token) { await Task.Delay(Timeout.InfiniteTimeSpan, token); return NeoDesktopResult<T>.Failure(NeoDesktopStatus.Failed); }
}
