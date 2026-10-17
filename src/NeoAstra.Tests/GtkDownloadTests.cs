// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace NeoAstra.Tests;

[TestClass]
public sealed class GtkDownloadTests
{
    [TestMethod]
    [DataRow("accept")]
    [DataRow("policy-cancellation")]
    [DataRow("download-cancellation")]
    [DataRow("timeout")]
    public async Task ManagedDownloadPolicyCanOutliveTheNativeDestinationSignal(string outcome)
    {
        if (!OperatingSystem.IsLinux() || (Environment.GetEnvironmentVariable("DISPLAY") is null && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is null))
        {
            Assert.Inconclusive("This regression needs a staged Linux runtime and a GTK display (Xvfb is sufficient).");
            return;
        }

        var root = Directory.CreateTempSubdirectory("neoastra-gtk-download-");
        try
        {
            using var server = new DownloadServer();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var run = Task.Run(() => NeoApplication.Run(new NeoApplicationOptions { ShutdownMode = NeoApplicationShutdownMode.Explicit }, async application =>
            {
                try
                {
                    var window = application.CreateWindow(new NeoWindowOptions { Label = "download", Width = 320, Height = 240 });
                    await using var environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions { IsPrivate = true });
                    await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window),
                        new NeoAstraOptions { ViewLabel = "download", DecisionTimeout = TimeSpan.FromSeconds(2) });
                    var policy = new TaskCompletionSource<NeoDownloadDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var requested = new TaskCompletionSource<NeoDownloadRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var completed = new TaskCompletionSource<NeoDownload>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var startedCount = 0;
                    var completedCount = 0;
                    view.DownloadRequested = request =>
                    {
                        requested.TrySetResult(request);
                        return new ValueTask<NeoDownloadDecision>(policy.Task);
                    };
                    view.DownloadStarted += (_, _) => startedCount++;
                    view.DownloadCompleted += (_, args) => { completedCount++; completed.TrySetResult(args.Download); };
                    var native = webkit_web_view_download_uri(view.GetNativeHandle(NeoNativeHandleKind.WebKitGtkWebView).Value, server.Uri);
                    Assert.AreNotEqual((nint)0, native);
                    g_object_unref(native); // WebKit and the backend now own the running download.

                    var request = await requested.Task.WaitAsync(deadline.Token);
                    using var download = request.Download;
                    var destination = Path.Combine(root.FullName, "asynchronous \u00e9.bin");
                    // Ensure the managed ValueTask and its SafeDecisionHandle survive returning from the native signal.
                    await Task.Delay(30, deadline.Token);
                    download.Refresh();
                    Assert.AreEqual(NeoDownloadState.Requested, download.State);
                    Assert.IsFalse(completed.Task.IsCompleted);
                    Assert.AreEqual(0, startedCount);
                    Assert.IsFalse(File.Exists(destination));

                    if (outcome == "accept") policy.SetResult(NeoDownloadDecision.Accept(destination));
                    else if (outcome == "policy-cancellation") policy.SetCanceled();
                    else if (outcome == "download-cancellation") download.Cancel();
                    var terminal = await completed.Task.WaitAsync(deadline.Token);
                    Assert.AreSame(download, terminal);
                    Assert.AreEqual(outcome == "accept" ? NeoDownloadState.Completed : NeoDownloadState.Canceled, terminal.State);
                    Assert.AreEqual(outcome == "accept" ? 1 : 0, startedCount);
                    Assert.AreEqual(1, completedCount);
                    if (outcome == "accept")
                    {
                        Assert.AreEqual(destination, terminal.DestinationPath);
                        Assert.AreEqual(DownloadServer.Body, await File.ReadAllTextAsync(destination, deadline.Token));
                    }
                    else
                    {
                        policy.TrySetResult(NeoDownloadDecision.Accept(destination));
                        // RunDecisionAsync must safely complete/release the retained native handle after native cancellation/expiry.
                        await Task.Delay(100, deadline.Token);
                        download.Refresh();
                        Assert.AreEqual(NeoDownloadState.Canceled, download.State);
                        Assert.AreEqual(1, completedCount);
                        Assert.IsFalse(File.Exists(destination));
                    }
                }
                finally { application.ForceShutdown(); }
            }));
            Assert.AreEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(70)));
        }
        catch (NeoAstraNativeLibraryException) { Assert.Inconclusive("The Linux native runtime is not staged."); }
        finally { root.Delete(recursive: true); }
    }

    [DllImport("libwebkitgtk-6.0.so.4")]
    private static extern nint webkit_web_view_download_uri(nint view, [MarshalAs(UnmanagedType.LPUTF8Str)] string uri);

    [DllImport("libgobject-2.0.so.0")]
    private static extern void g_object_unref(nint value);

    private sealed class DownloadServer : IDisposable
    {
        internal const string Body = "NeoAstra managed deferred download";
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _serving;

        internal DownloadServer()
        {
            _listener.Start();
            Uri = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/download";
            _serving = ServeAsync(_stopping.Token);
        }

        internal string Uri { get; }

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Dispose();
            _serving.GetAwaiter().GetResult();
            _stopping.Dispose();
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                    var stream = client.GetStream();
                    var buffer = new byte[16 * 1024];
                    var length = 0;
                    while (length < buffer.Length && buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) < 0)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                        if (read == 0) break;
                        length += read;
                    }

                    var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=download.bin\r\nContent-Length: {Body.Length}\r\nConnection: close\r\n\r\n{Body}";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        }
    }
}
