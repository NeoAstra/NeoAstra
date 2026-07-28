// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using NeoAstra.Rpc;

namespace NeoAstra.Tests;

[TestClass]
public sealed class NeoAppTests
{
    [TestMethod]
    public void CodeFirstCapabilitiesRemainDefaultDenyUntilExplicitlyGranted()
    {
        var builder = CreateBuilder(ApplicationPermission());
        StringAssert.Contains(Assert.Throws<InvalidOperationException>(builder.ValidateConfiguration).Message, "GrantMainView");

        builder.GrantMainView("greeting:read");
        builder.ValidateConfiguration();
        var granted = builder.CreateCapabilityManifest(NeoSecurityProfile.ProductionLocalApp, release: true);
        StringAssert.Contains(granted.Json, "greeting:read");
        CollectionAssert.Contains(granted.GrantSummaries.ToArray(), "main: views=1, permissions=1, wholeViewTrust=false, originAuthenticated=false");
    }

    [TestMethod]
    public void CodeFirstGrantRejectsUnknownAndScopedPermissions()
    {
        var unknown = CreateBuilder(ApplicationPermission());
        unknown.GrantMainView("unknown:permission");
        StringAssert.Contains(Assert.Throws<InvalidOperationException>(unknown.ValidateConfiguration).Message, "not declared");

        var scoped = CreateBuilder(new NeoPermissionDeclaration(
            "files:read",
            1,
            ["files.read"],
            NeoPermissionRisk.High,
            NeoScopeFamily.Filesystem)
        {
            ScopeRequired = true,
        });
        scoped.GrantMainView("files:read");
        StringAssert.Contains(Assert.Throws<InvalidOperationException>(scoped.ValidateConfiguration).Message, "scoped capability manifest");
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task StartupRemainsOnTheUiDispatcherAcrossAsynchronousInitialization()
    {
        if (!OperatingSystem.IsWindows()) return;

        var assetsDirectory = CreateAssetDirectory();
        var builder = CreateBuilder(ApplicationPermission());
        builder.AssetsDirectory = assetsDirectory;
        builder.GrantMainView("greeting:read");
        try
        {
            var exitCode = await RunStaAsync(() => NeoApplication.Run(
                new NeoApplicationOptions
                {
                    ApplicationName = "NeoAstra startup dispatcher test",
                    ShutdownMode = NeoApplicationShutdownMode.Explicit,
                },
                async application =>
                {
                    await builder.StartAsync(application, CancellationToken.None);
                    Assert.IsTrue(application.Dispatcher.CheckAccess());
                    application.Shutdown();
                }));

            Assert.AreEqual(0, exitCode);
        }
        catch (NeoAstraNativeLibraryException)
        {
            // Native assets are optional for the managed unit-test project.
        }
        finally
        {
            await builder.StopAsync();
            Directory.Delete(assetsDirectory, recursive: true);
        }
    }

    private static NeoAppBuilder CreateBuilder(NeoPermissionDeclaration declaration) =>
        new NeoAppBuilder().ConfigureGeneratedRpc("contract", [declaration], static _ => { });

    private static NeoPermissionDeclaration ApplicationPermission() =>
        new("greeting:read", 1, ["greeting.hello"], NeoPermissionRisk.Low, NeoScopeFamily.None);

    private static string CreateAssetDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "neoastra-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var content = Encoding.UTF8.GetBytes("<!doctype html><html><body>startup</body></html>");
        File.WriteAllBytes(Path.Combine(directory, "index.html"), content);
        var entry = new NeoAssetEntry(
            "index.html",
            content.Length,
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            "text/html; charset=utf-8",
            "no-cache");
        var manifest = new NeoAssetManifest(
            1,
            "index.html",
            "index.html",
            "app://neoastra",
            "default-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'",
            "no-referrer",
            [],
            ["/api", "/_neoastra"],
            [entry]);
        File.WriteAllText(Path.Combine(directory, "neoastra-assets.json"), manifest.ToJson(), new UTF8Encoding(false));
        return directory;
    }

    [SupportedOSPlatform("windows")]
    private static Task<T> RunStaAsync<T>(Func<T> callback)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(callback()); }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
