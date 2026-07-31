// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.Json;
using NeoAstra.Rpc;

namespace NeoAstra.Tool;

internal static class CapabilityCommand
{
    internal static int Resolve(string capabilitiesPath, string catalogPath, string platformName, string configuration, string outputPath)
    {
        try
        {
            var release = configuration switch
            {
                "Release" => true,
                "Debug" => false,
                _ => throw new ArgumentException("The build configuration must be Release or Debug."),
            };
            var catalog = LoadCatalog(catalogPath);
            var platform = platformName switch
            {
                "windows" => NeoCapabilityPlatform.Windows,
                "macos" => NeoCapabilityPlatform.MacOS,
                "linux" => NeoCapabilityPlatform.Linux,
                _ => throw new ArgumentException("Unknown target platform.")
            };
            var profile = release ? NeoSecurityProfile.ProductionLocalApp : NeoSecurityProfile.DevelopmentLocalApp;
            var manifest = NeoCapabilityManifest.Resolve(ReadBoundedFile(capabilitiesPath, "capability document"), catalog, new() { Platform = platform, Release = release, Profile = profile, AllowReviewedViewPatterns = !release });
            var output = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, manifest.Json + "\n", new System.Text.UTF8Encoding(false));
            Console.WriteLine($"Resolved {platform} capability manifest {manifest.Hash}.");
            return 0;
        }
        catch (NeoCapabilityValidationException exception)
        {
            Console.Error.WriteLine($"{exception.Code}: {exception.Message}");
            return 1;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("configuration_error: The permission catalog or capability configuration is invalid.");
            return 1;
        }
    }

    static NeoPermissionCatalog LoadCatalog(string path)
    {
        using var document = JsonDocument.Parse(ReadBoundedFile(path, "permission catalog"), new JsonDocumentOptions { MaxDepth = 32 });
        ValidateJsonComplexity(document.RootElement);
        var root = document.RootElement;
        ExactFields(root, "version", "permissions", "plugins");
        if (!root.TryGetProperty("version", out var version) || version.GetInt32() != 1 || !root.TryGetProperty("permissions", out var permissions) || permissions.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("The permission catalog must use version 1.");
        if (permissions.GetArrayLength() > 4096)
            throw new ArgumentException("The permission catalog contains too many application permissions.");
        var builder = new NeoPermissionCatalogBuilder();
        foreach (var item in permissions.EnumerateArray())
            builder.Add(ReadDeclaration(item));
        if (root.TryGetProperty("plugins", out var plugins))
        {
            if (plugins.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Catalog plugins must be an array.");
            if (plugins.GetArrayLength() > 256)
                throw new ArgumentException("The permission catalog contains too many plugins.");
            foreach (var plugin in plugins.EnumerateArray())
            {
                ExactFields(plugin, "id", "version", "minimumNeoAstraVersion", "permissions", "permissionSets");
                var pluginPermissions = plugin.GetProperty("permissions");
                if (pluginPermissions.ValueKind != JsonValueKind.Array || pluginPermissions.GetArrayLength() is < 1 or > 512)
                    throw new ArgumentException("A plugin permission list is outside its bound.");
                var declarations = pluginPermissions.EnumerateArray().Select(ReadDeclaration).ToArray();
                var sets = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                if (plugin.TryGetProperty("permissionSets", out var permissionSets))
                {
                    if (permissionSets.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("Plugin permissionSets must be an object.");
                    var setProperties = permissionSets.EnumerateObject().ToArray();
                    if (setProperties.Length > 128)
                        throw new ArgumentException("A plugin contains too many permission sets.");
                    var setEntryCount = 0;
                    foreach (var set in setProperties)
                    {
                        if (set.Value.ValueKind != JsonValueKind.Array || set.Value.GetArrayLength() is < 1 or > 256 || (setEntryCount += set.Value.GetArrayLength()) > 4096)
                            throw new ArgumentException("A plugin permission set is outside its bound.");
                        sets.Add(set.Name, set.Value.EnumerateArray().Select(static value => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ArgumentException("A permission set entry must be a string.")).ToArray());
                    }
                }

                builder.AddPlugin(new NeoPluginPermissionCatalog(plugin.GetProperty("id").GetString()!, plugin.GetProperty("version").GetString()!, plugin.GetProperty("minimumNeoAstraVersion").GetString()!, declarations, sets));
            }
        }

        return builder.Build();
    }

    static NeoPermissionDeclaration ReadDeclaration(JsonElement item)
    {
        ExactFields(item, "id", "version", "commands", "risk", "scopeFamily", "scopeRequired", "unionSafe", "platforms", "timeoutMilliseconds", "maximumConcurrency", "redaction", "documentation");
        var id = item.GetProperty("id").GetString()!;
        var commands = item.GetProperty("commands").EnumerateArray().Select(static value => value.GetString()!).ToArray();
        var risk = ParseRisk(item.GetProperty("risk").GetString());
        var family = ParseScopeFamily(item.GetProperty("scopeFamily").GetString());
        var platforms = item.TryGetProperty("platforms", out var platformItems) ? platformItems.EnumerateArray().Select(static value => ParsePlatform(value.GetString())).ToArray() : Array.Empty<NeoCapabilityPlatform>();
        return new NeoPermissionDeclaration(id, item.GetProperty("version").GetInt32(), commands, risk, family)
        {
            ScopeRequired = item.TryGetProperty("scopeRequired", out var required) && required.GetBoolean(),
            UnionSafe = item.TryGetProperty("unionSafe", out var union) && union.GetBoolean(),
            Platforms = platforms,
            DefaultTimeout = TimeSpan.FromMilliseconds(item.TryGetProperty("timeoutMilliseconds", out var timeout) ? timeout.GetInt32() : 30_000),
            MaximumConcurrency = item.TryGetProperty("maximumConcurrency", out var concurrency) ? concurrency.GetInt32() : 8,
            Redaction = item.TryGetProperty("redaction", out var redaction) ? ParseRedaction(redaction.GetString()) : NeoAuditRedaction.Full,
            Documentation = item.TryGetProperty("documentation", out var docs) ? docs.GetString()! : string.Empty,
        };
    }

    static NeoPermissionRisk ParseRisk(string? value) => value switch
    {
        "low" => NeoPermissionRisk.Low,
        "sensitive" => NeoPermissionRisk.Sensitive,
        "high" => NeoPermissionRisk.High,
        _ => throw new ArgumentException("Unknown permission risk.")
    };
    static NeoScopeFamily ParseScopeFamily(string? value) => value switch
    {
        "none" => NeoScopeFamily.None,
        "filesystem" => NeoScopeFamily.Filesystem,
        "url" => NeoScopeFamily.Url,
        "process" => NeoScopeFamily.Process,
        "clipboard" => NeoScopeFamily.Clipboard,
        "notifications" => NeoScopeFamily.Notifications,
        "dialogs" => NeoScopeFamily.Dialogs,
        "network" => NeoScopeFamily.Network,
        "persistence" => NeoScopeFamily.Persistence,
        _ => throw new ArgumentException("Unknown scope family.")
    };
    static NeoCapabilityPlatform ParsePlatform(string? value) => value switch
    {
        "windows" => NeoCapabilityPlatform.Windows,
        "macos" => NeoCapabilityPlatform.MacOS,
        "linux" => NeoCapabilityPlatform.Linux,
        _ => throw new ArgumentException("Unknown permission platform.")
    };
    static NeoAuditRedaction ParseRedaction(string? value) => value switch
    {
        "full" => NeoAuditRedaction.Full,
        "sensitiveValues" => NeoAuditRedaction.SensitiveValues,
        _ => throw new ArgumentException("Unknown redaction policy.")
    };
    static void ExactFields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("A permission catalog member must be an object.");
        var allowed = names.ToHashSet(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name))
                throw new ArgumentException("The permission catalog contains an unknown field.");
    }

    static byte[] ReadBoundedFile(string path, string description)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 1 or > 1024 * 1024)
            throw new ArgumentException($"The {description} must be between 1 byte and 1 MiB.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is < 1 or > 1024 * 1024)
            throw new ArgumentException($"The {description} must be between 1 byte and 1 MiB.");
        return bytes;
    }

    static void ValidateJsonComplexity(JsonElement root)
    {
        var nodes = 0;
        Visit(root);
        void Visit(JsonElement element)
        {
            if (++nodes > 50_000)
                throw new ArgumentException("The permission catalog exceeds its structural complexity bound.");
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new ArgumentException("The permission catalog contains a duplicate JSON property.");
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    Visit(item);
            }
        }
    }
}