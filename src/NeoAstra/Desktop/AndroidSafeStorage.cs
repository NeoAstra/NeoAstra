// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.Json;

namespace NeoAstra.Desktop.SafeStorage;

internal sealed class AndroidKeystoreSafeStorage(string applicationNamespace) : INeoSafeStorage
{
    private readonly string _namespace = applicationNamespace;

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Native, 1, 0,
        "Secrets encrypted with an AES-GCM key of the Android Keystore that never leaves it, stored in the private preferences of the application under the exact key, with no enumeration and no plaintext fallback.");

    public ValueTask<NeoDesktopStatus> StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default)
    {
        NeoSafeStorage.ValidateKey(key); UnsupportedSafeStorage.ValidateSecret(secret); cancellationToken.ThrowIfCancellationRequested();
        using var response = Call("store", key, ("data", Convert.ToBase64String(secret.Span)));
        return ValueTask.FromResult(AndroidPlatform.Status(response));
    }

    public ValueTask<NeoDesktopResult<byte[]>> RetrieveAsync(string key, CancellationToken cancellationToken = default)
    {
        NeoSafeStorage.ValidateKey(key); cancellationToken.ThrowIfCancellationRequested();
        using var response = Call("retrieve", key);
        var status = AndroidPlatform.Status(response);
        var data = AndroidPlatform.Text(response, "data");
        if (status != NeoDesktopStatus.Success || data is null) return ValueTask.FromResult(NeoDesktopResult<byte[]>.Failure(status == NeoDesktopStatus.Success ? NeoDesktopStatus.Corrupt : status));
        try { return ValueTask.FromResult(NeoDesktopResult<byte[]>.Success(Convert.FromBase64String(data))); }
        catch (FormatException) { return ValueTask.FromResult(NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Corrupt)); }
    }

    public ValueTask<NeoDesktopStatus> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        NeoSafeStorage.ValidateKey(key); cancellationToken.ThrowIfCancellationRequested();
        using var response = Call("delete", key);
        return ValueTask.FromResult(AndroidPlatform.Status(response));
    }

    public ValueTask<NeoDesktopResult<bool>> ContainsAsync(string key, CancellationToken cancellationToken = default)
    {
        NeoSafeStorage.ValidateKey(key); cancellationToken.ThrowIfCancellationRequested();
        using var response = Call("contains", key);
        var status = AndroidPlatform.Status(response);
        return ValueTask.FromResult(status == NeoDesktopStatus.Success && response.RootElement.TryGetProperty("value", out var value)
            ? NeoDesktopResult<bool>.Success(value.ValueKind == JsonValueKind.True)
            : NeoDesktopResult<bool>.Failure(status == NeoDesktopStatus.Success ? NeoDesktopStatus.Failed : status));
    }

    private JsonDocument Call(string operation, string key, params (string Name, string Value)[] extra)
        => AndroidPlatform.Call("safe-storage", [("operation", operation), ("namespace", _namespace), ("key", key), .. extra]);
}
