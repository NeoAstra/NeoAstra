// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using NeoWebView.Interop;
using NeoWebView.Interop.Generated;

namespace NeoWebView;

/// <summary>Represents browser identity, cookies, and browsing-data storage.</summary>
public sealed unsafe class NeoProfile : IAsyncDisposable
{
    private readonly SafeProfileHandle _handle;
    private int _disposed;

    internal NeoProfile(NeoEnvironment environment, SafeProfileHandle handle, bool isEphemeral)
    {
        Environment = environment;
        _handle = handle;
        IsEphemeral = isEphemeral;
    }

    /// <summary>Gets whether the profile avoids persistent browser storage.</summary>
    public bool IsEphemeral { get; }

    /// <summary>Gets cookies matching an absolute URI.</summary>
    /// <param name="uri">The URI whose cookies are requested.</param>
    /// <param name="cancellationToken">Cancels the managed wait and requests native cancellation.</param>
    /// <returns>A read-only cookie list.</returns>
    public ValueTask<IReadOnlyList<NeoCookie>> GetCookiesAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateAbsoluteUri(uri, nameof(uri));
        cancellationToken.ThrowIfCancellationRequested();
        using var nativeUri = new Utf8String(uri.AbsoluteUri);
        var operation = new NativeOperation<IReadOnlyList<NeoCookie>>(cancellationToken, "get cookies");
        NativeMethods.neo_webview_operation_t nativeOperation = default;
        NativeMethods.neo_webview_error_t error = default;
        NativeMethods.neo_webview_result_t result;
        try
        {
            result = NativeMethods.neo_webview_profile_get_cookies_async(
                NativeHandle,
                nativeUri.View,
                (delegate* unmanaged[Cdecl]<void*, NativeMethods.neo_webview_result_t, NativeMethods.neo_webview_buffer_t, NativeMethods.neo_webview_error_t, void>)&CookiesCompleted,
                (void*)operation.Context,
                &nativeOperation,
                &error);
        }
        catch (Exception ex)
        {
            operation.FailStart(ex);
            return operation.ValueTask;
        }

        CompleteStart(operation, nativeOperation, result, error, "get cookies", cancellationToken);
        return operation.ValueTask;
    }

    /// <summary>Sets or replaces a cookie.</summary>
    /// <param name="cookie">The cookie to set.</param>
    /// <param name="cancellationToken">Cancels the managed wait and requests native cancellation.</param>
    /// <returns>A task completed by the native backend.</returns>
    public ValueTask SetCookieAsync(NeoCookie cookie, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cookie);
        cookie.Validate();
        return StartCookieOperation(cookie, delete: false, cancellationToken);
    }

    /// <summary>Deletes a matching cookie.</summary>
    /// <param name="cookie">The cookie identity to delete.</param>
    /// <param name="cancellationToken">Cancels the managed wait and requests native cancellation.</param>
    /// <returns>A task completed by the native backend.</returns>
    public ValueTask DeleteCookieAsync(NeoCookie cookie, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cookie);
        cookie.Validate();
        return StartCookieOperation(cookie, delete: true, cancellationToken);
    }

    /// <summary>Clears selected browser-data categories.</summary>
    /// <param name="kinds">The categories to clear.</param>
    /// <param name="timeRange">An optional inclusive time range.</param>
    /// <param name="cancellationToken">Cancels the managed wait and requests native cancellation.</param>
    /// <returns>A task completed by the native backend.</returns>
    public ValueTask ClearDataAsync(NeoBrowsingDataKinds kinds, NeoTimeRange? timeRange = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (kinds == NeoBrowsingDataKinds.None)
        {
            throw new ArgumentOutOfRangeException(nameof(kinds), "At least one browsing-data kind is required.");
        }

        const NeoBrowsingDataKinds known = NeoBrowsingDataKinds.Cookies | NeoBrowsingDataKinds.Cache |
            NeoBrowsingDataKinds.LocalStorage | NeoBrowsingDataKinds.IndexedDb | NeoBrowsingDataKinds.ServiceWorkers |
            NeoBrowsingDataKinds.Permissions | NeoBrowsingDataKinds.DownloadHistory;
        if (kinds != NeoBrowsingDataKinds.All && (kinds & ~known) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(kinds));
        }

        timeRange?.Validate();
        var start = timeRange?.Start?.ToUnixTimeMilliseconds() ?? long.MinValue;
        var end = timeRange?.End?.ToUnixTimeMilliseconds() ?? long.MaxValue;
        var operation = new NativeOperation<bool>(cancellationToken, "clear browsing data");
        NativeMethods.neo_webview_operation_t nativeOperation = default;
        NativeMethods.neo_webview_error_t error = default;
        NativeMethods.neo_webview_result_t result;
        try
        {
            result = NativeMethods.neo_webview_profile_clear_data_async(
                NativeHandle,
                (ulong)kinds,
                start,
                end,
                (delegate* unmanaged[Cdecl]<void*, NativeMethods.neo_webview_result_t, NativeMethods.neo_webview_error_t, void>)&Completion,
                (void*)operation.Context,
                &nativeOperation,
                &error);
        }
        catch (Exception ex)
        {
            operation.FailStart(ex);
            return new ValueTask(operation.ValueTask.AsTask());
        }

        CompleteStart(operation, nativeOperation, result, error, "clear browsing data", cancellationToken);
        return new ValueTask(operation.ValueTask.AsTask());
    }

    /// <summary>Releases the native profile reference.</summary>
    /// <returns>A completed value task.</returns>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _handle.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    internal NeoEnvironment Environment { get; }

    internal NativeMethods.neo_webview_profile_t NativeHandle
    {
        get
        {
            ThrowIfDisposed();
            return new(_handle.DangerousGetHandle());
        }
    }

    private ValueTask StartCookieOperation(NeoCookie cookie, bool delete, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        using var name = new Utf8String(cookie.Name);
        using var value = new Utf8String(cookie.Value);
        using var domain = new Utf8String(cookie.Domain);
        using var path = new Utf8String(cookie.Path);
        var raw = new NativeMethods.neo_webview_cookie
        {
            size = (uint)sizeof(NativeMethods.neo_webview_cookie),
            version = 1,
            name = name.View,
            value = value.View,
            domain = domain.View,
            path = path.View,
            expires_unix_ms = cookie.Expires?.ToUnixTimeMilliseconds() ?? 0,
            flags = (cookie.IsSecure ? 1u : 0u) | (cookie.IsHttpOnly ? 2u : 0u) | (cookie.IsSession ? 4u : 0u),
            same_site = (uint)cookie.SameSite,
        };
        var nativeCookie = new NativeMethods.neo_webview_cookie_t(raw);
        var operationName = delete ? "delete cookie" : "set cookie";
        var operation = new NativeOperation<bool>(cancellationToken, operationName);
        NativeMethods.neo_webview_operation_t nativeOperation = default;
        NativeMethods.neo_webview_error_t error = default;
        NativeMethods.neo_webview_result_t result;
        try
        {
            result = delete
                ? NativeMethods.neo_webview_profile_delete_cookie_async(NativeHandle, &nativeCookie, (delegate* unmanaged[Cdecl]<void*, NativeMethods.neo_webview_result_t, NativeMethods.neo_webview_error_t, void>)&Completion, (void*)operation.Context, &nativeOperation, &error)
                : NativeMethods.neo_webview_profile_set_cookie_async(NativeHandle, &nativeCookie, (delegate* unmanaged[Cdecl]<void*, NativeMethods.neo_webview_result_t, NativeMethods.neo_webview_error_t, void>)&Completion, (void*)operation.Context, &nativeOperation, &error);
        }
        catch (Exception ex)
        {
            operation.FailStart(ex);
            return new ValueTask(operation.ValueTask.AsTask());
        }

        CompleteStart(operation, nativeOperation, result, error, operationName, cancellationToken);
        return new ValueTask(operation.ValueTask.AsTask());
    }

    private static void CompleteStart<T>(NativeOperation<T> operation, NativeMethods.neo_webview_operation_t nativeOperation, NativeMethods.neo_webview_result_t result, NativeMethods.neo_webview_error_t error, string name, CancellationToken cancellationToken)
    {
        if (NativeError.Code(result) != NeoErrorCode.Success)
        {
            var info = NativeError.Read(NativeError.Code(result), error.Handle);
            if (error.Handle != 0) new SafeErrorHandle(error.Handle).Dispose();
            operation.FailStart(NativeError.CreateException(info, name, cancellationToken));
            return;
        }

        operation.AttachOperation(nativeOperation.Handle);
    }

    private static void ValidateAbsoluteUri(Uri? uri, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(uri, parameterName);
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException("An absolute URI is required.", parameterName);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Completion(void* context, NativeMethods.neo_webview_result_t result, NativeMethods.neo_webview_error_t error)
    {
        try
        {
            var operation = NativeOperation.Get<bool>(context);
            if (operation is null) return;
            if (NativeError.Code(result) == NeoErrorCode.Success)
            {
                operation.Complete(true);
            }
            else
            {
                operation.Fail(NativeError.CreateException(
                    NativeError.Read(NativeError.Code(result), error.Handle),
                    operation.Owner as string ?? "profile operation"));
            }
        }
        catch (Exception ex)
        {
            NativeOperation.Get<bool>(context)?.Fail(ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CookiesCompleted(void* context, NativeMethods.neo_webview_result_t result, NativeMethods.neo_webview_buffer_t buffer, NativeMethods.neo_webview_error_t error)
    {
        SafeBufferHandle? bufferHandle = buffer.Handle == 0 ? null : new SafeBufferHandle(buffer.Handle);
        try
        {
            var operation = NativeOperation.Get<IReadOnlyList<NeoCookie>>(context);
            if (operation is null) return;
            if (NativeError.Code(result) != NeoErrorCode.Success)
            {
                operation.Fail(NativeError.CreateException(NativeError.Read(NativeError.Code(result), error.Handle), "get cookies"));
                return;
            }

            operation.Complete(ParseCookies(buffer));
        }
        catch (Exception ex)
        {
            NativeOperation.Get<IReadOnlyList<NeoCookie>>(context)?.Fail(ex);
        }
        finally
        {
            bufferHandle?.Dispose();
        }
    }

    private static IReadOnlyList<NeoCookie> ParseCookies(NativeMethods.neo_webview_buffer_t buffer)
    {
        if (buffer.Handle == 0)
        {
            return Array.Empty<NeoCookie>();
        }

        var length = NativeMethods.neo_webview_buffer_get_length(buffer);
        if (length == 0)
        {
            return Array.Empty<NeoCookie>();
        }

        if (length > int.MaxValue)
        {
            throw new InvalidDataException("The native cookie buffer is too large.");
        }

        var data = NativeMethods.neo_webview_buffer_get_data(buffer);
        if (data is null)
        {
            throw new InvalidDataException("The native cookie buffer has no data.");
        }

        var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(data, checked((int)length)));
        using var document = JsonDocument.ParseValue(ref reader);
        if (reader.Read())
        {
            throw new InvalidDataException("The native cookie buffer contains trailing JSON data.");
        }
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The native cookie buffer is not a JSON array.");
        }

        var cookies = new List<NeoCookie>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var cookie = new NeoCookie(
                element.GetProperty("name").GetString() ?? string.Empty,
                element.GetProperty("value").GetString() ?? string.Empty,
                element.GetProperty("domain").GetString() ?? string.Empty,
                element.TryGetProperty("path", out var path) ? path.GetString() ?? "/" : "/")
            {
                IsSecure = element.TryGetProperty("secure", out var secure) && secure.GetBoolean(),
                IsHttpOnly = element.TryGetProperty("httpOnly", out var httpOnly) && httpOnly.GetBoolean(),
                SameSite = element.TryGetProperty("sameSite", out var sameSite) ? (NeoCookieSameSite)sameSite.GetInt32() : NeoCookieSameSite.Unspecified,
            };
            if (element.TryGetProperty("expiresUnixMs", out var expires) && expires.ValueKind == JsonValueKind.Number)
            {
                cookie.Expires = DateTimeOffset.FromUnixTimeMilliseconds(expires.GetInt64());
            }

            cookie.Validate();
            cookies.Add(cookie);
        }

        return cookies.AsReadOnly();
    }
}
