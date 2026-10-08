// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NeoAstra.Tests;

[TestClass]
public sealed class NavigationRequestTests
{
    [TestMethod]
    public async Task ARefusedNavigationIsNotRequestedByTheView()
    {
        using var server = new LoopbackServer();
        await LiveBrowser.RunAsync(new Dictionary<string, string> { ["links.html"] = "<!doctype html><title>Links</title><p>Links</p>" }, async session =>
        {
            var view = session.View;
            var requests = new ConcurrentQueue<NeoNavigationRequest>();
            view.NavigationRequested = request =>
            {
                if (request.Uri.Scheme == "app") return ValueTask.FromResult(NeoNavigationDecision.Allow);
                requests.Enqueue(request);
                var allowed = request.Uri.AbsolutePath is "/allowed" or "/redirect";
                return ValueTask.FromResult(allowed ? NeoNavigationDecision.Allow : request.Uri.AbsolutePath == "/outside" ? NeoNavigationDecision.OpenExternal : NeoNavigationDecision.Cancel);
            };
            await session.NavigateAsync("links.html");

            // WebView2 starts the request of a navigation while it asks about it. A navigation that is allowed afterwards
            // reaches the server after the request of a refused one would have.
            session.Stage = "a navigation that a script starts";
            await session.RunAsync($"location.href = '{server.Origin}/refused?by=script#part'; true");
            Assert.AreEqual("/refused", (await WaitForRequestAsync(requests, session.CancellationToken)).Uri.AbsolutePath);

            session.Stage = "a navigation that the host starts";
            await view.NavigateAsync(new Uri($"{server.Origin}/refused?by=host"), session.CancellationToken);
            Assert.AreEqual("/refused", (await WaitForRequestAsync(requests, session.CancellationToken)).Uri.AbsolutePath);

            session.Stage = "a form that a script posts";
            await session.RunAsync($"(() => {{ const form = document.createElement('form'); form.method = 'post'; form.action = '{server.Origin}/refused?by=form'; document.body.appendChild(form); form.submit(); return true; }})()");
            Assert.AreEqual("/refused", (await WaitForRequestAsync(requests, session.CancellationToken)).Uri.AbsolutePath);

            // An address with credentials is refused by the native library, so nothing is opened outside the view either.
            // A library from before that check would open it.
            if (session.Environment.RuntimeInfo.ChecksExternalOpen)
            {
                session.Stage = "a navigation that the host sends outside the view";
                var outcome = new TaskCompletionSource<NeoExternalOpenCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                view.ExternalOpenCompleted += (_, result) => outcome.TrySetResult(result);
                await session.RunAsync($"location.href = '{server.Origin.Replace("http://", "http://user@", StringComparison.Ordinal)}/outside'; true");
                Assert.AreEqual("/outside", (await WaitForRequestAsync(requests, session.CancellationToken)).Uri.AbsolutePath);
                Assert.AreEqual(NeoExternalOpenStatus.Refused, (await outcome.Task.WaitAsync(session.CancellationToken)).Status);
            }

            session.Stage = "the redirect of an allowed navigation";
            await session.RunAsync($"location.href = '{server.Origin}/redirect'; true");
            Assert.AreEqual("/redirect", (await WaitForRequestAsync(requests, session.CancellationToken)).Uri.AbsolutePath);
            var redirect = await WaitForRequestAsync(requests, session.CancellationToken);
            Assert.AreEqual("/refused", redirect.Uri.AbsolutePath);
            Assert.IsFalse(redirect.IsUserInitiated);
            await session.WaitUntilAsync("location.href === 'app://neoastra/links.html'");

            session.Stage = "an allowed navigation";
            await session.RunAsync($"location.href = '{server.Origin}/allowed'; true");
            Assert.AreEqual("/allowed", (await WaitForRequestAsync(requests, session.CancellationToken)).Uri.AbsolutePath);
            await session.WaitUntilAsync($"location.href === '{server.Origin}/allowed' && document.readyState === 'complete'");
            CollectionAssert.AreEqual(new[] { "GET /redirect", "GET /allowed" }, server.Requests.ToArray());
            await session.NavigateAsync("links.html");

            // The server keeps the request of a navigation that it never answers. A script of the host does not wait for it.
            session.Stage = "a refused navigation to a server that does not answer";
            await session.RunAsync($"location.href = '{server.Origin}/silent'; true");
            Assert.AreEqual("/silent", (await WaitForRequestAsync(requests, session.CancellationToken)).Uri.AbsolutePath);
            Assert.AreEqual("2", await view.EvaluateScriptAsync("1 + 1", session.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), session.CancellationToken));
            Assert.AreEqual("\"app://neoastra/links.html\"", await view.EvaluateScriptAsync("location.href", session.CancellationToken));
            Assert.IsFalse(server.Requests.Contains("GET /silent"));
        });
    }

    private static async Task<NeoNavigationRequest> WaitForRequestAsync(ConcurrentQueue<NeoNavigationRequest> requests, CancellationToken cancellationToken)
    {
        NeoNavigationRequest? request;
        while (!requests.TryDequeue(out request)) await Task.Delay(20, cancellationToken);
        return request;
    }

    /// <summary>A web server on the loopback interface that records what it is asked for.</summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();

        internal LoopbackServer()
        {
            _listener.Start();
            Origin = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = AcceptAsync();
        }

        internal string Origin { get; }

        /// <summary>Gets the method and the path of each request, in the order of their arrival.</summary>
        internal ConcurrentQueue<string> Requests { get; } = new();

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

                // "GET /path?query HTTP/1.1"
                var line = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n")[0].Split(' ');
                if (line.Length < 2) return;
                var path = line[1].Split('?')[0];
                if (path == "/favicon.ico") return;
                Requests.Enqueue($"{line[0]} {path}");
                if (path == "/silent")
                {
                    await Task.Delay(Timeout.Infinite, _stopping.Token).ConfigureAwait(false);
                    return;
                }

                const string body = "<!doctype html><title>Served</title><p>Served</p>";
                var response = path == "/redirect"
                    ? "HTTP/1.1 302 Found\r\nLocation: /refused?by=redirect\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stopping.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        }
    }
}
