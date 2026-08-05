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
            .AddNeoAstraDesktopPermissions()
            .Build();

    internal static NeoCapabilityPlatform CurrentPlatform() =>
        OperatingSystem.IsWindows()
            ? NeoCapabilityPlatform.Windows
            : OperatingSystem.IsMacOS()
                ? NeoCapabilityPlatform.MacOS
                : NeoCapabilityPlatform.Linux;
}
