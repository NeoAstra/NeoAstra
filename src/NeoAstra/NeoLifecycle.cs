// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra;

/// <summary>Identifies the deterministic managed application lifecycle state.</summary>
public enum NeoApplicationState
{
    /// <summary>The native application has been created but startup has not begun.</summary>
    Created,
    /// <summary>Startup is running and launch events are being queued.</summary>
    Starting,
    /// <summary>The application accepts normal top-level work.</summary>
    Ready,
    /// <summary>A coalesced quit negotiation is active.</summary>
    QuitRequested,
    /// <summary>Approved windows are being closed in child-before-owner order.</summary>
    ClosingWindows,
    /// <summary>Authority is revoked and services are stopping.</summary>
    Stopping,
    /// <summary>Native and managed teardown has completed.</summary>
    Stopped,
}

/// <summary>Identifies why a window close was requested.</summary>
public enum NeoWindowCloseReason
{
    /// <summary>The user used native window chrome.</summary>
    User,
    /// <summary>The owner window is closing.</summary>
    Owner,
    /// <summary>An approved application quit is closing the window.</summary>
    ApplicationQuit,
    /// <summary>The operating-system session is ending.</summary>
    SessionEnd,
    /// <summary>The platform requires the window to close.</summary>
    System,
    /// <summary>Managed application code requested close.</summary>
    Programmatic,
}

/// <summary>Provides an asynchronous, exactly-once native window close negotiation.</summary>
public sealed class NeoWindowCloseRequest
{
    private int _canceled;

    internal NeoWindowCloseRequest(NeoWindowCloseReason reason, bool canCancel, CancellationToken deadlineToken)
    {
        Reason = reason;
        CanCancel = canCancel;
        DeadlineToken = deadlineToken;
    }

    /// <summary>Gets the portable close reason.</summary>
    public NeoWindowCloseReason Reason { get; }

    /// <summary>Gets whether the platform permits cancellation.</summary>
    public bool CanCancel { get; }

    /// <summary>Gets a token canceled when the native decision deadline expires.</summary>
    public CancellationToken DeadlineToken { get; }

    /// <summary>Gets whether a handler requested cancellation.</summary>
    public bool IsCanceled => Volatile.Read(ref _canceled) != 0;

    /// <summary>Requests cancellation when <see cref="CanCancel"/> is true.</summary>
    public void Cancel()
    {
        if (CanCancel) Interlocked.Exchange(ref _canceled, 1);
    }
}

/// <summary>Identifies why application quit was requested.</summary>
public enum NeoQuitReason
{
    /// <summary>Application code requested normal quit.</summary>
    Programmatic,
    /// <summary>The last window closed under application policy.</summary>
    LastWindowClosed,
    /// <summary>The main window closed under application policy.</summary>
    MainWindowClosed,
    /// <summary>A platform session-end request was observed.</summary>
    SessionEnd,
    /// <summary>The Generic Host requested coordinated shutdown.</summary>
    HostStopping,
    /// <summary>An urgent backend shutdown bypassed cancellation.</summary>
    Forced,
}

/// <summary>Identifies the result of a coalesced quit request.</summary>
public enum NeoQuitResult
{
    /// <summary>An application or window handler canceled quit.</summary>
    Canceled,
    /// <summary>Normal quit was approved and stopping began.</summary>
    Completed,
    /// <summary>Cancellation was unavailable or deliberately bypassed.</summary>
    Forced,
}

/// <summary>Provides application-level quit negotiation data.</summary>
public sealed class NeoQuitRequest
{
    private int _canceled;

    internal NeoQuitRequest(NeoQuitReason reason, int exitCode, bool canCancel, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        Reason = reason;
        ExitCode = exitCode;
        CanCancel = canCancel;
        Deadline = deadline;
        CancellationToken = cancellationToken;
    }

    /// <summary>Gets the reason for quit.</summary>
    public NeoQuitReason Reason { get; }

    /// <summary>Gets the requested process exit code.</summary>
    public int ExitCode { get; }

    /// <summary>Gets whether this platform phase can honor cancellation.</summary>
    public bool CanCancel { get; }

    /// <summary>Gets the bounded UTC deadline for lifecycle work.</summary>
    public DateTimeOffset Deadline { get; }

    /// <summary>Gets the bounded negotiation cancellation token.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Gets whether a handler canceled quit.</summary>
    public bool IsCanceled => Volatile.Read(ref _canceled) != 0;

    /// <summary>Cancels normal quit.</summary>
    public void Cancel()
    {
        if (CanCancel) Interlocked.Exchange(ref _canceled, 1);
    }
}

/// <summary>Configures one application quit request.</summary>
public sealed class NeoQuitOptions
{
    /// <summary>Gets or sets whether every window is preflighted before any window is destroyed.</summary>
    public bool PreflightWindows { get; set; } = true;

    /// <summary>Gets or sets the bounded negotiation timeout.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(Timeout), "Quit timeout must be positive and no more than ten minutes.");
    }
}

/// <summary>Identifies an operating-system or routed process launch reason.</summary>
public enum NeoLaunchReason
{
    /// <summary>The initial process activation.</summary>
    Initial,
    /// <summary>A normal activation or macOS reopen.</summary>
    Activated,
    /// <summary>One or more local files were opened.</summary>
    OpenFiles,
    /// <summary>One or more absolute URLs were opened.</summary>
    OpenUrls,
    /// <summary>A securely authenticated second instance routed activation.</summary>
    SecondInstance,
    /// <summary>The platform session is ending.</summary>
    SessionEnd,
    /// <summary>A plugin supplied a bounded activation.</summary>
    Extension,
}

/// <summary>Immutable, validated launch data delivered in backend arrival order.</summary>
public sealed record NeoLaunchEvent
{
    /// <summary>Creates validated launch data.</summary>
    /// <param name="reason">The launch reason.</param>
    /// <param name="arguments">Bounded command-line arguments without environment data.</param>
    /// <param name="workingDirectory">An absolute working directory, when available.</param>
    /// <param name="files">Absolute file paths.</param>
    /// <param name="urls">Absolute non-file URLs.</param>
    /// <param name="metadata">Optional bounded, non-sensitive platform metadata.</param>
    /// <exception cref="ArgumentException">A value is invalid, relative, contains controls, or exceeds a bound.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is undefined.</exception>
    public NeoLaunchEvent(NeoLaunchReason reason, IReadOnlyList<string>? arguments = null, string? workingDirectory = null,
        IReadOnlyList<string>? files = null, IReadOnlyList<Uri>? urls = null, IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        Reason = reason;
        Arguments = CopyStrings(arguments, 256, 4096, false, nameof(arguments));
        if (workingDirectory is not null && (!Path.IsPathFullyQualified(workingDirectory) || workingDirectory.Length > 4096 || HasControl(workingDirectory)))
            throw new ArgumentException("The working directory must be an absolute bounded path without control characters.", nameof(workingDirectory));
        WorkingDirectory = workingDirectory;
        Files = CopyStrings(files, 256, 4096, true, nameof(files));
        var uriValues = urls?.ToArray() ?? [];
        if (uriValues.Length > 256 || uriValues.Any(static uri => uri is null || !uri.IsAbsoluteUri || uri.IsFile || uri.OriginalString.Length > 4096 || HasControl(uri.OriginalString)))
            throw new ArgumentException("URLs must be bounded absolute non-file URIs.", nameof(urls));
        Urls = Array.AsReadOnly(uriValues);
        var pairs = metadata?.ToArray() ?? [];
        if (pairs.Length > 32 || pairs.Any(static pair => string.IsNullOrEmpty(pair.Key) || pair.Key.Length > 64 || pair.Value is null || pair.Value.Length > 512 || HasControl(pair.Key) || HasControl(pair.Value)))
            throw new ArgumentException("Platform metadata is invalid or exceeds its bounds.", nameof(metadata));
        Metadata = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(pairs.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal));
        Timestamp = DateTimeOffset.UtcNow;
    }

    /// <summary>Gets the reason.</summary>
    public NeoLaunchReason Reason { get; }
    /// <summary>Gets the UTC receipt timestamp.</summary>
    public DateTimeOffset Timestamp { get; }
    /// <summary>Gets the application-assigned monotonic delivery order.</summary>
    public ulong Order { get; internal init; }
    /// <summary>Gets bounded arguments.</summary>
    public IReadOnlyList<string> Arguments { get; }
    /// <summary>Gets the absolute working directory, when supplied.</summary>
    public string? WorkingDirectory { get; }
    /// <summary>Gets absolute file paths.</summary>
    public IReadOnlyList<string> Files { get; }
    /// <summary>Gets absolute non-file URLs.</summary>
    public IReadOnlyList<Uri> Urls { get; }
    /// <summary>Gets bounded non-sensitive metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    private static IReadOnlyList<string> CopyStrings(IReadOnlyList<string>? source, int maximumCount, int maximumLength, bool paths, string parameterName)
    {
        var values = source?.ToArray() ?? [];
        if (values.Length > maximumCount || values.Any(value => string.IsNullOrEmpty(value) || value.Length > maximumLength || HasControl(value) || paths && !Path.IsPathFullyQualified(value)))
            throw new ArgumentException("Launch values are invalid or exceed their bounds.", parameterName);
        return Array.AsReadOnly(values);
    }

    private static bool HasControl(string value) => value.Any(char.IsControl);
}

/// <summary>Provides an application lifecycle state transition.</summary>
public sealed class NeoApplicationStateChangedEventArgs : EventArgs
{
    internal NeoApplicationStateChangedEventArgs(NeoApplicationState previous, NeoApplicationState current)
    {
        Previous = previous;
        Current = current;
    }

    /// <summary>Gets the previous state.</summary>
    public NeoApplicationState Previous { get; }

    /// <summary>Gets the current state.</summary>
    public NeoApplicationState Current { get; }
}
