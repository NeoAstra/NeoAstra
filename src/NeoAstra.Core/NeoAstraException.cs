// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;

namespace NeoAstra;

/// <summary>Represents a detailed failure reported by the NeoAstra native runtime.</summary>
public class NeoAstraException : Exception
{
    /// <summary>Initializes an exception from portable and native error details.</summary>
    /// <param name="code">The portable error code.</param>
    /// <param name="message">The error message.</param>
    /// <param name="operation">The operation that failed.</param>
    /// <param name="domain">The backend error domain.</param>
    /// <param name="nativeCode">The backend-native error code.</param>
    public NeoAstraException(NeoErrorCode code, string message, string? operation = null, string? domain = null, long nativeCode = 0)
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

/// <summary>Indicates that the native NeoAstra library could not be loaded or was incompatible.</summary>
/// <remarks>
/// The message lists the paths that were tried with the error of the platform loader for each, the operating
/// system, architecture, and runtime identifier of the process, and what to do. <see cref="Probes"/> and
/// <see cref="Guidance"/> give the same details to code.
/// </remarks>
public sealed class NeoAstraNativeLibraryException : Exception
{
    /// <summary>Initializes a native-library exception.</summary>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="innerException">The underlying loader exception.</param>
    public NeoAstraNativeLibraryException(string message, Exception? innerException = null)
        : this(message, [], [], RuntimeInformation.RuntimeIdentifier, RuntimeInformation.ProcessArchitecture, RuntimeInformation.OSDescription, innerException)
    {
    }

    internal NeoAstraNativeLibraryException(
        string message,
        IReadOnlyList<NeoNativeLibraryProbe> probes,
        IReadOnlyList<string> guidance,
        string runtimeIdentifier,
        Architecture processArchitecture,
        string operatingSystemDescription,
        Exception? innerException)
        : base(message, innerException)
    {
        Probes = probes;
        Guidance = guidance;
        RuntimeIdentifier = runtimeIdentifier;
        ProcessArchitecture = processArchitecture;
        OperatingSystemDescription = operatingSystemDescription;
    }

    /// <summary>Gets the paths that were tried, in order, with the outcome and the platform error of each.</summary>
    /// <remarks>Empty when the failure did not come from probing paths.</remarks>
    public IReadOnlyList<NeoNativeLibraryProbe> Probes { get; }

    /// <summary>Gets the actions that can fix the failure, most specific first.</summary>
    public IReadOnlyList<string> Guidance { get; }

    /// <summary>Gets the runtime identifier of the process, such as <c>win-x64</c> or <c>linux-arm64</c>.</summary>
    public string RuntimeIdentifier { get; }

    /// <summary>Gets the processor architecture of the process.</summary>
    public Architecture ProcessArchitecture { get; }

    /// <summary>Gets the description of the operating system the process runs on.</summary>
    public string OperatingSystemDescription { get; }
}
