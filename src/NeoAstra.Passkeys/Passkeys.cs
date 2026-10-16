// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.Json;
using NeoAstra.Desktop;

namespace NeoAstra.Passkeys;

/// <summary>Creates and uses the passkeys of the platform for a relying party, with the WebAuthn options of its server.</summary>
/// <remarks>
/// <para>
/// The server of the relying party gives the options as JSON (<c>PublicKeyCredentialCreationOptionsJSON</c> to create a
/// passkey, <c>PublicKeyCredentialRequestOptionsJSON</c> to sign in), and receives the answer of the platform as JSON
/// (<c>RegistrationResponseJSON</c> and <c>AuthenticationResponseJSON</c>, with base64url values), which it verifies. The
/// platform signs for the relying party of the options only when the application is associated with its domain: the
/// <c>webcredentials</c> of <c>/.well-known/apple-app-site-association</c> and the associated domains entitlement on iOS, the
/// <c>delegate_permission/common.get_login_creds</c> of <c>/.well-known/assetlinks.json</c> with the package and its signing
/// certificate on Android.
/// </para>
/// <para>A request shows the sheet of the platform and waits for the user.</para>
/// </remarks>
public interface INeoPasskeys
{
    /// <summary>Gets truthful platform support.</summary>
    NeoCapabilityInfo Support { get; }

    /// <summary>Creates a passkey for the relying party and the user of the options.</summary>
    /// <param name="options">The creation options of the server, a WebAuthn JSON object of at most 64 KiB.</param>
    /// <param name="cancellationToken">Stops waiting for the user.</param>
    /// <returns>
    /// The registration response as JSON; <see cref="NeoDesktopStatus.Canceled"/> when the user closes the sheet;
    /// <see cref="NeoDesktopStatus.Conflict"/> with the code <c>excluded</c> when the device already has a passkey that the
    /// options exclude; <see cref="NeoDesktopStatus.Denied"/> with the code <c>not-associated</c> when the application is
    /// not associated with the relying party; <see cref="NeoDesktopStatus.Unsupported"/> on a platform without passkeys.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="options"/> are not creation options.</exception>
    ValueTask<NeoDesktopResult<string>> CreateAsync(string options, CancellationToken cancellationToken = default);

    /// <summary>Signs in with a passkey of the device for the relying party of the options.</summary>
    /// <param name="options">The request options of the server, a WebAuthn JSON object of at most 64 KiB.</param>
    /// <param name="cancellationToken">Stops waiting for the user.</param>
    /// <returns>
    /// The authentication response as JSON; <see cref="NeoDesktopStatus.Canceled"/> when the user closes the sheet;
    /// <see cref="NeoDesktopStatus.NotFound"/> with the code <c>no-credential</c> when the device has no passkey for the
    /// options; <see cref="NeoDesktopStatus.Denied"/> with the code <c>not-associated</c> when the application is not
    /// associated with the relying party; <see cref="NeoDesktopStatus.Unsupported"/> on a platform without passkeys.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="options"/> are not request options.</exception>
    ValueTask<NeoDesktopResult<string>> GetAsync(string options, CancellationToken cancellationToken = default);
}

/// <summary>Creates the statically selected passkeys adapter.</summary>
public static class NeoPasskeys
{
    /// <summary>The largest options that a request accepts, in UTF-16 code units.</summary>
    public const int MaximumOptionsLength = 65536;

    /// <summary>The code of a result for a device that already has a passkey that the options exclude.</summary>
    public const string ExcludedCode = "excluded";

    /// <summary>The code of a result for a device without a passkey for the options.</summary>
    public const string NoCredentialCode = "no-credential";

    /// <summary>The code of a result for an application that is not associated with the relying party.</summary>
    public const string NotAssociatedCode = "not-associated";

    /// <summary>Creates the adapter of the current operating system: Authentication Services on iOS, the Credential Manager on Android.</summary>
    /// <param name="dispatcher">Optional application UI dispatcher, on which iOS shows its sheet; the plugin binds it otherwise.</param>
    /// <returns>The adapter, which answers <see cref="NeoDesktopStatus.Unsupported"/> on a desktop.</returns>
    public static INeoPasskeys CreateSystem(NeoDispatcher? dispatcher = null)
    {
        if (OperatingSystem.IsIOS()) return IosPasskeys.Create(dispatcher);
        if (OperatingSystem.IsAndroid()) return new AndroidPasskeys();
        return new UnsupportedPasskeys("The passkeys of a desktop are not available yet.");
    }

    internal static NeoDesktopResult<string> Failure(NeoDesktopStatus status, string? code) => new(status, null, code);

    internal static NeoPasskeyCreation ParseCreation(string options)
    {
        using var document = Parse(options);
        var root = document.RootElement;
        var rp = Member(root, "rp", JsonValueKind.Object);
        var user = Member(root, "user", JsonValueKind.Object);
        var selection = root.TryGetProperty("authenticatorSelection", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
        return new(Text(rp, "id"), Bytes(root, "challenge"), Bytes(user, "id"), Text(user, "name"),
            user.TryGetProperty("displayName", out var display) && display.ValueKind == JsonValueKind.String ? display.GetString()! : Text(user, "name"),
            Descriptors(root, "excludeCredentials"),
            selection.ValueKind == JsonValueKind.Object && selection.TryGetProperty("userVerification", out var verification) && verification.ValueKind == JsonValueKind.String ? verification.GetString()! : "preferred",
            root.TryGetProperty("attestation", out var attestation) && attestation.ValueKind == JsonValueKind.String ? attestation.GetString()! : "none");
    }

    internal static NeoPasskeyRequest ParseRequest(string options)
    {
        using var document = Parse(options);
        var root = document.RootElement;
        return new(Text(root, "rpId"), Bytes(root, "challenge"), Descriptors(root, "allowCredentials"),
            root.TryGetProperty("userVerification", out var verification) && verification.ValueKind == JsonValueKind.String ? verification.GetString()! : "preferred");
    }

    internal static void Validate(string options, bool creation)
    {
        if (creation) _ = ParseCreation(options);
        else _ = ParseRequest(options);
    }

    internal static string Base64Url(ReadOnlySpan<byte> value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static JsonDocument Parse(string options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Length > MaximumOptionsLength) throw new ArgumentException("The passkey options are larger than 64 KiB.", nameof(options));
        try
        {
            var document = JsonDocument.Parse(options, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
        }
        catch (JsonException) { }
        throw new ArgumentException("The passkey options are not a JSON object.", nameof(options));
    }

    private static JsonElement Member(JsonElement parent, string name, JsonValueKind kind)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == kind ? value : throw new ArgumentException($"The passkey options have no valid '{name}'.", "options");

    private static string Text(JsonElement parent, string name)
    {
        var value = Member(parent, name, JsonValueKind.String).GetString()!;
        if (value.Length is 0 or > 256) throw new ArgumentException($"The passkey options have no valid '{name}'.", "options");
        return value;
    }

    private static byte[] Bytes(JsonElement parent, string name)
    {
        var text = Member(parent, name, JsonValueKind.String).GetString()!;
        var bytes = FromBase64Url(text) ?? throw new ArgumentException($"The passkey options have no base64url '{name}'.", "options");
        if (bytes.Length is 0 or > 1024) throw new ArgumentException($"The passkey options have no valid '{name}'.", "options");
        return bytes;
    }

    private static IReadOnlyList<byte[]> Descriptors(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return [];
        if (list.GetArrayLength() > 256) throw new ArgumentException($"The passkey options have too many '{name}'.", "options");
        return [.. list.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Object ? Bytes(item, "id") : throw new ArgumentException($"The passkey options have no valid '{name}'.", "options"))];
    }

    private static byte[]? FromBase64Url(string value)
    {
        if (value.Length > 2048) return null;
        var text = value.Replace('-', '+').Replace('_', '/');
        text = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
        try { return Convert.FromBase64String(text); }
        catch (FormatException) { return null; }
    }
}

// What a platform needs of creation options and request options.
internal sealed record NeoPasskeyCreation(string RelyingParty, byte[] Challenge, byte[] UserId, string UserName, string DisplayName, IReadOnlyList<byte[]> Excluded, string UserVerification, string Attestation);
internal sealed record NeoPasskeyRequest(string RelyingParty, byte[] Challenge, IReadOnlyList<byte[]> Allowed, string UserVerification);

/// <summary>A deterministic passkeys service for tests, which answers what the test sets.</summary>
public sealed class NeoFakePasskeys : INeoPasskeys
{
    /// <inheritdoc />
    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Emulated, 1, 0, "In-memory test passkeys.");

    /// <summary>Gets or sets the result of <see cref="CreateAsync"/>.</summary>
    public NeoDesktopResult<string> Creation { get; set; } = NeoPasskeys.Failure(NeoDesktopStatus.Canceled, null);

    /// <summary>Gets or sets the result of <see cref="GetAsync"/>.</summary>
    public NeoDesktopResult<string> Assertion { get; set; } = NeoPasskeys.Failure(NeoDesktopStatus.NotFound, NeoPasskeys.NoCredentialCode);

    /// <summary>Gets the options of the last request.</summary>
    public string? LastOptions { get; private set; }

    /// <inheritdoc />
    public ValueTask<NeoDesktopResult<string>> CreateAsync(string options, CancellationToken cancellationToken = default)
    {
        NeoPasskeys.Validate(options, creation: true);
        cancellationToken.ThrowIfCancellationRequested();
        LastOptions = options;
        return ValueTask.FromResult(Creation);
    }

    /// <inheritdoc />
    public ValueTask<NeoDesktopResult<string>> GetAsync(string options, CancellationToken cancellationToken = default)
    {
        NeoPasskeys.Validate(options, creation: false);
        cancellationToken.ThrowIfCancellationRequested();
        LastOptions = options;
        return ValueTask.FromResult(Assertion);
    }
}

internal sealed class UnsupportedPasskeys(string details) : INeoPasskeys
{
    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.None, 1, 0, details);

    public ValueTask<NeoDesktopResult<string>> CreateAsync(string options, CancellationToken cancellationToken = default)
    {
        NeoPasskeys.Validate(options, creation: true);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(NeoPasskeys.Failure(NeoDesktopStatus.Unsupported, null));
    }

    public ValueTask<NeoDesktopResult<string>> GetAsync(string options, CancellationToken cancellationToken = default)
    {
        NeoPasskeys.Validate(options, creation: false);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(NeoPasskeys.Failure(NeoDesktopStatus.Unsupported, null));
    }
}

// What iOS binds to the UI dispatcher of the application when the plugin attaches.
internal interface INeoPasskeysDispatcherBinding
{
    void Bind(NeoDispatcher dispatcher);
}
