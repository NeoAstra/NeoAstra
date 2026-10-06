// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NeoAstra;

/// <summary>A node of a page snapshot: an element or a text of the page with its accessibility role and name.</summary>
public sealed class NeoAutomationNode
{
    internal NeoAutomationNode(string uid, string role, string name, IReadOnlyDictionary<string, object> properties, IReadOnlyList<NeoAutomationNode> children)
    {
        Uid = uid;
        Role = role;
        Name = name;
        Properties = properties;
        Children = children;
    }

    /// <summary>Gets the identifier that input operations take. It stays with the element across snapshots of a document.</summary>
    public string Uid { get; }

    /// <summary>Gets the accessibility role, with the names Chrome DevTools uses, such as <c>button</c>, <c>link</c>, or <c>StaticText</c>.</summary>
    public string Role { get; }

    /// <summary>Gets the accessible name, or an empty string.</summary>
    public string Name { get; }

    /// <summary>
    /// Gets the other properties of the node, such as <c>value</c>, <c>checked</c>, <c>disabled</c>, <c>expanded</c>,
    /// <c>focused</c>, <c>level</c>, or <c>url</c>. A value is a <see cref="bool"/>, a <see cref="double"/>, or a <see cref="string"/>.
    /// </summary>
    public IReadOnlyDictionary<string, object> Properties { get; }

    /// <summary>Gets the child nodes.</summary>
    public IReadOnlyList<NeoAutomationNode> Children { get; }
}

/// <summary>A text snapshot of a page, based on its accessibility tree.</summary>
public sealed class NeoAutomationSnapshot
{
    private static readonly Dictionary<string, string> BooleanStateNames = new(StringComparer.Ordinal)
    {
        ["disabled"] = "disableable",
        ["expanded"] = "expandable",
        ["focused"] = "focusable",
        ["selected"] = "selectable",
    };

    private readonly Dictionary<string, NeoAutomationNode> _nodes = new(StringComparer.Ordinal);

    internal NeoAutomationSnapshot(string id, NeoAutomationNode root, bool isVerbose, bool isTruncated)
    {
        Id = id;
        Root = root;
        IsVerbose = isVerbose;
        IsTruncated = isTruncated;
        Index(root);
    }

    /// <summary>Gets the number of the snapshot, which starts the identifiers of the nodes first seen in it.</summary>
    public string Id { get; }

    /// <summary>Gets the root node, which stands for the document.</summary>
    public NeoAutomationNode Root { get; }

    /// <summary>Gets whether the snapshot lists every node, not only the ones that matter to a reader.</summary>
    public bool IsVerbose { get; }

    /// <summary>Gets whether the page had more nodes than a snapshot holds.</summary>
    public bool IsTruncated { get; }

    /// <summary>Finds a node by its identifier.</summary>
    /// <param name="uid">The identifier.</param>
    /// <returns>The node, or <see langword="null"/> when the snapshot has no such node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uid"/> is <see langword="null"/>.</exception>
    public NeoAutomationNode? Find(string uid)
    {
        ArgumentNullException.ThrowIfNull(uid);
        return _nodes.GetValueOrDefault(uid);
    }

    /// <summary>Formats the snapshot as indented text, one node per line, in the layout of Chrome DevTools MCP.</summary>
    /// <returns>The text, which ends with a line break.</returns>
    public override string ToString()
    {
        var builder = new StringBuilder();
        Append(builder, Root, 0);
        return builder.ToString();
    }

    internal static NeoAutomationSnapshot Parse(JsonElement value, bool verbose)
    {
        var id = value.GetProperty("snapshotId").GetString() ?? string.Empty;
        var truncated = value.TryGetProperty("truncated", out var flag) && flag.ValueKind == JsonValueKind.True;
        return new NeoAutomationSnapshot(id, ParseNode(value.GetProperty("root"), 0), verbose, truncated);
    }

    private static NeoAutomationNode ParseNode(JsonElement value, int depth)
    {
        // The agent bounds the number of nodes; this bounds the depth of a hostile or degenerate tree.
        if (depth > 512) throw new NeoAutomationException("snapshot-too-deep", "The page is nested too deeply to be captured in a snapshot.");
        var uid = value.GetProperty("i").GetString() ?? string.Empty;
        var role = value.TryGetProperty("r", out var roleValue) ? roleValue.GetString() ?? string.Empty : string.Empty;
        var name = value.TryGetProperty("n", out var nameValue) ? nameValue.GetString() ?? string.Empty : string.Empty;
        IReadOnlyDictionary<string, object> properties = EmptyProperties;
        if (value.TryGetProperty("p", out var propertyValues) && propertyValues.ValueKind == JsonValueKind.Object)
        {
            var parsed = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var property in propertyValues.EnumerateObject())
            {
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.True: parsed[property.Name] = true; break;
                    case JsonValueKind.False: parsed[property.Name] = false; break;
                    case JsonValueKind.Number: parsed[property.Name] = property.Value.GetDouble(); break;
                    case JsonValueKind.String: parsed[property.Name] = property.Value.GetString() ?? string.Empty; break;
                }
            }

            properties = parsed;
        }

        IReadOnlyList<NeoAutomationNode> children = [];
        if (value.TryGetProperty("c", out var childValues) && childValues.ValueKind == JsonValueKind.Array)
        {
            var parsed = new List<NeoAutomationNode>(childValues.GetArrayLength());
            foreach (var child in childValues.EnumerateArray()) parsed.Add(ParseNode(child, depth + 1));
            children = parsed;
        }

        return new NeoAutomationNode(uid, role, name, properties, children);
    }

    private static IReadOnlyDictionary<string, object> EmptyProperties { get; } = new Dictionary<string, object>();

    private void Index(NeoAutomationNode node)
    {
        _nodes[node.Uid] = node;
        foreach (var child in node.Children) Index(child);
    }

    private void Append(StringBuilder builder, NeoAutomationNode node, int depth)
    {
        builder.Append(' ', depth * 2).Append("uid=").Append(node.Uid);
        if (node.Role.Length != 0 && node.Role != "StaticText")
        {
            builder.Append(' ').Append(node.Role == "none" ? "ignored" : node.Role);
        }

        if (node.Name.Length != 0) builder.Append(" \"").Append(node.Name).Append('"');

        var isOption = node.Role == "option";
        foreach (var name in node.Properties.Keys.Order(StringComparer.Ordinal))
        {
            var value = node.Properties[name];
            // A state that can change is announced by its "-able" name, then by the state itself when it is set.
            if (value is bool && BooleanStateNames.TryGetValue(name, out var stateName) && !(isOption && name == "selected"))
            {
                builder.Append(' ').Append(stateName);
            }

            switch (value)
            {
                case true:
                    builder.Append(' ').Append(name);
                    break;
                case string text when !(isOption && name == "value" && text == node.Name):
                    builder.Append(' ').Append(name).Append("=\"").Append(text).Append('"');
                    break;
                case double number:
                    builder.Append(' ').Append(name).Append("=\"").Append(number.ToString(CultureInfo.InvariantCulture)).Append('"');
                    break;
            }
        }

        builder.Append('\n');
        if (HasRedundantTextChildren(node)) return;
        foreach (var child in node.Children) Append(builder, child, depth + 1);
    }

    // Text children that only repeat the name of their parent add nothing to a compact snapshot.
    private bool HasRedundantTextChildren(NeoAutomationNode node)
    {
        if (IsVerbose || node.Name.Length == 0 || node.Children.Count == 0) return false;
        var text = new StringBuilder();
        foreach (var child in node.Children)
        {
            if (child.Role != "StaticText" || child.Children.Count != 0) return false;
            text.Append(child.Name);
        }

        return StripWhitespace(text.ToString()) == StripWhitespace(node.Name);
    }

    private static string StripWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (!char.IsWhiteSpace(character)) builder.Append(character);
        }

        return builder.ToString();
    }
}
