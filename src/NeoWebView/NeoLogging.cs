// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoWebView;

/// <summary>Identifies the severity of a native NeoWebView diagnostic message.</summary>
public enum NeoLogLevel
{
    /// <summary>Highly detailed diagnostic information.</summary>
    Trace,
    /// <summary>Developer-oriented diagnostic information.</summary>
    Debug,
    /// <summary>Normal lifecycle and status information.</summary>
    Information,
    /// <summary>A condition that may require attention.</summary>
    Warning,
    /// <summary>An operation failed.</summary>
    Error,
    /// <summary>A critical failure occurred.</summary>
    Critical,
}

/// <summary>Describes one native diagnostic log message.</summary>
public sealed class NeoLogMessage
{
    internal NeoLogMessage(
        NeoLogLevel level,
        string category,
        string message,
        ulong nativeThreadId,
        ulong timestampNanoseconds,
        long nativeCode,
        ulong objectId)
    {
        Level = level;
        Category = category;
        Message = message;
        NativeThreadId = nativeThreadId;
        TimestampNanoseconds = timestampNanoseconds;
        NativeCode = nativeCode;
        ObjectId = objectId;
    }

    /// <summary>Gets the message severity.</summary>
    public NeoLogLevel Level { get; }

    /// <summary>Gets the native subsystem category.</summary>
    public string Category { get; }

    /// <summary>Gets the UTF-8 diagnostic message decoded as a managed string.</summary>
    public string Message { get; }

    /// <summary>Gets the platform-independent identifier of the native thread that emitted the message.</summary>
    public ulong NativeThreadId { get; }

    /// <summary>Gets the monotonic native timestamp in nanoseconds.</summary>
    public ulong TimestampNanoseconds { get; }

    /// <summary>Gets the optional backend-native error code, or zero when none was supplied.</summary>
    public long NativeCode { get; }

    /// <summary>Gets the associated native object identifier, or zero when no object was associated.</summary>
    public ulong ObjectId { get; }
}
