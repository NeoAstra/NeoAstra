// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NeoAstra.Tests;

/// <summary>The page of a frame that tries to reach the bridge of the view that shows it, and the page that frames it.</summary>
/// <remarks>Shared by the live tests of WebView2 and by the browser conformance harness, which runs on every backend.</remarks>
internal static class FrameProbe
{
    /// <summary>The kind of the messages that a frame posts to the bridge, which the host must never receive.</summary>
    internal const string AttackKind = "frame-attack";

    /// <summary>
    /// The script of a frame. It reports to its parent, with <c>postMessage</c>, which bridge adapters it could see, and tries
    /// the bridge three ways: through the adapter of its own document, through the adapter of an empty frame that it creates,
    /// and by listening for what the host posts, which it forwards to its parent as a <c>frame-received</c> message.
    /// </summary>
    internal const string Script = """
        (() => {
          const name = new URLSearchParams(location.search).get("name") ?? location.origin;
          const report = { kind: "frame-report", name, origin: location.origin, webview: false, webkit: false, transport: false, errors: [] };
          const adapters = target => {
            const found = [];
            try { if (target.chrome?.webview) { report.webview = true; found.push(target.chrome.webview); } } catch (error) { report.errors.push("webview: " + error); }
            try { const handler = target.webkit?.messageHandlers?._neoastra_transport_v1; if (handler) { report.webkit = true; found.push(handler); } } catch (error) { report.errors.push("webkit: " + error); }
            return found;
          };
          report.transport = Object.prototype.hasOwnProperty.call(globalThis, Symbol.for("@neoastra/client/transport/v1"));
          const forward = data => parent.postMessage({ kind: "frame-received", name, data: String(data) }, "*");
          globalThis.addEventListener("neoastramessage", event => forward(JSON.stringify(event.detail)));
          for (const adapter of adapters(globalThis)) {
            try { adapter.addEventListener?.("message", event => forward(JSON.stringify(event.data))); } catch (error) { report.errors.push("listen: " + error); }
            try { adapter.postMessage({ kind: "frame-attack", name }); } catch (error) { report.errors.push("post: " + error); }
            try { adapter.postMessage(JSON.stringify({ kind: "frame-attack", name })); } catch (error) { report.errors.push("post text: " + error); }
          }
          const blank = document.createElement("iframe");
          document.documentElement.appendChild(blank);
          try {
            for (const adapter of adapters(blank.contentWindow)) adapter.postMessage({ kind: "frame-attack", name: name + "/blank" });
          } catch (error) { report.errors.push("blank: " + error); }
          parent.postMessage(report, "*");
        })();
        """;

    /// <summary>The document of a frame, which runs <see cref="Script"/>.</summary>
    internal const string Document = "<!doctype html><title>Frame</title><script>" + Script + "</script>";

    /// <summary>
    /// The script of the page that frames the probes: it collects their reports in <c>frameReports</c> and what they received
    /// in <c>frameReceived</c>, and frames <paramref name="frames"/>, each an address and its <c>sandbox</c> attribute, or
    /// <see langword="null"/> for a frame without one.
    /// </summary>
    internal static string HostScript(IEnumerable<(string Source, string? Sandbox)> frames)
    {
        var builder = new StringBuilder("""
            globalThis.frameReports = [];
            globalThis.frameReceived = [];
            addEventListener("message", event => {
              if (event.data?.kind === "frame-report") globalThis.frameReports.push(event.data);
              else if (event.data?.kind === "frame-received") globalThis.frameReceived.push(event.data);
            });
            const frame = (source, sandbox) => {
              const element = document.createElement("iframe");
              if (sandbox !== null) element.setAttribute("sandbox", sandbox);
              element.src = source;
              document.documentElement.appendChild(element);
            };

            """);
        foreach (var (source, sandbox) in frames)
        {
            builder.Append("frame(").Append(System.Text.Json.JsonSerializer.Serialize(source)).Append(", ")
                .Append(sandbox is null ? "null" : System.Text.Json.JsonSerializer.Serialize(sandbox)).Append(");\n");
        }
        return builder.ToString();
    }
}

/// <summary>Serves one HTML document to every request on a port of the loopback interface: a web origin for a frame.</summary>
internal sealed class LoopbackPageServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly byte[] _response;

    internal LoopbackPageServer(string html)
    {
        var body = Encoding.UTF8.GetBytes(html);
        _response = [.. Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), .. body];
        _listener.Start();
        Origin = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _ = AcceptAsync();
    }

    /// <summary>Gets the origin of the server, such as <c>http://127.0.0.1:50000</c>.</summary>
    internal string Origin { get; }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Dispose();
        _stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true) _ = ServeAsync(await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using var owned = client;
            var stream = client.GetStream();
            var buffer = new byte[16 * 1024];
            var length = 0;
            while (length < buffer.Length && buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) < 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), _stopping.Token).ConfigureAwait(false);
                if (read == 0) return;
                length += read;
            }
            await stream.WriteAsync(_response, _stopping.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
    }
}
