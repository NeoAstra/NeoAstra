// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text;
using System.Text.Json;
using NeoAstra.Desktop;

namespace NeoAstra.Passkeys;

// The Credential Manager of Android through the Java service `neoastra.passkeys` of this package, which the platform services of
// NeoAstra reach. Java blocks until the user answers the sheet, so every call leaves the calling thread. The Java side is there
// only when the application references the Credential Manager. The support is the one of the device, read at each access:
// passkeys require Android 9 (API 28) or later and a provider, the credential service of Android 14 and later or Google Play
// services, which a Fire tablet or another Android without them lacks.
internal sealed class AndroidPasskeys : INeoPasskeys
{
    internal const string Service = "neoastra.passkeys";

    public NeoCapabilityInfo Support
    {
        get
        {
            try { return Describe(Call("support", null)); }
            catch (Exception exception) when (exception is NotSupportedException or NeoAstraException)
            {
                return new(NeoSupportLevel.None, 1, 0, "The platform services of Android cannot tell the passkeys of the device: " + exception.Message);
            }
        }
    }

    internal static NeoCapabilityInfo Describe(JsonDocument response)
    {
        using (response)
        {
            var root = response.RootElement;
            var status = root.TryGetProperty("status", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (status == nameof(NeoDesktopStatus.Unsupported))
                return new(NeoSupportLevel.None, 1, 0, "The application does not reference the Credential Manager (Xamarin.AndroidX.Credentials), or NeoAstraPasskeysCredentialManager is false.");
            if (status != nameof(NeoDesktopStatus.Success)) return new(NeoSupportLevel.None, 1, 0, "The passkeys service of Android did not answer.");
            var provider = root.TryGetProperty("provider", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
            return provider switch
            {
                null => new(NeoSupportLevel.None, 1, 0, "Passkeys require Android 9 (API 28) or later and a provider for the Credential Manager: the credential service of Android 14 or later or Google Play services (Xamarin.AndroidX.Credentials.PlayServicesAuth)."),
                "system" => new(NeoSupportLevel.Native, 1, 0, "The Credential Manager of Android with the credential service of Android and the passkeys of the password manager of the user (assetlinks.json of the relying party)."),
                _ => new(NeoSupportLevel.Native, 1, 0, $"The Credential Manager of Android with the provider {provider} and the passkeys of the password manager of the user (assetlinks.json of the relying party)."),
            };
        }
    }

    public ValueTask<NeoDesktopResult<string>> CreateAsync(string options, CancellationToken cancellationToken = default) => Run("create", options, creation: true, cancellationToken);

    public ValueTask<NeoDesktopResult<string>> GetAsync(string options, CancellationToken cancellationToken = default) => Run("get", options, creation: false, cancellationToken);

    private static ValueTask<NeoDesktopResult<string>> Run(string operation, string options, bool creation, CancellationToken cancellationToken)
    {
        NeoPasskeys.Validate(options, creation);
        cancellationToken.ThrowIfCancellationRequested();
        return new(Task.Run(() => Result(Call(operation, options)), cancellationToken).WaitAsync(cancellationToken));
    }

    internal static NeoDesktopResult<string> Result(JsonDocument response)
    {
        using (response)
        {
            var root = response.RootElement;
            var status = root.TryGetProperty("status", out var value) && Enum.TryParse<NeoDesktopStatus>(value.GetString(), out var parsed) ? parsed : NeoDesktopStatus.Failed;
            var code = root.TryGetProperty("code", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
            if (status != NeoDesktopStatus.Success) return NeoPasskeys.Failure(status, code);
            return root.TryGetProperty("response", out var answer) && answer.ValueKind == JsonValueKind.String ? NeoDesktopResult<string>.Success(answer.GetString()!) : NeoPasskeys.Failure(NeoDesktopStatus.Failed, null);
        }
    }

    internal static JsonDocument Call(string operation, string? options)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", operation);
            if (options is not null) writer.WriteString("options", options);
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(NeoPlatformServices.Call(Service, Encoding.UTF8.GetString(stream.ToArray())));
    }
}
