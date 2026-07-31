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
        var manifestPath = FindRepositoryFile("samples", "NeoAstra.Sample", "assets", "neoastra-assets.json");
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
    public void CodeFirstCapabilitiesRemainDefaultDenyUntilExplicitlyGranted()
    {
        var builder = CreateBuilder(ApplicationPermission());
        StringAssert.Contains(Assert.Throws<InvalidOperationException>(builder.ValidateConfiguration).Message, "GrantMainView");

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
