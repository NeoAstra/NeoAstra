// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using global::NeoAstra.Desktop.Menus;

namespace NeoAstra.Desktop.Tray;

internal sealed class LinuxTrayPresenter : INeoTrayPresenter, INeoApplicationBoundDesktopService
{
    private NeoDispatcher? _dispatcher;
    private bool _disposed;

    internal LinuxTrayPresenter(NeoCommandService commands, NeoDispatcher? dispatcher)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _dispatcher = dispatcher;
    }

    public NeoCapabilityInfo Support { get; } = new(
        NeoSupportLevel.None,
        1,
        0,
        "GTK4 removed GtkStatusIcon and does not provide a native tray/status-item replacement. NeoAstra does not silently depend on a desktop-specific AppIndicator or StatusNotifier extension.");

    public event Action<string, bool>? Activated { add { } remove { } }

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_dispatcher is not null && !ReferenceEquals(_dispatcher, application.Dispatcher)) throw new InvalidOperationException("The tray presenter is already bound to another dispatcher.");
        _dispatcher = application.Dispatcher;
    }

    public void Set(NeoTrayItemOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ObjectDisposedException.ThrowIf(_disposed, this);
        throw new NotSupportedException("GTK4 has no native tray/status-item API. Use an explicit desktop StatusNotifier/AppIndicator integration when required.");
    }

    public bool Remove(string id)
    {
        NeoMenuItem.ValidateId(id, nameof(id));
        ObjectDisposedException.ThrowIf(_disposed, this);
        return false;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
