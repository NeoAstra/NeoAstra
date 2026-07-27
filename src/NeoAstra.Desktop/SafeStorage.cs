// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NeoAstra.Desktop.SafeStorage;

/// <summary>Provides small OS-protected binary secrets without plaintext fallback or enumeration.</summary>
public interface INeoSafeStorage
{
    /// <summary>Gets truthful platform support and interaction limitations.</summary>
    NeoCapabilityInfo Support { get; }
    /// <summary>Stores a copied secret under an application-namespaced key.</summary>
    ValueTask<NeoDesktopStatus> StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default);
    /// <summary>Retrieves an owned secret byte array.</summary>
    ValueTask<NeoDesktopResult<byte[]>> RetrieveAsync(string key, CancellationToken cancellationToken = default);
    /// <summary>Deletes one secret.</summary>
    ValueTask<NeoDesktopStatus> DeleteAsync(string key, CancellationToken cancellationToken = default);
    /// <summary>Checks one exact key without exposing enumeration.</summary>
    ValueTask<NeoDesktopResult<bool>> ContainsAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>Creates statically selected OS-backed safe storage.</summary>
public static class NeoSafeStorage
{
    /// <summary>Creates safe storage for an explicit application namespace and private data directory.</summary>
    public static INeoSafeStorage CreateSystem(string applicationNamespace, string privateDataDirectory)
    {
        ValidateNamespace(applicationNamespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateDataDirectory);
        if (!Path.IsPathFullyQualified(privateDataDirectory)) throw new ArgumentException("Safe-storage data directory must be absolute.", nameof(privateDataDirectory));
        if (OperatingSystem.IsWindows()) return new WindowsSafeStorage(applicationNamespace, privateDataDirectory);
        if (OperatingSystem.IsMacOS()) return new MacKeychainSafeStorage(applicationNamespace);
        if (OperatingSystem.IsLinux()) return new SecretToolSafeStorage(applicationNamespace, DesktopProcess.FindTrustedExecutable("/usr/bin/secret-tool", "/usr/local/bin/secret-tool"));
        return new UnsupportedSafeStorage("No OS credential service is available.");
    }

    internal static void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 128 || key.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':'))) throw new ArgumentException("A safe-storage key is malformed.", nameof(key));
    }

    private static void ValidateNamespace(string value) => NeoPluginMetadata.ValidateId(value, nameof(value));
}

internal sealed class UnsupportedSafeStorage(string details) : INeoSafeStorage
{
    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.None, 1, 0, details);
    public ValueTask<NeoDesktopStatus> StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default) { NeoSafeStorage.ValidateKey(key); ValidateSecret(secret); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoDesktopStatus.Unsupported); }
    public ValueTask<NeoDesktopResult<byte[]>> RetrieveAsync(string key, CancellationToken cancellationToken = default) { NeoSafeStorage.ValidateKey(key); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Unsupported)); }
    public ValueTask<NeoDesktopStatus> DeleteAsync(string key, CancellationToken cancellationToken = default) { NeoSafeStorage.ValidateKey(key); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoDesktopStatus.Unsupported); }
    public ValueTask<NeoDesktopResult<bool>> ContainsAsync(string key, CancellationToken cancellationToken = default) { NeoSafeStorage.ValidateKey(key); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoDesktopResult<bool>.Failure(NeoDesktopStatus.Unsupported)); }
    internal static void ValidateSecret(ReadOnlyMemory<byte> secret) { if (secret.Length is < 1 or > NeoDesktopLimits.MaximumSecretBytes) throw new ArgumentOutOfRangeException(nameof(secret), $"A secret must contain 1 to {NeoDesktopLimits.MaximumSecretBytes} bytes."); }
}

internal sealed class SecretToolSafeStorage(string applicationNamespace, string executable) : INeoSafeStorage
{
    public NeoCapabilityInfo Support { get; } = string.IsNullOrEmpty(executable) ? new(NeoSupportLevel.None, 1, 0, "Secret Service secret-tool is unavailable; no fallback is used.") : new(NeoSupportLevel.Limited, 1, 0, "Secret Service through fixed secret-tool operations; the desktop keyring may be locked and prompt the user.");

    public async ValueTask<NeoDesktopStatus> StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default)
    {
        NeoSafeStorage.ValidateKey(key); UnsupportedSafeStorage.ValidateSecret(secret);
        if (string.IsNullOrEmpty(executable)) return NeoDesktopStatus.Unsupported;
        var encoded = new byte[Base64.GetMaxEncodedToUtf8Length(secret.Length)];
        try
        {
            Base64.EncodeToUtf8(secret.Span, encoded, out _, out var written);
            var result = await DesktopProcess.RunAsync(executable, ["store", "--label=NeoAstra protected value", "application", applicationNamespace, "key", key], encoded.AsMemory(0, written), TimeSpan.FromSeconds(30), false, cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 ? NeoDesktopStatus.Success : NeoDesktopStatus.Locked;
        }
        catch (OperationCanceledException) { throw; }
        catch { return NeoDesktopStatus.Failed; }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    public async ValueTask<NeoDesktopResult<byte[]>> RetrieveAsync(string key, CancellationToken cancellationToken = default)
    {
        NeoSafeStorage.ValidateKey(key);
        if (string.IsNullOrEmpty(executable)) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Unsupported);
        byte[]? output = null;
        try
        {
            var result = await DesktopProcess.RunAsync(executable, ["lookup", "application", applicationNamespace, "key", key], default, TimeSpan.FromSeconds(30), true, cancellationToken).ConfigureAwait(false);
            output = result.Output;
            if (result.ExitCode != 0 || output.Length == 0) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.NotFound);
            var length = output.Length;
            while (length > 0 && output[length - 1] is (byte)'\r' or (byte)'\n') length--;
            var decoded = new byte[Base64.GetMaxDecodedFromUtf8Length(length)];
            var status = Base64.DecodeFromUtf8(output.AsSpan(0, length), decoded, out _, out var written);
            if (status != System.Buffers.OperationStatus.Done || written is < 1 or > NeoDesktopLimits.MaximumSecretBytes) { CryptographicOperations.ZeroMemory(decoded); return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Corrupt); }
            if (written == decoded.Length) return NeoDesktopResult<byte[]>.Success(decoded);
            var exact = decoded.AsSpan(0, written).ToArray();
            CryptographicOperations.ZeroMemory(decoded);
            return NeoDesktopResult<byte[]>.Success(exact);
        }
        catch (OperationCanceledException) { throw; }
        catch { return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Locked); }
        finally { if (output is not null) CryptographicOperations.ZeroMemory(output); }
    }

    public async ValueTask<NeoDesktopStatus> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        NeoSafeStorage.ValidateKey(key); if (string.IsNullOrEmpty(executable)) return NeoDesktopStatus.Unsupported;
        try { var result = await DesktopProcess.RunAsync(executable, ["clear", "application", applicationNamespace, "key", key], default, TimeSpan.FromSeconds(30), false, cancellationToken).ConfigureAwait(false); return result.ExitCode == 0 ? NeoDesktopStatus.Success : NeoDesktopStatus.NotFound; }
        catch (OperationCanceledException) { throw; }
        catch { return NeoDesktopStatus.Locked; }
    }

    public async ValueTask<NeoDesktopResult<bool>> ContainsAsync(string key, CancellationToken cancellationToken = default)
    {
        var result = await RetrieveAsync(key, cancellationToken).ConfigureAwait(false);
        if (result.Value is { } secret) CryptographicOperations.ZeroMemory(secret);
        return result.Status switch { NeoDesktopStatus.Success => NeoDesktopResult<bool>.Success(true), NeoDesktopStatus.NotFound => NeoDesktopResult<bool>.Success(false), _ => NeoDesktopResult<bool>.Failure(result.Status, result.Code) };
    }
}

internal sealed partial class WindowsSafeStorage : INeoSafeStorage
{
    private readonly string _directory;
    private readonly byte[] _entropy;

    internal WindowsSafeStorage(string applicationNamespace, string privateDataDirectory)
    {
        var namespaceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(applicationNamespace))).ToLowerInvariant();
        _directory = Path.Combine(Path.GetFullPath(privateDataDirectory), namespaceHash);
        Directory.CreateDirectory(_directory);
        _entropy = SHA256.HashData(Encoding.UTF8.GetBytes("NeoAstra.SafeStorage/v1/" + applicationNamespace));
    }

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Native, 1, 0, "Per-user DPAPI encryption with atomic encrypted-file persistence and no plaintext fallback.");

    public async ValueTask<NeoDesktopStatus> StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default)
    {
        NeoSafeStorage.ValidateKey(key); UnsupportedSafeStorage.ValidateSecret(secret); cancellationToken.ThrowIfCancellationRequested();
        byte[] protectedBytes;
        try { protectedBytes = Protect(secret.Span); }
        catch (System.ComponentModel.Win32Exception) { return NeoDesktopStatus.Locked; }
        var target = PathForKey(key); var temporary = target + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, target, overwrite: true);
            return NeoDesktopStatus.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch { return NeoDesktopStatus.Failed; }
        finally { CryptographicOperations.ZeroMemory(protectedBytes); try { File.Delete(temporary); } catch { } }
    }

    public async ValueTask<NeoDesktopResult<byte[]>> RetrieveAsync(string key, CancellationToken cancellationToken = default)
    {
        NeoSafeStorage.ValidateKey(key); var path = PathForKey(key); if (!File.Exists(path)) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.NotFound);
        try { if (new FileInfo(path).Length is < 1 or > NeoDesktopLimits.MaximumSecretBytes * 4L) return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Corrupt); }
        catch { return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Failed); }
        byte[] encrypted;
        try { encrypted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Failed); }
        try { return NeoDesktopResult<byte[]>.Success(Unprotect(encrypted)); }
        catch (System.ComponentModel.Win32Exception) { return NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Corrupt); }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
    }

    public ValueTask<NeoDesktopStatus> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        NeoSafeStorage.ValidateKey(key); cancellationToken.ThrowIfCancellationRequested(); var path = PathForKey(key);
        try { if (!File.Exists(path)) return ValueTask.FromResult(NeoDesktopStatus.NotFound); File.Delete(path); return ValueTask.FromResult(NeoDesktopStatus.Success); }
        catch { return ValueTask.FromResult(NeoDesktopStatus.Failed); }
    }

    public ValueTask<NeoDesktopResult<bool>> ContainsAsync(string key, CancellationToken cancellationToken = default) { NeoSafeStorage.ValidateKey(key); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(NeoDesktopResult<bool>.Success(File.Exists(PathForKey(key)))); }

    private string PathForKey(string key) => Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".bin");

    private unsafe byte[] Protect(ReadOnlySpan<byte> source)
    {
        fixed (byte* sourcePointer = source)
        fixed (byte* entropyPointer = _entropy)
        {
            var input = new DataBlob((uint)source.Length, sourcePointer); var entropy = new DataBlob((uint)_entropy.Length, entropyPointer);
            if (!SafeStorageNative.CryptProtectData(&input, null, &entropy, 0, 0, 1, out var output)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            try { return new ReadOnlySpan<byte>(output.Data, checked((int)output.Length)).ToArray(); }
            finally { CryptographicOperations.ZeroMemory(new Span<byte>(output.Data, checked((int)output.Length))); _ = SafeStorageNative.LocalFree((nint)output.Data); }
        }
    }

    private unsafe byte[] Unprotect(ReadOnlySpan<byte> source)
    {
        fixed (byte* sourcePointer = source)
        fixed (byte* entropyPointer = _entropy)
        {
            var input = new DataBlob((uint)source.Length, sourcePointer); var entropy = new DataBlob((uint)_entropy.Length, entropyPointer);
            if (!SafeStorageNative.CryptUnprotectData(&input, 0, &entropy, 0, 0, 1, out var output)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            try { if (output.Length > NeoDesktopLimits.MaximumSecretBytes) throw new System.ComponentModel.Win32Exception("DPAPI output exceeded the secret limit."); return new ReadOnlySpan<byte>(output.Data, checked((int)output.Length)).ToArray(); }
            finally { CryptographicOperations.ZeroMemory(new Span<byte>(output.Data, checked((int)output.Length))); _ = SafeStorageNative.LocalFree((nint)output.Data); }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private readonly unsafe struct DataBlob(uint length, byte* data) { internal readonly uint Length = length; internal readonly byte* Data = data; }

    private static unsafe partial class SafeStorageNative
    {
        [LibraryImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool CryptProtectData(DataBlob* input, char* description, DataBlob* entropy, nint reserved, nint prompt, uint flags, out DataBlob output);
        [LibraryImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool CryptUnprotectData(DataBlob* input, nint description, DataBlob* entropy, nint reserved, nint prompt, uint flags, out DataBlob output);
        [LibraryImport("kernel32.dll")] internal static partial nint LocalFree(nint memory);
    }
}
