// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Desktop.Menus;

internal static class NeoMenuRolePresentation
{
    internal static string RequireExplicitLabel(NeoMenuItem item, string platform)
    {
        if (item.Text is { } label) return label;
        throw new NotSupportedException($"{platform} does not expose a reliable localized standard label for the '{item.Role}' role. Supply an application-localized label with NeoMenuItem.RoleItem(id, role, localizedText).");
    }
}

internal static class LinuxRoleTargetSelection
{
    // Labeled views sort first by their application-unique ordinal label. Unlabeled
    // views sort by native handle, providing a deterministic tie-breaker.
    internal static T? Select<T>(IEnumerable<T> candidates, object owner, Func<T, object?> getOwner, Func<T, string?> getLabel, Func<T, nint> getWidget)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(getOwner);
        ArgumentNullException.ThrowIfNull(getLabel);
        ArgumentNullException.ThrowIfNull(getWidget);

        return candidates
            .Where(candidate => ReferenceEquals(getOwner(candidate), owner) && getWidget(candidate) != 0)
            .OrderBy(candidate => getLabel(candidate) is null ? 1 : 0)
            .ThenBy(getLabel, StringComparer.Ordinal)
            .ThenBy(candidate => unchecked((nuint)getWidget(candidate)))
            .FirstOrDefault();
    }

    internal static bool IsCurrentWidget(nint capturedWidget, nint currentWidget) => capturedWidget != 0 && currentWidget == capturedWidget;
}
