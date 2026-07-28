using System.Text.Json;
using NeoAstra;

internal static class AdvancedValidation
{
    internal static int Run()
    {
        var assetRoot = Path.Combine(AppContext.BaseDirectory, "assets");
        var manifest = NeoAssetManifest.Load(
            Path.Combine(assetRoot, "neoastra-assets.json"));
        var provider = new NeoManifestResourceProvider(assetRoot, manifest);

        if (!ValidateRoutes(provider) || !ValidateEveryAsset(provider, manifest))
        {
            return 2;
        }

        if (!TryFindRepresentativeAssets(
            manifest,
            out var mainScript,
            out var dynamicScript,
            out var workerScript,
            out var style,
            out var font,
            out var image))
        {
            return 3;
        }

        if (!ValidateModuleGraph(
            assetRoot,
            manifest,
            mainScript!,
            dynamicScript!,
            workerScript!,
            style!,
            font!,
            image!))
        {
            return 4;
        }

        if (!ValidateAccessibleTour(assetRoot, manifest, mainScript!))
        {
            return 5;
        }

        if (!ValidateCapabilities())
        {
            return 6;
        }

        Console.WriteLine(
            "NeoAstra React feature tour, restricted preview, desktop grants, " +
            $"and generated contract {NeoRpcGeneratedContract.Hash} validated.");
        return 0;
    }

    private static bool ValidateRoutes(NeoManifestResourceProvider provider)
    {
        var response = provider.GetResponse(new NeoResourceRequest(
            new Uri("app://neoastra/route"),
            "HEAD",
            new Dictionary<string, string>
            {
                ["Accept"] = "text/html",
            },
            null,
            NeoResourceKind.Document,
            true,
            default));
        return response?.StatusCode == 200 &&
               response.MimeType == "text/html; charset=utf-8";
    }

    private static bool ValidateEveryAsset(
        NeoManifestResourceProvider provider,
        NeoAssetManifest manifest)
    {
        foreach (var asset in manifest.Assets)
        {
            var response = provider.GetResponse(new NeoResourceRequest(
                new Uri("app://neoastra/" + asset.Path),
                "HEAD",
                new Dictionary<string, string>(),
                null,
                NeoResourceKind.Other,
                false,
                default));
            if (response?.StatusCode != 200)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryFindRepresentativeAssets(
        NeoAssetManifest manifest,
        out NeoAssetEntry? mainScript,
        out NeoAssetEntry? dynamicScript,
        out NeoAssetEntry? workerScript,
        out NeoAssetEntry? style,
        out NeoAssetEntry? font,
        out NeoAssetEntry? image)
    {
        var scripts = manifest.Assets
            .Where(static asset => asset.Path.EndsWith(".js", StringComparison.Ordinal))
            .ToArray();
        mainScript = scripts.SingleOrDefault(static asset =>
            Path.GetFileName(asset.Path).StartsWith("index-", StringComparison.Ordinal));
        dynamicScript = scripts.SingleOrDefault(static asset =>
            Path.GetFileName(asset.Path).StartsWith("details-", StringComparison.Ordinal));
        workerScript = scripts.SingleOrDefault(static asset =>
            Path.GetFileName(asset.Path).StartsWith("advanced.worker-", StringComparison.Ordinal));
        style = manifest.Assets.SingleOrDefault(static asset =>
            asset.Path.EndsWith(".css", StringComparison.Ordinal));
        font = manifest.Assets.SingleOrDefault(static asset =>
            asset.Path.EndsWith(".ttf", StringComparison.Ordinal));
        image = manifest.Assets.SingleOrDefault(static asset =>
            asset.Path.EndsWith(".svg", StringComparison.Ordinal));

        var allImmutable = manifest.Assets
            .Where(asset => asset.Path != manifest.EntryDocument)
            .All(static asset =>
                asset.CacheControl == "public,max-age=31536000,immutable");
        return mainScript is not null &&
               dynamicScript is not null &&
               workerScript is not null &&
               style is not null &&
               font is not null &&
               image is not null &&
               allImmutable;
    }

    private static bool ValidateModuleGraph(
        string assetRoot,
        NeoAssetManifest manifest,
        NeoAssetEntry mainScript,
        NeoAssetEntry dynamicScript,
        NeoAssetEntry workerScript,
        NeoAssetEntry style,
        NeoAssetEntry font,
        NeoAssetEntry image)
    {
        var html = File.ReadAllText(Path.Combine(assetRoot, manifest.EntryDocument));
        var main = File.ReadAllText(Path.Combine(assetRoot, mainScript.Path));
        var css = File.ReadAllText(Path.Combine(assetRoot, style.Path));

        return html.Contains(mainScript.Path, StringComparison.Ordinal) &&
               html.Contains(style.Path, StringComparison.Ordinal) &&
               main.Contains(Path.GetFileName(dynamicScript.Path), StringComparison.Ordinal) &&
               main.Contains(Path.GetFileName(workerScript.Path), StringComparison.Ordinal) &&
               main.Contains(Path.GetFileName(image.Path), StringComparison.Ordinal) &&
               css.Contains(Path.GetFileName(font.Path), StringComparison.Ordinal);
    }

    private static bool ValidateAccessibleTour(
        string assetRoot,
        NeoAssetManifest manifest,
        NeoAssetEntry mainScript)
    {
        var html = File.ReadAllText(Path.Combine(assetRoot, manifest.EntryDocument));
        var main = File.ReadAllText(Path.Combine(assetRoot, mainScript.Path));

        return html.Contains("<html lang=\"en\">", StringComparison.Ordinal) &&
               html.Contains("charset=\"UTF-8\"", StringComparison.Ordinal) &&
               html.Contains("tabindex=\"-1\"", StringComparison.Ordinal) &&
               main.Contains("aria-live", StringComparison.Ordinal) &&
               main.Contains("Feature tour", StringComparison.Ordinal) &&
               main.Contains("Typed RPC", StringComparison.Ordinal) &&
               main.Contains("Desktop essentials", StringComparison.Ordinal) &&
               main.Contains("Restricted preview", StringComparison.Ordinal);
    }

    private static bool ValidateCapabilities()
    {
        var capabilities = AdvancedCapabilities.Load(development: false);
        using var document = JsonDocument.Parse(capabilities.Json);
        var grants = document.RootElement.GetProperty("capabilities");
        if (grants.GetArrayLength() != 2)
        {
            return false;
        }

        var main = grants.EnumerateArray().Single(grant =>
            grant.GetProperty("id").GetString() == "main-feature-tour");
        var preview = grants.EnumerateArray().Single(grant =>
            grant.GetProperty("id").GetString() == "restricted-preview");
        var mainPermissions = main.GetProperty("permissions");
        var previewPermissions = preview.GetProperty("permissions");

        return main.GetProperty("views")[0].GetString() == "main" &&
               mainPermissions.GetArrayLength() >= 25 &&
               preview.GetProperty("views")[0].GetString() == "preview" &&
               previewPermissions.GetArrayLength() == 2;
    }
}
