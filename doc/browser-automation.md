# Browser automation

`NeoAutomation` lets a host drive the views of its application the way
[Chrome DevTools MCP](https://github.com/ChromeDevTools/chrome-devtools-mcp) drives Chrome. It lists
pages, takes text snapshots of their accessibility tree, clicks, types, fills in forms, navigates, runs
scripts, takes screenshots, and reads console messages and network requests. The operations carry the
tool names, the arguments, and the response text of Chrome DevTools MCP, and they behave the same on
WebView2, WKWebView, and WebKitGTK.

NeoAstra does not open a Model Context Protocol (MCP) server. It provides the operations, and the host
decides whether to expose them: through an MCP server, to a test, or to anything else.

## Turning it on

```csharp
await using var automation = new NeoAutomation(application, new NeoAutomationOptions
{
    AllowedDirectories = { Path.Combine(Path.GetTempPath(), "my-app-automation") },
});
```

Nothing is injected into a view before an instance exists, and disposing it turns automation off
again. Every view of the application becomes a page with a numbered identifier; the first one is the
selected page, which a tool call acts on when it names no `pageId`. Calls may come from any thread.

Create the instance before the views navigate. A document that is already loaded keeps the console
messages and requests it produced before, and on Windows its JavaScript dialogs still open as WebView2
dialogs until the view loads another document.

| Option | Default | Purpose |
| --- | --- | --- |
| `PageFilter` | Every view | Selects the views that are pages |
| `NewPageHandler` | A plain window on the environment and profile of the selected page | Creates the view of `new_page` |
| `DefaultTimeout` | 5 seconds | Bounds a wait for a text or a script |
| `NavigationTimeout` | 10 seconds | Bounds a wait for a document |
| `AllowScriptEvaluation` | `true` | Removes `evaluate_script` and initial scripts when `false` |
| `AllowedDirectories` | Empty | The directories that tool calls may read files from and write files to |

## Surfacing the tools through MCP

`NeoAutomation.Tools` describes each operation as a tool, and `CallToolAsync` runs one. Both map onto
MCP without conversion:

| MCP | NeoAstra |
| --- | --- |
| `tools/list`: `name`, `description`, `inputSchema` | `NeoAutomationTool.Name`, `Description`, `InputSchema` |
| `tools/list`: `annotations.readOnlyHint` | `NeoAutomationTool.IsReadOnly` |
| `tools/call`: `name`, `arguments` | `CallToolAsync(name, arguments, cancellationToken)` |
| Result `content`, text | `NeoAutomationToolResult.Text` |
| Result `content`, images | `NeoAutomationToolResult.Images`: `Data` and `ContentType` of each |
| Result `isError` | `NeoAutomationToolResult.IsError` |

With the [C# SDK for MCP](https://github.com/modelcontextprotocol/csharp-sdk), a server that offers
every tool is two handlers:

```csharp
builder.Services.AddMcpServer()
    .WithStdioServerTransport() // or the HTTP transport of ModelContextProtocol.AspNetCore
    .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
    {
        Tools = [.. automation.Tools.Select(static tool => new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = tool.InputSchema,
            Annotations = new ToolAnnotations { ReadOnlyHint = tool.IsReadOnly },
        })],
    }))
    .WithCallToolHandler(async (request, cancellationToken) =>
    {
        var result = await automation.CallToolAsync(request.Params!.Name, request.Params.Arguments, cancellationToken);
        return new CallToolResult
        {
            IsError = result.IsError,
            Content =
            [
                new TextContentBlock { Text = result.Text },
                .. result.Images.Select(static image => ImageContentBlock.FromBytes(image.Data, image.ContentType)),
            ],
        };
    });
```

`CallToolAsync` takes the arguments as a `JsonElement`, as JSON text, or by name. A tool that cannot do
what it was asked completes with `IsError` and a text that says why and, where known, what to do next;
only cancellation and a disposed instance throw.

## Tools

| Category | Tools |
| --- | --- |
| Input | `click`, `click_at`, `drag`, `fill`, `fill_form`, `handle_dialog`, `hover`, `press_key`, `type_text`, `upload_file` |
| Navigation | `close_page`, `list_pages`, `navigate_page`, `new_page`, `select_page`, `wait_for` |
| Emulation | `resize_page` |
| Network | `get_network_request`, `list_network_requests` |
| Debugging | `evaluate_script`, `get_console_message`, `list_console_messages`, `take_screenshot`, `take_snapshot` |

The tools of Chrome DevTools MCP that depend on Chrome itself have no counterpart: `emulate`,
performance traces, Lighthouse audits, heap snapshots, CSS inspection, screencasts, extensions,
installed web applications, WebMCP, and the DevTools window.

`navigate_page` goes back and forward with `GoBack()` and `GoForward()` of the view. In a view whose
`NeoBrowserFeatures.HistoryNavigation` is off, which `NeoBrowserFeatures.ApplicationShell()` selects,
it answers that history navigation is turned off for the view and leaves the document as it is; a
reload and a navigation to an address work as in any view.

A host that calls the operations itself, such as a test, uses the typed interface instead. It returns
objects rather than text:

| Tools | Typed operations |
| --- | --- |
| `list_pages`, `select_page`, `new_page`, `close_page` | `NeoAutomation.Pages`, `SelectPageAsync`, `NewPageAsync`, `ClosePageAsync` |
| `take_snapshot`, `wait_for` | `NeoAutomationPage.TakeSnapshotAsync`, `WaitForAsync` |
| `click`, `click_at`, `hover`, `drag` | `ClickAsync`, `ClickAtAsync`, `HoverAsync`, `DragAsync` |
| `fill`, `fill_form`, `upload_file` | `FillAsync`, `FillFormAsync`, `UploadFileAsync` |
| `press_key`, `type_text`, `handle_dialog` | `PressKeyAsync`, `TypeTextAsync`, `HandleDialogAsync` |
| `navigate_page`, `resize_page` | `NavigateAsync`, `ResizeAsync` |
| `evaluate_script`, `take_screenshot` | `EvaluateScriptAsync`, `TakeScreenshotAsync` |
| `list_console_messages`, `get_console_message` | `GetConsoleMessagesAsync`, `GetConsoleMessageAsync` |
| `list_network_requests`, `get_network_request` | `GetNetworkRequestsAsync`, `GetNetworkRequestAsync` |

## How it differs from Chrome DevTools MCP

The three browser engines share no debugging protocol, so NeoAstra uses none. A script that NeoAstra
adds to every document does the work inside the page with standard DOM APIs, and the host calls it
through `EvaluateScriptAsync`. The script evaluates no text as code, so a content security policy does
not get in its way. What follows from this design:

- **Snapshots** are computed from the DOM and its ARIA attributes, with the roles and names that Chrome
  reports, rather than read from the accessibility tree of the engine. An element keeps its `uid` from
  one snapshot of a document to the next. Frames of another origin and closed shadow trees are not listed.
- **Input** is dispatched as DOM events, together with what an engine does for a real event and not
  for a dispatched one: text insertion, focus, activation, option selection, and form submission. The
  events are not trusted (`isTrusted` is `false`), so CSS `:hover` does not apply, native popups such as
  the list of a `<select>` do not open, and features that ask for a user activation refuse.
- **Focus in a window in the background** is told to the page by automation. A document without the
  system focus (`document.hasFocus()` is `false`, as in a window that is hidden or behind another one)
  gets no `focus`, `blur`, `focusin`, or `focusout` event from the engine when its active element
  changes, and the element that was active when its window was deactivated was told `blur` while it
  stays the active element. A page that goes by these events would then take what is typed for another
  element, or for none. Automation remembers which element the page was last told has the focus, and
  dispatches the missing events before the keys of `press_key` and `type_text`, before `fill`, and
  after the press of a `click`, also when the page moved the focus in its own `mousedown` handler. A
  document that has the focus hears from the engine alone.
- **Typed text** goes through the keys of a US keyboard. The key events of a character carry the
  `code` and the key code of the key that types it, with `shiftKey` set for a capital letter and for
  the upper symbol of a key, and without events for the Shift key itself. A character that is on no
  key of that keyboard is typed with events that name no key. `press_key` takes the key names of
  Puppeteer, where `*`, `+`, `-`, and `/` are the keys of the numeric keypad; `type_text` types these
  four on the main block of the keyboard.
- **Editors on an EditContext**, such as Monaco in WebView2, are neither inputs nor editable content:
  the engine hands what is typed to an
  [EditContext](https://developer.mozilla.org/docs/Web/API/EditContext_API), and the editor draws it.
  `type_text` and `press_key` do what the engine does there, with the same events, so an editor reacts
  as it does to typing: it closes brackets, indents, and offers suggestions. `fill` replaces the text
  in one change. It first presses the keys that select everything. An editor that takes them for
  itself, as Monaco does, then gets the text as a paste over its selection; otherwise the text of the
  EditContext is replaced. A `click` on an element that an editor keeps without area, as Monaco does at
  its cursor, lands on what the editor shows there. A read-only editor can take a text and ignore it
  without saying so, and a snapshot does not list a text that an editor hides from assistive technology.
  Monaco gives what is typed or pasted to the editor that believes it has the focus, which it knows
  from the focus events: in a window in the background, the events that automation dispatches are what
  sends the text to the editor that was clicked, and not to the one that had the focus before.
- **Console messages** are those of the `console` methods, uncaught errors, unhandled rejections, and
  elements that fail to load. Messages that the engine writes itself, such as a content security policy
  violation, are not seen.
- **Network requests** are those the page can observe. Requests made with `fetch` and `XMLHttpRequest`
  have their method, status, headers, and text bodies. Other loads over HTTP come from resource timing:
  address, kind, timing, sizes, and the status where the engine reports it. Loads from another scheme,
  which includes the files an application serves from a scheme of its own, are known from the element
  that asked for them and are listed as `finished` or `failed to load`. Requests of workers, loads
  that a style sheet starts from such a scheme, and headers that the engine adds are not seen.
- **Waiting** is clocked by the host, never by a timer of the page. An engine slows the timers of a
  view that is not shown down to one a second or less; automation keeps its pace there, while the
  page's own timers do not.
- **A view without a size** still takes input. On Linux, a view in a window that was never shown has a
  viewport without area, where the box of an element says nothing; an element that has a box at all is
  then taken as drawn. Screenshots and `click_at` need a window that is shown.
- **Dialogs** of JavaScript stay open until `handle_dialog` answers them, for at most
  `NeoAstraOptions.DecisionTimeout`. While automation is on, they do not reach the
  `ScriptDialogRequested` handler of the application.
- **Screenshots** are PNG or JPEG images from `NeoAstra.CaptureAsync`, with its
  [limits](known-limitations.md#backend-capability-differences): a view must be visible to be captured
  on Windows, and macOS captures no full page.
- **Page sizes** are in CSS pixels, and a window is not: Windows counts it in the pixels of its
  display, of which a CSS pixel takes more on a display that scales, the zoom of a view changes what a
  CSS pixel takes on every platform, and a GTK window draws its title bar inside its own size.
  `resize_page` gives the window the size that the page needs, in one call. A window stops at its screen
  and at the least size of its frame, and at some scales and zooms no window size comes to a given
  page size: WebView2 has no page of 4n + 1 CSS pixels on a display at 125 percent. The answer of the
  tool then says which size the page has.
- **Pages** are the views of the application. `new_page` opens a window through `NewPageHandler`; the
  default one gives the pages of an `isolatedContext` an ephemeral profile of their own. `close_page`
  asks the window of the page to close, which the application may refuse.
- **Files** named by a tool call, such as the `filePath` of a screenshot or the files of
  `upload_file`, must be inside `AllowedDirectories`; a relative path is resolved against the first one.

## Security

Automation gives its caller full control over the pages it drives, and through them over everything
those pages may ask of the application. Treat it as a debugging interface: create a `NeoAutomation`
only for a caller that the application trusts as much as its own user, such as a development or test
build, and keep a server that exposes it off the network. `AllowScriptEvaluation = false` removes the
caller's own scripts but not its control over the page.

## Platform status

The `automation:` scenarios of the
[conformance harness](building.md#browser-conformance-and-benchmarks) call every tool. They pass on
Windows 11 with WebView2, on macOS 15 with WKWebView, and on Ubuntu 24.04 with WebKitGTK 2.52. The
scenario for an element with an EditContext runs on WebView2 only, because WebKit has no EditContext.
The scenario for the focus events runs in the hidden window of the harness, on every engine.
The unit tests, which go further into each tool, drive a live browser on Windows only.
