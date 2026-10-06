// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.Json;

namespace NeoAstra;

/// <summary>
/// An automation operation described as a tool: a name, a description, and a JSON Schema for its arguments, with
/// the names and arguments of the tool of Chrome DevTools MCP that it stands for.
/// </summary>
/// <remarks>
/// The description maps onto a tool of the Model Context Protocol as it is; <see cref="NeoAutomation.CallToolAsync(string, JsonElement, CancellationToken)"/>
/// runs the tool.
/// </remarks>
public sealed class NeoAutomationTool
{
    internal NeoAutomationTool(string name, string category, string description, bool isReadOnly, string inputSchema, Func<NeoAutomationToolCall, Task> handler)
    {
        Name = name;
        Category = category;
        Description = description;
        IsReadOnly = isReadOnly;
        // The element owns its data, so that it stays valid for as long as the tool does.
        using var schema = JsonDocument.Parse(inputSchema);
        InputSchema = schema.RootElement.Clone();
        Handler = handler;
    }

    /// <summary>Gets the name of the tool, such as <c>take_snapshot</c> or <c>click</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the category of the tool: <c>input</c>, <c>navigation</c>, <c>emulation</c>, <c>network</c>, or <c>debugging</c>.</summary>
    public string Category { get; }

    /// <summary>Gets what the tool does, for the caller that chooses among the tools.</summary>
    public string Description { get; }

    /// <summary>Gets whether the tool only reads from the page.</summary>
    public bool IsReadOnly { get; }

    /// <summary>Gets the JSON Schema of the arguments, which is an object schema.</summary>
    /// <remarks><see cref="JsonElement.GetRawText"/> gives the schema as JSON text.</remarks>
    public JsonElement InputSchema { get; }

    internal Func<NeoAutomationToolCall, Task> Handler { get; }
}

/// <summary>The result of a tool call: a text for the caller and the images that go with it.</summary>
public sealed class NeoAutomationToolResult
{
    internal NeoAutomationToolResult(string text, IReadOnlyList<NeoCapturedImage> images, bool isError)
    {
        Text = text;
        Images = images;
        IsError = isError;
    }

    /// <summary>Gets the text of the result, in the layout of Chrome DevTools MCP.</summary>
    public string Text { get; }

    /// <summary>Gets the images of the result, such as a screenshot.</summary>
    public IReadOnlyList<NeoCapturedImage> Images { get; }

    /// <summary>Gets whether the tool failed. <see cref="Text"/> then says why and, where known, what to do instead.</summary>
    public bool IsError { get; }
}

/// <summary>One call of a tool: its arguments and the response it builds.</summary>
internal sealed class NeoAutomationToolCall(NeoAutomation automation, JsonElement arguments, CancellationToken cancellationToken)
{
    internal NeoAutomation Automation { get; } = automation;

    internal CancellationToken CancellationToken { get; } = cancellationToken;

    internal List<string> Lines { get; } = [];

    internal List<NeoCapturedImage> Images { get; } = [];

    /// <summary>Gets or sets the page whose dialog, if any, is reported with the response.</summary>
    internal NeoAutomationPage? Page { get; set; }

    internal bool IncludePages { get; set; }

    internal NeoAutomationSnapshot? Snapshot { get; set; }

    /// <summary>Gets the sections that follow the snapshot, such as a list of requests.</summary>
    internal List<string> Sections { get; } = [];

    internal bool Has(string name)
        => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    internal string? OptionalString(string name)
    {
        if (!Has(name)) return null;
        var value = arguments.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String) throw Invalid(name, "a string");
        return value.GetString();
    }

    internal string RequiredString(string name) => OptionalString(name) ?? throw Missing(name);

    internal bool? OptionalBoolean(string name)
    {
        if (!Has(name)) return null;
        var value = arguments.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid(name, "a boolean"),
        };
    }

    internal double? OptionalNumber(string name)
    {
        if (!Has(name)) return null;
        var value = arguments.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !double.IsFinite(value.GetDouble())) throw Invalid(name, "a number");
        return value.GetDouble();
    }

    internal double RequiredNumber(string name) => OptionalNumber(name) ?? throw Missing(name);

    internal int? OptionalInteger(string name)
    {
        if (OptionalNumber(name) is not { } number) return null;
        if (number != Math.Floor(number) || number is < int.MinValue or > int.MaxValue) throw Invalid(name, "an integer");
        return (int)number;
    }

    internal IReadOnlyList<string>? OptionalStrings(string name)
    {
        if (!Has(name)) return null;
        var value = arguments.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array) throw Invalid(name, "an array of strings");
        var result = new List<string>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw Invalid(name, "an array of strings");
            result.Add(item.GetString()!);
        }

        return result;
    }

    internal JsonElement RequiredArray(string name)
    {
        if (!Has(name)) throw Missing(name);
        var value = arguments.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array) throw Invalid(name, "an array");
        return value;
    }

    /// <summary>Reads a timeout in milliseconds. Zero and negative values select the default.</summary>
    internal TimeSpan? OptionalTimeout(string name)
    {
        if (OptionalInteger(name) is not { } milliseconds || milliseconds <= 0) return null;
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, 600_000));
    }

    /// <summary>Resolves the page a tool acts on: the one named by <c>pageId</c>, or the selected one.</summary>
    internal NeoAutomationPage ResolvePage()
    {
        var page = OptionalInteger("pageId") is { } id
            ? Automation.GetPage(id)
            : Automation.SelectedPage ?? throw new NeoAutomationException("no-page", "No page is open. Call list_pages to see the open pages.");
        Page = page;
        return page;
    }

    internal static NeoAutomationException Missing(string name) => new("invalid-arguments", $"The argument \"{name}\" is required.");

    internal static NeoAutomationException Invalid(string name, string expected) => new("invalid-arguments", $"The argument \"{name}\" must be {expected}.");
}
