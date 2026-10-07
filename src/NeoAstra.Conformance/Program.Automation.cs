using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NeoAstra;
using NeoAstra.Tests;

internal static partial class Program
{
    private static readonly Uri AutomationUri = new("conformance://fixture/automation.html");

    private sealed partial class ConformanceSuite
    {
        // Browser automation is the same set of tools on every engine: these scenarios call the tools by name, as a
        // Model Context Protocol server would, against a page whose content security policy allows no inline script.
        // Each scenario starts from a fresh document and reports its own failure, so that one run on a platform says
        // everything that differs there; the run fails at the end when any of them did.
        private async ValueTask RunAutomationScenariosAsync(NeoEnvironment environment)
        {
            var directory = Path.Combine(Path.GetTempPath(), "neoastra-conformance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var extraWindows = new List<NeoWindow>();
            var failures = new List<string>();
            var automationOptions = new NeoAutomationOptions
            {
                // Pages opened by the scenarios stay hidden, like every other window of this harness.
                NewPageHandler = async (_, cancellationToken) =>
                {
                    var extra = CreateHiddenWindow("NeoAstra automation conformance page");
                    extraWindows.Add(extra);
                    return await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(extra), cancellationToken: cancellationToken);
                },
            };
            automationOptions.AllowedDirectories.Add(directory);

            var window = CreateHiddenWindow("NeoAstra automation conformance");
            NeoAutomation? automation = null;
            NeoAstra.NeoAstra? view = null;
            try
            {
                // Automation comes first, so that the view routes its dialogs to it from its first document on.
                automation = new NeoAutomation(application, automationOptions);
                view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window)).AsTask().WaitAsync(options.Timeout);
                var driver = new AutomationDriver(automation);

                async ValueTask RunAsync(string name, Func<Task> scenario)
                {
                    var stopwatch = Stopwatch.StartNew();
                    using var deadline = new CancellationTokenSource(options.Timeout);
                    driver.CancellationToken = deadline.Token;
                    try
                    {
                        // The token ends the tool call that hangs; the margin covers one that does not observe it.
                        await scenario().WaitAsync(options.Timeout + TimeSpan.FromSeconds(10));
                        _passed++;
                        Console.WriteLine($"PASS automation: {name} ({stopwatch.Elapsed.TotalMilliseconds:F1} ms)");
                    }
                    catch (ScenarioSkippedException skipped)
                    {
                        Skip($"automation: {name}", skipped.Message);
                    }
                    catch (Exception exception)
                    {
                        failures.Add(name);
                        var reason = exception is OperationCanceledException or TimeoutException
                            ? $"timed out after {stopwatch.Elapsed.TotalSeconds:F0} s during: {driver.LastCall}"
                            : exception.Message;
                        Console.WriteLine($"FAIL automation: {name}: {reason}");
                    }
                }

                await RunAsync("tool catalog and page list", async () =>
                {
                    var names = automation.Tools.Select(static tool => tool.Name).ToArray();
                    foreach (var name in new[] { "click", "fill_form", "take_snapshot", "take_screenshot", "evaluate_script", "navigate_page", "list_console_messages", "list_network_requests", "handle_dialog", "wait_for" })
                    {
                        Require(names.Contains(name), $"The tool {name} is missing from the catalog.");
                    }

                    foreach (var tool in automation.Tools)
                    {
                        Require(tool.InputSchema.GetProperty("type").GetString() == "object", $"The schema of {tool.Name} is not an object schema.");
                    }

                    var pages = await driver.CallAsync("list_pages");
                    Require(pages.StartsWith("## Pages\n1: ", StringComparison.Ordinal) && pages.Contains("[selected]", StringComparison.Ordinal), $"Unexpected page list: {pages}");
                    Require(automation.SelectedPage is { } page && ReferenceEquals(page.View, view), "The view was not selected as the first page.");
                });

                await RunAsync("navigation and snapshot of the accessibility tree", async () =>
                {
                    var navigated = await driver.CallAsync("navigate_page", $$"""{"url":"{{AutomationUri}}"}""");
                    Require(navigated.StartsWith($"Successfully navigated to {AutomationUri}.\n## Pages\n1: NeoAstra automation fixture ({AutomationUri}) [selected]", StringComparison.Ordinal), $"Unexpected navigation result: {navigated}");
                    var snapshot = await driver.CallAsync("take_snapshot");
                    Require(Regex.IsMatch(snapshot, $$"""^## Latest page snapshot\nuid=(\d+)_0 RootWebArea "NeoAstra automation fixture" url="{{Regex.Escape(AutomationUri.ToString())}}"\n  uid=\1_1 banner\n    uid=\1_2 heading "Automation fixture" level="1"\n    uid=\1_3 navigation "Main"\n      uid=\1_4 link "Second page" url="conformance://fixture/second\.html"\n"""),
                        $"The snapshot does not start as expected:\n{snapshot}");
                    foreach (var line in new[]
                    {
                        " button \"Clicked 0 times\"\n", " button \"Disabled\" disableable disabled\n", " textbox \"Name\"\n", " textbox \"Email\" required\n",
                        " checkbox \"I agree\"\n", " radio \"Large\"\n", " textbox \"Notes\" multiline\n", " textbox \"Editor\"\n", " button \"Attachment\"\n",
                        " image \"Blue swatch\"\n", " heading \"Far section\" level=\"2\"\n",
                    })
                    {
                        Require(snapshot.Contains(line, StringComparison.Ordinal), $"The snapshot has no line{line.TrimEnd()}:\n{snapshot}");
                    }

                    Require(Regex.IsMatch(snapshot, """uid=\S+ combobox "Color" haspopup="menu" value="Green"\n\s+uid=\S+ option "Red"\n\s+uid=\S+ option "Green" selected\n\s+uid=\S+ option "Blue"\n"""),
                        $"The snapshot does not list the select and its options:\n{snapshot}");
                    Require(!snapshot.Contains("Hidden from the tree", StringComparison.Ordinal), "The snapshot lists content hidden from the accessibility tree.");

                    var counter = driver.Uid("button \"Clicked 0 times\"");
                    var clicked = await driver.CallAsync("click", $$"""{"uid":"{{counter}}","includeSnapshot":true}""");
                    Require(clicked.StartsWith("Successfully clicked on the element\n## Latest page snapshot\n", StringComparison.Ordinal), $"Unexpected click result: {clicked}");
                    Require(clicked.Contains($"uid={counter} button \"Clicked 1 times\" focusable focused\n", StringComparison.Ordinal), $"The clicked button did not keep its identifier and take the focus:\n{clicked}");
                });

                await RunAsync("form filling, toggles, selects, and editable content", async () =>
                {
                    await driver.OpenFixtureAsync();
                    var form = $$"""
                        {"elements":[
                          {"uid":"{{driver.Uid("textbox \"Name\"")}}","value":"Ada Lovelace"},
                          {"uid":"{{driver.Uid("textbox \"Email\"")}}","value":"ada@example.com"},
                          {"uid":"{{driver.Uid("combobox \"Color\"")}}","value":"Blue"},
                          {"uid":"{{driver.Uid("checkbox \"I agree\"")}}","value":"true"},
                          {"uid":"{{driver.Uid("radio \"Large\"")}}","value":"true"},
                          {"uid":"{{driver.Uid("textbox \"Notes\"")}}","value":"Line one"},
                          {"uid":"{{driver.Uid("textbox \"Editor\"")}}","value":"Rich text"}]}
                        """;
                    Require(await driver.CallAsync("fill_form", form) == "Successfully filled out the form", "The form was not filled out.");
                    Require(await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("button \"Send\"")}}"}""") == "Successfully clicked on the element", "The form was not submitted.");
                    using var submitted = JsonDocument.Parse(JsonDocument.Parse(await driver.EvaluateAsync("() => document.getElementById('result').textContent")).RootElement.GetString()!);
                    var data = submitted.RootElement;
                    Require(data.GetProperty("name").GetString() == "Ada Lovelace" && data.GetProperty("email").GetString() == "ada@example.com" &&
                            data.GetProperty("color").GetString() == "b" && data.GetProperty("agree").GetString() == "on" && data.GetProperty("size").GetString() == "l" &&
                            data.GetProperty("notes").GetString() == "Line one" && data.GetProperty("editor").GetString() == "Rich text",
                        $"The submitted form differs from what was filled in: {data.GetRawText()}");
                    var refused = await driver.CallAsync("fill", $$"""{"uid":"{{driver.Uid("combobox \"Color\"")}}","value":"Purple"}""", expectError: true);
                    Require(refused.Contains("Could not find option with text \"Purple\"", StringComparison.Ordinal), $"Unexpected error for an unknown option: {refused}");
                    var disabled = await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("button \"Disabled\"")}}"}""", expectError: true);
                    Require(disabled.Contains("is disabled", StringComparison.Ordinal), $"Unexpected error for a disabled button: {disabled}");
                });

                await RunAsync("keyboard, hover, drag, and double click", async () =>
                {
                    await driver.OpenFixtureAsync();
                    await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("textbox \"Name\"")}}"}""");
                    Require(await driver.CallAsync("type_text", """{"text":"Grace","submitKey":"Tab"}""") == "Typed text \"Grace + Tab\"", "The text was not typed.");
                    await driver.CallAsync("press_key", """{"key":"Control+k"}""");
                    var typed = await driver.EvaluateAsync("() => ({ name: document.getElementById('name').value, active: document.activeElement.id, result: document.getElementById('result').textContent })");
                    Require(typed == """{"name":"Grace","active":"email","result":"shortcut"}""", $"The keyboard input did not arrive as typed: {typed}");
                    await driver.CallAsync("press_key", """{"key":"Shift+Tab"}""");
                    await driver.CallAsync("press_key", """{"key":"End"}""");
                    await driver.CallAsync("press_key", """{"key":"Backspace"}""");
                    await driver.CallAsync("type_text", """{"text":"E!"}""");
                    var edited = await driver.EvaluateAsync("() => document.getElementById('name').value");
                    Require(edited == "\"GracE!\"", $"Editing keys did not change the text as expected: {edited}");
                    await driver.CallAsync("press_key", """{"key":"Control+A"}""");
                    await driver.CallAsync("type_text", """{"text":"Ada"}""");
                    var replaced = await driver.EvaluateAsync("() => document.getElementById('name').value");
                    Require(replaced == "\"Ada\"", $"Typing over a selection did not replace it: {replaced}");
                    // The keys of a typed text say which keys they are, as those of a keyboard do: a letter, a symbol, and the upper symbol of a key.
                    await driver.EvaluateAsync("() => { window.typedKeys = []; document.getElementById('name').addEventListener('keydown', e => window.typedKeys.push([e.key, e.code, e.keyCode, e.shiftKey])); return true; }");
                    await driver.CallAsync("type_text", """{"text":"a.?"}""");
                    var keys = await driver.EvaluateAsync("() => window.typedKeys");
                    Require(keys == """[["a","KeyA",65,false],[".","Period",190,false],["?","Slash",191,true]]""", $"The keys of a typed text did not say which keys they are: {keys}");

                    await driver.CallAsync("hover", $$"""{"uid":"{{driver.Uid("\"Hover me\"")}}"}""");
                    await driver.CallAsync("drag", $$"""{"from_uid":"{{driver.Uid("\"Drag me\"")}}","to_uid":"{{driver.Uid("\"Drop here\"")}}"}""");
                    await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("button \"Clicked 0 times\"")}}","dblClick":true}""");
                    var pointer = await driver.EvaluateAsync("() => ({ hover: document.getElementById('hover-state').textContent, drop: document.getElementById('drop-target').textContent, double: document.getElementById('counter').dataset.double, text: document.getElementById('counter').textContent })");
                    Require(pointer == """{"hover":"hovered","drop":"Dropped payload","double":"yes","text":"Clicked 2 times"}""", $"Pointer input did not arrive as expected: {pointer}");
                    var unknown = await driver.CallAsync("press_key", """{"key":"Bogus"}""", expectError: true);
                    Require(unknown.Length != 0, "An unknown key was not refused.");
                });

                await RunAsync("typing and filling in an element with an EditContext", async () =>
                {
                    await driver.OpenFixtureAsync();
                    // An editor such as Monaco takes its text from an EditContext, which WebKit does not have.
                    if (await driver.EvaluateAsync("() => typeof EditContext === 'function'") != "true") throw new ScenarioSkippedException("The browser engine has no EditContext.");
                    var editor = driver.Uid("textbox \"Context editor\"");
                    await driver.CallAsync("click", $$"""{"uid":"{{editor}}"}""");
                    await driver.CallAsync("type_text", """{"text":"Hi 1"}""");
                    await driver.CallAsync("press_key", """{"key":"Enter"}""");
                    await driver.CallAsync("type_text", """{"text":"ab"}""");
                    await driver.CallAsync("press_key", """{"key":"Backspace"}""");
                    var typed = await driver.EvaluateAsync("() => { const editor = document.getElementById('context-editor'); return [editor.editContext.text, editor.textContent, document.activeElement.id]; }");
                    Require(typed == """["Hi 1\na","Hi 1\na","context-editor"]""", $"The typed text did not reach the EditContext as typed: {typed}");
                    Require(await driver.CallAsync("fill", $$"""{"uid":"{{editor}}","value":"filled (\n  twice"}""") == "Successfully filled out the element", "The editor was not filled out.");
                    var filled = await driver.EvaluateAsync("() => document.getElementById('context-editor').editContext.text");
                    Require(filled == "\"filled (\\n  twice\"", $"Filling did not replace the text of the EditContext: {filled}");
                    await driver.CallAsync("fill", $$"""{"uid":"{{editor}}","value":""}""");
                    var cleared = await driver.EvaluateAsync("() => document.getElementById('context-editor').editContext.text");
                    Require(cleared == "\"\"", $"Filling with nothing did not clear the EditContext: {cleared}");
                });

                await RunAsync("focus events in a document without the focus", async () =>
                {
                    await driver.OpenFixtureAsync();
                    // The window of this harness is hidden, as the one of an application that is driven in the background:
                    // its document has no focus, and the engine may change the active element without telling the page.
                    if (await driver.EvaluateAsync("() => document.hasFocus()") != "false") throw new ScenarioSkippedException("The document of the window has the focus.");
                    var first = driver.Uid("textbox \"First focus field\"");
                    var second = driver.Uid("textbox \"Second focus field\"");
                    await driver.CallAsync("click", $$"""{"uid":"{{first}}"}""");
                    var state = await driver.EvaluateAsync("() => focusState()");
                    Require(state == """{"first":true,"second":false,"events":["focus first"]}""", $"The clicked field was not told once that it has the focus: {state}");

                    // A window that is deactivated tells its active element that it lost the focus, and keeps it active.
                    await driver.EvaluateAsync("() => { const field = document.getElementById('focus-first'); field.dispatchEvent(new FocusEvent('blur')); field.dispatchEvent(new FocusEvent('focusout', { bubbles: true })); focusState(); }");
                    await driver.CallAsync("type_text", """{"text":"a"}""");
                    state = await driver.EvaluateAsync("() => focusState()");
                    Require(state == """{"first":true,"second":false,"events":["focus first"]}""", $"The active field was not told that it has the focus before the keys: {state}");

                    // An element that a script made the active one, with or without an event from the engine.
                    await driver.EvaluateAsync("() => { document.getElementById('focus-second').focus(); }");
                    await driver.CallAsync("type_text", """{"text":"b"}""");
                    state = await driver.EvaluateAsync("() => { const { first, second } = focusState(); return { first, second }; }");
                    Require(state == """{"first":false,"second":true}""", $"The page was not told about the element that a script focused: {state}");

                    // A field that focuses itself when it is pressed leaves nothing to do but to tell the other one.
                    await driver.CallAsync("click", $$"""{"uid":"{{first}}"}""");
                    state = await driver.EvaluateAsync("() => focusState()");
                    Require(state == """{"first":true,"second":false,"events":["blur second","focus first"]}""", $"A click did not move the focus from one field to the other: {state}");
                    await driver.CallAsync("click", $$"""{"uid":"{{second}}"}""");
                    await driver.CallAsync("type_text", """{"text":"c"}""");
                    state = await driver.EvaluateAsync("() => ({ ...focusState(), values: [document.getElementById('focus-first').value, document.getElementById('focus-second').value] })");
                    Require(state == """{"first":false,"second":true,"events":["blur first","focus second"],"values":["a","bc"]}""", $"The field that focused itself did not take the focus from the other one: {state}");
                });

                await RunAsync("file upload through a file input", async () =>
                {
                    await driver.OpenFixtureAsync();
                    File.WriteAllText(Path.Combine(directory, "upload.txt"), "hello upload");
                    var uploaded = await driver.CallAsync("upload_file", $$"""{"uid":"{{driver.Uid("button \"Attachment\"")}}","filePaths":["upload.txt"]}""");
                    Require(uploaded.StartsWith("File uploaded from ", StringComparison.Ordinal), $"Unexpected upload result: {uploaded}");
                    var content = await driver.EvaluateAsync("async () => ({ note: document.getElementById('result').textContent, text: await document.getElementById('file').files[0].text() })");
                    Require(content == """{"note":"files upload.txt:12","text":"hello upload"}""", $"The page did not receive the file: {content}");
                });

                await RunAsync("scripts, Promise results, and errors under a strict content security policy", async () =>
                {
                    await driver.OpenFixtureAsync();
                    var title = await driver.CallAsync("evaluate_script", """{"function":"() => document.title"}""");
                    Require(title == "Script ran on page and returned:\n```json\n\"NeoAstra automation fixture\"\n```", $"Unexpected script result: {title}");
                    var awaited = await driver.EvaluateAsync("async (el) => { await new Promise(resolve => setTimeout(resolve, 30)); return { text: el.textContent, list: [1, 2, 3] }; }",
                        $$""","args":["{{driver.Uid("button \"Send\"")}}"]""");
                    Require(awaited == """{"text":"Send","list":[1,2,3]}""", $"A Promise result was not awaited: {awaited}");
                    Require(await driver.EvaluateAsync("() => undefined") == "undefined", "An undefined result was not reported as undefined.");
                    var thrown = await driver.CallAsync("evaluate_script", """{"function":"() => { throw new TypeError('bad thing'); }"}""", expectError: true);
                    Require(thrown.Contains("TypeError: bad thing", StringComparison.Ordinal), $"Unexpected error for a script that throws: {thrown}");
                    var rejected = await driver.CallAsync("evaluate_script", """{"function":"async () => { await Promise.reject(new Error('later')); }"}""", expectError: true);
                    Require(rejected.Contains("later", StringComparison.Ordinal), $"Unexpected error for a rejected Promise: {rejected}");
                    var saved = await driver.CallAsync("evaluate_script", """{"function":"() => [1, 2]","filePath":"out.json"}""");
                    Require(saved.StartsWith("Script ran on page.", StringComparison.Ordinal) && File.ReadAllText(Path.Combine(directory, "out.json")) == "[1,2]", $"The script output was not saved: {saved}");
                    var escaped = await driver.CallAsync("evaluate_script", """{"function":"() => 1","filePath":"../out.json"}""", expectError: true);
                    Require(escaped.Length != 0, "A path outside the allowed directories was not refused.");
                });

                if (IsSupported(environment, NeoCapability.ScriptDialogs))
                {
                    await RunAsync("a JavaScript dialog waits for handle_dialog", async () =>
                    {
                        await driver.OpenFixtureAsync();
                        var ask = driver.Uid("button \"Ask\"");
                        var opened = await driver.CallAsync("click", $$"""{"uid":"{{ask}}"}""");
                        Require(opened.Contains("# Open dialog\nconfirm: Proceed?", StringComparison.Ordinal), $"Unexpected result of a click that opens a dialog: {opened}");
                        var blocked = await driver.CallAsync("take_snapshot", expectError: true);
                        Require(blocked.Contains("handle_dialog", StringComparison.Ordinal), $"Unexpected result while a dialog is open: {blocked}");
                        var accepted = await driver.CallAsync("handle_dialog", """{"action":"accept"}""");
                        Require(accepted.StartsWith("Successfully accepted the dialog", StringComparison.Ordinal), $"Unexpected result of accepting the dialog: {accepted}");
                        Require(await driver.EvaluateAsync("() => document.getElementById('confirm').textContent") == "\"Confirmed\"", "The page did not see the dialog accepted.");

                        await driver.CallAsync("click", $$"""{"uid":"{{ask}}"}""");
                        await driver.CallAsync("handle_dialog", """{"action":"dismiss"}""");
                        Require(await driver.EvaluateAsync("() => document.getElementById('confirm').textContent") == "\"Declined\"", "The page did not see the dialog dismissed.");

                        // A script answers the dialogs it opens itself.
                        Require(await driver.EvaluateAsync("() => confirm('sure?')") == "true", "A dialog of a script was not accepted by default.");
                        Require(await driver.EvaluateAsync("() => prompt('Value?', 'initial')", ""","dialogAction":"typed" """) == "\"typed\"", "A prompt of a script did not get its text.");
                        Require(await driver.EvaluateAsync("() => prompt('Value?', 'initial')") == "\"initial\"", "A prompt of a script did not keep its default text.");
                    });
                }
                else
                {
                    Skip("automation: a JavaScript dialog waits for handle_dialog", CapabilityReason(environment, NeoCapability.ScriptDialogs));
                }

                await RunAsync("console messages with their sources", async () =>
                {
                    await driver.OpenFixtureAsync();
                    await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("button \"Log\"")}}"}""");
                    var messages = await driver.CallAsync("list_console_messages");
                    Require(messages.StartsWith("## Console messages\nShowing 1-", StringComparison.Ordinal) && messages.Contains("[info] fixture ready (1 args)", StringComparison.Ordinal), $"The message of the page load is missing:\n{messages}");
                    Require(Regex.IsMatch(messages, """msgid=\d+ \[log\] clicked log \{answer: 42\} \(3 args\)\nmsgid=\d+ \[warn\] careful \(1 args\)\nmsgid=\d+ \[error\] Error: boom \(1 args\)"""), $"The logged messages are not listed as expected:\n{messages}");
                    var logId = Regex.Match(messages, """msgid=(\d+) \[log\] clicked""").Groups[1].Value;
                    var detail = await driver.CallAsync("get_console_message", $$"""{"msgid":{{logId}}}""");
                    Require(Regex.IsMatch(detail, """^ID: \d+\nMessage: log> clicked log \{answer: 42\}\nSource: conformance://fixture/automation\.js:\d+:\d+\n### Arguments\nArg #0: clicked %s\nArg #1: log\nArg #2: \{answer: 42\}$"""), $"Unexpected message detail:\n{detail}");
                    var errors = await driver.CallAsync("list_console_messages", """{"types":["error"],"includeStackTraces":true}""");
                    Require(errors.Contains("conformance://fixture/automation.js:", StringComparison.Ordinal), $"The stack of the logged error does not name the script of the page:\n{errors}");
                    Require(!Regex.IsMatch(errors, """neoastra-automation|user-script|fireMouse|clickTarget"""), $"The stack of the logged error shows frames of the automation script:\n{errors}");

                    await driver.EvaluateAsync("() => { setTimeout(() => { throw new RangeError('late failure'); }, 0); Promise.reject(new Error('rejected')); for (let i = 0; i < 3; i++) console.debug('again'); }");
                    // The timer of a page that is not shown runs late; the error is waited for rather than assumed.
                    var more = string.Empty;
                    for (var attempt = 0; attempt < 40; attempt++)
                    {
                        more = await driver.CallAsync("list_console_messages", """{"types":["error","debug"]}""");
                        if (more.Contains("late failure", StringComparison.Ordinal) && more.Contains("rejected", StringComparison.Ordinal)) break;
                        await Task.Delay(100);
                    }

                    Require(more.Contains("late failure", StringComparison.Ordinal) && more.Contains("Uncaught (in promise) Error: rejected", StringComparison.Ordinal), $"Uncaught errors are missing:\n{more}");
                    Require(more.Contains("[debug] again (1 args) [3 times]", StringComparison.Ordinal), $"Repeated messages are not counted:\n{more}");
                });

                await RunAsync("network requests of the page and its scripts", async () =>
                {
                    await driver.OpenFixtureAsync();
                    await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("button \"Fetch\"")}}"}""");
                    await driver.CallAsync("wait_for", """{"text":["fetched 7"]}""");
                    var requests = await driver.CallAsync("list_network_requests");
                    Require(requests.StartsWith("## Network requests\nShowing 1-", StringComparison.Ordinal), $"Unexpected request list:\n{requests}");
                    foreach (var name in new[] { "automation.html", "automation.css", "automation.js" })
                    {
                        Require(Regex.Matches(requests, $"""reqid=\d+ GET conformance://fixture/{Regex.Escape(name)} \[(200|finished)\]""").Count == 1, $"The request of {name} is not listed once:\n{requests}");
                    }

                    var fetchId = Regex.Match(requests, """reqid=(\d+) GET conformance://fixture/automation-data\.json \[200\]""").Groups[1].Value;
                    Require(fetchId.Length != 0, $"The request made with fetch is not listed with its status:\n{requests}");
                    var request = await driver.CallAsync("get_network_request", $$"""{"reqid":{{fetchId}}}""");
                    Require(request.StartsWith("## Request conformance://fixture/automation-data.json\nStatus: 200\nResource type: fetch\n### Request Headers\n- x-test:1\n", StringComparison.Ordinal) &&
                            request.EndsWith("### Response Body\n{\"value\": 7}", StringComparison.Ordinal), $"Unexpected request detail:\n{request}");
                    var filtered = await driver.CallAsync("list_network_requests", """{"resourceTypes":["fetch"],"pageSize":5}""");
                    Require(Regex.IsMatch(filtered, """Showing 1-1 of 1 \(Page 1 of 1\)\.\nreqid=\d+ GET \S+automation-data\.json \[200\]$"""), $"The requests were not filtered by type:\n{filtered}");
                    var sheets = await driver.CallAsync("list_network_requests", """{"resourceTypes":["stylesheet","script"]}""");
                    Require(sheets.Contains("Showing 1-2 of 2", StringComparison.Ordinal), $"The style sheet and the script are not listed by their kind:\n{sheets}");
                });

                await RunAsync("history, wait_for, and logs kept for each navigation", async () =>
                {
                    await driver.OpenFixtureAsync();
                    var old = driver.Uid("button \"Show later\"");
                    await driver.CallAsync("click", $$"""{"uid":"{{old}}"}""");
                    var waited = await driver.CallAsync("wait_for", """{"text":["Never there","Late arrival"],"timeout":10000}""");
                    Require(waited.StartsWith("Element matching one of [\"Never there\",\"Late arrival\"] found.\n## Latest page snapshot\n", StringComparison.Ordinal) && waited.EndsWith(" \"Late arrival\"", StringComparison.Ordinal), $"Unexpected wait result:\n{waited}");
                    var timedOut = await driver.CallAsync("wait_for", """{"text":["Never there"],"timeout":300}""", expectError: true);
                    Require(timedOut == "Timed out after 300 ms waiting for one of [\"Never there\"] to appear on the page.", $"Unexpected wait timeout: {timedOut}");

                    var jumped = await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("link \"Jump\"")}}"}""");
                    Require(jumped.Contains($"Page navigated to {AutomationUri}#section.", StringComparison.Ordinal), $"A move within the document was not reported: {jumped}");
                    var followed = await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("link \"Second page\"")}}","includeSnapshot":true}""");
                    Require(followed.StartsWith($"Successfully clicked on the element\nPage navigated to {SecondUri}.\n## Latest page snapshot\n", StringComparison.Ordinal) &&
                            followed.Contains("RootWebArea \"NeoAstra conformance second\"", StringComparison.Ordinal), $"The followed link was not reported with its page:\n{followed}");
                    var stale = await driver.CallAsync("click", $$"""{"uid":"{{old}}"}""", expectError: true);
                    Require(stale.Contains("take_snapshot", StringComparison.Ordinal), $"An identifier of the previous document was not refused with what to do next: {stale}");

                    var current = await driver.CallAsync("list_console_messages");
                    Require(current == "## Console messages\n<no console messages found>", $"The new document did not start with an empty console:\n{current}");
                    var preserved = await driver.CallAsync("list_console_messages", """{"includePreservedMessages":true}""");
                    Require(preserved.Contains("[info] fixture ready (1 args)", StringComparison.Ordinal), $"The messages of the previous document were not kept:\n{preserved}");
                    var preservedRequests = await driver.CallAsync("list_network_requests", """{"includePreservedRequests":true}""");
                    Require(preservedRequests.Contains("automation.js", StringComparison.Ordinal) && preservedRequests.Contains("second.html", StringComparison.Ordinal), $"The requests of both documents are not listed:\n{preservedRequests}");

                    var back = await driver.CallAsync("navigate_page", """{"type":"back"}""");
                    Require(back.StartsWith("Successfully navigated back to conformance://fixture/automation.html", StringComparison.Ordinal), $"Unexpected result of going back: {back}");
                    var forward = await driver.CallAsync("navigate_page", """{"type":"forward"}""");
                    Require(forward.StartsWith($"Successfully navigated forward to {SecondUri}.", StringComparison.Ordinal), $"Unexpected result of going forward: {forward}");
                    var reloaded = await driver.CallAsync("navigate_page", """{"type":"reload"}""");
                    Require(reloaded.StartsWith("Successfully reloaded the page.", StringComparison.Ordinal), $"Unexpected result of a reload: {reloaded}");
                    await driver.EvaluateAsync("() => { window.marker = 'set'; }");
                    var refetched = await driver.CallAsync("navigate_page", """{"type":"reload","ignoreCache":true}""");
                    Require(refetched.StartsWith("Successfully reloaded the page.", StringComparison.Ordinal) && await driver.EvaluateAsync("() => window.marker") == "undefined",
                        $"Unexpected result of a reload that leaves the cache out: {refetched}");
                    var scripted = await driver.CallAsync("navigate_page", $$"""{"url":"{{SecondUri}}","initScript":"window.marker = 'early';"}""");
                    Require(scripted.StartsWith($"Successfully navigated to {SecondUri}.", StringComparison.Ordinal) && await driver.EvaluateAsync("() => window.marker") == "\"early\"", $"The initial script did not run: {scripted}");
                });

                await RunAsync("pages are opened, selected, and closed", async () =>
                {
                    await driver.OpenAsync(SecondUri);
                    var last = await driver.CallAsync("close_page", """{"pageId":1}""");
                    Require(last.StartsWith("The last open page cannot be closed. It is fine to keep it open.\n## Pages\n", StringComparison.Ordinal), $"The last page was not kept: {last}");
                    var opened = await driver.CallAsync("new_page", $$"""{"url":"{{AutomationUri}}"}""");
                    var newId = Regex.Match(opened, $$"""(\d+): NeoAstra automation fixture \({{Regex.Escape(AutomationUri.ToString())}}\) \[selected\]$""").Groups[1].Value;
                    Require(newId.Length != 0, $"Unexpected result of opening a page:\n{opened}");
                    Require(await driver.EvaluateAsync("() => document.title") == "\"NeoAstra automation fixture\"", "A tool did not act on the selected page.");
                    Require(await driver.EvaluateAsync("() => document.title", ""","pageId":1""") == "\"NeoAstra conformance second\"", "A tool did not act on the page it named.");
                    var selected = await driver.CallAsync("select_page", """{"pageId":1}""");
                    Require(selected.Contains($"1: NeoAstra conformance second ({SecondUri}) [selected]\n{newId}: ", StringComparison.Ordinal), $"Unexpected result of selecting a page:\n{selected}");
                    var closed = await driver.CallAsync("close_page", $$"""{"pageId":{{newId}}}""");
                    Require(closed == $"## Pages\n1: NeoAstra conformance second ({SecondUri}) [selected]", $"Unexpected result of closing a page:\n{closed}");
                });

                if (IsSupported(environment, NeoCapability.CaptureViewport))
                {
                    await RunAsync("screenshots, coordinates, and resizing in a shown window", async () =>
                    {
                        // A view is captured while it is drawn, and coordinates are those of its viewport, so this
                        // window is shown for the time of the scenario.
                        window.Show();
                        try
                        {
                            await driver.OpenFixtureAsync();
                            using (var center = JsonDocument.Parse(await driver.EvaluateAsync("() => { const box = document.getElementById('counter').getBoundingClientRect(); return [Math.round(box.left + box.width / 2), Math.round(box.top + box.height / 2)]; }")))
                            {
                                await driver.CallAsync("click_at", $$"""{"x":{{center.RootElement[0].GetInt32()}},"y":{{center.RootElement[1].GetInt32()}}}""");
                                Require(await driver.EvaluateAsync("() => document.getElementById('counter').textContent") == "\"Clicked 1 times\"", "A click at coordinates did not reach the element that is there.");
                            }

                            var scale = double.Parse(await driver.EvaluateAsync("() => devicePixelRatio"), CultureInfo.InvariantCulture);
                            var width = int.Parse(await driver.EvaluateAsync("() => innerWidth"), CultureInfo.InvariantCulture);
                            var viewport = await driver.CallToolAsync("take_screenshot");
                            Require(viewport.Text == "Took a screenshot of the current page's viewport." && viewport.Images.Count == 1 && viewport.Images[0].ContentType == "image/png", $"Unexpected screenshot result: {viewport.Text}");
                            var pixels = TestPng.Decode(viewport.Images[0].Data.Span);
                            Require(Math.Abs(pixels.Width - width * scale) <= 2 && pixels.Width == viewport.Images[0].Width, $"The screenshot is {pixels.Width} pixels wide for a viewport of {width} at scale {scale}.");
                            RequireColor(pixels.GetPixel(2, 2), white: true, "the corner of the page");

                            var element = await driver.CallToolAsync("take_screenshot", $$"""{"uid":"{{driver.Uid("image \"Blue swatch\"")}}"}""");
                            Require(element.Images.Count == 1, $"The screenshot of an element has no image: {element.Text}");
                            pixels = TestPng.Decode(element.Images[0].Data.Span);
                            Require(Math.Abs(pixels.Width - 80 * scale) <= 2 && Math.Abs(pixels.Height - 40 * scale) <= 2, $"The screenshot of an 80 by 40 element is {pixels.Width} by {pixels.Height} at scale {scale}.");
                            RequireColor(pixels.GetPixel(pixels.Width / 2, pixels.Height / 2), white: false, "the middle of the blue element");
                            RequireColor(pixels.GetPixel(1, 1), white: false, "the corner of the blue element");

                            // An element below the first screen is scrolled into view for its screenshot.
                            var far = await driver.CallToolAsync("take_screenshot", $$"""{"uid":"{{driver.Uid("button \"Far button\"")}}","format":"jpeg","quality":60}""");
                            Require(far.Images.Count == 1 && far.Images[0].ContentType == "image/jpeg" && far.Images[0].Width > 0, "The JPEG screenshot of an element is missing.");

                            if (IsSupported(environment, NeoCapability.CaptureFullPage))
                            {
                                var full = await driver.CallToolAsync("take_screenshot", """{"fullPage":true,"filePath":"full.png"}""");
                                Require(full.Text.StartsWith("Took a screenshot of the full current page.\nSaved screenshot to ", StringComparison.Ordinal) && full.Images.Count == 0, $"Unexpected full-page result: {full.Text}");
                                var height = int.Parse(await driver.EvaluateAsync("() => document.documentElement.scrollHeight"), CultureInfo.InvariantCulture);
                                pixels = TestPng.Decode(File.ReadAllBytes(Path.Combine(directory, "full.png")));
                                Require(Math.Abs(pixels.Height - height * scale) <= 2, $"The full-page screenshot is {pixels.Height} pixels high for a document of {height} at scale {scale}.");
                            }
                            else
                            {
                                var refused = await driver.CallAsync("take_screenshot", """{"fullPage":true}""", expectError: true);
                                Require(refused.Length != 0, "A full-page screenshot was neither taken nor refused.");
                                Skip("automation: full-page screenshot", CapabilityReason(environment, NeoCapability.CaptureFullPage));
                            }

                            async Task ResizeAsync(int width, int height, string where)
                            {
                                var resized = await driver.CallAsync("resize_page", $$"""{"width":{{width}},"height":{{height}}}""");
                                Require(resized.Contains("## Pages\n", StringComparison.Ordinal), $"Unexpected result of resizing the page {where}: {resized}");
                                // The page learns its size from the window, which a window manager resizes in its own time:
                                // the tool may have answered before, with the size that the page had then.
                                var size = string.Empty;
                                for (var attempt = 0; attempt < 30 && size != $"[{width},{height}]"; attempt++)
                                {
                                    if (attempt != 0) await Task.Delay(100);
                                    size = await driver.EvaluateAsync("() => [innerWidth, innerHeight]");
                                }

                                Require(size == $"[{width},{height}]", $"The page is {size} after it was resized to [{width},{height}] {where}.");
                            }

                            await ResizeAsync(500, 400, "in a view that is not zoomed");
                            await ResizeAsync(501, 401, "in a view that is not zoomed");
                            // A window is not counted in the CSS pixels of its page: what a CSS pixel takes of it
                            // depends on the zoom of the view, and on Windows on the scale of the display.
                            view.ZoomFactor = 2;
                            try
                            {
                                await ResizeAsync(400, 300, "in a view zoomed to 200 percent");
                            }
                            finally
                            {
                                view.ZoomFactor = 1;
                            }
                        }
                        finally
                        {
                            window.Hide();
                        }
                    });
                }
                else
                {
                    Skip("automation: screenshots, coordinates, and resizing in a shown window", CapabilityReason(environment, NeoCapability.CaptureViewport));
                }
            }
            finally
            {
                if (automation is not null) await automation.DisposeAsync();
                if (view is not null) await view.DisposeAsync();
                await window.DisposeAsync();
                foreach (var extra in extraWindows) await extra.DisposeAsync();
                try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }

            Require(failures.Count == 0, $"{failures.Count} automation scenario(s) failed: {string.Join("; ", failures)}.");
        }

        private static void RequireColor((byte Red, byte Green, byte Blue) pixel, bool white, string where)
        {
            // A display profile shifts flat colors a little; the test is for white against blue, not for exact values.
            var matches = white
                ? pixel is { Red: > 230, Green: > 230, Blue: > 230 }
                : pixel is { Red: < 90, Green: < 90, Blue: > 160 };
            Require(matches, $"Expected {(white ? "white" : "blue")} at {where} but found ({pixel.Red}, {pixel.Green}, {pixel.Blue}).");
        }

        /// <summary>Ends a scenario that the browser engine has nothing to run against.</summary>
        private sealed class ScenarioSkippedException(string reason) : Exception(reason);

        /// <summary>Calls the automation tools by name with JSON arguments and reads their text results.</summary>
        private sealed class AutomationDriver(NeoAutomation automation)
        {
            private string _lastSnapshot = string.Empty;

            /// <summary>Gets or sets the token that ends the calls of the scenario that is running.</summary>
            internal CancellationToken CancellationToken { get; set; }

            /// <summary>Gets the last call that was started, to name it when a scenario does not return.</summary>
            internal string LastCall { get; private set; } = "(no tool call)";

            internal async Task<NeoAutomationToolResult> CallToolAsync(string name, string? arguments = null, bool expectError = false)
            {
                LastCall = $"{name} {arguments}";
                var result = await automation.CallToolAsync(name, arguments, CancellationToken);
                Require(result.IsError == expectError, $"The tool {name} {arguments} {(expectError ? "succeeded" : "failed")}: {result.Text}");
                if (result.Text.Contains("## Latest page snapshot", StringComparison.Ordinal)) _lastSnapshot = result.Text;
                return result;
            }

            internal async Task<string> CallAsync(string name, string? arguments = null, bool expectError = false)
                => (await CallToolAsync(name, arguments, expectError)).Text;

            /// <summary>Loads the fixture again, so that a scenario starts from a known document, and takes its snapshot.</summary>
            internal Task OpenFixtureAsync() => OpenAsync(AutomationUri);

            internal async Task OpenAsync(Uri uri)
            {
                // A dialog that an earlier scenario left open would stop every call of this one.
                if (automation.SelectedPage is { Dialog: not null }) await automation.CallToolAsync("handle_dialog", """{"action":"dismiss"}""", CancellationToken);
                if (automation.SelectedPage is { Id: not 1 }) await CallAsync("select_page", """{"pageId":1}""");
                await CallAsync("navigate_page", $$"""{"url":"{{uri}}"}""");
                await CallAsync("take_snapshot");
            }

            /// <summary>Runs a function in the page and returns the JSON text of its result.</summary>
            internal async Task<string> EvaluateAsync(string function, string moreArguments = "")
            {
                var text = await CallAsync("evaluate_script", $"{{\"function\":\"{JsonEncodedText.Encode(function)}\"{moreArguments}}}");
                var match = Regex.Match(text, "^Script ran on page and returned:\n```json\n(.*)\n```", RegexOptions.Singleline);
                Require(match.Success, $"The script did not return a result: {text}");
                return match.Groups[1].Value;
            }

            /// <summary>Finds the identifier of the node of the latest snapshot whose line continues with the given text.</summary>
            internal string Uid(string description)
            {
                var match = Regex.Match(_lastSnapshot, @"uid=(\S+) " + Regex.Escape(description) + @"(?: |$)", RegexOptions.Multiline);
                Require(match.Success, $"The latest snapshot has no node {description}:\n{_lastSnapshot}");
                return match.Groups[1].Value;
            }
        }
    }
}
