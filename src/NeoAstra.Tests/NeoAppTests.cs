// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.RegularExpressions;
using NeoAstra.Rpc;

namespace NeoAstra.Tests;

[TestClass]
public sealed class NeoAppTests
{
    [TestMethod]
    public void SampleAssetsMatchTheirManifestAndContainTheImportedModuleGraph()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var manifestPath = FindRepositoryFile("samples", "NeoAstra.Sample", "bin", configuration, "net10.0", "assets", "neoastra-assets.json");
        var root = Path.GetDirectoryName(manifestPath)!;
        var manifest = NeoAssetManifest.Load(manifestPath);
        var provider = new NeoManifestResourceProvider(root, manifest);
        var assetPaths = manifest.Assets.Select(static asset => asset.Path).ToHashSet(StringComparer.Ordinal);

        foreach (var asset in manifest.Assets)
        {
            var uri = new Uri($"{manifest.Origin}/{asset.Path}");
            var response = provider.GetResponse(new NeoResourceRequest(uri, "GET", new Dictionary<string, string>(), null, NeoResourceKind.Other, false, default));
            Assert.IsNotNull(response, $"Manifest asset '{asset.Path}' was not served.");
            Assert.AreEqual(200, response.StatusCode, $"Manifest asset '{asset.Path}' failed its integrity check.");

            if (!asset.Path.EndsWith(".js", StringComparison.Ordinal)) continue;
            var source = File.ReadAllText(Path.Combine(root, asset.Path.Replace('/', Path.DirectorySeparatorChar)));
            foreach (Match match in Regex.Matches(source, "(?:\\bfrom\\s+|\\bimport\\s*(?:\\(\\s*)?)[\\\"'](?<specifier>\\.[^\\\"']+)[\\\"']", RegexOptions.CultureInvariant))
            {
                var dependency = Uri.UnescapeDataString(new Uri(uri, match.Groups["specifier"].Value).AbsolutePath.TrimStart('/'));
                Assert.IsTrue(assetPaths.Contains(dependency), $"Module '{asset.Path}' imports '{dependency}', which is absent from the asset manifest.");
            }
        }
    }

    [TestMethod]
    public void GrantMainViewIsOptionalAndCanEnableExplicitPermissions()
    {
        var builder = CreateBuilder(ApplicationPermission());
        builder.ValidateConfiguration();

        builder.GrantMainView("greeting:read");
        builder.ValidateConfiguration();
        var granted = builder.CreateCapabilityManifest(NeoSecurityProfile.ProductionLocalApp, release: true);
        StringAssert.Contains(granted.Json, "greeting:read");
        var wholeViewTrust = OperatingSystem.IsLinux() ? "required" : "false";
        CollectionAssert.Contains(granted.GrantSummaries.ToArray(), $"main: views=1, permissions=1, wholeViewTrust={wholeViewTrust}, originAuthenticated=false");
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
    public void ConventionalAppNavigationStaysOnItsTrustedOriginByDefault()
    {
        var builder = CreateBuilder(ApplicationPermission());
        var origin = new Uri("app://neoastra");

        Assert.AreEqual(NeoDecisionAction.Allow, builder.DecideNavigation(new Uri("app://neoastra/settings?tab=general#title"), true, true, origin));
        Assert.AreEqual(NeoDecisionAction.Cancel, builder.DecideNavigation(new Uri("app://attacker/index.html"), true, true, origin));
        Assert.AreEqual(NeoDecisionAction.Cancel, builder.DecideNavigation(new Uri("app://neoastra.evil.test/index.html"), true, true, origin));
        Assert.AreEqual(NeoDecisionAction.Cancel, builder.DecideNavigation(new Uri("https://example.com"), true, true, origin));
        Assert.AreEqual(NeoDecisionAction.Cancel, builder.DecideNavigation(new Uri("https://example.com"), true, false, origin));
        Assert.AreEqual(NeoDecisionAction.Cancel, builder.DecideNewWindow(new Uri("app://neoastra/other"), true));
        Assert.AreEqual(NeoDecisionAction.Cancel, builder.DecideNewWindow(null, true));
    }

    [TestMethod]
    public void ConventionalAppDevelopmentNavigationRequiresTheExactConfiguredOrigin()
    {
        var builder = CreateBuilder(ApplicationPermission());
        var origin = NeoAppBuilder.ValidateDevelopmentUrl("http://127.0.0.1:5173");

        Assert.AreEqual(NeoDecisionAction.Allow, builder.DecideNavigation(new Uri("http://127.0.0.1:5173/dashboard#status"), true, false, origin));
        Assert.AreEqual(NeoDecisionAction.Cancel, builder.DecideNavigation(new Uri("http://127.0.0.1:5174/dashboard"), true, true, origin));
        Assert.AreEqual(NeoDecisionAction.Cancel, builder.DecideNavigation(new Uri("http://localhost:5173/dashboard"), true, true, origin));
        Assert.Throws<InvalidOperationException>(() => NeoAppBuilder.ValidateDevelopmentUrl("http://localhost:5173"));
        Assert.Throws<InvalidOperationException>(() => NeoAppBuilder.ValidateDevelopmentUrl("http://127.0.0.1:5173/subpath"));
        Assert.Throws<InvalidOperationException>(() => NeoAppBuilder.ValidateDevelopmentUrl("https://user@127.0.0.1:5173"));
    }

    [TestMethod]
    public void ConventionalAppOpensOnlyConfiguredUserInitiatedExternalOrigins()
    {
        var builder = CreateBuilder(ApplicationPermission())
            .OpenExternalLinksInSystemBrowser("https://docs.neoastra.dev", "http://127.0.0.1:8080");
        var origin = new Uri("app://neoastra");

        Assert.AreEqual(NeoDecisionAction.OpenExternal, builder.DecideNavigation(new Uri("https://docs.neoastra.dev/guide?q=security"), true, true, origin));
        Assert.AreEqual(NeoDecisionAction.OpenExternal, builder.DecideNewWindow(new Uri("http://127.0.0.1:8080/help"), true));
        Assert.AreEqual(NeoDecisionAction.Cancel, builder.DecideNavigation(new Uri("https://docs.neoastra.dev/guide"), true, false, origin));
        Assert.AreEqual(NeoDecisionAction.Cancel, builder.DecideNavigation(new Uri("https://docs.neoastra.dev/guide"), false, true, origin));
        Assert.AreEqual(NeoDecisionAction.Cancel, builder.DecideNewWindow(new Uri("https://docs.neoastra.dev/guide"), false));
        Assert.AreEqual(NeoDecisionAction.Cancel, builder.DecideNavigation(new Uri("https://docs.neoastra.dev.evil.test"), true, true, origin));
        Assert.AreEqual(NeoDecisionAction.Cancel, builder.DecideNavigation(new Uri("https://user@docs.neoastra.dev/guide"), true, true, origin));
        Assert.AreEqual(NeoDecisionAction.Cancel, builder.DecideNewWindow(new Uri("file:///tmp/document"), true));
    }

    [TestMethod]
    public void ConventionalAppExternalLinkConfigurationIsExplicitAndBounded()
    {
        var builder = CreateBuilder(ApplicationPermission());
        Assert.Throws<ArgumentException>(() => builder.OpenExternalLinksInSystemBrowser());
        Assert.Throws<ArgumentException>(() => builder.OpenExternalLinksInSystemBrowser("mailto:"));
        Assert.Throws<ArgumentException>(() => builder.OpenExternalLinksInSystemBrowser("https://example.com/path"));

        builder.OpenExternalLinksInSystemBrowser("https://example.com");
        Assert.Throws<InvalidOperationException>(() => builder.OpenExternalLinksInSystemBrowser("https://other.example"));
    }

    [TestMethod]
    public void ConventionalAppUsesAuthenticatedBridgeOriginsWhereAvailable()
    {
        var options = NeoAppBuilder.CreateLocalViewOptions(new Uri("app://neoastra/index.html"));
        if (OperatingSystem.IsLinux())
        {
            Assert.AreEqual(NeoBridgePolicy.TrustEntireView, options.BridgePolicy);
            Assert.AreEqual(0, options.BridgeOrigins.Count);
        }
        else
        {
            Assert.AreEqual(NeoBridgePolicy.TrustedOrigins, options.BridgePolicy);
            CollectionAssert.AreEqual(new[] { "app://neoastra" }, options.BridgeOrigins.ToArray());
        }
    }

    [TestMethod]
    public void MainWindowConfigurationRejectsNullAndDuplicateRegistration()
    {
        var builder = CreateBuilder(ApplicationPermission());
        Assert.ThrowsExactly<ArgumentNullException>(() => builder.ConfigureMainWindow(null!));
        Assert.AreSame(builder, builder.ConfigureMainWindow(static (_, _) => { }));
        Assert.ThrowsExactly<InvalidOperationException>(() => builder.ConfigureMainWindow(static (_, _) => { }));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MainWindowConfigurationRunsBeforeShowingAndFailureCleansUp(bool failInWindowCallback)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Native main-window callback ordering is qualified by this test on Windows only.");
            return;
        }
        try
        {
            await RunStaAsync(() =>
            {
                var configured = 0;
                var rpcConfigured = false;
                NeoWindow? captured = null;
                var expected = new InvalidOperationException("Deliberate startup stop before browser creation.");
                Exception? failure = null;
                try
                {
                    NeoApp.Run([], builder =>
                    {
                        builder.ConfigureMainWindow((application, window) =>
                        {
                            configured++;
                            captured = window;
                            Assert.AreSame(window, application.MainWindow);
                            Assert.IsTrue(application.Dispatcher.CheckAccess());
                            Assert.IsFalse(window.IsVisible);
                            Assert.IsFalse(rpcConfigured);
                            window.Title = "Configured before showing";
                            if (failInWindowCallback) throw expected;
                        });
                        builder.ConfigureGeneratedRpc("contract", [], _ =>
                        {
                            rpcConfigured = true;
                            Assert.AreEqual(1, configured);
                            Assert.AreEqual("Configured before showing", captured!.Title);
                            // Prove ordering without needing a browser runtime or an asset directory.
                            throw expected;
                        });
                    });
                }
                catch (NeoAstraNativeLibraryException) { throw; }
                catch (Exception exception) { failure = exception; }
                Assert.AreSame(expected, failure);
                Assert.AreEqual(1, configured);
                Assert.AreEqual(!failInWindowCallback, rpcConfigured);
                Assert.IsNotNull(captured);
                Assert.IsTrue(captured.IsClosed, "Application teardown must close the window after startup failure.");
            });
        }
        catch (NeoAstraNativeLibraryException)
        {
            Assert.Inconclusive("The native runtime is not staged for main-window callback tests.");
        }
    }

    private static Task RunStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static NeoAppBuilder CreateBuilder(NeoPermissionDeclaration declaration) =>
        new NeoAppBuilder().ConfigureGeneratedRpc("contract", [declaration], static _ => { });

    private static NeoPermissionDeclaration ApplicationPermission() =>
        new("greeting:read", 1, ["greeting.hello"], NeoPermissionRisk.Low, NeoScopeFamily.None);

    private static string FindRepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"Could not locate repository file '{Path.Combine(segments)}'.");
    }
}
