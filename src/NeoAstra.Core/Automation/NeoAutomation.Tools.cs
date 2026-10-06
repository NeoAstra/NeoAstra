// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NeoAstra;

// The automation operations as named tools with JSON arguments. The names, the arguments, and the text of the
// results follow the tools of Chrome DevTools MCP, so that a caller that knows those tools can use these unchanged.
public sealed partial class NeoAutomation
{
    private const string LastPageMessage = "The last open page cannot be closed. It is fine to keep it open.";
    private const int InlineImageLimit = 2_000_000;
    private const int DefaultPageSize = 20;

    private static readonly string[] ConsoleTypes =
    [
        "log", "debug", "info", "error", "warn", "dir", "dirxml", "table", "trace", "clear", "startGroup", "startGroupCollapsed",
        "endGroup", "assert", "profile", "profileEnd", "count", "timeEnd", "verbose", "issue",
    ];

    private static readonly string[] ResourceTypes =
    [
        "document", "stylesheet", "image", "media", "font", "script", "texttrack", "xhr", "fetch", "prefetch", "eventsource",
        "websocket", "manifest", "signedexchange", "ping", "cspviolationreport", "preflight", "fedcm", "other",
    ];

    private readonly NeoAutomationTool[] _tools;

    /// <summary>Gets the automation operations as tools, sorted by name.</summary>
    /// <remarks>
    /// The list holds the tools of Chrome DevTools MCP that work the same on every browser engine: input, navigation,
    /// <c>resize_page</c>, the network and console lists, <c>evaluate_script</c>, <c>take_screenshot</c>, and
    /// <c>take_snapshot</c>. <c>evaluate_script</c> is left out when <see cref="NeoAutomationOptions.AllowScriptEvaluation"/>
    /// is <see langword="false"/>.
    /// </remarks>
    public IReadOnlyList<NeoAutomationTool> Tools => _tools;

    /// <summary>Runs a tool.</summary>
    /// <param name="name">The name of a tool of <see cref="Tools"/>.</param>
    /// <param name="arguments">The arguments as a JSON object that follows the input schema of the tool. An undefined or null value stands for no arguments.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>
    /// The result. A tool that cannot do what it was asked completes with <see cref="NeoAutomationToolResult.IsError"/>
    /// set and a text that says why, as a Model Context Protocol server reports a failed tool call.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ObjectDisposedException">Automation was turned off.</exception>
    public ValueTask<NeoAutomationToolResult> CallToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var tool = Array.Find(_tools, candidate => candidate.Name == name);
        if (tool is null)
        {
            return ValueTask.FromResult(new NeoAutomationToolResult($"Unknown tool \"{name}\". Call a tool of the list of tools.", [], true));
        }

        if (arguments.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined))
        {
            return ValueTask.FromResult(new NeoAutomationToolResult("The arguments of a tool must be a JSON object.", [], true));
        }

        // The arguments may belong to a document that the caller disposes as soon as this method returns.
        var owned = arguments.ValueKind == JsonValueKind.Object ? arguments.Clone() : default;
        return InvokeAsync(async () =>
        {
            var call = new NeoAutomationToolCall(this, owned, cancellationToken);
            var failed = false;
            try
            {
                await tool.Handler(call);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is NeoAutomationException or ArgumentException or NotSupportedException or IOException
                or UnauthorizedAccessException or TimeoutException or InvalidOperationException or OperationCanceledException or JsonException)
            {
                failed = true;
                call.Lines.Clear();
                call.Lines.Add(exception is OperationCanceledException ? "The operation was canceled before it completed." : exception.Message);
                call.Snapshot = null;
                call.IncludePages = false;
                call.Sections.Clear();
                call.Images.Clear();
                // The message of that error already describes the dialog.
                if (exception is NeoAutomationException { Code: "dialog-open" }) call.Page = null;
            }

            return new NeoAutomationToolResult(Format(call), call.Images.ToArray(), failed);
        }, cancellationToken);
    }

    /// <summary>Runs a tool with arguments given as JSON text.</summary>
    /// <param name="name">The name of a tool of <see cref="Tools"/>.</param>
    /// <param name="argumentsJson">The arguments as the JSON text of an object, or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The result, as for the overload that takes a <see cref="JsonElement"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException"><paramref name="argumentsJson"/> is not valid JSON.</exception>
    public ValueTask<NeoAutomationToolResult> CallToolAsync(string name, string? argumentsJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return CallToolAsync(name, default(JsonElement), cancellationToken);
        using var document = JsonDocument.Parse(argumentsJson);
        return CallToolAsync(name, document.RootElement, cancellationToken);
    }

    /// <summary>Runs a tool with arguments given by name, as a Model Context Protocol server library hands them over.</summary>
    /// <param name="name">The name of a tool of <see cref="Tools"/>.</param>
    /// <param name="arguments">The arguments by name, or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The result, as for the overload that takes a <see cref="JsonElement"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An argument has no name.</exception>
    public ValueTask<NeoAutomationToolResult> CallToolAsync(string name, IEnumerable<KeyValuePair<string, JsonElement>>? arguments, CancellationToken cancellationToken = default)
    {
        if (arguments is null) return CallToolAsync(name, default(JsonElement), cancellationToken);
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in arguments)
            {
                if (key is null) throw new ArgumentException("An argument has no name.", nameof(arguments));
                writer.WritePropertyName(key);
                // A value that was never set stands for an argument that is not given.
                if (value.ValueKind == JsonValueKind.Undefined) writer.WriteNullValue();
                else value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return CallToolAsync(name, document.RootElement, cancellationToken);
    }

    /// <summary>Runs a tool that needs no arguments.</summary>
    /// <param name="name">The name of a tool of <see cref="Tools"/>.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The result, as for the overload that takes a <see cref="JsonElement"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public ValueTask<NeoAutomationToolResult> CallToolAsync(string name, CancellationToken cancellationToken = default)
        => CallToolAsync(name, default(JsonElement), cancellationToken);

    // -------------------------------------------------------------------------------------------------------------
    // The response text
    // -------------------------------------------------------------------------------------------------------------

    private string Format(NeoAutomationToolCall call)
    {
        var builder = new StringBuilder();
        foreach (var line in call.Lines) builder.Append(line).Append('\n');

        if (call.Page?.Dialog is { } dialog)
        {
            var defaultValue = dialog.Kind == NeoScriptDialogKind.Prompt ? $" (default value: \"{dialog.DefaultText}\")" : string.Empty;
            builder.Append("# Open dialog\n")
                .Append(dialog.TypeName).Append(": ").Append(dialog.Message).Append(defaultValue).Append(".\n")
                .Append("Call handle_dialog to handle it before continuing.\n");
        }

        if (call.IncludePages)
        {
            if (TakeSelectionNote() is { } note) builder.Append(note).Append('\n');
            var pages = Pages;
            if (pages.Count != 0)
            {
                builder.Append("## Pages\n");
                foreach (var page in pages)
                {
                    var url = page.Url?.AbsoluteUri ?? "about:blank";
                    var title = page.Title;
                    var label = title.Length != 0 && title != url ? $"{Truncate(title, 50)} ({url})" : url;
                    builder.Append(page.Id.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(label);
                    if (IsSelected(page)) builder.Append(" [selected]");
                    if (page.IsolatedContext is { } context) builder.Append(" isolatedContext=").Append(context);
                    builder.Append('\n');
                }
            }
        }

        if (call.Snapshot is { } snapshot)
        {
            builder.Append("## Latest page snapshot\n").Append(snapshot.ToString());
            if (snapshot.IsTruncated) builder.Append("Note: the page has more elements than a snapshot holds; the rest is left out.\n");
        }

        foreach (var section in call.Sections) builder.Append(section).Append('\n');
        return builder.ToString().TrimEnd('\n');
    }

    private static string Truncate(string text, int limit) => text.Length <= limit ? text : string.Concat(text.AsSpan(0, limit - 3), "...");

    private static void AppendAction(NeoAutomationToolCall call, NeoAutomationActionResult result, string succeeded, string openedDialog)
    {
        if (result.Dialog is not null)
        {
            call.Lines.Add(result.Note ?? openedDialog);
            return;
        }

        call.Lines.Add(succeeded);
        if (result.Note is not null) call.Lines.Add(result.Note);
        if (result.NavigatedTo is { } url) call.Lines.Add($"Page navigated to {url.AbsoluteUri}.");
    }

    private static async Task IncludeSnapshotAsync(NeoAutomationToolCall call, NeoAutomationPage page, bool verbose = false)
    {
        // A page that is stopped in a dialog cannot be read; the dialog is reported instead.
        if (page.Dialog is not null) return;
        call.Snapshot = await page.TakeSnapshotAsync(verbose, call.CancellationToken);
    }

    private static (int Start, int End, List<string> Info) Paginate(int total, int? pageSize, int? pageIndex)
    {
        var info = new List<string>();
        if (pageSize is null && pageIndex is null)
        {
            info.Add($"Showing {(total == 0 ? 0 : 1)}-{total} of {total} (Page 1 of 1).");
            return (0, total, info);
        }

        var size = pageSize ?? DefaultPageSize;
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)size));
        var current = pageIndex ?? 0;
        if (current < 0 || current >= pages)
        {
            info.Add("Invalid page number provided. Showing first page.");
            current = 0;
        }

        var start = current * size;
        var end = Math.Min(total, start + size);
        info.Add($"Showing {(total == 0 ? 0 : start + 1)}-{end} of {total} (Page {current + 1} of {pages}).");
        if (current < pages - 1) info.Add($"Next page: {current + 1}");
        if (current > 0) info.Add($"Previous page: {current - 1}");
        return (start, end, info);
    }

    // -------------------------------------------------------------------------------------------------------------
    // Files
    // -------------------------------------------------------------------------------------------------------------

    /// <summary>Resolves a file path of a tool call inside the directories the application allows.</summary>
    internal string ResolveFilePath(string path, string argument) => ResolveFilePath(Options, path, argument);

    internal static string ResolveFilePath(NeoAutomationOptions options, string path, string argument)
    {
        if (string.IsNullOrWhiteSpace(path)) throw NeoAutomationToolCall.Invalid(argument, "a file path");
        if (options.AllowedDirectories.Count == 0)
        {
            throw new NeoAutomationException("not-allowed",
                $"File access is turned off for this application, so \"{argument}\" cannot be used. Omit it to get the result inline.");
        }

        string full;
        try { full = Path.GetFullPath(path, options.AllowedDirectories[0]); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new NeoAutomationException("invalid-arguments", $"The argument \"{argument}\" is not a valid file path.", exception);
        }

        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (var directory in options.AllowedDirectories)
        {
            var root = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, comparison)) return full;
        }

        throw new NeoAutomationException("not-allowed", $"The path of \"{argument}\" is outside the directories this application allows for files.");
    }

    private async Task<string> SaveFileAsync(string path, string argument, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var full = ResolveFilePath(path, argument);
        if (Path.GetDirectoryName(full) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(full, data, cancellationToken);
        return full;
    }

    // -------------------------------------------------------------------------------------------------------------
    // The tools
    // -------------------------------------------------------------------------------------------------------------

    /// <summary>Describes the tools for a set of options. The handlers act on the automation of the call they are given.</summary>
    internal static NeoAutomationTool[] CreateTools(NeoAutomationOptions options)
    {
        const string uid = "The uid of an element on the page from the page content snapshot";
        const string includeSnapshot = "Whether to include a snapshot in the response. Default is false.";
        const string dblClick = "Set to true for double clicks. Default is false.";
        const string timeout = "Maximum wait time in milliseconds. If set to 0, the default timeout will be used.";

        var tools = new List<NeoAutomationTool>
        {
            Tool("click", "input", "Clicks on the provided element", false,
                Schema().String("uid", uid, required: true).Boolean("dblClick", dblClick).Boolean("includeSnapshot", includeSnapshot),
                async call =>
                {
                    var page = call.ResolvePage();
                    var twice = call.OptionalBoolean("dblClick") ?? false;
                    var result = await page.ClickAsync(call.RequiredString("uid"), twice, call.CancellationToken);
                    AppendAction(call, result,
                        twice ? "Successfully double clicked on the element" : "Successfully clicked on the element",
                        twice ? "The element was double clicked and it opened a dialog." : "The element was clicked and it opened a dialog.");
                    if (call.OptionalBoolean("includeSnapshot") == true) await IncludeSnapshotAsync(call, page);
                }),

            Tool("click_at", "input", "Clicks at the provided coordinates", false,
                Schema().Number("x", "The x coordinate", required: true).Number("y", "The y coordinate", required: true).Boolean("dblClick", dblClick).Boolean("includeSnapshot", includeSnapshot),
                async call =>
                {
                    var page = call.ResolvePage();
                    var twice = call.OptionalBoolean("dblClick") ?? false;
                    var result = await page.ClickAtAsync(call.RequiredNumber("x"), call.RequiredNumber("y"), twice, call.CancellationToken);
                    AppendAction(call, result,
                        twice ? "Successfully double clicked at the coordinates" : "Successfully clicked at the coordinates",
                        "The click opened a dialog.");
                    if (call.OptionalBoolean("includeSnapshot") == true) await IncludeSnapshotAsync(call, page);
                }),

            Tool("drag", "input", "Drag an element onto another element", false,
                Schema().String("from_uid", "The uid of the element to drag", required: true).String("to_uid", "The uid of the element to drop into", required: true).Boolean("includeSnapshot", includeSnapshot),
                async call =>
                {
                    var page = call.ResolvePage();
                    var result = await page.DragAsync(call.RequiredString("from_uid"), call.RequiredString("to_uid"), call.CancellationToken);
                    AppendAction(call, result, "Successfully dragged an element", "The drag opened a dialog.");
                    if (call.OptionalBoolean("includeSnapshot") == true) await IncludeSnapshotAsync(call, page);
                }),

            Tool("fill", "input", "Type text into an input, text area or select an option from a <select> element.", false,
                Schema().String("uid", uid, required: true)
                    .String("value", "The value to fill in. \"true\" or \"false\" for checkboxes and toggles, \"true\" for radio buttons.", required: true)
                    .Boolean("includeSnapshot", includeSnapshot),
                async call =>
                {
                    var page = call.ResolvePage();
                    var result = await page.FillAsync(call.RequiredString("uid"), call.RequiredString("value"), call.CancellationToken);
                    AppendAction(call, result, "Successfully filled out the element", "The element was filled out and it opened a dialog.");
                    if (call.OptionalBoolean("includeSnapshot") == true) await IncludeSnapshotAsync(call, page);
                }),

            Tool("fill_form", "input",
                "Fill out multiple form elements (inputs, selects, checkboxes, radios) at once. ALWAYS prefer this tool over multiple individual 'fill' or 'click' calls when interacting with forms. " +
                "It is significantly faster, more reliable, and reduces turn count. Example: Fill username, password, and check \"Remember Me\" in one call.", false,
                Schema().Array("elements", "Elements from snapshot to fill out.", required: true, items: writer =>
                {
                    writer.WriteString("type", "object");
                    writer.WriteString("description", "An element to fill out");
                    writer.WriteStartObject("properties");
                    WriteProperty(writer, "uid", "string", "The uid of the element to fill out");
                    WriteProperty(writer, "value", "string", "Value for the element. \"true\" or \"false\" for checkboxes and toggles, \"true\" for radio buttons.");
                    writer.WriteEndObject();
                    writer.WriteStartArray("required");
                    writer.WriteStringValue("uid");
                    writer.WriteStringValue("value");
                    writer.WriteEndArray();
                    writer.WriteBoolean("additionalProperties", false);
                }).Boolean("includeSnapshot", includeSnapshot),
                async call =>
                {
                    var page = call.ResolvePage();
                    var elements = new List<NeoAutomationFormValue>();
                    foreach (var element in call.RequiredArray("elements").EnumerateArray())
                    {
                        if (element.ValueKind != JsonValueKind.Object ||
                            !element.TryGetProperty("uid", out var elementUid) || elementUid.ValueKind != JsonValueKind.String ||
                            !element.TryGetProperty("value", out var elementValue) || elementValue.ValueKind != JsonValueKind.String)
                        {
                            throw NeoAutomationToolCall.Invalid("elements", "an array of objects with a string \"uid\" and a string \"value\"");
                        }

                        elements.Add(new NeoAutomationFormValue(elementUid.GetString()!, elementValue.GetString()!));
                    }

                    if (elements.Count == 0) throw NeoAutomationToolCall.Invalid("elements", "a non-empty array");
                    var result = await page.FillFormAsync(elements, call.CancellationToken);
                    AppendAction(call, result, "Successfully filled out the form", "Filling out the form opened a dialog.");
                    if (call.OptionalBoolean("includeSnapshot") == true) await IncludeSnapshotAsync(call, page);
                }),

            Tool("handle_dialog", "input", "If a browser dialog was opened, use this command to handle it", false,
                Schema().Enum("action", "Whether to dismiss or accept the dialog", ["accept", "dismiss"], required: true).String("promptText", "Optional prompt text to enter into the dialog."),
                async call =>
                {
                    var page = call.ResolvePage();
                    var action = call.RequiredString("action");
                    if (action is not ("accept" or "dismiss")) throw NeoAutomationToolCall.Invalid("action", "\"accept\" or \"dismiss\"");
                    await page.HandleDialogAsync(action == "accept", call.OptionalString("promptText"), call.CancellationToken);
                    call.Lines.Add(action == "accept" ? "Successfully accepted the dialog" : "Successfully dismissed the dialog");
                    call.IncludePages = true;
                }),

            Tool("hover", "input", "Hover over the provided element", false,
                Schema().String("uid", uid, required: true).Boolean("includeSnapshot", includeSnapshot),
                async call =>
                {
                    var page = call.ResolvePage();
                    var result = await page.HoverAsync(call.RequiredString("uid"), call.CancellationToken);
                    AppendAction(call, result, "Successfully hovered over the element", "The element was hovered and it opened a dialog.");
                    if (call.OptionalBoolean("includeSnapshot") == true) await IncludeSnapshotAsync(call, page);
                }),

            Tool("press_key", "input",
                "Press a key or key combination. Use this when other input methods like fill() cannot be used (e.g., keyboard shortcuts, navigation keys, or special key combinations).", false,
                Schema().String("key", "A key or a combination (e.g., \"Enter\", \"Control+A\", \"Control++\", \"Control+Shift+R\"). Modifiers: Control, Shift, Alt, Meta", required: true)
                    .Boolean("includeSnapshot", includeSnapshot),
                async call =>
                {
                    var page = call.ResolvePage();
                    var key = call.RequiredString("key");
                    var result = await page.PressKeyAsync(key, call.CancellationToken);
                    AppendAction(call, result, $"Successfully pressed key: {key}", $"The key {key} was pressed and it opened a dialog.");
                    if (call.OptionalBoolean("includeSnapshot") == true) await IncludeSnapshotAsync(call, page);
                }),

            Tool("type_text", "input", "Type text using keyboard into a previously focused input", false,
                Schema().String("text", "The text to type", required: true).String("submitKey", "Optional key to press after typing. E.g., \"Enter\", \"Tab\", \"Escape\""),
                async call =>
                {
                    var page = call.ResolvePage();
                    var text = call.RequiredString("text");
                    var submitKey = call.OptionalString("submitKey");
                    var result = await page.TypeTextAsync(text, submitKey, call.CancellationToken);
                    var typed = $"Typed text \"{text}{(string.IsNullOrEmpty(submitKey) ? string.Empty : $" + {submitKey}")}\"";
                    AppendAction(call, result, typed, typed + " and it opened a dialog.");
                }),

            Tool("upload_file", "input", "Upload a file through a provided element.", false,
                Schema().String("uid", "The uid of the file input element or an element that holds one on the page from the page content snapshot", required: true)
                    .Array("filePaths", "One or more files paths to upload. File paths have to be local to the application.", required: true, items: writer => writer.WriteString("type", "string"), minItems: 1)
                    .Boolean("includeSnapshot", includeSnapshot),
                async call =>
                {
                    var page = call.ResolvePage();
                    var paths = call.OptionalStrings("filePaths") ?? throw NeoAutomationToolCall.Missing("filePaths");
                    if (paths.Count == 0) throw NeoAutomationToolCall.Invalid("filePaths", "a non-empty array");
                    var resolved = paths.Select(path => call.Automation.ResolveFilePath(path, "filePaths")).ToArray();
                    var result = await page.UploadFileAsync(call.RequiredString("uid"), resolved, call.CancellationToken);
                    AppendAction(call, result, $"File uploaded from {string.Join(", ", paths)}.", "The upload opened a dialog.");
                    if (call.OptionalBoolean("includeSnapshot") == true) await IncludeSnapshotAsync(call, page);
                }),

            Tool("close_page", "navigation", "Closes the page by its index. The last open page cannot be closed.", false,
                Schema(pageId: false).Number("pageId", "The ID of the page to close. Call list_pages to list pages.", required: true),
                async call =>
                {
                    var page = call.Automation.GetPage(call.OptionalInteger("pageId") ?? throw NeoAutomationToolCall.Missing("pageId"));
                    if (!await call.Automation.ClosePageAsync(page, call.CancellationToken)) call.Lines.Add(LastPageMessage);
                    else if (!page.IsClosed) call.Lines.Add($"Page {page.Id} was asked to close, and the application kept it open.");
                    call.IncludePages = true;
                }),

            Tool("list_pages", "navigation", "Get a list of pages open in the browser.", true, Schema(pageId: false),
                call =>
                {
                    call.IncludePages = true;
                    call.Page = call.Automation.SelectedPage;
                    return Task.CompletedTask;
                }),

            Tool("navigate_page", "navigation", "Go to a URL, or back, forward, or reload. Use project URL if not specified otherwise.", false,
                NavigateSchema(options, timeout),
                async call =>
                {
                    var page = call.ResolvePage();
                    var type = call.OptionalString("type");
                    var url = call.OptionalString("url");
                    if (type is null && url is null) throw new NeoAutomationException("invalid-arguments", "Either URL or a type is required.");
                    var kind = (type ?? "url") switch
                    {
                        "url" => NeoAutomationNavigationKind.Url,
                        "back" => NeoAutomationNavigationKind.Back,
                        "forward" => NeoAutomationNavigationKind.Forward,
                        "reload" => NeoAutomationNavigationKind.Reload,
                        _ => throw NeoAutomationToolCall.Invalid("type", "\"url\", \"back\", \"forward\", or \"reload\""),
                    };
                    var beforeUnload = call.OptionalString("handleBeforeUnload") ?? "accept";
                    if (beforeUnload is not ("accept" or "dismiss")) throw NeoAutomationToolCall.Invalid("handleBeforeUnload", "\"accept\" or \"dismiss\"");
                    var options = new NeoAutomationNavigationOptions
                    {
                        Kind = kind,
                        IgnoreCache = call.OptionalBoolean("ignoreCache") ?? false,
                        AcceptBeforeUnload = beforeUnload == "accept",
                        InitScript = call.OptionalString("initScript"),
                        Timeout = call.OptionalTimeout("timeout"),
                    };
                    if (kind == NeoAutomationNavigationKind.Url)
                    {
                        if (url is null) throw new NeoAutomationException("invalid-arguments", "A URL is required for navigation of type=url.");
                        options.Url = ParseUrl(url);
                    }

                    var result = await page.NavigateAsync(options, call.CancellationToken);
                    call.Lines.Add(result.Message);
                    if (result.BeforeUnloadHandled) call.Lines.Add(beforeUnload == "dismiss" ? "Dismissed a beforeunload dialog." : "Accepted a beforeunload dialog.");
                    call.IncludePages = true;
                }),

            Tool("new_page", "navigation", "Open a new tab and load a URL. Use project URL if not specified otherwise.", false,
                Schema(pageId: false).String("url", "URL to load in a new page.", required: true)
                    .Boolean("background", "Whether to open the page in the background without bringing it to the front. Default is false (foreground).")
                    .String("isolatedContext",
                        "If specified, the page is created in an isolated browser context with the given name. Pages in the same browser context share cookies and storage. " +
                        "Pages in different browser contexts are fully isolated (useful for clean-slate testing of cookies and authentication).")
                    .Integer("timeout", timeout),
                async call =>
                {
                    var page = await call.Automation.NewPageAsync(ParseUrl(call.RequiredString("url")), call.OptionalBoolean("background") ?? false,
                        call.OptionalString("isolatedContext"), call.OptionalTimeout("timeout"), call.CancellationToken);
                    call.Page = page;
                    call.IncludePages = true;
                }),

            Tool("select_page", "navigation", "Select a page as a context for future tool calls.", true,
                Schema(pageId: false).Number("pageId", "The ID of the page to select. Call list_pages to get available pages.", required: true)
                    .Boolean("bringToFront", "Whether to focus the page and bring it to the top."),
                async call =>
                {
                    var page = call.Automation.GetPage(call.OptionalInteger("pageId") ?? throw NeoAutomationToolCall.Missing("pageId"));
                    await call.Automation.SelectPageAsync(page, call.OptionalBoolean("bringToFront") ?? false, call.CancellationToken);
                    call.Page = page;
                    call.IncludePages = true;
                }),

            Tool("wait_for", "navigation", "Wait for the specified text to appear on the selected page.", true,
                Schema().Array("text", "Non-empty list of texts. Resolves when any value appears on the page.", required: true, items: writer => writer.WriteString("type", "string"), minItems: 1)
                    .Integer("timeout", timeout),
                async call =>
                {
                    var page = call.ResolvePage();
                    var texts = call.OptionalStrings("text") ?? throw NeoAutomationToolCall.Missing("text");
                    if (texts.Count == 0) throw NeoAutomationToolCall.Invalid("text", "a non-empty array");
                    await page.WaitForAsync(texts, call.OptionalTimeout("timeout"), call.CancellationToken);
                    call.Lines.Add($"Element matching one of {StringArrayJson(texts)} found.");
                    await IncludeSnapshotAsync(call, page);
                }),

            Tool("resize_page", "emulation", "Resizes the page's window so that the page has specified dimension", false,
                Schema().Number("width", "Page width", required: true).Number("height", "Page height", required: true),
                async call =>
                {
                    var page = call.ResolvePage();
                    var width = call.OptionalInteger("width") ?? throw NeoAutomationToolCall.Missing("width");
                    var height = call.OptionalInteger("height") ?? throw NeoAutomationToolCall.Missing("height");
                    if (width <= 0 || height <= 0) throw new NeoAutomationException("invalid-arguments", "The page width and height must be positive.");
                    await page.ResizeAsync(width, height, call.CancellationToken);
                    call.IncludePages = true;
                }),

            Tool("get_network_request", "network",
                "Gets a network request by its reqid. Useful for inspecting the request and response headers and bodies that the page itself can see.", true,
                Schema().Number("reqid", "The reqid of the network request, from list_network_requests.", required: true)
                    .String("requestFilePath", "The absolute or relative path to a .network-request file to save the request body to. If omitted, the body is returned inline.")
                    .String("responseFilePath", "The absolute or relative path to a .network-response file to save the response body to. If omitted, the body is returned inline."),
                async call =>
                {
                    var page = call.ResolvePage();
                    var id = call.OptionalInteger("reqid") ?? throw NeoAutomationToolCall.Missing("reqid");
                    var request = await page.GetNetworkRequestAsync(id, call.CancellationToken)
                        ?? throw new NeoAutomationException("no-request", $"No network request with reqid {id} was found. Call list_network_requests to list the requests.");
                    call.Sections.Add(await call.Automation.FormatRequestAsync(call, request));
                }),

            Tool("list_network_requests", "network", "Lists the most recent requests for the target page since the last navigation.", true,
                Schema().Integer("pageSize", "Maximum number of requests to return. When omitted, returns all requests.", minimum: 1)
                    .Integer("pageIdx", "Page number to return (0-based). When omitted, returns the first page.", minimum: 0)
                    .Array("resourceTypes", "Filter requests to only return requests of the specified resource types. When omitted or empty, returns all requests.", required: false, items: writer => WriteEnum(writer, ResourceTypes))
                    .Boolean("includePreservedRequests", "Set to true to return the preserved requests over the last 3 navigations."),
                async call =>
                {
                    var page = call.ResolvePage();
                    var requests = await page.GetNetworkRequestsAsync(call.OptionalBoolean("includePreservedRequests") ?? false, call.CancellationToken);
                    if (call.OptionalStrings("resourceTypes") is { Count: > 0 } types) requests = requests.Where(request => types.Contains(request.ResourceType)).ToArray();
                    var section = new StringBuilder("## Network requests\n");
                    if (requests.Count == 0) section.Append("No requests found.");
                    else
                    {
                        var (start, end, info) = Paginate(requests.Count, PositiveInteger(call, "pageSize"), call.OptionalInteger("pageIdx"));
                        foreach (var line in info) section.Append(line).Append('\n');
                        for (var index = start; index < end; index++) section.Append(FormatRequestLine(requests[index])).Append('\n');
                    }

                    call.Sections.Add(section.ToString().TrimEnd('\n'));
                }),

            Tool("get_console_message", "debugging", "Gets a console message by its ID. You can get all messages by calling list_console_messages.", true,
                Schema().Number("msgid", "The msgid of a console message on the page from the listed console messages", required: true),
                async call =>
                {
                    var page = call.ResolvePage();
                    var id = call.OptionalInteger("msgid") ?? throw NeoAutomationToolCall.Missing("msgid");
                    var message = await page.GetConsoleMessageAsync(id, call.CancellationToken)
                        ?? throw new NeoAutomationException("no-message", $"No console message with msgid {id} was found. Call list_console_messages to list the messages.");
                    call.Sections.Add(FormatMessage(message));
                }),

            Tool("list_console_messages", "debugging", "List all console messages for the target page since the last navigation.", true,
                Schema().Integer("pageSize", "Maximum number of messages to return. When omitted, returns all messages.", minimum: 1)
                    .Integer("pageIdx", "Page number to return (0-based). When omitted, returns the first page.", minimum: 0)
                    .Array("types", "Filter messages to only return messages of the specified resource types. When omitted or empty, returns all messages.", required: false, items: writer => WriteEnum(writer, ConsoleTypes))
                    .Boolean("includePreservedMessages", "Set to true to return the preserved messages over the last 3 navigations.")
                    .Boolean("includeStackTraces", "Set to true to include the stack trace for each message when available. Increases the response size."),
                async call =>
                {
                    var page = call.ResolvePage();
                    var messages = await page.GetConsoleMessagesAsync(call.OptionalBoolean("includePreservedMessages") ?? false, call.CancellationToken);
                    if (call.OptionalStrings("types") is { Count: > 0 } types) messages = messages.Where(message => types.Contains(message.Type)).ToArray();
                    var section = new StringBuilder("## Console messages\n");
                    if (messages.Count == 0) section.Append("<no console messages found>");
                    else
                    {
                        // Consecutive messages that repeat are shown once with their count, as the console does.
                        var groups = new List<(NeoAutomationConsoleMessage Message, int Count)>();
                        foreach (var message in messages)
                        {
                            if (groups.Count != 0 && groups[^1].Message is var previous && previous.Type == message.Type && previous.Text == message.Text && previous.Arguments.Count == message.Arguments.Count)
                            {
                                groups[^1] = (previous, groups[^1].Count + 1);
                            }
                            else groups.Add((message, 1));
                        }

                        var stacks = call.OptionalBoolean("includeStackTraces") ?? false;
                        var (start, end, info) = Paginate(groups.Count, PositiveInteger(call, "pageSize"), call.OptionalInteger("pageIdx"));
                        foreach (var line in info) section.Append(line).Append('\n');
                        var anyStack = false;
                        for (var index = start; index < end; index++)
                        {
                            var (message, count) = groups[index];
                            section.Append("msgid=").Append(message.Id).Append(" [").Append(message.Type).Append("] ").Append(message.Text)
                                .Append(" (").Append(message.Arguments.Count).Append(" args)");
                            if (count > 1) section.Append(" [").Append(count).Append(" times]");
                            section.Append('\n');
                            if (stacks && message.StackTrace is { } stack)
                            {
                                anyStack = true;
                                section.Append(stack).Append('\n');
                            }
                        }

                        if (anyStack) section.Append("Note: stack trace line and column numbers use 1-based indexing\n");
                    }

                    call.Sections.Add(section.ToString().TrimEnd('\n'));
                }),

            Tool("take_screenshot", "debugging", "Take a screenshot of the page or element.", false,
                Schema().Enum("format", "Type of format to save the screenshot as. Default is \"png\"", ["png", "jpeg"])
                    .Number("quality", "Compression quality for JPEG format (0-100). Higher values mean better quality but larger file sizes. Ignored for PNG format.", minimum: 0, maximum: 100)
                    .String("uid", "The uid of an element on the page from the page content snapshot. If omitted, takes a page screenshot.")
                    .Boolean("fullPage", "If set to true takes a screenshot of the full page instead of the currently visible viewport. Incompatible with uid.")
                    .String("filePath", "The absolute path, or a path relative to the first allowed directory, to save the screenshot to instead of attaching it to the response."),
                async call =>
                {
                    var page = call.ResolvePage();
                    var format = (call.OptionalString("format") ?? "png") switch
                    {
                        "png" => NeoCaptureFormat.Png,
                        "jpeg" => NeoCaptureFormat.Jpeg,
                        "webp" => throw new NeoAutomationException("not-supported", "The webp format is not available on every browser engine. Use \"png\" or \"jpeg\"."),
                        _ => throw NeoAutomationToolCall.Invalid("format", "\"png\" or \"jpeg\""),
                    };
                    var elementUid = call.OptionalString("uid");
                    var fullPage = call.OptionalBoolean("fullPage") ?? false;
                    if (elementUid is not null && fullPage) throw new NeoAutomationException("invalid-arguments", "Providing both \"uid\" and \"fullPage\" is not allowed.");
                    int? quality = null;
                    if (format == NeoCaptureFormat.Jpeg && call.OptionalNumber("quality") is { } requested) quality = Math.Clamp((int)Math.Round(requested), 1, 100);
                    var filePath = call.OptionalString("filePath");
                    if (filePath is not null) _ = call.Automation.ResolveFilePath(filePath, "filePath");

                    var image = await page.TakeScreenshotAsync(new NeoAutomationScreenshotOptions { Format = format, Quality = quality, Uid = elementUid, FullPage = fullPage }, call.CancellationToken);
                    call.Lines.Add(elementUid is not null ? $"Took a screenshot of node with uid \"{elementUid}\"."
                        : fullPage ? "Took a screenshot of the full current page."
                        : "Took a screenshot of the current page's viewport.");
                    if (filePath is not null)
                    {
                        call.Lines.Add($"Saved screenshot to {await call.Automation.SaveFileAsync(filePath, "filePath", image.Data, call.CancellationToken)}.");
                    }
                    else if (image.Data.Length >= InlineImageLimit)
                    {
                        // A large image is handed over as a file, where a caller can read the part it needs.
                        var directory = Path.Combine(Path.GetTempPath(), "neoastra-automation");
                        Directory.CreateDirectory(directory);
                        var path = Path.Combine(directory, $"screenshot-{Guid.NewGuid():N}{(format == NeoCaptureFormat.Jpeg ? ".jpeg" : ".png")}");
                        await File.WriteAllBytesAsync(path, image.Data, call.CancellationToken);
                        call.Lines.Add($"Saved screenshot to {path}.");
                    }
                    else call.Images.Add(image);
                }),

            Tool("take_snapshot", "debugging",
                "Take a text snapshot of the target page based on the a11y tree. The snapshot lists page elements along with a unique\n" +
                "identifier (uid). Always use the latest snapshot. Prefer taking a snapshot over taking a screenshot.", false,
                Schema().Boolean("verbose", "Whether to include all possible information available in the full a11y tree. Default is false.")
                    .String("filePath", "The absolute path, or a path relative to the first allowed directory, to save the snapshot to instead of attaching it to the response."),
                async call =>
                {
                    var page = call.ResolvePage();
                    var filePath = call.OptionalString("filePath");
                    if (filePath is not null) _ = call.Automation.ResolveFilePath(filePath, "filePath");
                    var snapshot = await page.TakeSnapshotAsync(call.OptionalBoolean("verbose") ?? false, call.CancellationToken);
                    if (filePath is null) call.Snapshot = snapshot;
                    else call.Lines.Add($"Saved snapshot to {await call.Automation.SaveFileAsync(filePath, "filePath", Encoding.UTF8.GetBytes(snapshot.ToString()), call.CancellationToken)}.");
                }),
        };

        if (options.AllowScriptEvaluation)
        {
            tools.Add(Tool("evaluate_script", "debugging",
                "Evaluate JavaScript inside the target page. The source can be provided inline or loaded from a local file. Returns the response as JSON, so returned values have to be JSON-serializable.", false,
                Schema().String("function",
                        "JavaScript source to execute in the target page. Provide either this or sourcePath, but not both. The source is interpreted according to format.\n" +
                        "Example without arguments: `() => document.title` or `async () => await fetch(\"example.com\")`.\n" +
                        "Example with arguments: `(el) => el.innerText`\n")
                    .String("sourcePath", "The absolute or relative path to a JavaScript file on the local filesystem of the application. Provide either this or function, but not both.")
                    .Enum("format",
                        "How to interpret the source. \"function\" treats it as a function declaration and supports args. \"script\" evaluates it as classic JavaScript and does not support args. " +
                        "Defaults to \"function\". ECMAScript modules are not supported.", ["function", "script"])
                    .Array("args", "An optional list of arguments to pass to the function.", required: false, items: writer =>
                    {
                        writer.WriteString("type", "string");
                        writer.WriteString("description", uid);
                    })
                    .String("filePath", "The absolute or relative path to a file to save the script output to. If omitted, the output is returned inline.")
                    .String("dialogAction", "Handle dialogs while execution. \"accept\", \"dismiss\", or string for response of window.prompt. Defaults to accept.")
                    .Boolean("waitForStableDom", "Whether to wait for the DOM to settle. Pass false if the script only reads data. Defaults to true."),
                async call =>
                {
                    var page = call.ResolvePage();
                    var inline = call.OptionalString("function");
                    var sourcePath = call.OptionalString("sourcePath");
                    if ((inline is null) == (sourcePath is null)) throw new NeoAutomationException("invalid-arguments", "Specify exactly one of function or sourcePath.");
                    var format = call.OptionalString("format") ?? "function";
                    if (format is not ("function" or "script")) throw NeoAutomationToolCall.Invalid("format", "\"function\" or \"script\"");
                    var args = call.OptionalStrings("args");
                    if (format == "script" && args is { Count: > 0 }) throw new NeoAutomationException("invalid-arguments", "args cannot be used when format is \"script\".");
                    var filePath = call.OptionalString("filePath");
                    if (filePath is not null) _ = call.Automation.ResolveFilePath(filePath, "filePath");

                    string source;
                    if (inline is not null) source = inline;
                    else
                    {
                        var full = call.Automation.ResolveFilePath(sourcePath!, "sourcePath");
                        try { source = await File.ReadAllTextAsync(full, call.CancellationToken); }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            throw new NeoAutomationException("no-file", $"Unable to read script source from {sourcePath}: {exception.Message}", exception);
                        }
                    }

                    var result = await page.EvaluateScriptAsync(source, new NeoAutomationEvaluateOptions
                    {
                        IsFunction = format == "function",
                        Arguments = args,
                        DialogAction = call.OptionalString("dialogAction"),
                        WaitForStableDom = call.OptionalBoolean("waitForStableDom") ?? true,
                    }, call.CancellationToken);

                    var output = result.Json ?? "undefined";
                    if (filePath is not null)
                    {
                        call.Lines.Add($"Script ran on page. Output saved to {await call.Automation.SaveFileAsync(filePath, "filePath", Encoding.UTF8.GetBytes(output), call.CancellationToken)}.");
                    }
                    else
                    {
                        call.Lines.Add("Script ran on page and returned:");
                        call.Lines.Add("```json");
                        call.Lines.Add(output);
                        call.Lines.Add("```");
                    }

                    if (result.Action.NavigatedTo is { } url) call.Lines.Add($"Page navigated to {url.AbsoluteUri}.");
                }));
        }

        tools.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        return tools.ToArray();
    }

    private static NeoAutomationTool Tool(string name, string category, string description, bool isReadOnly, SchemaBuilder schema, Func<NeoAutomationToolCall, Task> handler)
        => new(name, category, description, isReadOnly, schema.Build(), handler);

    private static SchemaBuilder NavigateSchema(NeoAutomationOptions options, string timeout)
    {
        var schema = Schema()
            .Enum("type", "Navigate the page by URL, back or forward in history, or reload.", ["url", "back", "forward", "reload"])
            .String("url", "Target URL (only type=url)")
            .Boolean("ignoreCache", "Whether to ignore cache on reload.")
            .Enum("handleBeforeUnload", "Whether to auto accept or beforeunload dialogs triggered by this navigation. Default is accept.", ["accept", "dismiss"]);
        if (options.AllowScriptEvaluation)
        {
            schema.String("initScript", "A JavaScript script to be executed on each new document before any other scripts for the next navigation.");
        }

        return schema.Integer("timeout", timeout);
    }

    private static Uri ParseUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            ? parsed
            : throw new NeoAutomationException("invalid-arguments", $"Invalid URL: \"{url}\". URLs must be valid according to the URL standard.");

    private static int? PositiveInteger(NeoAutomationToolCall call, string name)
    {
        if (call.OptionalInteger(name) is not { } value) return null;
        if (value <= 0) throw NeoAutomationToolCall.Invalid(name, "a positive integer");
        return value;
    }

    private static string StringArrayJson(IReadOnlyList<string> values) => NeoAutomationAgent.Json(writer =>
    {
        writer.WriteStartArray();
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }, readable: true);

    private static string FormatRequestLine(NeoAutomationNetworkRequest request)
    {
        // A long address, such as a data: URL, would crowd the list; the detail has it in full.
        var url = request.Url.Length > 1000 ? request.Url[..1000] + "... <truncated>" : request.Url;
        return $"reqid={request.Id} {request.Method} {url} [{request.Status}]";
    }

    private async Task<string> FormatRequestAsync(NeoAutomationToolCall call, NeoAutomationNetworkRequest request)
    {
        var builder = new StringBuilder();
        builder.Append("## Request ").Append(request.Url).Append('\n');
        builder.Append("Status: ").Append(request.Status).Append('\n');
        builder.Append("Resource type: ").Append(request.ResourceType).Append('\n');
        builder.Append("### Request Headers\n");
        foreach (var header in request.RequestHeaders) builder.Append("- ").Append(header.Key).Append(':').Append(header.Value).Append('\n');

        if (request.RequestBody is { } requestBody)
        {
            builder.Append("### Request Body\n");
            if (call.OptionalString("requestFilePath") is { } requestPath)
            {
                builder.Append("Saved to ").Append(await SaveFileAsync(requestPath, "requestFilePath", Encoding.UTF8.GetBytes(requestBody), call.CancellationToken)).Append(".\n");
            }
            else builder.Append(requestBody).Append('\n');
        }

        if (request.ResponseHeaders is { } responseHeaders)
        {
            builder.Append("### Response Headers\n");
            foreach (var header in responseHeaders) builder.Append("- ").Append(header.Key).Append(':').Append(header.Value).Append('\n');
        }

        if (request.ResponseBody is { } responseBody)
        {
            builder.Append("### Response Body\n");
            if (call.OptionalString("responseFilePath") is { } responsePath)
            {
                builder.Append("Saved to ").Append(await SaveFileAsync(responsePath, "responseFilePath", Encoding.UTF8.GetBytes(responseBody), call.CancellationToken)).Append(".\n");
            }
            else
            {
                builder.Append(responseBody.Length == 0 ? "<empty response>" : responseBody);
                if (request.IsResponseBodyTruncated) builder.Append("... <truncated>");
                builder.Append('\n');
            }
        }

        if (request.Failure is { } failure) builder.Append("### Request failed with\n").Append(failure).Append('\n');
        return builder.ToString().TrimEnd('\n');
    }

    private static string FormatMessage(NeoAutomationConsoleMessage message)
    {
        var builder = new StringBuilder();
        builder.Append("ID: ").Append(message.Id).Append('\n');
        builder.Append("Message: ").Append(message.Type).Append("> ").Append(message.Text).Append('\n');
        if (message.Url is { } url)
        {
            builder.Append("Source: ").Append(url);
            if (message.Line > 0) builder.Append(':').Append(message.Line).Append(':').Append(message.Column);
            builder.Append('\n');
        }

        if (message.Arguments.Count != 0)
        {
            builder.Append("### Arguments\n");
            for (var index = 0; index < message.Arguments.Count; index++) builder.Append("Arg #").Append(index).Append(": ").Append(message.Arguments[index]).Append('\n');
        }

        if (message.StackTrace is { } stack)
        {
            builder.Append("### Stack trace\n").Append(stack).Append('\n').Append("Note: line and column numbers use 1-based indexing\n");
        }

        return builder.ToString().TrimEnd('\n');
    }

    // -------------------------------------------------------------------------------------------------------------
    // JSON Schema of the arguments
    // -------------------------------------------------------------------------------------------------------------

    private static SchemaBuilder Schema(bool pageId = true)
    {
        var schema = new SchemaBuilder();
        if (pageId) schema.Number("pageId", "Targets a specific page by ID. When omitted, the selected page is used.");
        return schema;
    }

    private static void WriteProperty(Utf8JsonWriter writer, string name, string type, string description)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", type);
        writer.WriteString("description", description);
        writer.WriteEndObject();
    }

    private static void WriteEnum(Utf8JsonWriter writer, string[] values)
    {
        writer.WriteString("type", "string");
        writer.WriteStartArray("enum");
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    /// <summary>Builds the JSON Schema of an object whose properties are the arguments of a tool.</summary>
    private sealed class SchemaBuilder
    {
        private readonly List<(string Name, Action<Utf8JsonWriter> Write)> _properties = [];
        private readonly List<string> _required = [];

        internal SchemaBuilder String(string name, string description, bool required = false)
            => Add(name, required, writer =>
            {
                writer.WriteString("type", "string");
                writer.WriteString("description", description);
            });

        internal SchemaBuilder Boolean(string name, string description)
            => Add(name, false, writer =>
            {
                writer.WriteString("type", "boolean");
                writer.WriteString("description", description);
            });

        internal SchemaBuilder Number(string name, string description, bool required = false, double? minimum = null, double? maximum = null)
            => Add(name, required, writer =>
            {
                writer.WriteString("type", "number");
                if (minimum is { } low) writer.WriteNumber("minimum", low);
                if (maximum is { } high) writer.WriteNumber("maximum", high);
                writer.WriteString("description", description);
            });

        internal SchemaBuilder Integer(string name, string description, int? minimum = null)
            => Add(name, false, writer =>
            {
                writer.WriteString("type", "integer");
                if (minimum is { } low) writer.WriteNumber("minimum", low);
                writer.WriteString("description", description);
            });

        internal SchemaBuilder Enum(string name, string description, string[] values, bool required = false)
            => Add(name, required, writer =>
            {
                WriteEnum(writer, values);
                writer.WriteString("description", description);
            });

        internal SchemaBuilder Array(string name, string description, bool required, Action<Utf8JsonWriter> items, int? minItems = null)
            => Add(name, required, writer =>
            {
                writer.WriteString("type", "array");
                if (minItems is { } count) writer.WriteNumber("minItems", count);
                writer.WriteStartObject("items");
                items(writer);
                writer.WriteEndObject();
                writer.WriteString("description", description);
            });

        internal string Build() => NeoAutomationAgent.Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            foreach (var (name, write) in _properties)
            {
                writer.WriteStartObject(name);
                write(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            if (_required.Count != 0)
            {
                writer.WriteStartArray("required");
                foreach (var name in _required) writer.WriteStringValue(name);
                writer.WriteEndArray();
            }

            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
        }, readable: true);

        private SchemaBuilder Add(string name, bool required, Action<Utf8JsonWriter> write)
        {
            _properties.Add((name, write));
            if (required) _required.Add(name);
            return this;
        }
    }
}
