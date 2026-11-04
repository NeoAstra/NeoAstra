// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace NeoAstra.Mcp;

/// <summary>
/// The MCP server that an application embeds: it serves the tools of its views to an agent over a transport of its own, such as
/// the standard input and output of a process that the agent started, without a development session or a relay.
/// </summary>
internal static class NeoMcpEmbeddedServer
{
    internal static McpServerOptions CreateOptions(INeoMcpToolHost host, string applicationName)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new()
        {
            ServerInfo = new Implementation { Name = applicationName, Version = typeof(NeoMcpEmbeddedServer).Assembly.GetName().Version?.ToString() ?? "0.0.0" },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            ServerInstructions = $"Drives the pages of the NeoAstra application {applicationName}. The tools are those of Chrome DevTools MCP.",
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult
                {
                    Tools = [.. host.Tools.Select(static tool => new Tool
                    {
                        Name = tool.Name,
                        Description = tool.Description,
                        InputSchema = tool.InputSchema,
                        Annotations = new ToolAnnotations { ReadOnlyHint = tool.IsReadOnly },
                    })],
                }),
                CallToolHandler = async (request, cancellationToken) =>
                {
                    var parameters = request.Params ?? throw new ArgumentException("A tool call names a tool.");
                    if (!host.Tools.Any(tool => tool.Name == parameters.Name))
                        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = $"The application has no tool '{parameters.Name}'." }] };
                    var outcome = await host.CallToolAsync(parameters.Name, Arguments(parameters.Arguments), cancellationToken).ConfigureAwait(false);
                    var result = new CallToolResult { IsError = outcome.IsError, Content = [new TextContentBlock { Text = outcome.Text }] };
                    foreach (var image in outcome.Images) result.Content.Add(ImageContentBlock.FromBytes(image.Data, image.ContentType));
                    return result;
                },
            },
        };
    }

    /// <summary>Serves an agent until it goes away or the token is canceled.</summary>
    internal static async Task RunAsync(INeoMcpToolHost host, string applicationName, ITransport transport, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        await using var server = McpServer.Create(transport, CreateOptions(host, applicationName));
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private static JsonElement? Arguments(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0) return null;
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in arguments)
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
