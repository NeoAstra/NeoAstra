using NeoAstra;
using NeoAstra.Desktop;
using NeoAstra.Rpc;

internal static class AdvancedCapabilities
{
    internal static NeoCapabilityManifest Load(bool development)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "capabilities",
            "main.json");
        return NeoCapabilityManifest.Resolve(
            File.ReadAllBytes(path),
            CreateCatalog(),
            new NeoCapabilityResolutionOptions
            {
                Platform = CurrentPlatform(),
                Release = !development,
                Profile = development
                    ? NeoSecurityProfile.DevelopmentLocalApp
                    : NeoSecurityProfile.ProductionLocalApp,
            });
    }

    internal static NeoPermissionCatalog CreateCatalog() =>
        new NeoPermissionCatalogBuilder()
            .Add(new NeoPermissionDeclaration(
                "tour:read",
                1,
                ["tour.hello"],
                NeoPermissionRisk.Low,
                NeoScopeFamily.None))
            .Add(new NeoPermissionDeclaration(
                "tour:control",
                1,
                ["tour.delay", "tour.stream", "tour.setDirty", "tour.showPreview"],
                NeoPermissionRisk.Low,
                NeoScopeFamily.None))
            .Add(new NeoPermissionDeclaration(
                "tour:events",
                1,
                ["tour.activity"],
                NeoPermissionRisk.Low,
                NeoScopeFamily.None))
            .AddNeoAstraDesktopPermissions()
            .Build();

    internal static NeoCapabilityPlatform CurrentPlatform() =>
        OperatingSystem.IsWindows()
            ? NeoCapabilityPlatform.Windows
            : OperatingSystem.IsMacOS()
                ? NeoCapabilityPlatform.MacOS
                : NeoCapabilityPlatform.Linux;
}
