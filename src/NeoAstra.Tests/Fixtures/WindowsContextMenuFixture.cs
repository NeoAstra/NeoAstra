// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Reflection;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Menus;

namespace NeoAstra.Tests;

/// <summary>Uses the shared recording native boundary with a context menu's externally owned HWND.</summary>
internal sealed class WindowsContextMenuFixture : IAsyncDisposable
{
    internal const string TargetId = "context:main";
    private readonly Type _native;
    private readonly nint _window;
    private bool _ownerClosed;

    internal WindowsContextMenuFixture()
    {
        var assembly = TrayPresenterFixture.PresenterAssembly;
        _native = assembly.GetType("NeoAstra.Desktop.Tray.TrayTestNative", throwOnError: true)!;
        _native.GetMethod("Reset")!.Invoke(null, null);
        _window = (nint)_native.GetMethod("Allocate")!.Invoke(null, [1])!;
        _native.GetField("ContextWindow")!.SetValue(null, _window);
        _native.GetField("OwnerCanClose")!.SetValue(null, true);
        Commands = new NeoCommandService();
        var type = assembly.GetType("NeoAstra.Desktop.Menus.WindowsMenuPresenter", throwOnError: true)!;
        Presenter = (INeoMenuPresenter)Activator.CreateInstance(type, Commands, new NeoDispatcher(null!, Environment.CurrentManagedThreadId))!;
        Service = new NeoMenuService(Commands, presenter: Presenter);
    }

    internal NeoCommandService Commands { get; }
    internal INeoMenuPresenter Presenter { get; }
    internal NeoMenuService Service { get; }
    internal List<string> Violations => (List<string>)Get("Violations");
    internal int PopupCount => (int)Get("PopupCount");
    internal int LiveResources => ((Dictionary<nint, int>)Get("References")).Count(static pair => pair.Value > 0);
    internal Action? DuringPopup { set => _native.GetField("DuringPopup")!.SetValue(null, value); }
    internal nint AttachedMenu => ((Dictionary<nint, nint>)Get("WindowMenus")).GetValueOrDefault(_window);
    internal bool IsLive(nint value) => ((Dictionary<nint, int>)Get("References")).GetValueOrDefault(value) > 0;

    internal NeoDesktopStatus Show() => Service.ShowContextMenuAsync(TargetId, new NeoPoint(10, 20)).GetAwaiter().GetResult();

    internal void CloseOwner(bool notifyPresenter)
    {
        if (_ownerClosed) return;
        _ownerClosed = true;
        _native.GetMethod("Release")!.Invoke(null, [_window]);
        if (notifyPresenter) Presenter.GetType().GetMethod("OwnerDestroyed", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Presenter, [_window]);
    }

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        await Commands.DisposeAsync();
        CloseOwner(notifyPresenter: false);
    }

    private object Get(string name) => _native.GetField(name)!.GetValue(null)!;
}
