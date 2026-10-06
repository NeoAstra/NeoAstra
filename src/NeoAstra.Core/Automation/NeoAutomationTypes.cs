// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra;

/// <summary>Configures browser automation for an application.</summary>
public sealed class NeoAutomationOptions
{
    private TimeSpan _defaultTimeout = TimeSpan.FromSeconds(5);
    private TimeSpan _navigationTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets which views automation exposes as pages, or <see langword="null"/> for every view of the application.</summary>
    /// <remarks>The filter runs on the UI thread once for each view, when automation first sees it.</remarks>
    public Func<NeoAstra, bool>? PageFilter { get; set; }

    /// <summary>Gets or sets the handler that creates the view of a new page, or <see langword="null"/> for the default.</summary>
    /// <remarks>
    /// The default opens a window with a view on the environment and profile of the selected page, without a bridge to
    /// the host. The handler runs on the UI thread and returns a view that it has not navigated yet.
    /// </remarks>
    public Func<NeoAutomationNewPageRequest, CancellationToken, ValueTask<NeoAstra>>? NewPageHandler { get; set; }

    /// <summary>Gets or sets how long an operation waits for an element, a text, or a script, when it is given no timeout of its own.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive or exceeds ten minutes.</exception>
    public TimeSpan DefaultTimeout
    {
        get => _defaultTimeout;
        set => _defaultTimeout = ValidateTimeout(value, nameof(DefaultTimeout));
    }

    /// <summary>Gets or sets how long a navigation waits for its document, when it is given no timeout of its own.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive or exceeds ten minutes.</exception>
    public TimeSpan NavigationTimeout
    {
        get => _navigationTimeout;
        set => _navigationTimeout = ValidateTimeout(value, nameof(NavigationTimeout));
    }

    /// <summary>Gets or sets whether a caller may run its own JavaScript in a page.</summary>
    /// <remarks>
    /// When <see langword="false"/>, <see cref="NeoAutomationPage.EvaluateScriptAsync"/>, the initial script of a navigation, and
    /// navigations to <c>javascript:</c>, <c>data:</c>, and <c>vbscript:</c> addresses are refused. The other operations
    /// still act on the page through NeoAstra's own script.
    /// </remarks>
    public bool AllowScriptEvaluation { get; set; } = true;

    /// <summary>Gets the directories that tool calls may read files from and write files to.</summary>
    /// <remarks>
    /// The list is empty by default, and a tool call with a file path is then refused. It covers the files of
    /// <c>upload_file</c>, the script file of <c>evaluate_script</c>, and every <c>filePath</c> argument. A path is accepted
    /// when, after resolution, it is inside one of these directories; a relative path is resolved against the first one.
    /// </remarks>
    public IList<string> AllowedDirectories { get; } = new List<string>();

    internal NeoAutomationOptions Clone()
    {
        var copy = new NeoAutomationOptions
        {
            PageFilter = PageFilter,
            NewPageHandler = NewPageHandler,
            DefaultTimeout = DefaultTimeout,
            NavigationTimeout = NavigationTimeout,
            AllowScriptEvaluation = AllowScriptEvaluation,
        };
        foreach (var directory in AllowedDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("An allowed directory must not be empty.", nameof(AllowedDirectories));
            copy.AllowedDirectories.Add(Path.GetFullPath(directory));
        }

        return copy;
    }

    private static TimeSpan ValidateTimeout(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(name, value, "The timeout must be positive and at most ten minutes.");
        }

        return value;
    }
}

/// <summary>Describes a page that automation is asked to open.</summary>
/// <param name="Url">The address the page will load.</param>
/// <param name="Background">Whether the page opens without being brought to the front.</param>
/// <param name="IsolatedContext">
/// The name of an isolated browser context, or <see langword="null"/>. Pages of one context share cookies and storage,
/// and pages of different contexts do not.
/// </param>
public sealed record NeoAutomationNewPageRequest(Uri Url, bool Background, string? IsolatedContext);

/// <summary>The error of an automation operation that the page or its state refused.</summary>
/// <remarks>
/// The message is written for the caller of a tool, which is often a language model: it says what to do next where
/// that is known, such as taking a new snapshot.
/// </remarks>
public sealed class NeoAutomationException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="code">A short stable identifier of the failure.</param>
    /// <param name="message">The message for the caller.</param>
    public NeoAutomationException(string code, string message) : base(message)
    {
        ArgumentNullException.ThrowIfNull(code);
        Code = code;
    }

    /// <summary>Initializes the exception with its cause.</summary>
    /// <param name="code">A short stable identifier of the failure.</param>
    /// <param name="message">The message for the caller.</param>
    /// <param name="innerException">The cause.</param>
    public NeoAutomationException(string code, string message, Exception innerException) : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(code);
        Code = code;
    }

    /// <summary>
    /// Gets a short stable identifier of the failure, such as <c>stale</c> for an element that is gone, <c>no-snapshot</c>,
    /// <c>dialog-open</c>, <c>timeout</c>, <c>not-supported</c>, or <c>script-error</c>.
    /// </summary>
    public string Code { get; }
}

/// <summary>A JavaScript dialog that a page has open and that waits for an answer.</summary>
/// <param name="Kind">The kind of dialog.</param>
/// <param name="Message">The message of the dialog.</param>
/// <param name="DefaultText">The initial text of a prompt, or <see langword="null"/>.</param>
public sealed record NeoAutomationDialog(NeoScriptDialogKind Kind, string Message, string? DefaultText)
{
    /// <summary>Gets the name Chrome DevTools uses for the kind: <c>alert</c>, <c>confirm</c>, <c>prompt</c>, or <c>beforeunload</c>.</summary>
    public string TypeName => Kind switch
    {
        NeoScriptDialogKind.Alert => "alert",
        NeoScriptDialogKind.Confirm => "confirm",
        NeoScriptDialogKind.Prompt => "prompt",
        _ => "beforeunload",
    };
}

/// <summary>Describes what an input action led to.</summary>
public sealed class NeoAutomationActionResult
{
    internal NeoAutomationActionResult(Uri? navigatedTo, NeoAutomationDialog? dialog, string? note, bool dialogHandled)
    {
        NavigatedTo = navigatedTo;
        Dialog = dialog;
        Note = note;
        DialogHandled = dialogHandled;
    }

    /// <summary>Gets the address the page moved to during the action, or <see langword="null"/> when it stayed where it was.</summary>
    public Uri? NavigatedTo { get; }

    /// <summary>Gets the dialog that the action opened and that is still open, or <see langword="null"/>.</summary>
    /// <remarks>The page is stopped until <see cref="NeoAutomationPage.HandleDialogAsync"/> answers the dialog.</remarks>
    public NeoAutomationDialog? Dialog { get; }

    /// <summary>Gets whether a dialog was opened and answered during the action, as its options asked.</summary>
    public bool DialogHandled { get; }

    /// <summary>Gets a remark about how the action went that the caller should know, or <see langword="null"/>.</summary>
    public string? Note { get; }
}

/// <summary>One value of a form to fill in.</summary>
/// <param name="Uid">The identifier of the element in the latest snapshot.</param>
/// <param name="Value">
/// The text of a field, the text of an option, or <c>true</c> or <c>false</c> for a checkbox, a radio button, or a switch.
/// </param>
public sealed record NeoAutomationFormValue(string Uid, string Value);

/// <summary>Identifies where a navigation goes.</summary>
public enum NeoAutomationNavigationKind
{
    /// <summary>To an address.</summary>
    Url,
    /// <summary>One entry back in the history.</summary>
    Back,
    /// <summary>One entry forward in the history.</summary>
    Forward,
    /// <summary>To the current document again.</summary>
    Reload,
}

/// <summary>Configures a navigation.</summary>
public sealed class NeoAutomationNavigationOptions
{
    /// <summary>Gets or sets where the navigation goes. The default is <see cref="NeoAutomationNavigationKind.Url"/>.</summary>
    public NeoAutomationNavigationKind Kind { get; set; }

    /// <summary>Gets or sets the address for <see cref="NeoAutomationNavigationKind.Url"/>.</summary>
    public Uri? Url { get; set; }

    /// <summary>Gets or sets whether a reload fetches the document again instead of using the cache.</summary>
    public bool IgnoreCache { get; set; }

    /// <summary>Gets or sets whether a <c>beforeunload</c> dialog of the document being left is accepted. The default is <see langword="true"/>.</summary>
    public bool AcceptBeforeUnload { get; set; } = true;

    /// <summary>Gets or sets a script that runs in the new document before its own scripts, or <see langword="null"/>.</summary>
    public string? InitScript { get; set; }

    /// <summary>Gets or sets how long to wait for the document, or <see langword="null"/> for <see cref="NeoAutomationOptions.NavigationTimeout"/>.</summary>
    public TimeSpan? Timeout { get; set; }
}

/// <summary>Describes how a navigation went.</summary>
public sealed class NeoAutomationNavigationResult
{
    internal NeoAutomationNavigationResult(bool succeeded, string message, Uri? url, bool beforeUnloadHandled)
    {
        Succeeded = succeeded;
        Message = message;
        Url = url;
        BeforeUnloadHandled = beforeUnloadHandled;
    }

    /// <summary>Gets whether the document loaded.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets a sentence that says how the navigation went.</summary>
    public string Message { get; }

    /// <summary>Gets the address of the page after the navigation.</summary>
    public Uri? Url { get; }

    /// <summary>Gets whether a <c>beforeunload</c> dialog was answered during the navigation.</summary>
    public bool BeforeUnloadHandled { get; }
}

/// <summary>Configures the evaluation of a caller's JavaScript in a page.</summary>
public sealed class NeoAutomationEvaluateOptions
{
    /// <summary>
    /// Gets or sets whether the source is a function, which is called and whose result is awaited, or a classic script,
    /// whose completion value is the result. The default is <see langword="true"/>, a function.
    /// </summary>
    /// <remarks>
    /// A function works under any content security policy and may be <c>async</c>. A script is given to the browser engine
    /// as it is: it takes no arguments, a Promise it ends in is not awaited, and its result must be a value the engine can
    /// return.
    /// </remarks>
    public bool IsFunction { get; set; } = true;

    /// <summary>Gets or sets the snapshot identifiers of the elements passed to the function as arguments.</summary>
    public IReadOnlyList<string>? Arguments { get; set; }

    /// <summary>
    /// Gets or sets the answer to a dialog that the script opens: <c>accept</c>, <c>dismiss</c>, or the text entered in a
    /// prompt. The default is <c>accept</c>.
    /// </summary>
    public string? DialogAction { get; set; }

    /// <summary>Gets or sets whether to wait until the page has stopped changing after the script. The default is <see langword="true"/>.</summary>
    public bool WaitForStableDom { get; set; } = true;

    /// <summary>Gets or sets how long to wait for the result, or <see langword="null"/> for <see cref="NeoAutomationOptions.DefaultTimeout"/>.</summary>
    public TimeSpan? Timeout { get; set; }
}

/// <summary>The result of a caller's JavaScript.</summary>
public sealed class NeoAutomationEvaluateResult
{
    internal NeoAutomationEvaluateResult(string? json, NeoAutomationActionResult action)
    {
        Json = json;
        Action = action;
    }

    /// <summary>Gets the result as JSON text, or <see langword="null"/> when the script returned <c>undefined</c>.</summary>
    public string? Json { get; }

    /// <summary>Gets what the script led to in the page.</summary>
    public NeoAutomationActionResult Action { get; }
}

/// <summary>Configures a screenshot.</summary>
public sealed class NeoAutomationScreenshotOptions
{
    /// <summary>Gets or sets the encoding of the image. The default is <see cref="NeoCaptureFormat.Png"/>.</summary>
    public NeoCaptureFormat Format { get; set; }

    /// <summary>Gets or sets the JPEG quality from 1 through 100, or <see langword="null"/> for the backend default.</summary>
    public int? Quality { get; set; }

    /// <summary>Gets or sets the snapshot identifier of the element to capture, or <see langword="null"/> for the page.</summary>
    public string? Uid { get; set; }

    /// <summary>Gets or sets whether the whole document is captured instead of the visible viewport.</summary>
    public bool FullPage { get; set; }
}

/// <summary>A console message, an uncaught error, or a failed resource load of a page.</summary>
public sealed class NeoAutomationConsoleMessage
{
    internal NeoAutomationConsoleMessage(int id, string type, string text, IReadOnlyList<string> arguments, string? url, int line, int column, string? stackTrace, DateTimeOffset timestamp)
    {
        Id = id;
        Type = type;
        Text = text;
        Arguments = arguments;
        Url = url;
        Line = line;
        Column = column;
        StackTrace = stackTrace;
        Timestamp = timestamp;
    }

    /// <summary>Gets the identifier of the message, which is unique for the page and never reused.</summary>
    public int Id { get; }

    /// <summary>Gets the type as Chrome DevTools names it, such as <c>log</c>, <c>warn</c>, <c>error</c>, or <c>startGroup</c>.</summary>
    public string Type { get; }

    /// <summary>Gets the message as the console shows it.</summary>
    public string Text { get; }

    /// <summary>Gets a text preview of each argument of the console call.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Gets the address of the script that logged the message, when known.</summary>
    public string? Url { get; }

    /// <summary>Gets the 1-based line of the call, or zero when unknown.</summary>
    public int Line { get; }

    /// <summary>Gets the 1-based column of the call, or zero when unknown.</summary>
    public int Column { get; }

    /// <summary>Gets the stack trace, one frame per line, when the message has one.</summary>
    public string? StackTrace { get; }

    /// <summary>Gets when the message was logged.</summary>
    public DateTimeOffset Timestamp { get; }
}

/// <summary>A network request of a page, as far as the page itself can observe it.</summary>
/// <remarks>
/// Requests made with <c>fetch</c> and <c>XMLHttpRequest</c> carry their method, headers, status, and text bodies. Other
/// loads are known from resource timing only: their address, kind, timing, and sizes, and their status where the engine
/// reports it.
/// </remarks>
public sealed class NeoAutomationNetworkRequest
{
    internal NeoAutomationNetworkRequest(int id)
    {
        Id = id;
    }

    /// <summary>Gets the identifier of the request, which is unique for the page and never reused.</summary>
    public int Id { get; }

    /// <summary>Gets the HTTP method.</summary>
    public string Method { get; internal set; } = "GET";

    /// <summary>Gets the address of the request.</summary>
    public string Url { get; internal set; } = string.Empty;

    /// <summary>Gets the kind of resource as Chrome DevTools names it, such as <c>document</c>, <c>script</c>, <c>fetch</c>, or <c>xhr</c>.</summary>
    public string ResourceType { get; internal set; } = "other";

    /// <summary>Gets the HTTP status code, or <see langword="null"/> when the request has no response yet or the page cannot see its status.</summary>
    public int? StatusCode { get; internal set; }

    /// <summary>Gets the status as text: the status code, <c>pending</c>, <c>finished</c> for a completed load of unknown status, or the reason of a failure.</summary>
    public string Status { get; internal set; } = "pending";

    /// <summary>Gets the reason of a failed request, or <see langword="null"/>.</summary>
    public string? Failure { get; internal set; }

    /// <summary>Gets the request headers that the page set.</summary>
    public IReadOnlyDictionary<string, string> RequestHeaders { get; internal set; } = EmptyHeaders;

    /// <summary>Gets the response headers that the page may read, or <see langword="null"/>.</summary>
    public IReadOnlyDictionary<string, string>? ResponseHeaders { get; internal set; }

    /// <summary>Gets the request body as text, or <see langword="null"/>.</summary>
    public string? RequestBody { get; internal set; }

    /// <summary>Gets the start of a text response body, or <see langword="null"/>.</summary>
    public string? ResponseBody { get; internal set; }

    /// <summary>Gets whether <see cref="ResponseBody"/> is only the beginning of the body.</summary>
    public bool IsResponseBodyTruncated { get; internal set; }

    /// <summary>Gets the media type of the response, or <see langword="null"/>.</summary>
    public string? MimeType { get; internal set; }

    /// <summary>Gets how long the request took, or <see langword="null"/> while it is pending or when unknown.</summary>
    public TimeSpan? Duration { get; internal set; }

    /// <summary>Gets the number of bytes transferred, or <see langword="null"/> when unknown.</summary>
    public long? TransferSize { get; internal set; }

    /// <summary>Gets when the request started.</summary>
    public DateTimeOffset StartTime { get; internal set; }

    internal static IReadOnlyDictionary<string, string> EmptyHeaders { get; } = new Dictionary<string, string>();

    /// <summary>Gets the document and sequence number under which the page reported the request.</summary>
    internal string Key { get; init; } = string.Empty;
}
