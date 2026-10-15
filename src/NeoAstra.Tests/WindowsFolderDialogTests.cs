// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;
using System.Text;
using global::NeoAstra.Desktop;
using global::NeoAstra.Desktop.Dialogs;

namespace NeoAstra.Tests;

[TestClass]
public sealed class WindowsFolderDialogTests
{
    private const uint WmCommand = 0x0111;
    private const nuint IdOk = 1;

    [TestMethod]
    [Timeout(60000)]
    public async Task FolderDialogIsTheExplorerDialogAndCanBeAcceptedOrCanceled()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This regression shows the Windows folder dialog.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "neoastra-folder-dialog-" + Guid.NewGuid().ToString("N"));
        var picked = Directory.CreateDirectory(Path.Combine(root, "picked")).FullName;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stage = "startup";
        var thread = new Thread(() =>
        {
            try
            {
                NeoApplication.Run(new NeoApplicationOptions { ApplicationName = "NeoAstra folder dialog test", QueueInitialLaunchEvent = false, ShutdownMode = NeoApplicationShutdownMode.Explicit }, async application =>
                {
                    try
                    {
                        var window = application.CreateWindow(new NeoWindowOptions { Label = "dialog-window", Title = "Folder dialog", Width = 480, Height = 320 });
                        window.Show();
                        var owner = window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
                        var dialogs = new WindowsDialogs(application.Dispatcher);
                        var options = new NeoFileDialogOptions { Owner = window, Title = "Pick a folder", InitialDirectory = picked, Scope = new NeoFileScope([root]) };

                        stage = "accepted folder dialog";
                        // Accepting with nothing typed chooses the folder the dialog shows: the initial one.
                        var accepting = Task.Run(async () =>
                        {
                            var dialog = await WaitForDialogAsync(owner);
                            var explorer = HasDescendant(dialog, "DUIViewWndClassName");
                            await Task.Delay(500);
                            PostMessage(dialog, WmCommand, IdOk, 0);
                            return explorer;
                        });
                        var accepted = await dialogs.OpenFoldersAsync(options);
                        Assert.IsTrue(await accepting, "The folder dialog must be the Explorer dialog, with an address bar and a path field.");
                        Assert.AreEqual(NeoDesktopStatus.Success, accepted.Status, accepted.Code);
                        CollectionAssert.AreEqual(new[] { picked }, accepted.Value!.ToArray());

                        stage = "canceled folder dialog";
                        using var cancellation = new CancellationTokenSource();
                        var canceling = Task.Run(async () => { await WaitForDialogAsync(owner); cancellation.Cancel(); });
                        var canceled = false;
                        try { _ = await dialogs.OpenFoldersAsync(options, cancellation.Token); }
                        catch (OperationCanceledException) { canceled = true; }
                        await canceling;
                        Assert.IsTrue(canceled, "Canceling the wait closes the dialog.");
                    }
                    finally { application.ForceShutdown(); }
                });
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(50)); }
        catch (TimeoutException) { Assert.Fail($"The folder dialog test hung during {stage}."); }
        catch (NeoAstraNativeLibraryException) { Assert.Inconclusive("The Windows native runtime is not staged."); }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    private static async Task<nint> WaitForDialogAsync(nint owner)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            nint found = 0;
            EnumWindows((window, _) =>
            {
                if (GetWindow(window, 4) == owner && IsWindowVisible(window) && ClassName(window) == "#32770") { found = window; return false; }
                return true;
            }, 0);
            if (found != 0) return found;
            await Task.Delay(20);
        }
        throw new TimeoutException("The folder dialog did not appear.");
    }

    private static bool HasDescendant(nint parent, string className)
    {
        var found = false;
        EnumChildWindows(parent, (window, _) => { found = ClassName(window) == className; return !found; }, 0);
        return found;
    }

    private static string ClassName(nint window)
    {
        var name = new StringBuilder(256);
        return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
    }

    private delegate bool EnumWindow(nint window, nint parameter);

#pragma warning disable SYSLIB1054 // Test-only Win32 inspection does not need source-generated interop.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindow callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, EnumWindow callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int capacity);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nuint wide, nint value);
#pragma warning restore SYSLIB1054
}
