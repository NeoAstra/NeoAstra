// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text;

namespace NeoAstra.Tests;

/// <summary>A page that tells which peer connection (WebRTC) interfaces each of its documents and workers has.</summary>
/// <remarks>Shared by the live tests of WebView2 and by the browser conformance harness, which runs on every backend.</remarks>
internal static class PeerConnectionProbe
{
    // A function that gives the names of the interfaces that a global object has, separated by commas. It has no double
    // quote, so that it fits in a string of a script.
    private const string Found =
        "(target => ['RTCPeerConnection', 'webkitRTCPeerConnection', 'RTCDataChannel', 'RTCIceCandidate', 'RTCSessionDescription', " +
        "'RTCPeerConnectionIceEvent', 'RTCDataChannelEvent', 'RTCTrackEvent', 'RTCRtpSender', 'RTCRtpReceiver', 'RTCRtpTransceiver', " +
        "'RTCCertificate', 'RTCDtlsTransport', 'RTCIceTransport', 'RTCSctpTransport', 'RTCStatsReport']" +
        ".filter(name => typeof target[name] !== 'undefined').join(','))";

    /// <summary>The contexts that <see cref="Script"/> always reports: the page, the frames that it makes without loading anything, and a worker.</summary>
    internal static readonly string[] OwnContexts = ["page", "blank", "nested", "written", "worker"];

    /// <summary>The document of the page that runs the probe: it has no script and no policy of its own.</summary>
    internal const string Document = "<!doctype html><title>Peer connections</title><body></body>";

    /// <summary>The document of a frame, which reports what it has to its parent under the <c>key</c> of its address.</summary>
    internal const string FrameDocument =
        "<!doctype html><title>Peer connection frame</title><script>parent.postMessage({ kind: 'peer-report', " +
        "key: new URLSearchParams(location.search).get('key'), value: " + Found + "(globalThis) }, '*');</script>";

    /// <summary>
    /// The script that a host evaluates in <see cref="Document"/>. It collects in <c>peerReport</c> what each context has, by
    /// its key: the page, an empty frame that it adds (the initial <c>about:blank</c> document, which loads nothing), an
    /// empty frame inside that one, a document that it writes, a worker, and <paramref name="frames"/>, each a key, the
    /// address of a <see cref="FrameDocument"/> with that key, and its <c>sandbox</c> attribute or <see langword="null"/>.
    /// <c>peerReport.count</c> is the number of contexts that have reported. A context without any interface reports an
    /// empty text, and a worker that did not run reports <c>error</c>.
    /// </summary>
    internal static string Script(IEnumerable<(string Key, string Address, string? Sandbox)> frames)
    {
        var builder = new StringBuilder("(() => {\n");
        builder.Append("  const found = ").Append(Found).Append(";\n");
        builder.Append("""
              const report = globalThis.peerReport = { count: 0 };
              const set = (key, value) => { if (!(key in report)) report.count++; report[key] = value; };
              set("page", found(globalThis));
              const blank = document.createElement("iframe");
              document.body.appendChild(blank);
              set("blank", found(blank.contentWindow));
              const nested = blank.contentDocument.createElement("iframe");
              blank.contentDocument.body.appendChild(nested);
              set("nested", found(nested.contentWindow));
              const written = document.createElement("iframe");
              document.body.appendChild(written);
              written.contentDocument.open();
              written.contentDocument.write("<!doctype html>written");
              written.contentDocument.close();
              set("written", found(written.contentWindow));
              addEventListener("message", event => { if (event.data?.kind === "peer-report") set(event.data.key, event.data.value); });
              const frame = (address, sandbox) => {
                const element = document.createElement("iframe");
                if (sandbox !== null) element.setAttribute("sandbox", sandbox);
                element.src = address;
                document.body.appendChild(element);
              };
              try {
                const worker = new Worker(URL.createObjectURL(new Blob(["postMessage(
            """.TrimEnd()).Append(Found).Append("""
            (self))"], { type: "text/javascript" })));
                worker.onmessage = event => set("worker", event.data);
                worker.onerror = () => set("worker", "error");
              } catch (error) { set("worker", "error"); }

            """);
        foreach (var (key, address, sandbox) in frames)
        {
            var separator = address.Contains('?') ? '&' : '?';
            builder.Append("  frame(\"").Append(address).Append(separator).Append("key=").Append(key).Append("\", ")
                .Append(sandbox is null ? "null" : $"\"{sandbox}\"").Append(");\n");
        }

        return builder.Append("  return true;\n})()").ToString();
    }

    /// <summary>Whether the names that a context reported include <paramref name="name"/>.</summary>
    internal static bool Has(string names, string name) => names.Split(',').Contains(name, StringComparer.Ordinal);
}
