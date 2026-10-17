// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Tests;

[TestClass]
public sealed class DownloadTests
{
    [TestMethod]
    public void GtkDownloadDestinationHonorsDeferredReferenceCountedDecisions()
    {
        // This guard runs on managed-only hosts too. The Linux native test exercises the actual signal, including final release.
        var path = FindRepositoryFile("native", "src", "linux", "gtk_backend.cpp");
        var source = File.ReadAllText(path);
        var start = source.IndexOf("gboolean download_decide_destination(", StringComparison.Ordinal);
        var end = source.IndexOf("void download_received(", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var handler = source[start..end];
        Assert.IsFalse(handler.Contains("make_unique<neoastra_decision>", StringComparison.Ordinal), "An event may retain the decision after this signal returns.");
        Assert.IsFalse(handler.Contains("finish_synchronous_decision(", StringComparison.Ordinal), "WebKitGTK 6.0 can wait for a download destination.");
        StringAssert.Contains(handler, "neo_finish_decision_event(");
    }

    [TestMethod]
    public async Task DownloadPolicyWaitsForTheAsynchronousDestination()
    {
        var pending = new TaskCompletionSource<NeoDownloadDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var destination = Path.GetFullPath("async-download.bin");
        var resolution = NeoAstra.ResolveDecisionAsync(() => new ValueTask<NeoDownloadDecision>(pending.Task),
            static value => value, NeoDownloadDecision.Cancel, TimeSpan.FromSeconds(5));

        Assert.IsFalse(resolution.IsCompleted);
        pending.SetResult(NeoDownloadDecision.Accept(destination));

        var decision = await resolution;
        Assert.AreEqual(NeoDecisionAction.Download, decision.Action);
        Assert.AreEqual(destination, decision.DestinationPath);
    }

    [TestMethod]
    public async Task DownloadPolicyTimeoutIgnoresALateDestination()
    {
        var pending = new TaskCompletionSource<NeoDownloadDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var conversions = 0;
        var decision = await NeoAstra.ResolveDecisionAsync(() => new ValueTask<NeoDownloadDecision>(pending.Task),
            value => { conversions++; return value; }, NeoDownloadDecision.Cancel, TimeSpan.FromMilliseconds(20));

        Assert.AreEqual(NeoDownloadDecision.Cancel, decision);
        pending.SetResult(NeoDownloadDecision.Accept(Path.GetFullPath("too-late.bin")));
        Assert.AreEqual(0, conversions, "A destination that arrives after the deadline must not be applied.");
    }

    [TestMethod]
    public async Task CanceledAndFailedDownloadPoliciesUseTheSafeCancelDefault()
    {
        var pending = new TaskCompletionSource<NeoDownloadDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolution = NeoAstra.ResolveDecisionAsync(() => new ValueTask<NeoDownloadDecision>(pending.Task),
            static value => value, NeoDownloadDecision.Cancel, TimeSpan.FromSeconds(5));
        pending.SetCanceled();
        Assert.AreEqual(NeoDownloadDecision.Cancel, await resolution);
        Assert.AreEqual(NeoDownloadDecision.Cancel, await NeoAstra.ResolveDecisionAsync<NeoDownloadDecision, NeoDownloadDecision>(
            static () => throw new InvalidOperationException("download policy failed"), static value => value,
            NeoDownloadDecision.Cancel, TimeSpan.FromSeconds(5)));
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(path)) return path;
        }

        throw new FileNotFoundException($"Could not locate repository file '{Path.Combine(parts)}'.");
    }
}
