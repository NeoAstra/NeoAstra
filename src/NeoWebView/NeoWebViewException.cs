// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoWebView;

/// <summary>Represents a detailed failure reported by the NeoWebView native runtime.</summary>
public class NeoWebViewException : Exception
{
    /// <summary>Initializes an exception from portable and native error details.</summary>
    /// <param name="code">The portable error code.</param>
    /// <param name="message">The error message.</param>
    /// <param name="operation">The operation that failed.</param>
    /// <param name="domain">The backend error domain.</param>
    /// <param name="nativeCode">The backend-native error code.</param>
    public NeoWebViewException(NeoErrorCode code, string message, string? operation = null, string? domain = null, long nativeCode = 0)
        : base(message)
    {
        Code = code;
        Operation = operation;
        Domain = domain;
        NativeCode = nativeCode;
    }

    /// <summary>Gets the portable error code.</summary>
    public NeoErrorCode Code { get; }

    /// <summary>Gets the operation that failed.</summary>
    public string? Operation { get; }

    /// <summary>Gets the backend error domain.</summary>
    public string? Domain { get; }

    /// <summary>Gets the backend-native error code.</summary>
    public long NativeCode { get; }
}

/// <summary>Indicates that the native NeoWebView library could not be loaded or was incompatible.</summary>
public sealed class NeoWebViewNativeLibraryException : Exception
{
    /// <summary>Initializes a native-library exception.</summary>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="innerException">The underlying loader exception.</param>
    public NeoWebViewNativeLibraryException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
