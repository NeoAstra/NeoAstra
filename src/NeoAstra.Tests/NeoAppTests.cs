// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

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

    private static NeoAppBuilder CreateBuilder(NeoPermissionDeclaration declaration) =>
        new NeoAppBuilder().ConfigureGeneratedRpc("contract", [declaration], static _ => { });

    private static NeoPermissionDeclaration ApplicationPermission() =>
        new("greeting:read", 1, ["greeting.hello"], NeoPermissionRisk.Low, NeoScopeFamily.None);
}
