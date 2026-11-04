// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Net;

namespace NeoAstra.Mcp;

/// <summary>The relay that an application connects to, and the token of its development session.</summary>
internal sealed record NeoMcpEndpoint(Uri Url, string Token)
{
    internal const string UrlVariable = "NEOASTRA_MCP_URL";
    internal const string TokenVariable = "NEOASTRA_MCP_TOKEN";

    /// <summary>Finds the relay in the options, else in the environment that a development session gives the application.</summary>
    /// <returns>The relay, or <see langword="null"/> when the application has no complete and valid one.</returns>
    /// <remarks>
    /// An address or a token that the application gave itself is checked when the plugin is created; one from the environment that
    /// is not valid only keeps the application from connecting, since it is the session's and not the application's.
    /// </remarks>
    internal static NeoMcpEndpoint? Resolve(NeoMcpOptions options, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        var url = options.RelayUrl;
        if (url is null && Uri.TryCreate(environment(UrlVariable)?.Trim(), UriKind.Absolute, out var fromEnvironment)) url = fromEnvironment;
        var token = options.RelayToken ?? environment(TokenVariable)?.Trim();
        if (url is null || string.IsNullOrEmpty(token)) return null;
        return IsValidUrl(url) && IsValidToken(token) ? new(url, token) : null;
    }

    /// <summary>Checks the address and the token that an application gave itself.</summary>
    /// <exception cref="ArgumentException">The address is not a WebSocket address of the loopback interface, or the token is not valid.</exception>
    internal static void Validate(NeoMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.RelayUrl is { } url && !IsValidUrl(url))
            throw new ArgumentException("The relay address must be an absolute ws or wss address of the loopback interface, without user information.", nameof(options));
        if (options.RelayToken is { } token && !IsValidToken(token))
            throw new ArgumentException("The relay token has 16 to 256 ASCII letters, digits, hyphens, and underscores.", nameof(options));
    }

    internal static bool IsValidUrl(Uri url)
        => url.IsAbsoluteUri && (url.Scheme == "ws" || url.Scheme == "wss") && url.UserInfo.Length == 0 && IsLoopback(url);

    internal static bool IsValidToken(string token)
        => token.Length is >= 16 and <= 256 && token.All(static value => value is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_');

    private static bool IsLoopback(Uri url)
        => url.IsLoopback || (IPAddress.TryParse(url.IdnHost, out var address) && IPAddress.IsLoopback(address));
}
