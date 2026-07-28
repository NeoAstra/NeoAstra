// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NeoAstra.Desktop.SafeStorage;

internal sealed unsafe partial class MacKeychainSafeStorage : INeoSafeStorage
{
    private const int ItemNotFound = -25300;
    private const int InteractionNotAllowed = -25308;
    private const int AuthFailed = -25293;
    private readonly byte[] _service;

    internal MacKeychainSafeStorage(string applicationNamespace) => _service = Encoding.UTF8.GetBytes(applicationNamespace);

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Native, 1, 0,
        "macOS Keychain generic-password items with binary-safe native APIs, application service namespace, exact account key, no enumeration, and no plaintext fallback. The keychain may prompt, be locked, or deny interaction.");

    public ValueTask<NeoDesktopStatus> StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default)
    {
        NeoSafeStorage.ValidateKey(key); UnsupportedSafeStorage.ValidateSecret(secret); cancellationToken.ThrowIfCancellationRequested();
        var account = Encoding.UTF8.GetBytes(key);
        try
        {
            fixed (byte* servicePointer = _service)
            fixed (byte* accountPointer = account)
            fixed (byte* secretPointer = secret.Span)
            {
                var status = KeychainNative.FindGenericPassword(0, (uint)_service.Length, servicePointer, (uint)account.Length, accountPointer, null, null, out var item);
                if (status == 0)
                {
                    try { status = KeychainNative.ModifyItem(item, 0, (uint)secret.Length, secretPointer); }
                    finally { KeychainNative.CFRelease(item); }
                }
                else if (status == ItemNotFound)
                {
                    status = KeychainNative.AddGenericPassword(0, (uint)_service.Length, servicePointer, (uint)account.Length, accountPointer, (uint)secret.Length, secretPointer, 0);
                }
                return ValueTask.FromResult(Map(status, notFound: NeoDesktopStatus.Failed));
            }
        }
        finally { CryptographicOperations.ZeroMemory(account); }
    }

    public ValueTask<NeoDesktopResult<byte[]>> RetrieveAsync(string key, CancellationToken cancellationToken = default)
    {
        NeoSafeStorage.ValidateKey(key); cancellationToken.ThrowIfCancellationRequested();
        var account = Encoding.UTF8.GetBytes(key);
        try
        {
            fixed (byte* servicePointer = _service)
            fixed (byte* accountPointer = account)
            {
                uint length = 0; byte* data = null;
                var status = KeychainNative.FindGenericPassword(0, (uint)_service.Length, servicePointer, (uint)account.Length, accountPointer, &length, &data, out var item);
                if (item != 0) KeychainNative.CFRelease(item);
                if (status != 0) return ValueTask.FromResult(NeoDesktopResult<byte[]>.Failure(Map(status, NeoDesktopStatus.NotFound)));
                if (data == null || length is < 1 || length > NeoDesktopLimits.MaximumSecretBytes)
                {
                    if (data != null) KeychainNative.FreeContent(0, data);
                    return ValueTask.FromResult(NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Corrupt));
                }
                try { return ValueTask.FromResult(NeoDesktopResult<byte[]>.Success(new ReadOnlySpan<byte>(data, checked((int)length)).ToArray())); }
                finally { CryptographicOperations.ZeroMemory(new Span<byte>(data, checked((int)length))); KeychainNative.FreeContent(0, data); }
            }
        }
        finally { CryptographicOperations.ZeroMemory(account); }
    }

    public ValueTask<NeoDesktopStatus> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        NeoSafeStorage.ValidateKey(key); cancellationToken.ThrowIfCancellationRequested();
        var account = Encoding.UTF8.GetBytes(key);
        try
        {
            fixed (byte* servicePointer = _service)
            fixed (byte* accountPointer = account)
            {
                var status = KeychainNative.FindGenericPassword(0, (uint)_service.Length, servicePointer, (uint)account.Length, accountPointer, null, null, out var item);
                if (status != 0) return ValueTask.FromResult(Map(status, NeoDesktopStatus.NotFound));
                try { return ValueTask.FromResult(Map(KeychainNative.DeleteItem(item), NeoDesktopStatus.NotFound)); }
                finally { KeychainNative.CFRelease(item); }
            }
        }
        finally { CryptographicOperations.ZeroMemory(account); }
    }

    public ValueTask<NeoDesktopResult<bool>> ContainsAsync(string key, CancellationToken cancellationToken = default)
    {
        NeoSafeStorage.ValidateKey(key); cancellationToken.ThrowIfCancellationRequested();
        var account = Encoding.UTF8.GetBytes(key);
        try
        {
            fixed (byte* servicePointer = _service)
            fixed (byte* accountPointer = account)
            {
                var status = KeychainNative.FindGenericPassword(0, (uint)_service.Length, servicePointer, (uint)account.Length, accountPointer, null, null, out var item);
                if (item != 0) KeychainNative.CFRelease(item);
                return status switch
                {
                    0 => ValueTask.FromResult(NeoDesktopResult<bool>.Success(true)),
                    ItemNotFound => ValueTask.FromResult(NeoDesktopResult<bool>.Success(false)),
                    _ => ValueTask.FromResult(NeoDesktopResult<bool>.Failure(Map(status, NeoDesktopStatus.NotFound))),
                };
            }
        }
        finally { CryptographicOperations.ZeroMemory(account); }
    }

    private static NeoDesktopStatus Map(int status, NeoDesktopStatus notFound) => status switch
    {
        0 => NeoDesktopStatus.Success,
        ItemNotFound => notFound,
        InteractionNotAllowed or AuthFailed => NeoDesktopStatus.Locked,
        _ => NeoDesktopStatus.Failed,
    };

    private static partial class KeychainNative
    {
        [LibraryImport("/System/Library/Frameworks/Security.framework/Security", EntryPoint = "SecKeychainAddGenericPassword")]
        internal static partial int AddGenericPassword(nint keychain, uint serviceLength, byte* service, uint accountLength, byte* account, uint passwordLength, byte* password, nint item);
        [LibraryImport("/System/Library/Frameworks/Security.framework/Security", EntryPoint = "SecKeychainFindGenericPassword")]
        internal static partial int FindGenericPassword(nint keychain, uint serviceLength, byte* service, uint accountLength, byte* account, uint* passwordLength, byte** password, out nint item);
        [LibraryImport("/System/Library/Frameworks/Security.framework/Security", EntryPoint = "SecKeychainItemModifyAttributesAndData")]
        internal static partial int ModifyItem(nint item, nint attributes, uint length, byte* data);
        [LibraryImport("/System/Library/Frameworks/Security.framework/Security", EntryPoint = "SecKeychainItemDelete")]
        internal static partial int DeleteItem(nint item);
        [LibraryImport("/System/Library/Frameworks/Security.framework/Security", EntryPoint = "SecKeychainItemFreeContent")]
        internal static partial int FreeContent(nint attributes, void* data);
        [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        internal static partial void CFRelease(nint value);
    }
}
