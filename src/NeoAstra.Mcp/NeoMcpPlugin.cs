// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Reflection;
using System.Text.Json;
using NeoAstra.Desktop;

namespace NeoAstra.Mcp;

/// <summary>
/// The MCP plugin of NeoAstra: in a development session of the NeoAstra tool, it connects the application to the relay of the
/// session, which serves the browser automation tools of its views to an agent over the Model Context Protocol.
/// </summary>
/// <remarks>
/// The plugin declares no renderer command and grants the page nothing. It creates the automation of the application and opens a
/// connection only when it has the address of a relay and the token of a session, from <see cref="NeoMcpOptions"/> or from the
/// environment that <c>dotnet neoastra dev</c> gives the application; otherwise it does nothing. An application registers it in
/// its development builds.
/// </remarks>
public sealed class NeoMcpPlugin : INeoAstraPlugin
{
    /// <summary>The stable plugin ID.</summary>
    public const string Id = "neoastra.mcp";

    private readonly NeoMcpOptions _options;

    /// <summary>Initializes the plugin.</summary>
    /// <param name="options">The options of the plugin.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The relay address of <paramref name="options"/> is not a WebSocket address of the loopback interface, or its token is not valid.</exception>
    public NeoMcpPlugin(NeoMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        NeoMcpEndpoint.Validate(options);
        _options = options;
    }

    /// <inheritdoc />
    public NeoPluginMetadata Metadata { get; } = new(Id, new Version(1, 0, 0), 1, new Version(0, 1, 0));

    /// <inheritdoc />
    public INeoPluginAdapter CreateAdapter() => new McpAdapter(_options);

    /// <inheritdoc />
    public ValueTask ConfigureAsync(NeoPluginContext context, CancellationToken cancellationToken) { ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }

    /// <inheritdoc />
    public ValueTask ReadyAsync(NeoPluginContext context, CancellationToken cancellationToken) { ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }

    /// <inheritdoc />
    public ValueTask StoppingAsync(NeoPluginContext context, CancellationToken cancellationToken) { ArgumentNullException.ThrowIfNull(context); return ValueTask.CompletedTask; }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal static NeoMcpApplicationInfo DescribeApplication() => new(
        Assembly.GetEntryAssembly()?.GetName().Name ?? AppDomain.CurrentDomain.FriendlyName,
        Platform(),
        Environment.MachineName,
        Environment.ProcessId);

    private static string Platform()
    {
        if (OperatingSystem.IsIOS()) return "ios";
        if (OperatingSystem.IsAndroid()) return "android";
        if (OperatingSystem.IsMacOS()) return "macos";
        if (OperatingSystem.IsWindows()) return "windows";
        return OperatingSystem.IsLinux() ? "linux" : "other";
    }

    // The automation exists only while the application is connected or tries to be: it injects its agent into every view.
    private sealed class McpAdapter(NeoMcpOptions options) : INeoPluginAdapter
    {
        private NeoAutomation? _automation;
        private NeoMcpConnector? _connector;

        public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Native, 1, 0,
            "Serves the browser automation tools of the views to the relay of a NeoAstra development session, which an agent reaches over MCP.");

        public ValueTask AttachAsync(NeoApplication application, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(application);
            cancellationToken.ThrowIfCancellationRequested();
            var endpoint = NeoMcpEndpoint.Resolve(options, Environment.GetEnvironmentVariable);
            if (endpoint is null) return ValueTask.CompletedTask;
            // Created before the first view navigates, so that every page has the agent of automation from its first document.
            _automation = new NeoAutomation(application, options.Automation);
            _connector = new NeoMcpConnector(new NeoAutomationToolHost(_automation), endpoint, DescribeApplication(), options.ReconnectDelay);
            _connector.Start();
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (_connector is { } connector) await connector.DisposeAsync().ConfigureAwait(false);
            if (_automation is { } automation) await automation.DisposeAsync().ConfigureAwait(false);
            _connector = null;
            _automation = null;
        }
    }
}

/// <summary>The tools of <see cref="NeoAutomation"/> as the connector offers them.</summary>
internal sealed class NeoAutomationToolHost(NeoAutomation automation) : INeoMcpToolHost
{
    public IReadOnlyList<NeoMcpToolInfo> Tools { get; } = automation.Tools.Select(static tool => new NeoMcpToolInfo(tool.Name, tool.Description, tool.InputSchema, tool.IsReadOnly)).ToArray();

    public async ValueTask<NeoMcpToolOutcome> CallToolAsync(string name, JsonElement? arguments, CancellationToken cancellationToken)
    {
        var result = arguments is { } value
            ? await automation.CallToolAsync(name, value, cancellationToken).ConfigureAwait(false)
            : await automation.CallToolAsync(name, cancellationToken).ConfigureAwait(false);
        return new(result.Text, result.Images.Select(static image => new NeoMcpImage(image.ContentType, image.Data)).ToArray(), result.IsError);
    }
}

/// <summary>Contains the static registration helpers of the MCP plugin.</summary>
public static class NeoMcpPluginExtensions
{
    /// <summary>Adds the MCP plugin with the default options: it connects only in a development session of the NeoAstra tool.</summary>
    /// <param name="builder">The plugin builder.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static NeoPluginBuilder AddNeoAstraMcp(this NeoPluginBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddNeoAstraMcp(new NeoMcpOptions());
    }

    /// <summary>Adds the MCP plugin with options.</summary>
    /// <param name="builder">The plugin builder.</param>
    /// <param name="options">The options of the plugin.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The relay address of <paramref name="options"/> is not a WebSocket address of the loopback interface, or its token is not valid.</exception>
    public static NeoPluginBuilder AddNeoAstraMcp(this NeoPluginBuilder builder, NeoMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        NeoMcpEndpoint.Validate(options);
        return builder.AddNeoAstraPlugin(() => new NeoMcpPlugin(options));
    }
}
