// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using NeoAstra.Interop;
using NeoAstra.Interop.Generated;

namespace NeoAstra;

/// <summary>Identifies the browser resource category of a custom-scheme request.</summary>
public enum NeoResourceKind
{
    /// <summary>An unclassified resource.</summary>
    Other,
    /// <summary>A document.</summary>
    Document,
    /// <summary>A stylesheet.</summary>
    Stylesheet,
    /// <summary>An image.</summary>
    Image,
    /// <summary>Audio or video media.</summary>
    Media,
    /// <summary>A font.</summary>
    Font,
    /// <summary>A script.</summary>
    Script,
    /// <summary>An XMLHttpRequest request.</summary>
    XmlHttpRequest,
    /// <summary>A Fetch API request.</summary>
    Fetch,
    /// <summary>A text track.</summary>
    TextTrack,
    /// <summary>An event source.</summary>
    EventSource,
    /// <summary>A WebSocket request.</summary>
    WebSocket,
    /// <summary>A web application manifest.</summary>
    Manifest,
}

/// <summary>Describes a request dispatched to a custom-scheme resource provider.</summary>
/// <param name="Uri">The requested URI.</param>
/// <param name="Method">The HTTP-style method.</param>
/// <param name="Headers">The request headers.</param>
/// <param name="InitiatingOrigin">The initiating origin when exposed by the backend.</param>
/// <param name="Kind">The browser resource category.</param>
/// <param name="IsMainFrame">Whether the backend identified a main-document request.</param>
/// <param name="Body">The request body when exposed by the backend.</param>
public sealed record NeoResourceRequest(
    Uri Uri,
    string Method,
    IReadOnlyDictionary<string, string> Headers,
    Uri? InitiatingOrigin,
    NeoResourceKind Kind,
    bool IsMainFrame,
    ReadOnlyMemory<byte> Body);

/// <summary>Produces responses for one registered custom URI scheme.</summary>
public interface INeoResourceProvider
{
    /// <summary>Resolves a custom-scheme request synchronously.</summary>
    /// <param name="request">The request to resolve.</param>
    /// <returns>A response, or <see langword="null"/> for a 404 response.</returns>
    /// <remarks>The callback runs on the native browser UI thread and should not block on unrelated work.</remarks>
    NeoResourceResponse? GetResponse(NeoResourceRequest request);
}

/// <summary>Describes an in-memory, file-backed, or empty custom-scheme response.</summary>
public sealed class NeoResourceResponse
{
    private const int MaximumBufferedBodySize = 64 * 1024 * 1024;
    private const int MaximumHeaderSize = 1024 * 1024;
    private const int MaximumMetadataSize = 32 * 1024;

    private NeoResourceResponse(int statusCode, string reasonPhrase, string? mimeType,
        IReadOnlyDictionary<string, string>? headers, ReadOnlyMemory<byte> bytes, string? filePath, long? contentLength)
    {
        if (statusCode is < 100 or > 599) throw new ArgumentOutOfRangeException(nameof(statusCode));
        ArgumentNullException.ThrowIfNull(reasonPhrase);
        ValidateHeaderText(reasonPhrase, nameof(reasonPhrase));
        if (mimeType is not null) ValidateHeaderText(mimeType, nameof(mimeType));
        if (contentLength < 0) throw new ArgumentOutOfRangeException(nameof(contentLength));
        if (bytes.Length > MaximumBufferedBodySize) throw new ArgumentOutOfRangeException(nameof(bytes), "Buffered resource responses must not exceed 64 MiB; use a file-backed response for larger content.");
        if (filePath is not null && bytes.Length != 0) throw new ArgumentException("A resource response cannot contain both bytes and a file path.");
        if (filePath is not null && !Path.IsPathFullyQualified(filePath)) throw new ArgumentException("A resource file path must be fully qualified.", nameof(filePath));

        var copiedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var encodedHeaderSize = 0;
        if (headers is not null)
        {
            foreach (var pair in headers)
            {
                if (string.IsNullOrEmpty(pair.Key) || !pair.Key.All(IsHeaderNameCharacter)) throw new ArgumentException("A response header name is invalid.", nameof(headers));
                ValidateHeaderText(pair.Value, nameof(headers));
                encodedHeaderSize = checked(encodedHeaderSize + Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value) + 4);
                if (encodedHeaderSize > MaximumHeaderSize) throw new ArgumentException("Response headers must not exceed 1 MiB when serialized as UTF-8.", nameof(headers));
                copiedHeaders.Add(pair.Key, pair.Value);
            }
        }

        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        MimeType = mimeType;
        Headers = copiedHeaders;
        Bytes = bytes;
        FilePath = filePath;
        ContentLength = contentLength ?? (filePath is null ? bytes.Length : null);
    }

    /// <summary>Gets the HTTP-style status code.</summary>
    public int StatusCode { get; }
    /// <summary>Gets the HTTP-style reason phrase.</summary>
    public string ReasonPhrase { get; }
    /// <summary>Gets the optional MIME type.</summary>
    public string? MimeType { get; }
    /// <summary>Gets additional response headers.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }
    /// <summary>Gets the in-memory response bytes.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }
    /// <summary>Gets the fully qualified response file path.</summary>
    public string? FilePath { get; }
    /// <summary>Gets the known content length.</summary>
    public long? ContentLength { get; }

    /// <summary>Creates a successful in-memory response.</summary>
    /// <param name="bytes">The response body.</param>
    /// <param name="mimeType">The MIME type.</param>
    /// <returns>The response.</returns>
    /// <exception cref="ArgumentException"><paramref name="mimeType"/> is invalid or exceeds the metadata limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="mimeType"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> exceeds the 64 MiB buffered-response limit.</exception>
    public static NeoResourceResponse FromBytes(ReadOnlyMemory<byte> bytes, string mimeType)
    {
        ArgumentNullException.ThrowIfNull(mimeType);
        return new(200, "OK", mimeType, null, bytes, null, bytes.Length);
    }

    /// <summary>Creates a successful file-backed response.</summary>
    /// <param name="filePath">A fully qualified file path.</param>
    /// <param name="mimeType">The MIME type.</param>
    /// <returns>The response.</returns>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is not fully qualified, or <paramref name="mimeType"/> is invalid or exceeds the metadata limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> or <paramref name="mimeType"/> is <see langword="null"/>.</exception>
    public static NeoResourceResponse FromFile(string filePath, string mimeType)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(mimeType);
        var fullPath = Path.GetFullPath(filePath);
        return new(200, "OK", mimeType, null, default, fullPath, new FileInfo(fullPath).Length);
    }

    /// <summary>Creates an empty response.</summary>
    /// <param name="statusCode">The HTTP-style status code.</param>
    /// <param name="reasonPhrase">The reason phrase.</param>
    /// <returns>The response.</returns>
    /// <exception cref="ArgumentException"><paramref name="reasonPhrase"/> is invalid or exceeds the metadata limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="reasonPhrase"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="statusCode"/> is outside 100 through 599.</exception>
    public static NeoResourceResponse Empty(int statusCode, string reasonPhrase)
        => new(statusCode, reasonPhrase, null, null, default, null, 0);

    internal static NeoResourceResponse CreateFile(string filePath, string mimeType, long contentLength, IReadOnlyDictionary<string, string>? headers)
        => new(200, "OK", mimeType, headers, default, filePath, contentLength);

    internal static NeoResourceResponse CreateEmpty(int statusCode, string reasonPhrase, long contentLength, IReadOnlyDictionary<string, string>? headers)
        => new(statusCode, reasonPhrase, null, headers, default, null, contentLength);

    internal static NeoResourceResponse CreateEmpty(int statusCode, string reasonPhrase, long contentLength, IReadOnlyDictionary<string, string>? headers, string mimeType)
        => new(statusCode, reasonPhrase, mimeType, headers, default, null, contentLength);

    private static void ValidateHeaderText(string value, string parameterName)
    {
        if (Encoding.UTF8.GetByteCount(value) > MaximumMetadataSize)
        {
            throw new ArgumentException("Response metadata must not exceed 32 KiB when UTF-8 encoded.", parameterName);
        }
        if (value.Any(static character => (character < ' ' && character != '\t') || character == '\x7f'))
        {
            throw new ArgumentException("Response metadata must not contain unsafe control characters.", parameterName);
        }
    }

    private static bool IsHeaderNameCharacter(char character)
        => (character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z') ||
           (character >= '0' && character <= '9') || "!#$%&'*+-.^_`|~".Contains(character);
}

/// <summary>Securely serves files rooted beneath one application asset directory.</summary>
public sealed class NeoDirectoryResourceProvider : INeoResourceProvider
{
    private static readonly IReadOnlyDictionary<string, string> MimeTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".css"] = "text/css; charset=utf-8",
        [".gif"] = "image/gif",
        [".html"] = "text/html; charset=utf-8",
        [".ico"] = "image/x-icon",
        [".jpeg"] = "image/jpeg",
        [".jpg"] = "image/jpeg",
        [".js"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".png"] = "image/png",
        [".svg"] = "image/svg+xml",
        [".txt"] = "text/plain; charset=utf-8",
        [".wasm"] = "application/wasm",
        [".webp"] = "image/webp",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".xml"] = "application/xml; charset=utf-8",
    };

    private readonly string _root;
    private readonly string _rootPrefix;
    private readonly StringComparison _pathComparison;

    /// <summary>Creates a provider that rejects symbolic links and directory junctions.</summary>
    /// <param name="rootDirectory">The application asset root.</param>
    /// <exception cref="ArgumentException"><paramref name="rootDirectory"/> is empty or is itself a symbolic link or reparse point.</exception>
    /// <exception cref="DirectoryNotFoundException">The root does not exist.</exception>
    public NeoDirectoryResourceProvider(string rootDirectory) : this(rootDirectory, false) { }

    /// <summary>Creates a provider with an explicit symbolic-link policy.</summary>
    /// <param name="rootDirectory">The application asset root.</param>
    /// <param name="followSymbolicLinks">Whether files reached through symbolic links or reparse points are allowed.</param>
    /// <exception cref="ArgumentException"><paramref name="rootDirectory"/> is empty or is a symbolic link/reparse point while <paramref name="followSymbolicLinks"/> is false.</exception>
    /// <exception cref="DirectoryNotFoundException">The root does not exist.</exception>
    public NeoDirectoryResourceProvider(string rootDirectory, bool followSymbolicLinks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException($"The asset root '{_root}' does not exist.");
        if (!followSymbolicLinks && (File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException("The asset root cannot be a symbolic link or reparse point when symbolic-link following is disabled.", nameof(rootDirectory));
        }
        _rootPrefix = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;
        _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        FollowSymbolicLinks = followSymbolicLinks;
    }

    /// <summary>Gets whether symbolic links and directory reparse points may be followed.</summary>
    public bool FollowSymbolicLinks { get; }

    /// <summary>Gets or sets the Cache-Control value applied to successful responses.</summary>
    public string CacheControl { get; set; } = "no-cache";

    /// <inheritdoc />
    public NeoResourceResponse? GetResponse(NeoResourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            return NeoResourceResponse.Empty(405, "Method Not Allowed");
        }

        string decoded;
        try { decoded = Uri.UnescapeDataString(request.Uri.AbsolutePath); }
        catch (UriFormatException) { return NeoResourceResponse.Empty(400, "Bad Request"); }
        if (decoded.Contains('\0') || ContainsEncodedSeparatorOrDot(decoded)) return NeoResourceResponse.Empty(400, "Bad Request");

        decoded = decoded.Replace('\\', '/');
        var segments = decoded.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or "..")) return NeoResourceResponse.Empty(403, "Forbidden");
        if (segments.Length == 0) segments = ["index.html"];

        var candidate = Path.GetFullPath(Path.Combine(_root, Path.Combine(segments)));
        if (!candidate.StartsWith(_rootPrefix, _pathComparison)) return NeoResourceResponse.Empty(403, "Forbidden");
        if (!FollowSymbolicLinks && ContainsReparsePoint(candidate)) return NeoResourceResponse.Empty(403, "Forbidden");
        if (!File.Exists(candidate)) return null;

        var length = new FileInfo(candidate).Length;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cache-Control"] = CacheControl,
            ["Content-Length"] = length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["X-Content-Type-Options"] = "nosniff",
        };
        if (string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            return NeoResourceResponse.CreateEmpty(200, "OK", length, headers);
        }

        var extension = Path.GetExtension(candidate);
        var mimeType = MimeTypes.TryGetValue(extension, out var known) ? known : "application/octet-stream";
        return NeoResourceResponse.CreateFile(candidate, mimeType, length, headers);
    }

    private bool ContainsReparsePoint(string candidate)
    {
        var relative = Path.GetRelativePath(_root, candidate);
        var current = _root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }

    private static bool ContainsEncodedSeparatorOrDot(string value)
        => value.Contains("%2e", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("%5c", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("%00", StringComparison.OrdinalIgnoreCase);
}

internal sealed unsafe class OwnedUtf8String : IDisposable
{
    private byte* _data;
    private int _length;

    internal OwnedUtf8String(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        _length = Encoding.UTF8.GetByteCount(value);
        _data = (byte*)NativeMemory.Alloc((nuint)_length);
        try { Encoding.UTF8.GetBytes(value, new Span<byte>(_data, _length)); }
        catch { NativeMemory.Free(_data); _data = null; _length = 0; throw; }
    }

    internal NativeMethods.neoastra_string_view_t View => new(new NativeMethods.neoastra_string_view
    {
        data = _data,
        length = (ulong)_length,
    });

    public void Dispose()
    {
        NativeMemory.Free(_data);
        _data = null;
        _length = 0;
    }
}

internal sealed unsafe class CustomSchemeMarshaller : IDisposable
{
    private readonly List<OwnedUtf8String> _strings = [];
    private readonly List<nint> _originArrays = [];
    private List<ResourceProviderRegistration>? _registrations = [];
    private NativeMethods.neoastra_custom_scheme_t* _schemes;

    internal CustomSchemeMarshaller(IReadOnlyList<NeoCustomScheme> schemes)
    {
        if (schemes.Count == 0) return;
        try
        {
            _schemes = (NativeMethods.neoastra_custom_scheme_t*)NativeMemory.Alloc(
                checked((nuint)schemes.Count), (nuint)sizeof(NativeMethods.neoastra_custom_scheme_t));
            NativeMemory.Clear(_schemes, checked((nuint)schemes.Count * (nuint)sizeof(NativeMethods.neoastra_custom_scheme_t)));
            for (var index = 0; index < schemes.Count; index++)
            {
                var scheme = schemes[index];
                var name = AddString(scheme.Name);
                var origins = (NativeMethods.neoastra_string_view_t*)null;
                if (scheme.AllowedOrigins.Count != 0)
                {
                    origins = (NativeMethods.neoastra_string_view_t*)NativeMemory.Alloc(
                        checked((nuint)scheme.AllowedOrigins.Count), (nuint)sizeof(NativeMethods.neoastra_string_view_t));
                    _originArrays.Add((nint)origins);
                    for (var origin = 0; origin < scheme.AllowedOrigins.Count; origin++) origins[origin] = AddString(scheme.AllowedOrigins[origin]);
                }

                var registration = new ResourceProviderRegistration(scheme.ResourceProvider!);
                _registrations!.Add(registration);
                var flags = (scheme.HasAuthority ? 1u : 0u) |
                            (scheme.IsSecure ? 2u : 0u) |
                            (scheme.IsCorsEnabled ? 4u : 0u) |
                            (scheme.IsApplicationScheme ? 8u : 0u) |
                            (scheme.SupportsServiceWorkers ? 16u : 0u);
                _schemes[index] = new NativeMethods.neoastra_custom_scheme_t(new NativeMethods.neoastra_custom_scheme
                {
                    size = (uint)sizeof(NativeMethods.neoastra_custom_scheme),
                    version = 1,
                    name = name,
                    flags = flags,
                    allowed_origin_count = (uint)scheme.AllowedOrigins.Count,
                    allowed_origins = origins,
                    resource_provider = (delegate* unmanaged[Cdecl]<void*, NativeMethods.neoastra_resource_request_t*, NativeMethods.neoastra_resource_response_t*, NativeMethods.neoastra_result_t>)&ResourceProviderRegistration.Invoke,
                    resource_provider_context = registration.Context,
                    release_resource_provider_context = (delegate* unmanaged[Cdecl]<void*, void>)&ResourceProviderRegistration.Release,
                });
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal uint Count => _schemes is null ? 0u : checked((uint)_registrations!.Count);
    internal uint Stride => _schemes is null ? 0u : (uint)sizeof(NativeMethods.neoastra_custom_scheme_t);
    internal NativeMethods.neoastra_custom_scheme_t* Schemes => _schemes;

    internal List<ResourceProviderRegistration> TakeRegistrations()
    {
        var registrations = _registrations ?? [];
        _registrations = null;
        return registrations;
    }

    public void Dispose()
    {
        if (_registrations is not null)
        {
            foreach (var registration in _registrations) registration.Dispose();
            _registrations = null;
        }
        foreach (var pointer in _originArrays) NativeMemory.Free((void*)pointer);
        _originArrays.Clear();
        foreach (var text in _strings) text.Dispose();
        _strings.Clear();
        NativeMemory.Free(_schemes);
        _schemes = null;
    }

    private NativeMethods.neoastra_string_view_t AddString(string value)
    {
        var text = new OwnedUtf8String(value);
        try { _strings.Add(text); }
        catch { text.Dispose(); throw; }
        return text.View;
    }
}

internal sealed unsafe class Utf8StringArray : IDisposable
{
    private readonly List<OwnedUtf8String> _strings = [];
    private NativeMethods.neoastra_string_view_t* _views;

    internal Utf8StringArray(IReadOnlyList<string> values)
    {
        if (values.Count == 0) return;
        try
        {
            _views = (NativeMethods.neoastra_string_view_t*)NativeMemory.Alloc(
                checked((nuint)values.Count), (nuint)sizeof(NativeMethods.neoastra_string_view_t));
            foreach (var value in values)
            {
                var text = new OwnedUtf8String(value.TrimEnd('/'));
                _strings.Add(text);
                _views[_strings.Count - 1] = text.View;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal uint Count => checked((uint)_strings.Count);
    internal NativeMethods.neoastra_string_view_t* Views => _views;

    public void Dispose()
    {
        foreach (var text in _strings) text.Dispose();
        _strings.Clear();
        NativeMemory.Free(_views);
        _views = null;
    }
}

internal sealed unsafe class ResourceProviderRegistration : IDisposable
{
    private const ulong MaximumBufferedBodySize = 64UL * 1024UL * 1024UL;
    private const ulong MaximumHeaderSize = 1024UL * 1024UL;
    private const ulong MaximumMetadataSize = 32UL * 1024UL;

    private GCHandle _root;
    private INeoResourceProvider? _provider;

    internal ResourceProviderRegistration(INeoResourceProvider provider)
    {
        _provider = provider;
        _root = GCHandle.Alloc(this);
    }

    internal void* Context => (void*)GCHandle.ToIntPtr(_root);

    public void Dispose()
    {
        if (!_root.IsAllocated) return;
        var root = _root;
        _root = default;
        _provider = null;
        root.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void Release(void* context)
    {
        try { (GCHandle.FromIntPtr((nint)context).Target as ResourceProviderRegistration)?.Dispose(); }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static NativeMethods.neoastra_result_t Invoke(
        void* context,
        NativeMethods.neoastra_resource_request_t* request,
        NativeMethods.neoastra_resource_response_t* response)
    {
        try
        {
            if (context is null || request is null || response is null ||
                request->Value.size < sizeof(NativeMethods.neoastra_resource_request) || request->Value.version != 1 ||
                (request->Value.body_length != 0 && request->Value.body == null) ||
                (uint)request->Value.resource_kind.Value > (uint)NativeMethods.neoastra_resource_kind.NEOASTRA_RESOURCE_MANIFEST)
            {
                return NativeMethods.neoastra_result.NEOASTRA_ERROR_INVALID_ARGUMENT;
            }
            var provider = (GCHandle.FromIntPtr((nint)context).Target as ResourceProviderRegistration)?._provider;
            if (provider is null) return NativeMethods.neoastra_result.NEOASTRA_ERROR_DISPOSED;
            var value = request->Value;
            if (value.uri.Value.length > MaximumMetadataSize || value.method.Value.length > MaximumMetadataSize ||
                value.initiating_origin.Value.length > MaximumMetadataSize || value.headers.Value.length > MaximumHeaderSize ||
                value.body_length > MaximumBufferedBodySize)
            {
                return NativeMethods.neoastra_result.NEOASTRA_ERROR_INVALID_ARGUMENT;
            }
            if (!Uri.TryCreate(Utf8String.Decode(value.uri), UriKind.Absolute, out var uri)) return NativeMethods.neoastra_result.NEOASTRA_ERROR_INVALID_ARGUMENT;
            var body = value.body_length == 0 ? Array.Empty<byte>() : new ReadOnlySpan<byte>(value.body, (int)value.body_length).ToArray();
            var managedRequest = new NeoResourceRequest(
                uri,
                Utf8String.Decode(value.method),
                ParseHeaders(Utf8String.Decode(value.headers)),
                TryParseAbsoluteUri(Utf8String.Decode(value.initiating_origin)),
                (NeoResourceKind)value.resource_kind.Value,
                value.main_frame != 0,
                body);
            var managedResponse = provider.GetResponse(managedRequest) ?? NeoResourceResponse.Empty(404, "Not Found");
            var lease = new NativeResourceResponseLease(managedResponse);
            GCHandle handle;
            try { handle = GCHandle.Alloc(lease); }
            catch { lease.Dispose(); throw; }
            var raw = lease.CreateNative((void*)GCHandle.ToIntPtr(handle));
            *response = new NativeMethods.neoastra_resource_response_t(raw);
            return NativeMethods.neoastra_result.NEOASTRA_OK;
        }
        catch
        {
            return NativeMethods.neoastra_result.NEOASTRA_ERROR_NATIVE_FAILURE;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReleaseResponse(void* context)
    {
        try
        {
            var handle = GCHandle.FromIntPtr((nint)context);
            (handle.Target as NativeResourceResponseLease)?.Dispose();
            handle.Free();
        }
        catch { }
    }

    private static IReadOnlyDictionary<string, string> ParseHeaders(string headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in headers.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            result[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return result;
    }

    private static Uri? TryParseAbsoluteUri(string value)
        => value.Length == 0 ? null : new Uri(value, UriKind.Absolute);

    private sealed class NativeResourceResponseLease : IDisposable
    {
        private readonly OwnedUtf8String? _reason;
        private readonly OwnedUtf8String? _headers;
        private readonly OwnedUtf8String? _mimeType;
        private readonly OwnedUtf8String? _filePath;
        private byte* _bytes;
        private readonly ulong _byteLength;
        private readonly NeoResourceResponse _response;

        internal NativeResourceResponseLease(NeoResourceResponse response)
        {
            _response = response;
            try
            {
                _reason = new OwnedUtf8String(response.ReasonPhrase);
                _headers = new OwnedUtf8String(FormatHeaders(response.Headers));
                _mimeType = new OwnedUtf8String(response.MimeType);
                _filePath = new OwnedUtf8String(response.FilePath);
                if (!response.Bytes.IsEmpty)
                {
                    _byteLength = (ulong)response.Bytes.Length;
                    _bytes = (byte*)NativeMemory.Alloc(checked((nuint)_byteLength));
                    response.Bytes.Span.CopyTo(new Span<byte>(_bytes, response.Bytes.Length));
                }
            }
            catch { Dispose(); throw; }
        }

        internal NativeMethods.neoastra_resource_response CreateNative(void* context)
            => new()
            {
                size = (uint)sizeof(NativeMethods.neoastra_resource_response),
                version = 1,
                status_code = (uint)_response.StatusCode,
                body_kind = _response.FilePath is not null
                    ? NativeMethods.neoastra_resource_body_kind.NEOASTRA_RESOURCE_BODY_FILE
                    : _response.Bytes.IsEmpty
                        ? NativeMethods.neoastra_resource_body_kind.NEOASTRA_RESOURCE_BODY_EMPTY
                        : NativeMethods.neoastra_resource_body_kind.NEOASTRA_RESOURCE_BODY_BYTES,
                reason_phrase = _reason!.View,
                headers = _headers!.View,
                mime_type = _mimeType!.View,
                content_length = _response.ContentLength is { } length ? checked((ulong)length) : ulong.MaxValue,
                bytes = _bytes,
                byte_length = _byteLength,
                file_path = _filePath!.View,
                release_context = context,
                release = (delegate* unmanaged[Cdecl]<void*, void>)&ReleaseResponse,
            };

        public void Dispose()
        {
            NativeMemory.Free(_bytes);
            _bytes = null;
            _reason?.Dispose();
            _headers?.Dispose();
            _mimeType?.Dispose();
            _filePath?.Dispose();
        }

        private static string FormatHeaders(IReadOnlyDictionary<string, string> headers)
        {
            if (headers.Count == 0) return string.Empty;
            var builder = new StringBuilder();
            foreach (var pair in headers) builder.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
            return builder.ToString();
        }
    }
}
