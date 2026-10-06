// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Diagnostics;
using System.Text.Json;

namespace NeoAstra;

/// <summary>
/// Drives one browser view the way a tool of Chrome DevTools MCP drives a browser tab: text snapshots of the page,
/// input on the elements of a snapshot, navigation, script evaluation, screenshots, and the console and network logs.
/// </summary>
/// <remarks>
/// <para>
/// A page is obtained from <see cref="NeoAutomation"/>. Every member may be called from any thread; the work runs on
/// the application UI thread, one operation of a page at a time.
/// </para>
/// <para>
/// Input is synthesized as DOM events inside the page, in the same way on every browser engine. The events carry
/// <c>isTrusted === false</c>, so a page feature that the browser reserves for a real user gesture, such as opening a
/// file chooser or a popup, does not react to them.
/// </para>
/// </remarks>
public sealed class NeoAutomationPage
{
    private const int PreservedNavigations = 3;
    private const int MaximumLogEntries = 5000;
    private const int UploadChunkSize = 192 * 1024;
    private const long MaximumUploadSize = 64L * 1024 * 1024;
    private static readonly TimeSpan NavigationStartWindow = TimeSpan.FromMilliseconds(100);
    // How long the engine is given to report a navigation that the page itself announced.
    private static readonly TimeSpan AnnouncedNavigationStartWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ActionNavigationTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StableDomTimeout = TimeSpan.FromSeconds(3);

    private readonly NeoAutomation _owner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<NavigationLog> _logs = [new NavigationLog()];
    private TaskCompletionSource _dialogOpened = NewSignal();
    private PendingDialog? _dialog;
    private DialogPolicy? _dialogPolicy;
    private bool _dialogHandled;
    private NeoUserScript? _agentScript;
    private NeoAutomationSnapshot? _snapshot;
    private long _nextJob;
    private int _nextSnapshot;
    private int _nextMessageId = 1;
    private int _nextRequestId = 1;
    private int _detached;

    internal NeoAutomationPage(NeoAutomation owner, NeoAstra view, int id, string? isolatedContext)
    {
        _owner = owner;
        View = view;
        Id = id;
        IsolatedContext = isolatedContext;
    }

    /// <summary>Gets the identifier of the page, which is unique for the automation and never reused.</summary>
    public int Id { get; }

    /// <summary>Gets the view that the page drives.</summary>
    public NeoAstra View { get; }

    /// <summary>Gets the name of the isolated browser context the page was opened in, or <see langword="null"/>.</summary>
    public string? IsolatedContext { get; }

    /// <summary>Gets the address of the page as the view last reported it.</summary>
    public Uri? Url => View.Source;

    /// <summary>Gets the title of the page as the view last reported it.</summary>
    public string Title => View.Title;

    /// <summary>Gets whether the view of the page is gone.</summary>
    public bool IsClosed => Volatile.Read(ref _detached) != 0;

    /// <summary>Gets the JavaScript dialog that the page has open, or <see langword="null"/>.</summary>
    /// <remarks>While a dialog is open, the page runs no script; answer it with <see cref="HandleDialogAsync"/>.</remarks>
    public NeoAutomationDialog? Dialog => Volatile.Read(ref _dialog)?.Info;

    /// <summary>Gets the latest snapshot taken of the page, or <see langword="null"/>.</summary>
    public NeoAutomationSnapshot? LatestSnapshot => Volatile.Read(ref _snapshot);

    // -------------------------------------------------------------------------------------------------------------
    // Snapshot and waiting
    // -------------------------------------------------------------------------------------------------------------

    /// <summary>Takes a text snapshot of the page from its accessibility tree. The snapshot lists the elements with the identifiers that input operations take.</summary>
    /// <param name="verbose">Whether to list every node instead of the ones that matter to a reader.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The snapshot. Always act on the latest one.</returns>
    /// <exception cref="NeoAutomationException">A dialog is open, or the page did not answer.</exception>
    public ValueTask<NeoAutomationSnapshot> TakeSnapshotAsync(bool verbose = false, CancellationToken cancellationToken = default)
        => _owner.InvokeAsync(() => ExclusiveAsync(() => SnapshotCoreAsync(verbose, cancellationToken), cancellationToken), cancellationToken);

    /// <summary>Waits until one of the given texts appears on the page, as rendered text or as the name of an element.</summary>
    /// <param name="texts">The texts to look for. The wait ends when any of them appears.</param>
    /// <param name="timeout">How long to wait, or <see langword="null"/> for <see cref="NeoAutomationOptions.DefaultTimeout"/>.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The text that appeared.</returns>
    /// <exception cref="ArgumentException"><paramref name="texts"/> is empty.</exception>
    /// <exception cref="NeoAutomationException">No text appeared in time, or a dialog is open.</exception>
    public ValueTask<string> WaitForAsync(IReadOnlyList<string> texts, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0 || texts.Any(string.IsNullOrEmpty)) throw new ArgumentException("At least one nonempty text is required.", nameof(texts));
        var limit = ResolveTimeout(timeout, _owner.Options.DefaultTimeout, nameof(timeout));
        return _owner.InvokeAsync(() => ExclusiveAsync(async () =>
        {
            var arguments = NeoAutomationAgent.Json(writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartArray("texts");
                foreach (var text in texts) writer.WriteStringValue(text);
                writer.WriteEndArray();
                writer.WriteNumber("timeout", (long)limit.TotalMilliseconds);
                writer.WriteEndObject();
            });
            // The wait survives a navigation: a new document simply starts it again for the time that is left.
            var clock = Stopwatch.StartNew();
            while (true)
            {
                var remaining = limit - clock.Elapsed;
                var result = await RunJobAsync(job => NeoAutomationAgent.Start(job, "waitForText", arguments), remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancellationToken);
                if (result.IsOk) return result.Value.GetProperty("text").GetString() ?? string.Empty;
                if (result.ErrorCode != "lost" || clock.Elapsed >= limit) throw result.ToException();
            }
        }, cancellationToken), cancellationToken);
    }

    // -------------------------------------------------------------------------------------------------------------
    // Input
    // -------------------------------------------------------------------------------------------------------------

    /// <summary>Clicks an element.</summary>
    /// <param name="uid">The identifier of the element in the latest snapshot.</param>
    /// <param name="doubleClick">Whether to click twice.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What the click led to.</returns>
    /// <exception cref="NeoAutomationException">The element is unknown, gone, disabled, or not visible, or a dialog is open.</exception>
    public ValueTask<NeoAutomationActionResult> ClickAsync(string uid, bool doubleClick = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(uid);
        return ActAsync("click", writer =>
        {
            writer.WriteString("uid", uid);
            writer.WriteBoolean("dblClick", doubleClick);
        }, uid, cancellationToken);
    }

    /// <summary>Clicks at a point of the viewport.</summary>
    /// <param name="x">The horizontal position in CSS pixels from the left edge of the viewport.</param>
    /// <param name="y">The vertical position in CSS pixels from the top edge of the viewport.</param>
    /// <param name="doubleClick">Whether to click twice.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What the click led to.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is not a finite number.</exception>
    /// <exception cref="NeoAutomationException">A dialog is open, or the page did not answer.</exception>
    public ValueTask<NeoAutomationActionResult> ClickAtAsync(double x, double y, bool doubleClick = false, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        return ActAsync("clickAt", writer =>
        {
            writer.WriteNumber("x", x);
            writer.WriteNumber("y", y);
            writer.WriteBoolean("dblClick", doubleClick);
        }, null, cancellationToken);
    }

    /// <summary>Moves the pointer over an element.</summary>
    /// <param name="uid">The identifier of the element in the latest snapshot.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What the move led to.</returns>
    /// <exception cref="NeoAutomationException">The element is unknown, gone, or not visible, or a dialog is open.</exception>
    public ValueTask<NeoAutomationActionResult> HoverAsync(string uid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(uid);
        return ActAsync("hover", writer => writer.WriteString("uid", uid), uid, cancellationToken);
    }

    /// <summary>Types a text into an input or a text area, selects an option of a select element, or sets a checkbox, a radio button, or a switch.</summary>
    /// <param name="uid">The identifier of the element in the latest snapshot.</param>
    /// <param name="value">The text, the text of the option, or <c>true</c> or <c>false</c> for a toggle.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What the change led to.</returns>
    /// <exception cref="NeoAutomationException">The element cannot take the value, or a dialog is open.</exception>
    public ValueTask<NeoAutomationActionResult> FillAsync(string uid, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(uid);
        ArgumentNullException.ThrowIfNull(value);
        return FillFormAsync([new NeoAutomationFormValue(uid, value)], cancellationToken);
    }

    /// <summary>Fills in several form elements in one operation.</summary>
    /// <param name="elements">The elements and their values, in the order to fill them in.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What the changes led to. When an element opens a dialog, the ones after it are not filled in.</returns>
    /// <exception cref="ArgumentException"><paramref name="elements"/> is empty or has an element without an identifier.</exception>
    /// <exception cref="NeoAutomationException">An element cannot take its value, or a dialog is open.</exception>
    public ValueTask<NeoAutomationActionResult> FillFormAsync(IReadOnlyList<NeoAutomationFormValue> elements, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(elements);
        if (elements.Count == 0 || elements.Any(static element => element is null || string.IsNullOrEmpty(element.Uid) || element.Value is null))
        {
            throw new ArgumentException("At least one element with an identifier and a value is required.", nameof(elements));
        }

        var values = elements.ToArray();
        return _owner.InvokeAsync(() => ExclusiveAsync(() => FillCoreAsync(values, cancellationToken), cancellationToken), cancellationToken);
    }

    /// <summary>Drags an element onto another one.</summary>
    /// <param name="fromUid">The identifier of the element to drag.</param>
    /// <param name="toUid">The identifier of the element to drop onto.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What the drag led to.</returns>
    /// <exception cref="NeoAutomationException">An element is unknown, gone, or not visible, or a dialog is open.</exception>
    public ValueTask<NeoAutomationActionResult> DragAsync(string fromUid, string toUid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(fromUid);
        ArgumentException.ThrowIfNullOrEmpty(toUid);
        return ActAsync("drag", writer =>
        {
            writer.WriteString("from", fromUid);
            writer.WriteString("to", toUid);
        }, null, cancellationToken);
    }

    /// <summary>Presses a key or a key combination, such as <c>Enter</c>, <c>Control+A</c>, or <c>Control+Shift+R</c>, on the focused element.</summary>
    /// <param name="key">The key, after the modifiers held with it, joined by plus signs.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What the key press led to.</returns>
    /// <exception cref="NeoAutomationException">The key is not known, or a dialog is open.</exception>
    public ValueTask<NeoAutomationActionResult> PressKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var (main, modifiers) = NeoAutomationKeys.ParseCombination(key);
        return ActAsync("pressKey", writer =>
        {
            writer.WritePropertyName("key");
            main.Write(writer);
            writer.WriteStartArray("modifiers");
            foreach (var modifier in modifiers) modifier.Write(writer);
            writer.WriteEndArray();
        }, null, cancellationToken);
    }

    /// <summary>Types a text with the keyboard into the focused element.</summary>
    /// <param name="text">The text to type.</param>
    /// <param name="submitKey">A key to press after the text, such as <c>Enter</c> or <c>Tab</c>, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What the typing led to.</returns>
    /// <exception cref="NeoAutomationException">The submit key is not known, or a dialog is open.</exception>
    public ValueTask<NeoAutomationActionResult> TypeTextAsync(string text, string? submitKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        NeoAutomationKey? submit = string.IsNullOrEmpty(submitKey) ? null : NeoAutomationKeys.Resolve(submitKey);
        return ActAsync("typeText", writer =>
        {
            writer.WriteString("text", text);
            if (submit is { } definition)
            {
                writer.WritePropertyName("submitKey");
                definition.Write(writer);
            }
        }, null, cancellationToken);
    }

    /// <summary>Gives files to a file input, or to the file input that an element stands for.</summary>
    /// <param name="uid">The identifier of the file input, or of a label or a button that holds one.</param>
    /// <param name="filePaths">The files to give, which are read on the host.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What the change led to.</returns>
    /// <remarks>
    /// The page receives copies of the files through its file input, without a file chooser. The files together may
    /// not exceed 64 MiB.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="filePaths"/> is empty.</exception>
    /// <exception cref="FileNotFoundException">A file does not exist.</exception>
    /// <exception cref="NeoAutomationException">The element has no file input, the files are too large, or a dialog is open.</exception>
    public ValueTask<NeoAutomationActionResult> UploadFileAsync(string uid, IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(uid);
        ArgumentNullException.ThrowIfNull(filePaths);
        if (filePaths.Count == 0 || filePaths.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("At least one file path is required.", nameof(filePaths));
        return _owner.InvokeAsync(() => ExclusiveAsync(() => UploadCoreAsync(uid, filePaths, cancellationToken), cancellationToken), cancellationToken);
    }

    /// <summary>Answers the JavaScript dialog that the page has open.</summary>
    /// <param name="accept">Whether to accept the dialog or to dismiss it.</param>
    /// <param name="promptText">The text to enter in a prompt, or <see langword="null"/> for its default text.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The dialog that was answered.</returns>
    /// <exception cref="NeoAutomationException">No dialog is open.</exception>
    public ValueTask<NeoAutomationDialog> HandleDialogAsync(bool accept, string? promptText = null, CancellationToken cancellationToken = default)
        => _owner.InvokeAsync(() =>
        {
            var pending = _dialog ?? throw new NeoAutomationException("no-dialog", "No open dialog found");
            ClearDialog(pending);
            var decision = !accept ? NeoScriptDialogDecision.Cancel
                : pending.Info.Kind == NeoScriptDialogKind.Prompt ? NeoScriptDialogDecision.AcceptPrompt(promptText ?? pending.Info.DefaultText ?? string.Empty)
                : NeoScriptDialogDecision.Accept;
            pending.Completion.TrySetResult(decision);
            return Task.FromResult(pending.Info);
        }, cancellationToken);

    // -------------------------------------------------------------------------------------------------------------
    // Navigation and scripts
    // -------------------------------------------------------------------------------------------------------------

    /// <summary>Goes to an address, back or forward in the history, or reloads the page, and waits for the document.</summary>
    /// <param name="options">Where to go and how to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>How the navigation went. A page that fails to load is reported in the result, not as an exception.</returns>
    /// <exception cref="ArgumentException">The options name no address for a navigation to one, or a relative one.</exception>
    /// <exception cref="NeoAutomationException">The navigation is not allowed by the automation options.</exception>
    public ValueTask<NeoAutomationNavigationResult> NavigateAsync(NeoAutomationNavigationOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Kind)) throw new ArgumentOutOfRangeException(nameof(options), options.Kind, "The navigation kind is not defined.");
        if (options.Kind == NeoAutomationNavigationKind.Url)
        {
            if (options.Url is null) throw new ArgumentException("A URL is required for navigation of type=url.", nameof(options));
            _owner.ValidateNavigationUrl(options.Url);
        }

        if (options.InitScript is not null && !_owner.Options.AllowScriptEvaluation)
        {
            throw new NeoAutomationException("not-allowed", "Script evaluation is turned off for this application, so a navigation cannot take an initial script.");
        }

        var timeout = ResolveTimeout(options.Timeout, _owner.Options.NavigationTimeout, nameof(options));
        return _owner.InvokeAsync(() => ExclusiveAsync(() => NavigateCoreAsync(options, timeout, cancellationToken), cancellationToken), cancellationToken);
    }

    /// <summary>Runs a caller's JavaScript in the page and returns its result as JSON text.</summary>
    /// <param name="source">
    /// A function, such as <c>() =&gt; document.title</c> or <c>async (el) =&gt; el.innerText</c>, or a classic script when
    /// <see cref="NeoAutomationEvaluateOptions.IsFunction"/> is <see langword="false"/>.
    /// </param>
    /// <param name="options">The evaluation options, or <see langword="null"/> for a function without arguments.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The result, which has to be serializable as JSON, and what the script led to.</returns>
    /// <exception cref="ArgumentException">A script is given arguments.</exception>
    /// <exception cref="NeoAutomationException">Script evaluation is turned off, the script threw or did not complete in time, or a dialog is open.</exception>
    public ValueTask<NeoAutomationEvaluateResult> EvaluateScriptAsync(string source, NeoAutomationEvaluateOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new NeoAutomationEvaluateOptions();
        if (!options.IsFunction && options.Arguments is { Count: > 0 }) throw new ArgumentException("args cannot be used when format is \"script\".", nameof(options));
        if (!_owner.Options.AllowScriptEvaluation) throw new NeoAutomationException("not-allowed", "Script evaluation is turned off for this application.");
        var timeout = ResolveTimeout(options.Timeout, _owner.Options.DefaultTimeout, nameof(options));
        return _owner.InvokeAsync(() => ExclusiveAsync(() => EvaluateCoreAsync(source, options, timeout, cancellationToken), cancellationToken), cancellationToken);
    }

    /// <summary>Takes a screenshot of the visible page, of the whole document, or of one element.</summary>
    /// <param name="options">The screenshot options, or <see langword="null"/> for a PNG image of the visible page.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The encoded image.</returns>
    /// <exception cref="ArgumentException">The options ask for both an element and the whole document.</exception>
    /// <exception cref="NotSupportedException">The active backend cannot capture a whole document.</exception>
    /// <exception cref="NeoAutomationException">The element is unknown or gone, the view produced no image in time, or a dialog is open.</exception>
    public ValueTask<NeoCapturedImage> TakeScreenshotAsync(NeoAutomationScreenshotOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new NeoAutomationScreenshotOptions();
        if (options.Uid is not null && options.FullPage) throw new ArgumentException("Providing both \"uid\" and \"fullPage\" is not allowed.", nameof(options));
        var capture = new NeoCaptureOptions { Format = options.Format, Quality = options.Quality, FullPage = options.FullPage };
        capture.Validate();
        return _owner.InvokeAsync(() => ExclusiveAsync(() => ScreenshotCoreAsync(options.Uid, capture, cancellationToken), cancellationToken), cancellationToken);
    }

    /// <summary>Resizes the window of the page so that the page has the given size.</summary>
    /// <param name="width">The width of the page in logical units.</param>
    /// <param name="height">The height of the page in logical units.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes after the window was resized.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive.</exception>
    /// <exception cref="NeoAutomationException">The view is hosted by a window that NeoAstra does not own.</exception>
    public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return new ValueTask(_owner.InvokeAsync(() => ExclusiveAsync(async () =>
        {
            ThrowIfClosed();
            var window = View.OwnedWindow ?? throw new NeoAutomationException("not-supported", "The view of this page is hosted by a window that NeoAstra does not own, so it cannot be resized.");
            if (window.State != NeoWindowState.Normal) window.State = NeoWindowState.Normal;

            // The size is the one of the page, and a window can be larger than its page: a GTK window draws its title
            // bar inside its own size, and an application may lay other things out around the view. What the window
            // has beyond the page now is what it keeps beyond the page it is asked for.
            var extraWidth = 0;
            var extraHeight = 0;
            if (View.ZoomFactor == 1d && await ReadViewportAsync(cancellationToken) is { Width: > 0, Height: > 0 } before)
            {
                var client = window.ClientSize;
                extraWidth = Math.Max(client.Width - before.Width, 0);
                extraHeight = Math.Max(client.Height - before.Height, 0);
            }

            window.ClientSize = new NeoSize(width + extraWidth, height + extraHeight);
            // The page learns its new size from a resize that the engine delivers a moment later.
            var clock = Stopwatch.StartNew();
            do
            {
                await Task.Delay(25, cancellationToken);
            }
            while (clock.ElapsedMilliseconds < 500 && await ReadViewportAsync(cancellationToken) is { } now && (now.Width != width || now.Height != height));
            return true;
        }, cancellationToken), cancellationToken).AsTask());
    }

    /// <summary>Reads the size of the viewport of the page, or <see langword="null"/> when the page does not say.</summary>
    private async Task<NeoSize?> ReadViewportAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = await CallAgentAsync("info", null, cancellationToken);
            if (info.IsOk && info.Value.TryGetProperty("viewport", out var viewport) &&
                viewport.TryGetProperty("width", out var width) && viewport.TryGetProperty("height", out var height))
            {
                return new NeoSize((int)Math.Round(width.GetDouble()), (int)Math.Round(height.GetDouble()));
            }
        }
        catch (DialogInterruptedException) { }
        catch (NeoAutomationException) { /* the page is between two documents */ }

        return null;
    }

    // -------------------------------------------------------------------------------------------------------------
    // Console and network logs
    // -------------------------------------------------------------------------------------------------------------

    /// <summary>Gets the console messages of the page since its last navigation.</summary>
    /// <param name="includePreserved">Whether to include the messages of the three navigations before it.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The messages, oldest first.</returns>
    public ValueTask<IReadOnlyList<NeoAutomationConsoleMessage>> GetConsoleMessagesAsync(bool includePreserved = false, CancellationToken cancellationToken = default)
        => _owner.InvokeAsync(async () =>
        {
            await DrainQuietlyAsync(cancellationToken);
            var result = new List<NeoAutomationConsoleMessage>();
            foreach (var log in SelectLogs(includePreserved)) result.AddRange(log.Console);
            return (IReadOnlyList<NeoAutomationConsoleMessage>)result;
        }, cancellationToken);

    /// <summary>Gets a console message by its identifier.</summary>
    /// <param name="id">The identifier of the message.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The message, or <see langword="null"/> when the page has no such message any more.</returns>
    public ValueTask<NeoAutomationConsoleMessage?> GetConsoleMessageAsync(int id, CancellationToken cancellationToken = default)
        => _owner.InvokeAsync<NeoAutomationConsoleMessage?>(async () =>
        {
            await DrainQuietlyAsync(cancellationToken);
            foreach (var log in _logs)
            {
                foreach (var message in log.Console)
                {
                    if (message.Id == id) return message;
                }
            }

            return null;
        }, cancellationToken);

    /// <summary>Gets the network requests of the page since its last navigation.</summary>
    /// <param name="includePreserved">Whether to include the requests of the three navigations before it.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The requests, oldest first.</returns>
    public ValueTask<IReadOnlyList<NeoAutomationNetworkRequest>> GetNetworkRequestsAsync(bool includePreserved = false, CancellationToken cancellationToken = default)
        => _owner.InvokeAsync(async () =>
        {
            await DrainQuietlyAsync(cancellationToken);
            var result = new List<NeoAutomationNetworkRequest>();
            foreach (var log in SelectLogs(includePreserved)) result.AddRange(log.Network);
            return (IReadOnlyList<NeoAutomationNetworkRequest>)result;
        }, cancellationToken);

    /// <summary>Gets a network request by its identifier.</summary>
    /// <param name="id">The identifier of the request.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The request, or <see langword="null"/> when the page has no such request any more.</returns>
    public ValueTask<NeoAutomationNetworkRequest?> GetNetworkRequestAsync(int id, CancellationToken cancellationToken = default)
        => _owner.InvokeAsync<NeoAutomationNetworkRequest?>(async () =>
        {
            await DrainQuietlyAsync(cancellationToken);
            foreach (var log in _logs)
            {
                foreach (var request in log.Network)
                {
                    if (request.Id == id) return request;
                }
            }

            return null;
        }, cancellationToken);

    // -------------------------------------------------------------------------------------------------------------
    // Attachment to the view. Everything below runs on the UI thread.
    // -------------------------------------------------------------------------------------------------------------

    internal void Attach()
    {
        View.AutomationScriptDialogRequested = OnScriptDialog;
        View.RouteScriptDialogsToHost();
        View.NativeNavigationStarted += OnNavigationStarted;
        View.Disposing += OnViewDisposing;
        _ = RegisterAgentAsync();
    }

    internal void Detach()
    {
        if (Interlocked.Exchange(ref _detached, 1) != 0) return;
        View.NativeNavigationStarted -= OnNavigationStarted;
        View.Disposing -= OnViewDisposing;
        if (ReferenceEquals(View.AutomationScriptDialogRequested?.Target, this)) View.AutomationScriptDialogRequested = null;
        if (_dialog is { } pending)
        {
            ClearDialog(pending);
            pending.Completion.TrySetResult(SafeDefault(pending.Info.Kind));
        }

        var script = _agentScript;
        _agentScript = null;
        if (script is not null) _ = RemoveAgentAsync(script);
    }

    private static async Task RemoveAgentAsync(NeoUserScript script)
    {
        try { await script.DisposeAsync(); }
        catch (Exception exception) when (exception is InvalidOperationException or NeoAstraException or ObjectDisposedException)
        {
            // The view is gone, and its scripts with it.
        }
    }

    // Registers the agent for every document the view loads from now on, so that it sees their first console
    // messages and requests. The document that is loaded already gets the agent on the first call.
    private async Task RegisterAgentAsync()
    {
        try
        {
            var script = await View.AddScriptAsync(NeoAutomationAgent.Source, new NeoScriptOptions { MainFrameOnly = false });
            if (IsClosed) await RemoveAgentAsync(script);
            else _agentScript = script;
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or NeoAstraException or ObjectDisposedException or OperationCanceledException)
        {
            // Without the registration the agent is injected on demand and misses what a document logs before that.
        }
    }

    private void OnViewDisposing() => _owner.RemovePage(this);

    private void OnNavigationStarted()
    {
        // The document that is being left is still there for a moment: keep what it has logged.
        if (_dialog is null && !IsClosed) _ = _owner.RunDetached(() => DrainQuietlyAsync(CancellationToken.None));
    }

    private ValueTask<NeoScriptDialogDecision> OnScriptDialog(NeoScriptDialogRequest request)
    {
        if (_dialogPolicy?.Decide(request) is { } decision)
        {
            _dialogHandled = true;
            return new ValueTask<NeoScriptDialogDecision>(decision);
        }

        if (_dialog is { } previous)
        {
            // A page shows one dialog at a time; a second one means that the first was answered behind our back.
            ClearDialog(previous);
            previous.Completion.TrySetResult(SafeDefault(previous.Info.Kind));
        }

        var pending = new PendingDialog(new NeoAutomationDialog(request.Kind, request.Message, request.DefaultText));
        Volatile.Write(ref _dialog, pending);
        _dialogOpened.TrySetResult();
        _ = ExpireDialogAsync(pending);
        return new ValueTask<NeoScriptDialogDecision>(pending.Completion.Task);
    }

    // The view answers a dialog by itself once its decision timeout has passed; the dialog is closed from then on.
    private async Task ExpireDialogAsync(PendingDialog pending)
    {
        try { await Task.Delay(View.DecisionTimeout, pending.Expiry.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        _owner.Post(() =>
        {
            if (!ReferenceEquals(_dialog, pending)) return;
            ClearDialog(pending);
            pending.Completion.TrySetResult(SafeDefault(pending.Info.Kind));
        });
    }

    private void ClearDialog(PendingDialog pending)
    {
        if (ReferenceEquals(_dialog, pending))
        {
            Volatile.Write(ref _dialog, null);
            _dialogOpened = NewSignal();
        }

        pending.Expiry.Cancel();
    }

    private static NeoScriptDialogDecision SafeDefault(NeoScriptDialogKind kind)
        => kind == NeoScriptDialogKind.Alert ? NeoScriptDialogDecision.Accept : NeoScriptDialogDecision.Cancel;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // -------------------------------------------------------------------------------------------------------------
    // Talking to the agent
    // -------------------------------------------------------------------------------------------------------------

    private async Task<T> ExclusiveAsync<T>(Func<Task<T>> body, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        await _gate.WaitAsync(cancellationToken);
        try { return await body(); }
        catch (DialogInterruptedException interrupted) { throw DialogOpenError(interrupted.Dialog); }
        finally { _gate.Release(); }
    }

    private void ThrowIfClosed()
    {
        if (IsClosed) throw new NeoAutomationException("page-closed", $"Page {Id} is closed. Call list_pages to see the open pages.");
    }

    private NeoAutomationException DialogOpenError(NeoAutomationDialog dialog)
        => new("dialog-open", $"A dialog is open ({dialog.TypeName}: {dialog.Message}). Call handle_dialog to handle it before continuing.");

    /// <summary>Evaluates a script and gives up, without stopping it, when the page opens a dialog that nobody answers.</summary>
    private async Task<string?> EvaluateAsync(string script, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        if (_dialog is { } open) throw DialogOpenError(open.Info);
        var opened = _dialogOpened.Task;
        Task<string?> evaluation;
        try { evaluation = View.EvaluateScriptAsync(script, cancellationToken).AsTask(); }
        catch (ObjectDisposedException exception) { throw new NeoAutomationException("page-closed", $"Page {Id} is closed.", exception); }
        if (!evaluation.IsCompleted && await Task.WhenAny(evaluation, opened) != evaluation)
        {
            // The script is stopped inside the dialog and completes once the dialog is answered. Nobody waits for it.
            _ = evaluation.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw new DialogInterruptedException(_dialog?.Info ?? new NeoAutomationDialog(NeoScriptDialogKind.Alert, string.Empty, null));
        }

        try { return await evaluation; }
        catch (NeoAstraException exception) { throw new NeoAutomationException("script-error", EngineMessage(exception), exception); }
        catch (ObjectDisposedException exception) { throw new NeoAutomationException("page-closed", $"Page {Id} is closed.", exception); }
    }

    private static string EngineMessage(NeoAstraException exception)
        => string.IsNullOrWhiteSpace(exception.Message) ? "The browser engine could not run the script." : exception.Message;

    /// <summary>Runs a script that calls the agent, installing the agent first in a document that does not have it.</summary>
    private async Task<NeoAutomationAgentResult> CallAgentAsync(string script, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var text = NeoAutomationAgent.ReadString(await EvaluateAsync(script, cancellationToken));
            if (text == NeoAutomationAgent.Missing)
            {
                if (attempt >= 2) throw new NeoAutomationException("no-agent", "The page did not accept the automation script. It may be navigating; try again.");
                await EvaluateAsync(NeoAutomationAgent.Source, cancellationToken);
                continue;
            }

            if (text is null) throw new NeoAutomationException("no-agent", "The page returned no answer to the automation script. It may be navigating; try again.");
            try { return NeoAutomationAgentResult.Parse(text); }
            catch (JsonException exception) { throw new NeoAutomationException("no-agent", "The page returned an answer that automation does not understand.", exception); }
        }
    }

    private Task<NeoAutomationAgentResult> CallAgentAsync(string operation, Action<Utf8JsonWriter>? writeArguments, CancellationToken cancellationToken)
        => CallAgentAsync(NeoAutomationAgent.Call(operation, ObjectJson(writeArguments)), cancellationToken);

    private static string ObjectJson(Action<Utf8JsonWriter>? writeProperties)
        => writeProperties is null ? NeoAutomationAgent.EmptyObject : NeoAutomationAgent.Json(writer =>
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
        });

    /// <summary>Starts an operation that completes later in the page and polls for its result.</summary>
    /// <returns>The result. Its error code is <c>lost</c> when the document was replaced before the operation completed.</returns>
    private async Task<NeoAutomationAgentResult> RunJobAsync(Func<long, string> startScript, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var job = ++_nextJob;
        var started = await CallAgentAsync(startScript(job), cancellationToken);
        if (!started.IsOk) return started;
        return await AwaitJobAsync(job, timeout, cancellationToken);
    }

    // The clock of a wait is the host's: the page is asked again and again, and never left to its own timers, which an
    // engine slows down to one a second, and later one a minute, in a view that is not shown.
    private async Task<NeoAutomationAgentResult> AwaitJobAsync(long job, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        // The operation ends by its own timeout; the margin only covers a page that stopped answering.
        var limit = timeout + TimeSpan.FromSeconds(2);
        var delay = 5;
        var backoff = 5;
        while (true)
        {
            await Task.Delay(delay, cancellationToken);
            var text = NeoAutomationAgent.ReadString(await EvaluateAsync(NeoAutomationAgent.Poll(job), cancellationToken));
            if (text is null || text == NeoAutomationAgent.Missing) return NeoAutomationAgentResult.Parse("""{"ok":false,"error":{"code":"lost","message":"The document was replaced before the operation completed."}}""");
            NeoAutomationAgentResult result;
            try { result = NeoAutomationAgentResult.Parse(text); }
            catch (JsonException exception) { throw new NeoAutomationException("no-agent", "The page returned an answer that automation does not understand.", exception); }
            if (!result.IsPending) return result;
            if (clock.Elapsed > limit) return NeoAutomationAgentResult.Parse("""{"ok":false,"error":{"code":"timeout","message":"The page did not complete the operation in time."}}""");
            backoff = Math.Min(backoff * 2, 50);
            // A wait that knows when it can end at the earliest is asked again then, and no later than the usual pace.
            delay = result.PollAfter is { } hint ? Math.Clamp(hint, 1, 50) : backoff;
        }
    }

    // -------------------------------------------------------------------------------------------------------------
    // Operations
    // -------------------------------------------------------------------------------------------------------------

    private async Task<NeoAutomationSnapshot> SnapshotCoreAsync(bool verbose, CancellationToken cancellationToken)
    {
        var id = ++_nextSnapshot;
        var result = await CallAgentAsync("snapshot", writer =>
        {
            writer.WriteNumber("snapshotId", id);
            writer.WriteBoolean("verbose", verbose);
        }, cancellationToken);
        if (!result.IsOk) throw result.ToException();
        var snapshot = NeoAutomationSnapshot.Parse(result.Value, verbose);
        Volatile.Write(ref _snapshot, snapshot);
        await DrainQuietlyAsync(cancellationToken);
        return snapshot;
    }

    private ValueTask<NeoAutomationActionResult> ActAsync(string operation, Action<Utf8JsonWriter> writeArguments, string? uid, CancellationToken cancellationToken)
        => _owner.InvokeAsync(() => ExclusiveAsync(() => ActCoreAsync(operation, ObjectJson(writeArguments), uid, cancellationToken), cancellationToken), cancellationToken);

    private async Task<NeoAutomationActionResult> ActCoreAsync(string operation, string arguments, string? uid, CancellationToken cancellationToken)
    {
        using var navigation = new NavigationWatch(View);
        _dialogHandled = false;
        NeoAutomationAgentResult result;
        try { result = await CallAgentAsync(NeoAutomationAgent.Call(operation, arguments), cancellationToken); }
        catch (DialogInterruptedException interrupted) { return new NeoAutomationActionResult(null, interrupted.Dialog, null, false); }
        if (!result.IsOk) throw ActionError(result, uid);

        string? note = null;
        if (result.Value.ValueKind == JsonValueKind.Object && result.Value.TryGetProperty("obscuredBy", out var obscured) && obscured.GetString() is { } cover)
        {
            note = $"Another element, {cover}, covers that point of the element and received the event instead.";
        }

        return await SettleAsync(navigation, result.UrlBefore, note, waitForStableDom: true, result.IsLeaving, cancellationToken);
    }

    // The elements are filled in one by one, so that a dialog that one of them opens is known to be its doing.
    private async Task<NeoAutomationActionResult> FillCoreAsync(NeoAutomationFormValue[] elements, CancellationToken cancellationToken)
    {
        using var navigation = new NavigationWatch(View);
        _dialogHandled = false;
        string? urlBefore = null;
        var leaving = false;
        for (var index = 0; index < elements.Length; index++)
        {
            var element = elements[index];
            NeoAutomationAgentResult result;
            try
            {
                result = await CallAgentAsync("fill", writer =>
                {
                    writer.WriteString("uid", element.Uid);
                    writer.WriteString("value", element.Value);
                }, cancellationToken);
            }
            catch (DialogInterruptedException interrupted)
            {
                var note = elements.Length == 1 ? null
                    : $"Filling out the element with uid {element.Uid} opened a dialog." + (index < elements.Length - 1 ? " The remaining elements were not filled out." : string.Empty);
                return new NeoAutomationActionResult(null, interrupted.Dialog, note, false);
            }

            if (!result.IsOk) throw ActionError(result, element.Uid);
            urlBefore ??= result.UrlBefore;
            leaving |= result.IsLeaving;
        }

        return await SettleAsync(navigation, urlBefore, null, waitForStableDom: true, leaving, cancellationToken);
    }

    private static NeoAutomationException ActionError(NeoAutomationAgentResult result, string? uid)
    {
        var target = result.ErrorUid ?? uid;
        return result.ErrorCode switch
        {
            // These already say what to do; the others get the element they are about.
            "no-snapshot" or "stale" or "unknown" => result.ToException(),
            _ when target is not null => new NeoAutomationException(result.ErrorCode ?? "error", $"Failed to interact with the element with uid {target}. {result.ErrorMessage}"),
            _ => result.ToException(),
        };
    }

    /// <summary>
    /// Waits for what an action set in motion: a navigation it started, then a page that has stopped changing.
    /// <c>leaving</c> is whether the page said that the action sent it to another document.
    /// </summary>
    private async Task<NeoAutomationActionResult> SettleAsync(NavigationWatch navigation, string? urlBefore, string? note, bool waitForStableDom, bool leaving, CancellationToken cancellationToken)
    {
        // The engine reports a navigation a moment after the event that caused it. That moment is short on an idle
        // machine and not on a busy one, so a navigation that the page announced is waited for longer.
        if (!navigation.HasStarted) await Task.WhenAny(navigation.Started, Task.Delay(leaving ? AnnouncedNavigationStartWindow : NavigationStartWindow, cancellationToken));
        if (navigation.HasStarted && !navigation.Completed.IsCompleted) await Task.WhenAny(navigation.Completed, Task.Delay(ActionNavigationTimeout, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();

        string? urlAfter = null;
        try
        {
            if (waitForStableDom)
            {
                var arguments = NeoAutomationAgent.Json(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("quiet", 100);
                    writer.WriteNumber("timeout", (long)StableDomTimeout.TotalMilliseconds);
                    writer.WriteEndObject();
                });
                var stable = await RunJobAsync(job => NeoAutomationAgent.Start(job, "waitForStableDom", arguments), StableDomTimeout, cancellationToken);
                urlAfter = stable.Url;
            }

            // A document that was replaced while it settled is asked once more for where the page is now.
            urlAfter ??= (await CallAgentAsync("info", null, cancellationToken)).Url;
        }
        catch (DialogInterruptedException interrupted) { return new NeoAutomationActionResult(null, interrupted.Dialog, note, _dialogHandled); }
        catch (NeoAutomationException) when (_dialog is not null) { return new NeoAutomationActionResult(null, _dialog.Info, note, _dialogHandled); }
        catch (NeoAutomationException) { /* the page is between two documents; the view knows where it is */ }

        await DrainQuietlyAsync(cancellationToken);
        Uri? navigatedTo = null;
        if (urlAfter is not null && urlBefore is not null) { if (!string.Equals(urlAfter, urlBefore, StringComparison.Ordinal)) Uri.TryCreate(urlAfter, UriKind.Absolute, out navigatedTo); }
        else if (navigation.HasStarted) navigatedTo = View.Source;
        return new NeoAutomationActionResult(navigatedTo, _dialog?.Info, note, _dialogHandled);
    }

    private async Task<NeoAutomationActionResult> UploadCoreAsync(string uid, IReadOnlyList<string> filePaths, CancellationToken cancellationToken)
    {
        var files = new List<(long Id, string Path, FileInfo Info)>(filePaths.Count);
        long total = 0;
        foreach (var path in filePaths)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException($"The file to upload was not found: {path}", path);
            total += info.Length;
            if (total > MaximumUploadSize) throw new NeoAutomationException("too-large", "The files to upload exceed 64 MiB together.");
            files.Add((++_nextJob, info.FullName, info));
        }

        // The page gets the bytes in pieces, since a script that carries them all at once can exceed what an engine evaluates.
        var buffer = new byte[UploadChunkSize];
        foreach (var file in files)
        {
            await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            int read;
            while ((read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)) > 0)
            {
                var data = Convert.ToBase64String(buffer, 0, read);
                var chunk = await CallAgentAsync("uploadChunk", writer =>
                {
                    writer.WriteNumber("id", file.Id);
                    writer.WriteString("data", data);
                }, cancellationToken);
                if (!chunk.IsOk) throw chunk.ToException();
            }
        }

        var arguments = NeoAutomationAgent.Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("uid", uid);
            writer.WriteStartArray("files");
            foreach (var file in files)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", file.Id);
                writer.WriteString("name", file.Info.Name);
                writer.WriteString("type", MediaTypeOf(file.Info.Extension));
                writer.WriteNumber("lastModified", new DateTimeOffset(file.Info.LastWriteTimeUtc).ToUnixTimeMilliseconds());
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
        return await ActCoreAsync("uploadCommit", arguments, uid, cancellationToken);
    }

    private static string MediaTypeOf(string extension) => extension.ToLowerInvariant() switch
    {
        ".txt" or ".log" => "text/plain",
        ".htm" or ".html" => "text/html",
        ".css" => "text/css",
        ".csv" => "text/csv",
        ".js" or ".mjs" => "text/javascript",
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".mp3" => "audio/mpeg",
        ".mp4" => "video/mp4",
        ".wav" => "audio/wav",
        _ => string.Empty,
    };

    private async Task<NeoAutomationNavigationResult> NavigateCoreAsync(NeoAutomationNavigationOptions options, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        if (_dialog is { } open) throw DialogOpenError(open.Info);
        await DrainQuietlyAsync(cancellationToken);

        NeoUserScript? initScript = null;
        if (options.InitScript is not null)
        {
            initScript = await View.AddScriptAsync(options.InitScript, new NeoScriptOptions { MainFrameOnly = false }, cancellationToken);
        }

        _dialogHandled = false;
        _dialogPolicy = DialogPolicy.ForBeforeUnload(options.AcceptBeforeUnload);
        using var navigation = new NavigationWatch(View);
        try
        {
            var clock = Stopwatch.StartNew();
            try
            {
                switch (options.Kind)
                {
                    case NeoAutomationNavigationKind.Url: await View.NavigateAsync(options.Url!, cancellationToken); break;
                    case NeoAutomationNavigationKind.Back: View.GoBack(); break;
                    case NeoAutomationNavigationKind.Forward: View.GoForward(); break;
                    default: View.Reload(options.IgnoreCache); break;
                }
            }
            catch (NeoAstraException exception)
            {
                return Failed(options, EngineMessage(exception).TrimEnd('.'));
            }

            // A history move without an entry to go to starts nothing, and a move within the document completes at once.
            var history = options.Kind is NeoAutomationNavigationKind.Back or NeoAutomationNavigationKind.Forward;
            var startWindow = history ? TimeSpan.FromSeconds(1) : timeout;
            await Task.WhenAny(navigation.Started, navigation.Completed, Task.Delay(startWindow, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (!navigation.HasStarted && !navigation.Completed.IsCompleted)
            {
                return history
                    ? Failed(options, "there is no such entry in the history")
                    : Failed(options, $"Navigation timeout of {(long)timeout.TotalMilliseconds} ms exceeded");
            }

            var remaining = timeout - clock.Elapsed;
            if (!navigation.Completed.IsCompleted && remaining > TimeSpan.Zero) await Task.WhenAny(navigation.Completed, Task.Delay(remaining, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (!navigation.Completed.IsCompleted) return Failed(options, $"Navigation timeout of {(long)timeout.TotalMilliseconds} ms exceeded");
            var completion = await navigation.Completed;
            if (!completion.IsSuccess)
            {
                var reason = completion.NativeErrorCode != 0 ? $"the browser engine reported error {completion.NativeErrorCode}" : "the document did not load";
                return Failed(options, reason);
            }

            // The engine can report a navigation as complete before the scripts of the document have run.
            await WaitForDocumentAsync(timeout - clock.Elapsed, cancellationToken);
            using var settled = new NavigationWatch(View);
            var action = await SettleAsync(settled, null, null, waitForStableDom: true, leaving: false, cancellationToken);
            var url = View.Source;
            var message = options.Kind switch
            {
                NeoAutomationNavigationKind.Url => $"Successfully navigated to {options.Url!.OriginalString}.",
                NeoAutomationNavigationKind.Back => $"Successfully navigated back to {url}.",
                NeoAutomationNavigationKind.Forward => $"Successfully navigated forward to {url}.",
                _ => "Successfully reloaded the page.",
            };
            return new NeoAutomationNavigationResult(true, message, url, action.DialogHandled || _dialogHandled);
        }
        finally
        {
            _dialogPolicy = null;
            if (initScript is not null) await RemoveAgentAsync(initScript);
        }

        NeoAutomationNavigationResult Failed(NeoAutomationNavigationOptions navigationOptions, string reason)
        {
            var message = navigationOptions.Kind switch
            {
                NeoAutomationNavigationKind.Url => $"Unable to navigate in the selected page: {reason}.",
                NeoAutomationNavigationKind.Back => $"Unable to navigate back in the selected page: {reason}.",
                NeoAutomationNavigationKind.Forward => $"Unable to navigate forward in the selected page: {reason}.",
                _ => $"Unable to reload the selected page: {reason}.",
            };
            return new NeoAutomationNavigationResult(false, message, View.Source, _dialogHandled);
        }
    }

    private async Task WaitForDocumentAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var limit = timeout < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : timeout;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < limit)
        {
            try
            {
                var info = await CallAgentAsync("info", null, cancellationToken);
                if (info.IsOk && info.Value.TryGetProperty("readyState", out var state) && state.GetString() == "complete") return;
            }
            catch (DialogInterruptedException) { return; }
            catch (NeoAutomationException) when (_dialog is null) { /* the document is not ready for scripts yet */ }
            catch (NeoAutomationException) { return; }
            await Task.Delay(20, cancellationToken);
        }
    }

    private async Task<NeoAutomationEvaluateResult> EvaluateCoreAsync(string source, NeoAutomationEvaluateOptions options, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var navigation = new NavigationWatch(View);
        _dialogHandled = false;
        _dialogPolicy = DialogPolicy.ForAll(options.DialogAction ?? "accept");
        try
        {
            string? json;
            string? urlBefore = null;
            var leaving = false;
            if (options.IsFunction)
            {
                var function = source.TrimEnd();
                if (function.EndsWith(';')) function = function[..^1];
                var uids = NeoAutomationAgent.Json(writer =>
                {
                    writer.WriteStartArray();
                    foreach (var uid in options.Arguments ?? []) writer.WriteStringValue(uid);
                    writer.WriteEndArray();
                });
                var job = ++_nextJob;
                var result = await CallAgentAsync(NeoAutomationAgent.Evaluate(job, function, uids), cancellationToken);
                urlBefore = result.UrlBefore;
                leaving = result.IsLeaving;
                JsonElement outcome = default;
                if (result.IsOk && result.Value.ValueKind == JsonValueKind.Object && result.Value.TryGetProperty("done", out _))
                {
                    // The function returned at once, and its result came back with the call that ran it.
                    outcome = result.Value.GetProperty("result");
                }
                else if (result.IsOk)
                {
                    result = await AwaitJobAsync(job, timeout, cancellationToken);
                    leaving |= result.IsLeaving;
                    if (result.IsOk) outcome = result.Value;
                }

                if (!result.IsOk)
                {
                    throw result.ErrorCode switch
                    {
                        "no-snapshot" or "stale" or "unknown" or "timeout" => result.ToException(),
                        "lost" => new NeoAutomationException("lost", "The page navigated before the script returned its result."),
                        _ => new NeoAutomationException("script-error", result.ErrorMessage ?? "The script threw an exception."),
                    };
                }

                json = outcome.ValueKind == JsonValueKind.Object && outcome.TryGetProperty("json", out var value) ? value.GetString() : null;
            }
            else
            {
                // A classic script is the engine's to run: its completion value comes back as the engine serializes it.
                json = await EvaluateAsync(source, cancellationToken) ?? "null";
            }

            var action = await SettleAsync(navigation, urlBefore, null, options.WaitForStableDom, leaving, cancellationToken);
            return new NeoAutomationEvaluateResult(json, action);
        }
        finally
        {
            _dialogPolicy = null;
        }
    }

    private async Task<NeoCapturedImage> ScreenshotCoreAsync(string? uid, NeoCaptureOptions capture, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        if (_dialog is { } open) throw DialogOpenError(open.Info);
        if (uid is not null)
        {
            var rect = await CallAgentAsync("elementRect", writer => writer.WriteString("uid", uid), cancellationToken);
            if (!rect.IsOk) throw rect.ToException();
            var left = Math.Floor(rect.Value.GetProperty("x").GetDouble());
            var top = Math.Floor(rect.Value.GetProperty("y").GetDouble());
            var right = Math.Ceiling(rect.Value.GetProperty("x").GetDouble() + rect.Value.GetProperty("width").GetDouble());
            var bottom = Math.Ceiling(rect.Value.GetProperty("y").GetDouble() + rect.Value.GetProperty("height").GetDouble());
            if (right - left < 1 || bottom - top < 1) throw new NeoAutomationException("not-visible", $"The element with uid {uid} has no visible box to capture.");
            capture.Region = new NeoRect((int)left, (int)top, (int)(right - left), (int)(bottom - top));
            // The element may just have been scrolled into view; let the page draw before it is captured.
            await RunJobAsync(job => NeoAutomationAgent.Start(job, "nextFrame", NeoAutomationAgent.EmptyObject), TimeSpan.FromSeconds(1), cancellationToken);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_owner.Options.DefaultTimeout + TimeSpan.FromSeconds(5));
        try { return await View.CaptureAsync(capture, deadline.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NeoAutomationException("timeout", "The view produced no image in time. A view that is not visible cannot be captured.");
        }
        catch (InvalidOperationException exception)
        {
            throw new NeoAutomationException("not-visible", exception.Message, exception);
        }
        catch (ArgumentException exception)
        {
            throw new NeoAutomationException("not-visible", exception.Message, exception);
        }
        catch (NeoAstraException exception)
        {
            throw new NeoAutomationException("capture-failed", EngineMessage(exception), exception);
        }
    }

    // -------------------------------------------------------------------------------------------------------------
    // Logs
    // -------------------------------------------------------------------------------------------------------------

    private IEnumerable<NavigationLog> SelectLogs(bool includePreserved)
        => includePreserved ? _logs : _logs.Skip(_logs.Count - 1);

    private async Task DrainQuietlyAsync(CancellationToken cancellationToken)
    {
        if (_dialog is not null || IsClosed) return;
        try
        {
            var result = await CallAgentAsync("drain", null, cancellationToken);
            if (result.IsOk) Absorb(result.Value);
        }
        catch (DialogInterruptedException) { }
        catch (NeoAutomationException) { /* the page is between two documents; its logs come with the next call */ }
    }

    private void Absorb(JsonElement drained)
    {
        var documentId = drained.TryGetProperty("documentId", out var idValue) ? idValue.GetString() : null;
        var log = _logs[^1];
        if (log.DocumentId is null) log.DocumentId = documentId;
        else if (documentId is not null && log.DocumentId != documentId)
        {
            // A new document: what came before belongs to an earlier navigation.
            log = new NavigationLog { DocumentId = documentId };
            _logs.Add(log);
            while (_logs.Count > PreservedNavigations + 1) _logs.RemoveAt(0);
        }

        if (drained.TryGetProperty("console", out var console) && console.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in console.EnumerateArray())
            {
                var arguments = new List<string>();
                if (entry.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                {
                    foreach (var argument in args.EnumerateArray()) arguments.Add(argument.GetString() ?? string.Empty);
                }

                log.Console.Add(new NeoAutomationConsoleMessage(
                    _nextMessageId++,
                    Text(entry, "type") ?? "log",
                    Text(entry, "text") ?? string.Empty,
                    arguments,
                    NullIfEmpty(Text(entry, "url")),
                    Number(entry, "line"),
                    Number(entry, "column"),
                    NullIfEmpty(Text(entry, "stack")),
                    Time(entry, "time")));
                if (log.Console.Count > MaximumLogEntries) log.Console.RemoveAt(0);
            }
        }

        if (drained.TryGetProperty("network", out var network) && network.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in network.EnumerateArray())
            {
                var key = (Text(entry, "frame") ?? documentId ?? string.Empty) + ":" + (entry.TryGetProperty("seq", out var seq) ? seq.GetRawText() : string.Empty);
                // A request that changed after it was read arrives again under the same sequence number.
                if (!TryFindRequest(key, out var request))
                {
                    request = new NeoAutomationNetworkRequest(_nextRequestId++) { Key = key };
                    log.Network.Add(request);
                    log.RequestsByKey[key] = request;
                    if (log.Network.Count > MaximumLogEntries)
                    {
                        log.RequestsByKey.Remove(log.Network[0].Key);
                        log.Network.RemoveAt(0);
                    }
                }

                Apply(request, entry);
            }
        }
    }

    private bool TryFindRequest(string key, out NeoAutomationNetworkRequest request)
    {
        for (var index = _logs.Count - 1; index >= 0; index--)
        {
            if (_logs[index].RequestsByKey.TryGetValue(key, out request!)) return true;
        }

        request = null!;
        return false;
    }

    private static void Apply(NeoAutomationNetworkRequest request, JsonElement entry)
    {
        request.Url = Text(entry, "url") ?? request.Url;
        request.Method = Text(entry, "method") ?? request.Method;
        request.ResourceType = Text(entry, "kind") ?? request.ResourceType;
        request.MimeType = NullIfEmpty(Text(entry, "mimeType")) ?? request.MimeType;
        request.StartTime = Time(entry, "wallTime");
        if (entry.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number && status.GetInt32() > 0) request.StatusCode = status.GetInt32();
        if (entry.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number) request.Duration = TimeSpan.FromMilliseconds(duration.GetDouble());
        else if (entry.TryGetProperty("end", out var end) && end.ValueKind == JsonValueKind.Number && entry.TryGetProperty("start", out var start) && start.ValueKind == JsonValueKind.Number)
        {
            request.Duration = TimeSpan.FromMilliseconds(Math.Max(0, end.GetDouble() - start.GetDouble()));
        }

        if (entry.TryGetProperty("transferSize", out var size) && size.ValueKind == JsonValueKind.Number) request.TransferSize = (long)size.GetDouble();
        if (Headers(entry, "requestHeaders") is { } requestHeaders) request.RequestHeaders = requestHeaders;
        if (Headers(entry, "responseHeaders") is { } responseHeaders) request.ResponseHeaders = responseHeaders;
        request.RequestBody = Text(entry, "requestBody") ?? request.RequestBody;
        request.ResponseBody = Text(entry, "responseBody") ?? request.ResponseBody;
        if (entry.TryGetProperty("responseBodyTruncated", out var truncated) && truncated.ValueKind == JsonValueKind.True) request.IsResponseBodyTruncated = true;

        var state = Text(entry, "state");
        if (state == "failed")
        {
            request.Failure = Text(entry, "error") ?? "failed";
            request.Status = request.Failure;
        }
        else if (state == "pending") request.Status = "pending";
        else request.Status = request.StatusCode is { } code ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "finished";
    }

    private static IReadOnlyDictionary<string, string>? Headers(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in value.EnumerateObject()) headers[header.Name] = header.Value.GetString() ?? string.Empty;
        return headers;
    }

    private static string? Text(JsonElement entry, string name)
        => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Number(JsonElement entry, string name)
        => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    private static DateTimeOffset Time(JsonElement entry, string name)
        => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.GetDouble() is > 0 and < 8.64e15 and var milliseconds
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds)
            : DateTimeOffset.UtcNow;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static TimeSpan ResolveTimeout(TimeSpan? requested, TimeSpan fallback, string parameterName)
    {
        if (requested is not { } value) return fallback;
        if (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(parameterName, value, "The timeout must be positive and at most ten minutes.");
        return value;
    }

    // -------------------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------------------

    private sealed class NavigationLog
    {
        internal string? DocumentId { get; set; }

        internal List<NeoAutomationConsoleMessage> Console { get; } = [];

        internal List<NeoAutomationNetworkRequest> Network { get; } = [];

        internal Dictionary<string, NeoAutomationNetworkRequest> RequestsByKey { get; } = new(StringComparer.Ordinal);
    }

    private sealed class PendingDialog(NeoAutomationDialog info)
    {
        internal NeoAutomationDialog Info { get; } = info;

        internal TaskCompletionSource<NeoScriptDialogDecision> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationTokenSource Expiry { get; } = new();
    }

    /// <summary>Answers the dialogs that an operation expects, so that they do not stop it.</summary>
    private sealed class DialogPolicy(Func<NeoScriptDialogRequest, NeoScriptDialogDecision?> decide)
    {
        internal NeoScriptDialogDecision? Decide(NeoScriptDialogRequest request) => decide(request);

        internal static DialogPolicy ForBeforeUnload(bool accept)
            => new(request => request.Kind == NeoScriptDialogKind.BeforeUnload ? (accept ? NeoScriptDialogDecision.Accept : NeoScriptDialogDecision.Cancel) : null);

        /// <summary>Accepts or dismisses every dialog, or accepts it with the given text for a prompt.</summary>
        internal static DialogPolicy ForAll(string action) => new(request => action switch
        {
            "dismiss" => NeoScriptDialogDecision.Cancel,
            "accept" => request.Kind == NeoScriptDialogKind.Prompt ? NeoScriptDialogDecision.AcceptPrompt(request.DefaultText ?? string.Empty) : NeoScriptDialogDecision.Accept,
            _ => request.Kind == NeoScriptDialogKind.Prompt ? NeoScriptDialogDecision.AcceptPrompt(action) : NeoScriptDialogDecision.Accept,
        });
    }

    /// <summary>Observes the navigations of a view for the time of one operation.</summary>
    private sealed class NavigationWatch : IDisposable
    {
        private readonly NeoAstra _view;
        private readonly TaskCompletionSource _started = NewSignal();
        private readonly TaskCompletionSource<NeoNavigationCompletedEventArgs> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal NavigationWatch(NeoAstra view)
        {
            _view = view;
            view.NativeNavigationStarted += OnStarted;
            view.NavigationCompleted += OnCompleted;
        }

        internal Task Started => _started.Task;

        internal bool HasStarted => _started.Task.IsCompleted;

        internal Task<NeoNavigationCompletedEventArgs> Completed => _completed.Task;

        public void Dispose()
        {
            _view.NativeNavigationStarted -= OnStarted;
            _view.NavigationCompleted -= OnCompleted;
        }

        private void OnStarted() => _started.TrySetResult();

        private void OnCompleted(object? sender, NeoNavigationCompletedEventArgs args) => _completed.TrySetResult(args);
    }

    /// <summary>Thrown inside the page class when a script was left stopped in a dialog that it opened.</summary>
    private sealed class DialogInterruptedException(NeoAutomationDialog dialog) : Exception("The action was interrupted by a dialog.")
    {
        internal NeoAutomationDialog Dialog { get; } = dialog;
    }
}
