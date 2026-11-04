// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Mcp;

/// <summary>Configures the MCP plugin: what the tools may do, how an agent reaches them, and which relay the application connects to.</summary>
/// <remarks>
/// The application connects to a relay only when it has both the address of the relay and the token of its session. A
/// development session of the NeoAstra tool passes both in <c>NEOASTRA_MCP_URL</c> and <c>NEOASTRA_MCP_TOKEN</c>;
/// <see cref="RelayUrl"/> and <see cref="RelayToken"/> take their place. Without a relay and without
/// <see cref="ServeStandardIo"/>, the plugin creates no automation and opens no connection.
/// </remarks>
public sealed class NeoMcpOptions
{
    private TimeSpan _reconnectDelay = TimeSpan.FromSeconds(2);

    /// <summary>Gets the options of the browser automation that the tools run on, such as whether a script may run in a page.</summary>
    public NeoAutomationOptions Automation { get; } = new();

    /// <summary>Gets or sets the WebSocket address of the relay, or <see langword="null"/> for the one of <c>NEOASTRA_MCP_URL</c>.</summary>
    /// <remarks>The address is a <c>ws</c> or <c>wss</c> address of the loopback interface.</remarks>
    public Uri? RelayUrl { get; set; }

    /// <summary>Gets or sets the token of the development session, or <see langword="null"/> for the one of <c>NEOASTRA_MCP_TOKEN</c>.</summary>
    public string? RelayToken { get; set; }

    /// <summary>Gets or sets whether the application serves MCP itself over its standard input and output, for an agent that starts it.</summary>
    /// <remarks>
    /// A desktop application embeds the server this way without a development session: the agent starts the application as the
    /// command of an MCP server, and the application turns this on, for example when an argument such as <c>--mcp</c> is given.
    /// Standard output then carries MCP alone, so what the application writes to the console goes to standard error instead; the
    /// application quits when the agent closes its standard input. It works together with a relay.
    /// </remarks>
    public bool ServeStandardIo { get; set; }

    /// <summary>Gets or sets how long the application waits before it connects again after the relay went away; 2 seconds by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is shorter than 100 milliseconds or longer than 5 minutes.</exception>
    public TimeSpan ReconnectDelay
    {
        get => _reconnectDelay;
        set => _reconnectDelay = value >= TimeSpan.FromMilliseconds(100) && value <= TimeSpan.FromMinutes(5) ? value : throw new ArgumentOutOfRangeException(nameof(value), "The reconnect delay is between 100 milliseconds and 5 minutes.");
    }
}
