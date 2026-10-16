// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text;
using System.Text.Json;
using NeoAstra.Desktop;

namespace NeoAstra.Passkeys;

// The Credential Manager of Android through the Java service `neoastra.passkeys` of this package, which the platform services of
// NeoAstra reach. Java blocks until the user answers the sheet, so every call leaves the calling thread. The Java side is there
// only when the application references the Credential Manager.
internal sealed class AndroidPasskeys : INeoPasskeys
{
    internal const string Service = "neoastra.passkeys";

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Native, 1, 0,
        "The Credential Manager of Android with the passkeys of the password manager of the user (Xamarin.AndroidX.Credentials, Xamarin.AndroidX.Credentials.PlayServicesAuth, assetlinks.json of the relying party).");

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

    internal static JsonDocument Call(string operation, string options)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", operation);
            writer.WriteString("options", options);
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(NeoPlatformServices.Call(Service, Encoding.UTF8.GetString(stream.ToArray())));
    }
}
