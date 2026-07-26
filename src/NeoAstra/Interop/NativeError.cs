// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Security;
using NeoAstra.Interop.Generated;

namespace NeoAstra.Interop;

internal readonly record struct NativeErrorInfo(NeoErrorCode Code, string Message, string? Domain, long NativeCode);

internal static class NativeError
{
    internal static NeoErrorCode Code(NativeMethods.neoastra_result_t result) => (NeoErrorCode)(int)result.Value;

    internal static void ThrowIfFailed(
        NativeMethods.neoastra_result_t result,
        NativeMethods.neoastra_error_t error,
        string operation,
        CancellationToken cancellationToken = default,
        bool ownsError = true)
    {
        var code = Code(result);
        if (code == NeoErrorCode.Success)
        {
            if (ownsError && error.Handle != 0)
            {
                new SafeErrorHandle(error.Handle).Dispose();
            }

            return;
        }

        NativeErrorInfo info;
        try
        {
            info = Read(code, error.Handle);
        }
        finally
        {
            if (ownsError && error.Handle != 0)
            {
                new SafeErrorHandle(error.Handle).Dispose();
            }
        }

        throw CreateException(info, operation, cancellationToken);
    }

    internal static Exception CreateException(NativeErrorInfo info, string operation, CancellationToken cancellationToken = default)
    {
        var message = string.IsNullOrEmpty(info.Message) ? $"NeoAstra operation '{operation}' failed with {info.Code}." : info.Message;
        Exception exception = info.Code switch
        {
            NeoErrorCode.InvalidArgument => new ArgumentException(message),
            NeoErrorCode.InvalidState or NeoErrorCode.NotInitialized or NeoErrorCode.AlreadyInitialized or NeoErrorCode.WrongThread => new InvalidOperationException(message),
            NeoErrorCode.NotSupported => new NotSupportedException(message),
            NeoErrorCode.Canceled => new OperationCanceledException(message, cancellationToken),
            NeoErrorCode.TimedOut => new TimeoutException(message),
            NeoErrorCode.Disposed => new ObjectDisposedException(operation, message),
            NeoErrorCode.Security => new SecurityException(message),
            _ => new NeoAstraException(info.Code, message, operation, info.Domain, info.NativeCode),
        };

        exception.Data[nameof(NeoErrorCode)] = info.Code;
        exception.Data["NeoAstra.Operation"] = operation;
        if (info.Domain is not null)
        {
            exception.Data["NeoAstra.Domain"] = info.Domain;
        }

        exception.Data["NeoAstra.NativeCode"] = info.NativeCode;
        return exception;
    }

    internal static NativeErrorInfo Read(NeoErrorCode fallbackCode, nint error)
    {
        if (error == 0)
        {
            return new NativeErrorInfo(fallbackCode, string.Empty, null, 0);
        }

        var nativeError = new NativeMethods.neoastra_error_t(error);
        var code = Code(NativeMethods.neoastra_error_get_code(nativeError));
        var message = Utf8String.Decode(NativeMethods.neoastra_error_get_message(nativeError));
        var domain = Utf8String.Decode(NativeMethods.neoastra_error_get_domain(nativeError));
        var nativeCode = NativeMethods.neoastra_error_get_native_code(nativeError);
        return new NativeErrorInfo(code == NeoErrorCode.Success ? fallbackCode : code, message, string.IsNullOrEmpty(domain) ? null : domain, nativeCode);
    }
}
