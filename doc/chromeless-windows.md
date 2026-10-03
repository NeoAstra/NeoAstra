# Chromeless windows and web title bars

A NeoAstra window can hand its title-bar area to the web content while keeping the parts of the
window that only the operating system can provide: the frame and its shadow, resize borders,
snapping, the system menu, and the native window controls. The application then renders its own
title bar in HTML and CSS.

## Title-bar styles

`NeoWindowTitleBarStyle` selects how much of the title bar the platform keeps.

| Style | Title-bar area | Window controls |
| --- | --- | --- |
| `Default` | Drawn by the platform | Drawn by the platform |
| `Overlay` | Covered by the web content | Native controls stay on top of the content |
| `Hidden` | Covered by the web content | Drawn by the application |

Both extended styles keep the platform frame. They differ from `HasDecorations = false`, which
removes the frame entirely and suits splash screens and popups rather than main windows. A
borderless window ignores the title-bar style.

Set the style when the window is created, or change it later through `NeoWindow.TitleBar`:

```csharp
var window = application.CreateWindow(new NeoWindowOptions
{
    Title = "My application",
    TitleBar = new NeoWindowTitleBar(NeoWindowTitleBarStyle.Overlay) { Height = 40 },
});

NeoWindowTitleBarLayout layout = window.GetTitleBarLayout();
```

`NeoWindowTitleBar` also carries the `SymbolColor` and `BackgroundColor` of natively drawn window
controls. Both default to transparent: the symbols then follow the system theme and the controls
show the web content beneath them. `GetTitleBarLayout()` reports the height of the title bar and
the width that native controls cover on each side, in logical units that match CSS pixels. The
insets are zero while no native controls are shown, such as in fullscreen.

## Frontend

A view granted `window:management` attaches its document to the native layout with one call:

```ts
import { createDesktopClient, invoke, subscribe } from "@neoastra/client";

const desktop = createDesktopClient({ invoke, subscribe });
const titleBar = await desktop.window.attachTitleBar({
  onChange: snapshot => console.log(snapshot.state, snapshot.titleBar),
});
```

`attachTitleBar` keeps these values current on the document element as the window is maximized,
focused, sent to fullscreen, or restyled:

| Name | Value |
| --- | --- |
| `--neoastra-titlebar-height` | Title-bar height |
| `--neoastra-titlebar-inset-left` | Width covered by native controls on the left |
| `--neoastra-titlebar-inset-right` | Width covered by native controls on the right |
| `data-neoastra-titlebar` | `default`, `overlay`, or `hidden` |
| `data-neoastra-window-state` | `normal`, `minimized`, `maximized`, or `fullscreen` |
| `data-neoastra-window-focused` | `true` or `false` |

Mark the element that should move the window with `data-neoastra-drag-region`. Links, buttons,
form fields, and anything marked `data-neoastra-no-drag` inside it keep their own pointer behavior.

```html
<header class="titlebar" data-neoastra-drag-region>
  <span>My application</span>
  <button type="button">Search</button>
</header>
```

```css
.titlebar {
  display: flex;
  align-items: center;
  height: var(--neoastra-titlebar-height, 2.5rem);
  padding-left: var(--neoastra-titlebar-inset-left, 0px);
  padding-right: var(--neoastra-titlebar-inset-right, 0px);
}

:root[data-neoastra-titlebar="default"] .titlebar {
  display: none;
}
```

When no native controls are shown (`Hidden`, fullscreen, or a backend without them), draw the
buttons in the page and call `desktop.window.minimize()`, `maximize()`, `restore()`, and `close()`.
`desktop.window.setTitleBar(...)` changes the style, height, and control colors at runtime, and
`desktop.window.onStateChanged(...)` reports window snapshots without attaching the document.

Keep the title bar outside the scrolling region of the page. A document-level scrollbar would
otherwise run underneath the native window controls.

The [advanced sample](../samples/NeoAstra.Sample.Advanced/readme.md) uses the `Overlay` style for
its main window and can switch between the three styles while running.

## Platform behavior

| Behavior | Windows / WebView2 | macOS / WKWebView | Linux / WebKitGTK 6.0 |
| --- | --- | --- | --- |
| Frame, shadow, and resize borders | Kept | Kept | Kept where GTK draws client-side decorations |
| Native controls in `Overlay` | Minimize, maximize/restore, and close at the right edge | Traffic lights at the leading edge | None; the application draws them |
| Title-bar height | Configurable; 32 by default | Fixed by the system | Configurable; 32 by default |
| Control colors | `SymbolColor` and `BackgroundColor` | System appearance | Not applicable |
| Drag regions | Native `app-region`, including double-click maximize and the system menu | Host drag started from the press; double-click toggles maximize | Host drag started from the press; double-click toggles maximize |
| Snap layouts on the maximize control | Available on Windows 11 | Not applicable | Not applicable |

On Windows the native caption buttons and the top-edge resize strip sit above the browser view.
They blend with the content only when the executable's application manifest declares Windows 8 or
later support through a `supportedOS` entry; Windows otherwise refuses layered child windows, and
NeoAstra falls back to an opaque button strip and no top-edge resizing. The left, right, and bottom
resize borders do not depend on the manifest.

```xml
<compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
  <application>
    <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
  </application>
</compatibility>
```

On macOS and Linux a drag starts only while the primary button of the press that reached the page
is still held; a released press reports `Canceled`. The same rule applies to
`desktop.window.startDrag()` and `desktop.window.startResize(edge)`, which borderless windows can
use to provide their own move and resize handles. macOS has no native interactive resize entry
point, so `startResize` reports `Unsupported` there and titled windows rely on their standard
resize borders.

See [known limitations](known-limitations.md) for the validation status of each backend.
