// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;
using System.Text;

namespace NeoAstra.Desktop.SafeStorage;

internal sealed unsafe partial class IosKeychainSafeStorage : INeoSafeStorage
{
    private const int ItemNotFound = -25300;
    private const int InteractionNotAllowed = -25308;
    private const int AuthFailed = -25293;
    private readonly string _service;

    internal IosKeychainSafeStorage(string applicationNamespace) => _service = applicationNamespace;

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Native, 1, 0,
        "iOS Keychain generic-password items of the application, with the application namespace as service, the exact key as account, no enumeration, and no plaintext fallback. Items are readable after the first unlock and stay on this device.");

    public ValueTask<NeoDesktopStatus> StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default)
    {
        NeoSafeStorage.ValidateKey(key); UnsupportedSafeStorage.ValidateSecret(secret); cancellationToken.ThrowIfCancellationRequested();
        using var scope = new CFScope();
        var query = Query(scope, key);
        var data = scope.Own(Native.CFDataCreate(0, secret.Span));
        // An update changes only the data of an existing item; the query stays the exact item.
        var change = scope.Dictionary((Native.Constant("kSecValueData"), data));
        var status = Native.SecItemUpdate(query, change);
        if (status == ItemNotFound)
        {
            var attributes = scope.Dictionary(
                (Native.Constant("kSecClass"), Native.Constant("kSecClassGenericPassword")),
                (Native.Constant("kSecAttrService"), scope.String(_service)),
                (Native.Constant("kSecAttrAccount"), scope.String(key)),
                (Native.Constant("kSecAttrAccessible"), Native.Constant("kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly")),
                (Native.Constant("kSecValueData"), data));
            status = Native.SecItemAdd(attributes, null);
        }
        return ValueTask.FromResult(Map(status, NeoDesktopStatus.Failed));
    }

    public ValueTask<NeoDesktopResult<byte[]>> RetrieveAsync(string key, CancellationToken cancellationToken = default)
    {
        NeoSafeStorage.ValidateKey(key); cancellationToken.ThrowIfCancellationRequested();
        using var scope = new CFScope();
        var query = Query(scope, key, (Native.Constant("kSecReturnData"), Native.Constant("kCFBooleanTrue")), (Native.Constant("kSecMatchLimit"), Native.Constant("kSecMatchLimitOne")));
        nint result = 0;
        var status = Native.SecItemCopyMatching(query, &result);
        if (status != 0) return ValueTask.FromResult(NeoDesktopResult<byte[]>.Failure(Map(status, NeoDesktopStatus.NotFound)));
        scope.Own(result);
        var length = Native.CFDataGetLength(result);
        if (length < 0 || length > NeoDesktopLimits.MaximumSecretBytes) return ValueTask.FromResult(NeoDesktopResult<byte[]>.Failure(NeoDesktopStatus.Corrupt));
        return ValueTask.FromResult(NeoDesktopResult<byte[]>.Success(new ReadOnlySpan<byte>((void*)Native.CFDataGetBytePtr(result), (int)length).ToArray()));
    }

    public ValueTask<NeoDesktopStatus> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        NeoSafeStorage.ValidateKey(key); cancellationToken.ThrowIfCancellationRequested();
        using var scope = new CFScope();
        return ValueTask.FromResult(Map(Native.SecItemDelete(Query(scope, key)), NeoDesktopStatus.NotFound));
    }

    public ValueTask<NeoDesktopResult<bool>> ContainsAsync(string key, CancellationToken cancellationToken = default)
    {
        NeoSafeStorage.ValidateKey(key); cancellationToken.ThrowIfCancellationRequested();
        using var scope = new CFScope();
        var status = Native.SecItemCopyMatching(Query(scope, key, (Native.Constant("kSecMatchLimit"), Native.Constant("kSecMatchLimitOne"))), null);
        return ValueTask.FromResult(status switch
        {
            0 => NeoDesktopResult<bool>.Success(true),
            ItemNotFound => NeoDesktopResult<bool>.Success(false),
            _ => NeoDesktopResult<bool>.Failure(Map(status, NeoDesktopStatus.NotFound)),
        });
    }

    private nint Query(CFScope scope, string key, params (nint Key, nint Value)[] extra)
    {
        var entries = new List<(nint, nint)>
        {
            (Native.Constant("kSecClass"), Native.Constant("kSecClassGenericPassword")),
            (Native.Constant("kSecAttrService"), scope.String(_service)),
            (Native.Constant("kSecAttrAccount"), scope.String(key)),
        };
        entries.AddRange(extra);
        return scope.Dictionary([.. entries]);
    }

    private static NeoDesktopStatus Map(int status, NeoDesktopStatus notFound) => status switch
    {
        0 => NeoDesktopStatus.Success,
        ItemNotFound => notFound,
        InteractionNotAllowed or AuthFailed => NeoDesktopStatus.Locked,
        _ => NeoDesktopStatus.Failed,
    };

    // Core Foundation objects created for one call, released together.
    private sealed class CFScope : IDisposable
    {
        private readonly List<nint> _owned = [];
        public nint Own(nint value) { if (value != 0) _owned.Add(value); return value; }
        public nint String(string value) { var bytes = Encoding.UTF8.GetBytes(value); return Own(Native.CFStringCreateWithBytes(0, bytes, bytes.Length, 0x08000100, false)); }
        public nint Dictionary(params (nint Key, nint Value)[] entries)
        {
            var keys = new nint[entries.Length]; var values = new nint[entries.Length];
            for (var index = 0; index < entries.Length; index++) (keys[index], values[index]) = entries[index];
            fixed (nint* keyPointer = keys) fixed (nint* valuePointer = values)
                return Own(Native.CFDictionaryCreate(0, keyPointer, valuePointer, entries.Length, Native.Export(Native.CoreFoundation, "kCFTypeDictionaryKeyCallBacks"), Native.Export(Native.CoreFoundation, "kCFTypeDictionaryValueCallBacks")));
        }
        public void Dispose() { foreach (var value in _owned) Native.CFRelease(value); _owned.Clear(); }
    }

    private static partial class Native
    {
        internal const string Security = "/System/Library/Frameworks/Security.framework/Security";
        internal const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        // The keys and values of the Keychain are exported constants: the address of a variable that holds a CFTypeRef.
        internal static nint Constant(string name)
        {
            var library = name.StartsWith("kCF", StringComparison.Ordinal) ? CoreFoundation : Security;
            return Marshal.ReadIntPtr(Export(library, name));
        }

        internal static nint Export(string library, string name) => NativeLibrary.GetExport(NativeLibrary.Load(library), name);

        [LibraryImport(Security)] internal static partial int SecItemAdd(nint attributes, nint* result);
        [LibraryImport(Security)] internal static partial int SecItemUpdate(nint query, nint attributesToUpdate);
        [LibraryImport(Security)] internal static partial int SecItemCopyMatching(nint query, nint* result);
        [LibraryImport(Security)] internal static partial int SecItemDelete(nint query);
        [LibraryImport(CoreFoundation)] internal static partial nint CFDictionaryCreate(nint allocator, nint* keys, nint* values, nint count, nint keyCallBacks, nint valueCallBacks);
        [LibraryImport(CoreFoundation)] internal static partial nint CFStringCreateWithBytes(nint allocator, byte[] bytes, nint length, uint encoding, [MarshalAs(UnmanagedType.I1)] bool external);
        [LibraryImport(CoreFoundation)] internal static partial nint CFDataGetLength(nint data);
        [LibraryImport(CoreFoundation)] internal static partial nint CFDataGetBytePtr(nint data);
        [LibraryImport(CoreFoundation)] internal static partial void CFRelease(nint value);
        internal static nint CFDataCreate(nint allocator, ReadOnlySpan<byte> bytes) { fixed (byte* pointer = bytes) return CFDataCreateRaw(allocator, pointer, bytes.Length); }
        [LibraryImport(CoreFoundation, EntryPoint = "CFDataCreate")] private static partial nint CFDataCreateRaw(nint allocator, byte* bytes, nint length);
    }
}
