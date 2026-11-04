// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
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
    /// <exception cref="ArgumentException">The relay address of <paramref name="options"/> is not a WebSocket address of the loopback interface or of a private IPv4 address, or its token is not valid.</exception>
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
        Device(),
        Environment.ProcessId);

    // An iPhone or iPad names itself localhost; its model, such as iPhone12,8, tells it apart. A simulator has the name of the Mac.
    private static string Device()
    {
        var name = Environment.MachineName;
        if (!OperatingSystem.IsIOS() || !string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase)) return name;
        nuint size = 0;
        if (NativeMethods.sysctlbyname("hw.machine", null, ref size, IntPtr.Zero, 0) != 0 || size is 0 or > 256) return name;
        var buffer = new byte[(int)size];
        if (NativeMethods.sysctlbyname("hw.machine", buffer, ref size, IntPtr.Zero, 0) != 0) return name;
        var model = System.Text.Encoding.ASCII.GetString(buffer).TrimEnd('\0');
        return model.Length == 0 ? name : model;
    }

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
        private readonly CancellationTokenSource _stop = new();
        private NeoAutomation? _automation;
        private NeoMcpConnector? _connector;
        private Task? _standardIo;
        private int _disposed;

        public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Native, 1, 0,
            "Serves the browser automation tools of the views to the relay of a NeoAstra development session, which an agent reaches over MCP.");

        public ValueTask AttachAsync(NeoApplication application, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(application);
            cancellationToken.ThrowIfCancellationRequested();
            var android = OperatingSystem.IsAndroid() ? AndroidDevelopment() : null;
            var environment = android?.Environment ?? Environment.GetEnvironmentVariable;
            var endpoint = NeoMcpEndpoint.Resolve(options, environment);
            if (endpoint is null && !options.ServeStandardIo) return ValueTask.CompletedTask;
            // An Android application has no entry assembly and no host name of its own: the package and the model name it instead.
            var description = DescribeApplication();
            if (android is not null) description = description with { Application = android.Application ?? description.Application, Device = android.Device ?? description.Device };
            // Created before the first view navigates, so that every page has the agent of automation from its first document.
            _automation = new NeoAutomation(application, options.Automation);
            var host = new NeoAutomationToolHost(_automation);
            if (endpoint is not null)
            {
                _connector = new NeoMcpConnector(host, endpoint, description, options.ReconnectDelay, StartTunnel(NeoMcpEndpoint.TunnelPort(environment)));
                _connector.Start();
            }
            if (options.ServeStandardIo) _standardIo = ServeStandardIoAsync(application, host, description.Application, _stop.Token);
            return ValueTask.CompletedTask;
        }

        // On a phone that does not reach the loopback interface of the machine, the session tunnels its development server: the
        // tunnel listens before the view navigates to it. A port that is taken on the phone leaves the view without the server.
        private static NeoMcpTunnel? StartTunnel(int? port)
        {
            if (port is not { } value) return null;
            var tunnel = new NeoMcpTunnel(value);
            try
            {
                tunnel.Start();
                return tunnel;
            }
            catch (System.Net.Sockets.SocketException)
            {
                tunnel.DisposeAsync().AsTask().GetAwaiter().GetResult();
                return null;
            }
        }

        // Standard output carries MCP alone while the application runs; the agent that started it ends it by closing its input.
        private static async Task ServeStandardIoAsync(NeoApplication application, INeoMcpToolHost host, string name, CancellationToken cancellationToken)
        {
            var input = Console.OpenStandardInput();
            var output = Console.OpenStandardOutput();
            Console.SetOut(Console.Error);
            await Task.Yield();
            try
            {
                await NeoMcpEmbeddedServer.RunAsync(host, name, new StreamServerTransport(input, output, name), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // The agent went away without closing the stream.
            }
            if (cancellationToken.IsCancellationRequested) return;
            try { await application.RequestQuitAsync(NeoQuitReason.Programmatic).ConfigureAwait(false); }
            catch (ObjectDisposedException) { /* the application ends already */ }
        }

        // An Android application has no environment of the session: adb wrote the relay of the session in a file of the private files
        // of the debug build, which the development service of NeoAstra reads.
        private static AndroidSession AndroidDevelopment()
        {
            string? url = null, token = null, name = null, device = null;
            try
            {
                using var response = JsonDocument.Parse(NeoPlatformServices.Call("development", "{}"));
                var root = response.RootElement;
                url = Text(root, "mcpUrl");
                token = Text(root, "mcpToken");
                name = Text(root, "mcpApplication");
                device = Text(root, "mcpDevice");
            }
            catch (Exception exception) when (exception is NeoAstraException or NotSupportedException or JsonException)
            {
                // No development service: the application connects only with options of its own.
            }
            return new(variable => Environment.GetEnvironmentVariable(variable) ?? variable switch
            {
                NeoMcpEndpoint.UrlVariable => url,
                NeoMcpEndpoint.TokenVariable => token,
                _ => null,
            }, name, device);

            static string? Text(JsonElement root, string property)
                => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        private sealed record AndroidSession(Func<string, string?> Environment, string? Application, string? Device);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await _stop.CancelAsync().ConfigureAwait(false);
            if (_standardIo is { } standardIo)
            {
                try { await standardIo.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (TimeoutException) { /* the server waits on standard input, which the process closes when it exits */ }
            }
            if (_connector is { } connector) await connector.DisposeAsync().ConfigureAwait(false);
            if (_automation is { } automation) await automation.DisposeAsync().ConfigureAwait(false);
            _connector = null;
            _automation = null;
            _stop.Dispose();
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
    /// <exception cref="ArgumentException">The relay address of <paramref name="options"/> is not a WebSocket address of the loopback interface or of a private IPv4 address, or its token is not valid.</exception>
    public static NeoPluginBuilder AddNeoAstraMcp(this NeoPluginBuilder builder, NeoMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        NeoMcpEndpoint.Validate(options);
        return builder.AddNeoAstraPlugin(() => new NeoMcpPlugin(options));
    }
}

internal static partial class NativeMethods
{
    [System.Runtime.InteropServices.LibraryImport("libc", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    internal static partial int sysctlbyname(string name, byte[]? value, ref nuint size, IntPtr newValue, nuint newSize);
}
