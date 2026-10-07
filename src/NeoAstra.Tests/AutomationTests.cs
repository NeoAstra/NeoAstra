// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace NeoAstra.Tests;

[TestClass]
public sealed class AutomationTests
{
    // The tools of Chrome DevTools MCP that NeoAstra offers on every browser engine.
    private static readonly string[] ExpectedTools =
    [
        "click", "click_at", "close_page", "drag", "evaluate_script", "fill", "fill_form", "get_console_message", "get_network_request",
        "handle_dialog", "hover", "list_console_messages", "list_network_requests", "list_pages", "navigate_page", "new_page", "press_key",
        "resize_page", "select_page", "take_screenshot", "take_snapshot", "type_text", "upload_file", "wait_for",
    ];

    // -------------------------------------------------------------------------------------------------------------
    // Without a browser
    // -------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public void KeyCombinationsResolveToKeysOfTheUsLayout()
    {
        var (key, modifiers) = NeoAutomationKeys.ParseCombination("Enter");
        Assert.AreEqual(("Enter", "Enter", 13), (key.Key, key.Code, key.KeyCode));
        Assert.IsEmpty(modifiers);

        (key, modifiers) = NeoAutomationKeys.ParseCombination("Control+Shift+R");
        Assert.AreEqual(("R", "KeyR", 82, "R"), (key.Key, key.Code, key.KeyCode, key.Text));
        CollectionAssert.AreEqual(new[] { "Control", "Shift" }, modifiers.Select(static modifier => modifier.Key).ToArray());
        Assert.IsTrue(modifiers.All(static modifier => modifier.IsModifier));

        // A plus sign after a separator is the key itself.
        (key, modifiers) = NeoAutomationKeys.ParseCombination("Control++");
        Assert.AreEqual(("+", "NumpadAdd"), (key.Key, key.Code));
        Assert.HasCount(1, modifiers);

        (key, _) = NeoAutomationKeys.ParseCombination("Shift+a");
        Assert.AreEqual(("A", "KeyA", "A"), (key.Key, key.Code, key.Text));
        (key, _) = NeoAutomationKeys.ParseCombination("Space");
        Assert.AreEqual((" ", "Space", 32, " "), (key.Key, key.Code, key.KeyCode, key.Text));
        (key, _) = NeoAutomationKeys.ParseCombination("ArrowDown");
        Assert.IsNull(key.Text);

        Assert.AreEqual("invalid-key", Assert.ThrowsExactly<NeoAutomationException>(() => NeoAutomationKeys.ParseCombination("Bogus")).Code);
        StringAssert.Contains(Assert.ThrowsExactly<NeoAutomationException>(() => NeoAutomationKeys.ParseCombination("Control+Control")).Message, "duplicate keys");
        Assert.ThrowsExactly<NeoAutomationException>(() => NeoAutomationKeys.ParseCombination(string.Empty));
    }

    [TestMethod]
    public void SnapshotTextFollowsTheLayoutOfChromeDevToolsMcp()
    {
        const string json = """
            {"snapshotId":"1","truncated":false,"root":{"i":"1_0","r":"RootWebArea","n":"My test page","p":{"url":"about:blank"},"c":[
              {"i":"1_1","r":"button","n":"Click me","p":{"focused":true}},
              {"i":"1_2","r":"textbox","p":{"value":"Input"}},
              {"i":"1_3","r":"StaticText","n":"username"},
              {"i":"1_4","r":"checkbox","n":"Agree","p":{"checked":true}},
              {"i":"1_5","r":"checkbox","n":"Partly","p":{"checked":"mixed"}},
              {"i":"1_6","r":"checkbox","n":"Off","p":{"checked":false}},
              {"i":"1_7","r":"combobox","n":"Color","p":{"expanded":false,"haspopup":"menu","value":"Green"},"c":[
                {"i":"1_8","r":"option","n":"Red","p":{"value":"Red"}},
                {"i":"1_9","r":"option","n":"Green","p":{"selected":true,"value":"Green"}}]},
              {"i":"1_10","r":"heading","n":"Title","p":{"level":2}},
              {"i":"1_11","r":"button","n":"Off limits","p":{"disabled":true}},
              {"i":"1_12","r":"none"},
              {"i":"1_13","r":"link","n":"Read more","p":{"url":"https://example.com/"},"c":[{"i":"1_14","r":"StaticText","n":"Read  more"}]},
              {"i":"1_15","r":"tab","n":"One","p":{"selected":true}}
            ]}}
            """;
        using var document = JsonDocument.Parse(json);
        var snapshot = NeoAutomationSnapshot.Parse(document.RootElement, verbose: false);
        const string expected = """
            uid=1_0 RootWebArea "My test page" url="about:blank"
              uid=1_1 button "Click me" focusable focused
              uid=1_2 textbox value="Input"
              uid=1_3 "username"
              uid=1_4 checkbox "Agree" checked
              uid=1_5 checkbox "Partly" checked="mixed"
              uid=1_6 checkbox "Off"
              uid=1_7 combobox "Color" expandable haspopup="menu" value="Green"
                uid=1_8 option "Red"
                uid=1_9 option "Green" selected
              uid=1_10 heading "Title" level="2"
              uid=1_11 button "Off limits" disableable disabled
              uid=1_12 ignored
              uid=1_13 link "Read more" url="https://example.com/"
              uid=1_15 tab "One" selectable selected

            """;
        Assert.AreEqual(expected.ReplaceLineEndings("\n"), snapshot.ToString());
        Assert.AreEqual("1", snapshot.Id);
        Assert.AreEqual("Green", snapshot.Find("1_9")!.Name);
        Assert.AreEqual(2d, snapshot.Find("1_10")!.Properties["level"]);
        Assert.IsNull(snapshot.Find("9_9"));

        // A verbose snapshot keeps the text children that only repeat a name.
        var verbose = NeoAutomationSnapshot.Parse(document.RootElement, verbose: true);
        StringAssert.Contains(verbose.ToString(), "    uid=1_14 \"Read  more\"\n");
    }

    [TestMethod]
    public void ToolCatalogHasTheToolsOfChromeDevToolsMcpWithObjectSchemas()
    {
        var tools = NeoAutomation.CreateTools(new NeoAutomationOptions());
        CollectionAssert.AreEqual(ExpectedTools, tools.Select(static tool => tool.Name).ToArray());
        foreach (var tool in tools)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(tool.Description), tool.Name);
            Assert.IsTrue(tool.Category is "input" or "navigation" or "emulation" or "network" or "debugging", tool.Name);
            var root = tool.InputSchema;
            Assert.AreEqual("object", root.GetProperty("type").GetString(), tool.Name);
            Assert.IsFalse(root.GetProperty("additionalProperties").GetBoolean(), tool.Name);
            var properties = root.GetProperty("properties");
            foreach (var property in properties.EnumerateObject())
            {
                Assert.IsTrue(property.Value.TryGetProperty("type", out _), $"{tool.Name}.{property.Name}");
                Assert.IsFalse(string.IsNullOrWhiteSpace(property.Value.GetProperty("description").GetString()), $"{tool.Name}.{property.Name}");
            }

            if (root.TryGetProperty("required", out var required))
            {
                foreach (var name in required.EnumerateArray()) Assert.IsTrue(properties.TryGetProperty(name.GetString()!, out _), $"{tool.Name}.{name}");
            }
        }

        static string[] Required(NeoAutomationTool tool)
            => tool.InputSchema.TryGetProperty("required", out var required) ? required.EnumerateArray().Select(static name => name.GetString()!).ToArray() : [];

        static string[] Properties(NeoAutomationTool tool)
            => tool.InputSchema.GetProperty("properties").EnumerateObject().Select(static property => property.Name).ToArray();

        var click = tools.Single(static tool => tool.Name == "click");
        CollectionAssert.AreEqual(new[] { "pageId", "uid", "dblClick", "includeSnapshot" }, Properties(click));
        CollectionAssert.AreEqual(new[] { "uid" }, Required(click));
        Assert.IsFalse(click.IsReadOnly);
        var navigate = tools.Single(static tool => tool.Name == "navigate_page");
        CollectionAssert.AreEqual(new[] { "pageId", "type", "url", "ignoreCache", "handleBeforeUnload", "initScript", "timeout" }, Properties(navigate));
        Assert.IsEmpty(Required(navigate));
        CollectionAssert.AreEqual(new[] { "pageId" }, Required(tools.Single(static tool => tool.Name == "close_page")));
        Assert.IsEmpty(Properties(tools.Single(static tool => tool.Name == "list_pages")));
        Assert.IsTrue(tools.Single(static tool => tool.Name == "list_pages").IsReadOnly);
        CollectionAssert.AreEqual(new[] { "elements" }, Required(tools.Single(static tool => tool.Name == "fill_form")));
    }

    [TestMethod]
    public void ToolCatalogLeavesCallerScriptsOutWhenScriptEvaluationIsOff()
    {
        var tools = NeoAutomation.CreateTools(new NeoAutomationOptions { AllowScriptEvaluation = false });
        CollectionAssert.AreEqual(ExpectedTools.Where(static name => name != "evaluate_script").ToArray(), tools.Select(static tool => tool.Name).ToArray());
        var schema = tools.Single(static tool => tool.Name == "navigate_page").InputSchema;
        Assert.IsFalse(schema.GetProperty("properties").TryGetProperty("initScript", out _));
    }

    [TestMethod]
    public void FilePathsOfToolCallsStayInsideTheAllowedDirectories()
    {
        var closed = new NeoAutomationOptions();
        Assert.AreEqual("not-allowed", Assert.ThrowsExactly<NeoAutomationException>(() => NeoAutomation.ResolveFilePath(closed, "shot.png", "filePath")).Code);

        var root = Path.Combine(Path.GetTempPath(), "neoastra-automation-tests", "allowed");
        var other = Path.Combine(Path.GetTempPath(), "neoastra-automation-tests", "also-allowed");
        var options = new NeoAutomationOptions();
        options.AllowedDirectories.Add(root);
        options.AllowedDirectories.Add(other);
        // The options an automation works with hold resolved directories.
        options = options.Clone();

        Assert.AreEqual(Path.Combine(root, "shot.png"), NeoAutomation.ResolveFilePath(options, "shot.png", "filePath"));
        Assert.AreEqual(Path.Combine(root, "nested", "shot.png"), NeoAutomation.ResolveFilePath(options, Path.Combine("nested", "shot.png"), "filePath"));
        Assert.AreEqual(Path.Combine(other, "a.txt"), NeoAutomation.ResolveFilePath(options, Path.Combine(other, "a.txt"), "filePath"));
        Assert.AreEqual("not-allowed", Assert.ThrowsExactly<NeoAutomationException>(() => NeoAutomation.ResolveFilePath(options, Path.Combine("..", "escape.png"), "filePath")).Code);
        Assert.AreEqual("not-allowed", Assert.ThrowsExactly<NeoAutomationException>(() => NeoAutomation.ResolveFilePath(options, root + "-sibling" + Path.DirectorySeparatorChar + "a.png", "filePath")).Code);
        Assert.AreEqual("not-allowed", Assert.ThrowsExactly<NeoAutomationException>(() => NeoAutomation.ResolveFilePath(options, root, "filePath")).Code);
        Assert.AreEqual("invalid-arguments", Assert.ThrowsExactly<NeoAutomationException>(() => NeoAutomation.ResolveFilePath(options, " ", "filePath")).Code);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new NeoAutomationOptions { DefaultTimeout = TimeSpan.Zero });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new NeoAutomationOptions { NavigationTimeout = TimeSpan.FromMinutes(11) });
    }

    [TestMethod]
    public void ResizeCountsAWindowInItsOwnUnitsAndItsPageInCssPixels()
    {
        // The first length to try is the one that comes to the page wanted. Windows at 150 percent: a window of 900 by
        // 700 pixels of the display shows a page of 600 by 467 CSS pixels, of which the last one may be cut.
        Assert.AreEqual(2400, new NeoAutomationPage.LengthSearch(1600, 1.5, pageRoundsDown: false).Next(900, 600));
        Assert.AreEqual(1500, new NeoAutomationPage.LengthSearch(1000, 1.5, pageRoundsDown: false).Next(700, 467));
        Assert.AreEqual(1201, new NeoAutomationPage.LengthSearch(801, 1.5, pageRoundsDown: false).Next(900, 600));
        Assert.AreEqual(1600, new NeoAutomationPage.LengthSearch(1600, 1, pageRoundsDown: false).Next(900, 900));
        // A view zoomed to 110 percent: 800 CSS pixels are 880 units, not one more for what a product of doubles adds.
        Assert.AreEqual(880, new NeoAutomationPage.LengthSearch(800, 1.1, pageRoundsDown: true).Next(1100, 1000));
        Assert.AreEqual(1002, new NeoAutomationPage.LengthSearch(801, 1.25, pageRoundsDown: true).Next(1000, 800));
        // What a window has beyond its page stays: the title bar that a GTK window draws inside its own size.
        Assert.AreEqual(437, new NeoAutomationPage.LengthSearch(400, 1, pageRoundsDown: true).Next(637, 600));
        Assert.AreEqual(837, new NeoAutomationPage.LengthSearch(400, 2, pageRoundsDown: true).Next(637, 300));
        // A page that does not say its size has a window without anything beyond it.
        Assert.AreEqual(1200, new NeoAutomationPage.LengthSearch(800, 1.5, pageRoundsDown: false).Next(900, 0));

        // A length that gave another page than the one wanted leads to the next one to try. The pages are the ones
        // WebView2 showed at 150 percent with a zoom of 125 percent, where a CSS pixel takes 1.875 pixels.
        var zoomed = new Dictionary<int, int>
        {
            [1200] = 640, [1201] = 641, [1202] = 641, [1203] = 641, [1204] = 642, [1205] = 643, [1206] = 643,
            [1207] = 644, [1208] = 645, [1209] = 645, [1210] = 646, [1211] = 646, [1212] = 646,
        };
        for (var wanted = 640; wanted <= 646; wanted++) Assert.AreEqual(wanted, Search(wanted, 1.875, false, 1206, 643, client => zoomed[client]).Page, $"{wanted}");
        // The page of 642 pixels takes a second length: the first one, 1203, shows 641.
        Assert.AreEqual((1204, 642, 2), Search(642, 1.875, false, 1206, 643, client => zoomed[client]));

        // No length gives a page of 961 CSS pixels where one of them takes 1.25 pixels by a zoom below 100 percent:
        // the search ends next to it.
        var skipping = new Dictionary<int, int> { [1198] = 959, [1199] = 960, [1200] = 960, [1201] = 962, [1202] = 962, [1203] = 962, [1204] = 964 };
        var (_, nearest, tries) = Search(961, 1.25, false, 1204, 964, client => skipping[client]);
        Assert.IsTrue(nearest is 960 or 962, $"{nearest}");
        Assert.IsLessThanOrEqualTo(4, tries);

        // A scale that is not the one of the page any more, as right after a zoom, is replaced by what the first
        // length shows: a page that takes 1.875 pixels for a CSS pixel when 1.5 were expected.
        Assert.AreEqual((1500, 800, 2), Search(800, 1.5, false, 900, 480, client => (int)Math.Ceiling(client / 1.875)));
        // WebKit counts the CSS pixels that fit: a view zoomed to 125 percent in a GTK window with a title bar.
        Assert.AreEqual((1039, 801, 1), Search(801, 1.25, true, 637, 480, client => (int)Math.Floor((client - 37) / 1.25)));
        // A GTK window reports the size it was given, also where its screen ended before: what it reports beyond its
        // page is then no title bar, and the page says what the window has beyond it.
        Assert.AreEqual((737, 700, 2), Search(700, 1, true, 20037, 2123, client => Math.Min(client, 2160) - 37));
        // With such a window and a scale that is wrong as well, a length that is too long and one that is too short
        // lead to the one between them.
        Assert.AreEqual((538, 400, 3), Search(400, 2, true, 5000, 300, client => (int)Math.Floor((client - 37) / 1.25)));

        static (int Client, int Page, int Tries) Search(int wanted, double scale, bool pageRoundsDown, int client, int page, Func<int, int> show)
        {
            var search = new NeoAutomationPage.LengthSearch(wanted, scale, pageRoundsDown);
            for (var tries = 0; ; tries++)
            {
                var next = search.Next(client, page);
                if (next == client || tries == 4) return (client, page, tries);
                (client, page) = (next, show(next));
            }
        }
    }

    // -------------------------------------------------------------------------------------------------------------
    // With WebView2
    // -------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task SnapshotListsThePageAndKeepsTheIdentifiersOfItsElements()
    {
        await RunAsync(async driver =>
        {
            var pages = await driver.CallAsync("list_pages");
            StringAssert.StartsWith(pages, "## Pages\n1: ");
            StringAssert.Contains(pages, "[selected]");

            // A tool also takes no arguments at all, or arguments by name as a Model Context Protocol library hands them over.
            Assert.AreEqual(pages, (await driver.Automation.CallToolAsync("list_pages", driver.CancellationToken)).Text);
            using var one = JsonDocument.Parse("1");
            var byName = new Dictionary<string, JsonElement> { ["pageId"] = one.RootElement, ["bringToFront"] = default };
            var selected = await driver.Automation.CallToolAsync("select_page", byName, driver.CancellationToken);
            Assert.IsFalse(selected.IsError, selected.Text);
            StringAssert.Contains(selected.Text, "[selected]");
            var missing = await driver.Automation.CallToolAsync("select_page", (IEnumerable<KeyValuePair<string, JsonElement>>?)null, driver.CancellationToken);
            Assert.IsTrue(missing.IsError);
            Assert.AreEqual("The argument \"pageId\" is required.", missing.Text);

            Assert.AreEqual(
                "Successfully navigated to app://neoastra/automation.html.\n## Pages\n1: Automation fixture (app://neoastra/automation.html) [selected]",
                await driver.CallAsync("navigate_page", """{"url":"app://neoastra/automation.html"}"""));

            var snapshot = await driver.CallAsync("take_snapshot");
            StringAssert.StartsWith(snapshot, "## Latest page snapshot\nuid=1_0 RootWebArea \"Automation fixture\" url=\"app://neoastra/automation.html\"\n  uid=1_1 banner\n    uid=1_2 heading \"Automation fixture\" level=\"1\"\n");
            StringAssert.Contains(snapshot, "    uid=1_3 navigation \"Main\"\n      uid=1_4 link \"Second page\" url=\"app://neoastra/second.html\"\n");
            StringAssert.Contains(snapshot, " button \"Disabled\" disableable disabled\n");
            StringAssert.Contains(snapshot, " textbox \"Email\" required\n");
            StringAssert.Contains(snapshot, " textbox \"Notes\" multiline\n");
            StringAssert.Contains(snapshot, " image \"Blue swatch\"\n");
            StringAssert.Contains(snapshot, " heading \"Far section\" level=\"2\"\n");
            // A select lists its options, by their text, and says which one is chosen.
            StringAssert.Matches(snapshot, new Regex("""uid=\S+ combobox "Color" haspopup="menu" value="Green"\n\s+uid=\S+ option "Red"\n\s+uid=\S+ option "Green" selected\n\s+uid=\S+ option "Blue"\n"""));
            // Text is listed where it stands alone, and left out where it only spells out the name of a control.
            StringAssert.Matches(snapshot, new Regex("""uid=\S+ "Welcome to the"\n\s+uid=\S+ "fixture"\n\s+uid=\S+ "page\."\n\s+uid=\S+ button "Clicked 0 times"\n"""));
            Assert.DoesNotContain("Hidden from the tree", snapshot);
            Assert.DoesNotContain("Not rendered", snapshot);

            var counter = driver.Uid("button \"Clicked 0 times\"");
            var clicked = await driver.CallAsync("click", $$"""{"uid":"{{counter}}","includeSnapshot":true}""");
            StringAssert.StartsWith(clicked, "Successfully clicked on the element\n## Latest page snapshot\n");
            // The element keeps its identifier, shows its new name, and has the focus.
            StringAssert.Contains(clicked, $"uid={counter} button \"Clicked 1 times\" focusable focused\n");

            var verbose = await driver.CallAsync("take_snapshot", """{"verbose":true}""");
            StringAssert.Contains(verbose, " paragraph\n");
            StringAssert.Contains(verbose, " LabelText \"Name\"\n");
            // A verbose snapshot spells out the text of a control as well.
            StringAssert.Matches(verbose, new Regex("""uid=\S+ button "Disabled" disableable disabled\n\s+uid=\S+ "Disabled"\n"""));
            StringAssert.Contains(verbose, " generic\n");

            // The typed interface returns the same tree as an object.
            var page = driver.Automation.SelectedPage!;
            var typed = await page.TakeSnapshotAsync(cancellationToken: driver.CancellationToken);
            Assert.AreSame(typed, page.LatestSnapshot);
            Assert.AreEqual("RootWebArea", typed.Root.Role);
            var button = typed.Find(counter)!;
            Assert.AreEqual(("button", "Clicked 1 times"), (button.Role, button.Name));
            Assert.IsTrue((bool)button.Properties["focused"]);
            Assert.IsFalse(typed.IsVerbose);
        });
    }

    [TestMethod]
    public async Task InputToolsDriveAFormLikeAUser()
    {
        var directory = CreateDirectory();
        File.WriteAllText(Path.Combine(directory, "upload.txt"), "hello upload");
        await RunAsync(async driver =>
        {
            await driver.CallAsync("navigate_page", """{"url":"app://neoastra/automation.html"}""");
            await driver.CallAsync("take_snapshot");
            var counter = driver.Uid("button \"Clicked 0 times\"");
            var name = driver.Uid("textbox \"Name\"");

            Assert.AreEqual("Successfully double clicked on the element", await driver.CallAsync("click", $$"""{"uid":"{{counter}}","dblClick":true}"""));
            Assert.AreEqual("""{"text":"Clicked 2 times","double":"yes"}""", await driver.EvaluateAsync("() => ({ text: document.getElementById('counter').textContent, double: document.getElementById('counter').dataset.double })"));

            // A disabled element and an unknown one are refused with what to do about it.
            var disabled = await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("button \"Disabled\"")}}"}""", expectError: true);
            StringAssert.Contains(disabled, "is disabled");
            Assert.AreEqual("Element uid \"99_99\" not found on the page. Take a new snapshot with take_snapshot.", await driver.CallAsync("click", """{"uid":"99_99"}""", expectError: true));

            var form = $$"""
                {"elements":[
                  {"uid":"{{name}}","value":"Ada Lovelace"},
                  {"uid":"{{driver.Uid("textbox \"Email\"")}}","value":"ada@example.com"},
                  {"uid":"{{driver.Uid("combobox \"Color\"")}}","value":"Blue"},
                  {"uid":"{{driver.Uid("checkbox \"I agree\"")}}","value":"true"},
                  {"uid":"{{driver.Uid("radio \"Large\"")}}","value":"true"},
                  {"uid":"{{driver.Uid("textbox \"Notes\"")}}","value":"Line one"},
                  {"uid":"{{driver.Uid("textbox \"Editor\"")}}","value":"Rich text"}]}
                """;
            Assert.AreEqual("Successfully filled out the form", await driver.CallAsync("fill_form", form));

            Assert.AreEqual("File uploaded from upload.txt.", await driver.CallAsync("upload_file", $$"""{"uid":"{{driver.Uid("button \"Attachment\"")}}","filePaths":["upload.txt"]}"""));
            Assert.AreEqual("\"files upload.txt:12\"", await driver.EvaluateAsync("() => document.getElementById('result').textContent"));
            Assert.AreEqual("\"hello upload\"", await driver.EvaluateAsync("async () => await document.getElementById('file').files[0].text()"));

            Assert.AreEqual("Successfully clicked on the element", await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("button \"Send\"")}}"}"""));
            using (var submitted = JsonDocument.Parse(JsonDocument.Parse(await driver.EvaluateAsync("() => document.getElementById('result').textContent")).RootElement.GetString()!))
            {
                var data = submitted.RootElement;
                Assert.AreEqual("Ada Lovelace", data.GetProperty("name").GetString());
                Assert.AreEqual("ada@example.com", data.GetProperty("email").GetString());
                Assert.AreEqual("b", data.GetProperty("color").GetString());
                Assert.AreEqual("on", data.GetProperty("agree").GetString());
                Assert.AreEqual("l", data.GetProperty("size").GetString());
                Assert.AreEqual("Line one", data.GetProperty("notes").GetString());
                Assert.AreEqual("Rich text", data.GetProperty("editor").GetString());
            }

            // A toggle takes "true" or "false" only, and a select needs one of its options.
            StringAssert.Contains(await driver.CallAsync("fill", $$"""{"uid":"{{driver.Uid("checkbox \"I agree\"")}}","value":"yes"}""", expectError: true),
                "Checkboxes, radio boxes and toggles require \"true\" or \"false\" value, but yes was used");
            StringAssert.Contains(await driver.CallAsync("fill", $$"""{"uid":"{{driver.Uid("combobox \"Color\"")}}","value":"Purple"}""", expectError: true),
                "Could not find option with text \"Purple\"");
            Assert.AreEqual("Successfully filled out the element", await driver.CallAsync("fill", $$"""{"uid":"{{driver.Uid("checkbox \"I agree\"")}}","value":"false"}"""));
            Assert.AreEqual("false", await driver.EvaluateAsync("() => document.getElementById('agree').checked"));
            // An option is also chosen by clicking it.
            Assert.AreEqual("Successfully clicked on the element", await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("option \"Red\"")}}"}"""));
            Assert.AreEqual("\"r\"", await driver.EvaluateAsync("() => document.getElementById('color').value"));

            Assert.AreEqual("Successfully hovered over the element", await driver.CallAsync("hover", $$"""{"uid":"{{driver.Uid("\"Hover me\"")}}"}"""));
            Assert.AreEqual("Successfully dragged an element", await driver.CallAsync("drag", $$"""{"from_uid":"{{driver.Uid("\"Drag me\"")}}","to_uid":"{{driver.Uid("\"Drop here\"")}}"}"""));
            Assert.AreEqual("""{"hover":"hovered","drop":"Dropped payload"}""",
                await driver.EvaluateAsync("() => ({ hover: document.getElementById('hover-state').textContent, drop: document.getElementById('drop-target').textContent })"));

            // The keyboard acts on the focused element: select all, type over it, and move on with Tab.
            await driver.CallAsync("click", $$"""{"uid":"{{name}}"}""");
            Assert.AreEqual("Successfully pressed key: Control+A", await driver.CallAsync("press_key", """{"key":"Control+A"}"""));
            Assert.AreEqual("Typed text \"Grace + Tab\"", await driver.CallAsync("type_text", """{"text":"Grace","submitKey":"Tab"}"""));
            Assert.AreEqual("Successfully pressed key: Control+k", await driver.CallAsync("press_key", """{"key":"Control+k"}"""));
            Assert.AreEqual("""{"name":"Grace","active":"email","result":"shortcut"}""",
                await driver.EvaluateAsync("() => ({ name: document.getElementById('name').value, active: document.activeElement.id, result: document.getElementById('result').textContent })"));
            await driver.CallAsync("press_key", """{"key":"Shift+Tab"}""");
            await driver.CallAsync("press_key", """{"key":"End"}""");
            await driver.CallAsync("press_key", """{"key":"Backspace"}""");
            await driver.CallAsync("type_text", """{"text":"E!"}""");
            Assert.AreEqual("\"GracE!\"", await driver.EvaluateAsync("() => document.getElementById('name').value"));
            // Enter in a text field submits its form.
            await driver.CallAsync("press_key", """{"key":"Enter"}""");
            StringAssert.Contains(await driver.EvaluateAsync("() => document.getElementById('result').textContent"), "GracE!");
            StringAssert.Contains(await driver.CallAsync("press_key", """{"key":"Bogus"}""", expectError: true), "Bogus is not a known key");

            // A click on an element far down the page scrolls it into view first.
            Assert.AreEqual("0", await driver.EvaluateAsync("() => scrollY"));
            await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("button \"Far button\"")}}"}""");
            Assert.AreEqual("true", await driver.EvaluateAsync("() => scrollY > 0 && document.activeElement.id === 'far'"));
            await driver.CallAsync("click_at", """{"x":5,"y":5}""");
            Assert.AreEqual("\"BODY\"", await driver.EvaluateAsync("() => document.activeElement.tagName"));
        }, directory);
    }

    [TestMethod]
    public async Task TypedCharactersCarryTheCodesOfTheirKeys()
    {
        // Keys of the main block of a US keyboard: the code and the key code of each, and the two characters that it types.
        (string Code, int KeyCode, char Lower, char Upper)[] keys =
        [
            ("Backquote", 192, '`', '~'), ("Digit1", 49, '1', '!'), ("Digit2", 50, '2', '@'), ("Digit3", 51, '3', '#'), ("Digit4", 52, '4', '$'),
            ("Digit5", 53, '5', '%'), ("Digit6", 54, '6', '^'), ("Digit7", 55, '7', '&'), ("Digit8", 56, '8', '*'), ("Digit9", 57, '9', '('),
            ("Digit0", 48, '0', ')'), ("Minus", 189, '-', '_'), ("Equal", 187, '=', '+'), ("BracketLeft", 219, '[', '{'), ("BracketRight", 221, ']', '}'),
            ("Backslash", 220, '\\', '|'), ("Semicolon", 186, ';', ':'), ("Quote", 222, '\'', '"'), ("Comma", 188, ',', '<'), ("Period", 190, '.', '>'),
            ("Slash", 191, '/', '?'), ("KeyA", 65, 'a', 'A'), ("KeyZ", 90, 'z', 'Z'),
        ];
        // The space has a key as well, and the last character is on no key of that keyboard.
        var text = string.Concat(keys.Select(static key => $"{key.Lower}{key.Upper}")) + " \u00e9";

        await RunAsync(async driver =>
        {
            await driver.CallAsync("navigate_page", """{"url":"app://neoastra/automation.html"}""");
            await driver.CallAsync("take_snapshot");
            await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("textbox \"Notes\"")}}"}""");
            Assert.AreEqual("true", await driver.EvaluateAsync("""
                () => {
                  window.keys = [];
                  for (const type of ['keydown', 'keypress', 'keyup']) {
                    document.getElementById('notes').addEventListener(type, e => window.keys.push(
                      { type: e.type, key: e.key, code: e.code, keyCode: e.keyCode, which: e.which, charCode: e.charCode, location: e.location, shift: e.shiftKey }));
                  }
                  return true;
                }
                """));

            await driver.CallAsync("type_text", $$"""{"text":{{JsonSerializer.Serialize(text, AutomationTestsJsonContext.Default.String)}}}""");
            using (var typed = JsonDocument.Parse(await driver.EvaluateAsync("() => document.getElementById('notes').value"))) Assert.AreEqual(text, typed.RootElement.GetString());

            // A key that types a character says which key it is when it goes down and up, as a keyboard does: a page
            // that reads the code or the key code of an event took a typed "." for no key at all. Shift is held for the
            // upper character of a key, so that "?" is not taken for the "/" of the same key.
            using (var recorded = JsonDocument.Parse(await driver.EvaluateAsync("() => window.keys")))
            {
                var events = recorded.RootElement;
                Assert.AreEqual(text.Length * 3, events.GetArrayLength());
                for (var index = 0; index < text.Length; index++)
                {
                    var character = text[index];
                    var key = keys.FirstOrDefault(key => key.Lower == character || key.Upper == character);
                    var (code, keyCode, shift) = character == ' ' ? ("Space", 32, false) : key.Code is null ? (string.Empty, 0, false) : (key.Code, key.KeyCode, key.Upper == character);
                    var where = $"'{character}'";
                    AssertKey(events[index * 3], "keydown", character, code, keyCode, 0, shift, where);
                    AssertKey(events[index * 3 + 1], "keypress", character, code, character, character, shift, where);
                    AssertKey(events[index * 3 + 2], "keyup", character, code, keyCode, 0, shift, where);
                }
            }

            // press_key takes the names of Puppeteer, where "-" is the key of the numeric keypad: a text is typed on the
            // main block. Where a name is the key of a character there, both tools press the same key.
            Assert.AreEqual("true", await driver.EvaluateAsync("() => { window.keys.length = 0; return true; }"));
            await driver.CallAsync("press_key", """{"key":"-"}""");
            await driver.CallAsync("press_key", """{"key":"."}""");
            using (var recorded = JsonDocument.Parse(await driver.EvaluateAsync("() => window.keys.filter(e => e.type === 'keydown')")))
            {
                var events = recorded.RootElement;
                Assert.AreEqual(2, events.GetArrayLength());
                Assert.AreEqual("NumpadSubtract", events[0].GetProperty("code").GetString());
                Assert.AreEqual(109, events[0].GetProperty("keyCode").GetInt32());
                Assert.AreEqual(3, events[0].GetProperty("location").GetInt32());
                AssertKey(events[1], "keydown", '.', "Period", 190, 0, false, "press_key");
            }

            static void AssertKey(JsonElement actual, string type, char key, string code, int keyCode, int charCode, bool shift, string where)
            {
                Assert.AreEqual(type, actual.GetProperty("type").GetString(), where);
                Assert.AreEqual(key.ToString(), actual.GetProperty("key").GetString(), $"{where} {type} key");
                Assert.AreEqual(code, actual.GetProperty("code").GetString(), $"{where} {type} code");
                Assert.AreEqual(keyCode, actual.GetProperty("keyCode").GetInt32(), $"{where} {type} keyCode");
                Assert.AreEqual(keyCode, actual.GetProperty("which").GetInt32(), $"{where} {type} which");
                Assert.AreEqual(charCode, actual.GetProperty("charCode").GetInt32(), $"{where} {type} charCode");
                Assert.AreEqual(0, actual.GetProperty("location").GetInt32(), $"{where} {type} location");
                Assert.AreEqual(shift, actual.GetProperty("shift").GetBoolean(), $"{where} {type} shiftKey");
            }
        });
    }

    [TestMethod]
    public async Task TypingGoesIntoAnElementThatTakesItsTextFromAnEditContext()
    {
        await RunAsync(async driver =>
        {
            await driver.CallAsync("navigate_page", """{"url":"app://neoastra/editcontext.html"}""");
            await driver.CallAsync("take_snapshot");

            // The keys arrive as they do from a keyboard: the EditContext has the character by the time the page hears of it.
            await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("textbox \"Plain editor\"")}}"}""");
            Assert.AreEqual("Typed text \"a B\"", await driver.CallAsync("type_text", """{"text":"a B"}"""));
            Assert.AreEqual(
                """{"text":"a B","selection":[3,3],"events":["keydown \"a\"","keypress \"a\"","beforeinput insertText \"a\"","textupdate \"a\" 0-0 1-1","keyup \"a\"","keydown \" \"","keypress \" \"","beforeinput insertText \" \"","textupdate \" \" 1-1 2-2","keyup \" \"","keydown \"B\"","keypress \"B\"","beforeinput insertText \"B\"","textupdate \"B\" 2-2 3-3","keyup \"B\""]}""",
                await driver.EvaluateAsync("() => editorState('plain')"));
            await driver.CallAsync("press_key", """{"key":"c"}""");
            Assert.AreEqual("\"a Bc\"", await driver.EvaluateAsync("() => editorState('plain').text"));

            // The keys that delete take the selection, or the character next to the cursor.
            await driver.CallAsync("press_key", """{"key":"Backspace"}""");
            Assert.AreEqual(
                """{"text":"a B","selection":[3,3],"events":["keydown \"Backspace\"","beforeinput deleteContentBackward","textupdate \"\" 3-4 3-3","keyup \"Backspace\""]}""",
                await driver.EvaluateAsync("() => editorState('plain')"));
            await driver.EvaluateAsync("() => { editors.plain.context.updateSelection(2, 0); }");
            await driver.CallAsync("type_text", """{"text":"X"}""");
            Assert.AreEqual(
                """{"text":"XB","selection":[1,1],"events":["keydown \"X\"","keypress \"X\"","beforeinput insertText \"X\"","textupdate \"X\" 0-2 1-1","keyup \"X\""]}""",
                await driver.EvaluateAsync("() => editorState('plain')"));
            await driver.CallAsync("press_key", """{"key":"Delete"}""");
            await driver.CallAsync("press_key", """{"key":"Delete"}""");
            // The page is asked before the engine looks for something to delete.
            Assert.AreEqual(
                """{"text":"X","selection":[1,1],"events":["keydown \"Delete\"","beforeinput deleteContentForward","textupdate \"\" 1-2 1-1","keyup \"Delete\"","keydown \"Delete\"","beforeinput deleteContentForward","keyup \"Delete\""]}""",
                await driver.EvaluateAsync("() => editorState('plain')"));

            // A line break is asked of the page, which puts it in.
            await driver.CallAsync("press_key", """{"key":"Enter"}""");
            await driver.CallAsync("press_key", """{"key":"Shift+Enter"}""");
            Assert.AreEqual(
                """{"text":"X\n\n","selection":[3,3],"events":["keydown \"Enter\"","keypress \"Enter\"","beforeinput insertParagraph","keyup \"Enter\"","keydown \"Shift\"","keydown \"Enter\"","keypress \"Enter\"","beforeinput insertLineBreak","keyup \"Enter\"","keyup \"Shift\""]}""",
                await driver.EvaluateAsync("() => editorState('plain')"));

            // A character of two code units, or one with a mark, is deleted whole.
            await driver.CallAsync("type_text", """{"text":"😀é"}""");
            Assert.AreEqual("[7,7,7]", await driver.EvaluateAsync("() => [editorState('plain').text.length, ...editors.plain.selection()]"));
            await driver.CallAsync("press_key", """{"key":"Backspace"}""");
            await driver.CallAsync("press_key", """{"key":"Backspace"}""");
            Assert.AreEqual(
                """{"text":"X\n\n","selection":[3,3],"events":["keydown \"Backspace\"","beforeinput deleteContentBackward","textupdate \"\" 5-7 5-5","keyup \"Backspace\"","keydown \"Backspace\"","beforeinput deleteContentBackward","textupdate \"\" 3-5 3-3","keyup \"Backspace\""]}""",
                await driver.EvaluateAsync("() => editorState('plain')"));

            // A page that cancels an event stops what follows it.
            foreach (var (canceled, events) in new[]
            {
                ("beforeinput", """["keydown \"z\"","keypress \"z\"","beforeinput insertText \"z\"","keyup \"z\"","keydown \"Backspace\"","beforeinput deleteContentBackward","keyup \"Backspace\"","keydown \"Enter\"","keypress \"Enter\"","beforeinput insertParagraph","keyup \"Enter\""]"""),
                ("keypress", """["keydown \"z\"","keypress \"z\"","keyup \"z\"","keydown \"Backspace\"","beforeinput deleteContentBackward","textupdate \"\" 2-3 2-2","keyup \"Backspace\"","keydown \"Enter\"","keypress \"Enter\"","keyup \"Enter\""]"""),
                ("keydown", """["keydown \"z\"","keyup \"z\"","keydown \"Backspace\"","keyup \"Backspace\"","keydown \"Enter\"","keyup \"Enter\""]"""),
            })
            {
                await driver.EvaluateAsync($"() => {{ editors.plain.context.updateText(0, 99, 'X\\n\\n'); editors.plain.context.updateSelection(3, 3); cancel.{canceled} = true; }}");
                await driver.CallAsync("type_text", """{"text":"z"}""");
                await driver.CallAsync("press_key", """{"key":"Backspace"}""");
                await driver.CallAsync("press_key", """{"key":"Enter"}""");
                Assert.AreEqual(events, await driver.EvaluateAsync($"() => {{ delete cancel.{canceled}; return editorState('plain').events; }}"), canceled);
            }

            // An element with an EditContext takes the focus like editable content, without a tab index.
            await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("\"Bare editor\"")}}"}""");
            await driver.CallAsync("type_text", """{"text":"!"}""");
            Assert.AreEqual("""["bare","Bare editor!"]""", await driver.EvaluateAsync("() => [document.activeElement.id, editorState('bare').text]"));

            // The EditContext of an element comes before its editable content, which the engine leaves alone.
            await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("textbox \"Editable editor\"")}}"}""");
            await driver.CallAsync("type_text", """{"text":"ok"}""");
            await driver.CallAsync("press_key", """{"key":"Backspace"}""");
            var both = await driver.EvaluateAsync("() => editorState('both')");
            StringAssert.StartsWith(both, """{"text":"o","selection":[1,1],""");
            Assert.DoesNotContain("\"input\"", both);

            // An editor with a model of its own puts the text where its selection is, and takes its keys itself. The
            // element with its EditContext has no width, as in Monaco: a click on it lands on what the editor draws.
            await driver.EvaluateAsync("() => { editors.code.set('one\\ntwo\\nthree', 5, 5); }");
            StringAssert.Matches(await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("textbox \"Code editor\"")}}"}"""),
                new Regex("""^Successfully clicked on the element\nAnother element, <div id="code-(view|text)".*>, covers that point of the element and received the event instead\.$"""));
            Assert.AreEqual("\"code\"", await driver.EvaluateAsync("() => document.activeElement.id"));
            await driver.CallAsync("type_text", """{"text":"X"}""");
            await driver.CallAsync("press_key", """{"key":"Enter"}""");
            Assert.AreEqual("\"one\\ntX\\nwo\\nthree\"", await driver.EvaluateAsync("() => editorState('code').text"));
            await driver.CallAsync("press_key", """{"key":"Backspace"}""");
            await driver.CallAsync("press_key", """{"key":"Control+A"}""");
            await driver.CallAsync("type_text", """{"text":"Z"}""");
            Assert.AreEqual("""{"text":"Z","selection":[1,1],"events":["own Backspace","own A","textupdate"]}""", await driver.EvaluateAsync("() => editorState('code')"));
        });
    }

    [TestMethod]
    public async Task FillReplacesTheTextOfAnEditorWithAnEditContext()
    {
        await RunAsync(async driver =>
        {
            await driver.CallAsync("navigate_page", """{"url":"app://neoastra/editcontext.html"}""");
            await driver.CallAsync("take_snapshot");
            var plain = driver.Uid("textbox \"Plain editor\"");
            var code = driver.Uid("textbox \"Code editor\"");

            // The keys that select everything are offered to the page first. A page that leaves them alone has its
            // text in the EditContext, which is replaced whole.
            Assert.AreEqual("Successfully filled out the element", await driver.CallAsync("fill", $$"""{"uid":"{{plain}}","value":"first"}"""));
            Assert.AreEqual(
                """{"text":"first","selection":[5,5],"events":["keydown \"Control\"","keydown \"a\"","keyup \"a\"","keyup \"Control\"","beforeinput insertReplacementText \"first\"","textupdate \"first\" 0-0 5-5"]}""",
                await driver.EvaluateAsync("() => editorState('plain')"));
            Assert.AreEqual("\"plain\"", await driver.EvaluateAsync("() => document.activeElement.id"));
            await driver.EvaluateAsync("() => { editors.plain.context.updateSelection(2, 2); }");
            await driver.CallAsync("fill", $$"""{"uid":"{{plain}}","value":"second\n  line ("}""");
            Assert.AreEqual(
                """{"text":"second\n  line (","selection":[15,15],"events":["keydown \"Control\"","keydown \"a\"","keyup \"a\"","keyup \"Control\"","beforeinput insertReplacementText \"second\\n  line (\"","textupdate \"second\\n  line (\" 0-5 15-15"]}""",
                await driver.EvaluateAsync("() => editorState('plain')"));
            await driver.CallAsync("fill", $$"""{"uid":"{{plain}}","value":""}""");
            await driver.CallAsync("fill", $$"""{"uid":"{{plain}}","value":""}""");
            Assert.AreEqual(
                """{"text":"","selection":[0,0],"events":["keydown \"Control\"","keydown \"a\"","keyup \"a\"","keyup \"Control\"","beforeinput deleteContentBackward","textupdate \"\" 0-15 0-0","keydown \"Control\"","keydown \"a\"","keyup \"a\"","keyup \"Control\"","beforeinput deleteContentBackward"]}""",
                await driver.EvaluateAsync("() => editorState('plain')"));

            // An editor that selects everything itself holds a part of its text in its EditContext only. The text
            // replaces the selection as a paste does, without what the editor does for typed characters.
            await driver.EvaluateAsync("() => { editors.code.set('one\\ntwo\\nthree', 5, 5); }");
            await driver.CallAsync("fill", $$"""{"uid":"{{code}}","value":"new\n  text ("}""");
            Assert.AreEqual("""{"text":"new\n  text (","selection":[12,12],"events":["own a","paste"]}""", await driver.EvaluateAsync("() => editorState('code')"));
            await driver.EvaluateAsync("() => { editors.code.set('one\\ntwo\\nthree', 4, 9); }");
            await driver.CallAsync("fill", $$"""{"uid":"{{code}}","value":"Q"}""");
            Assert.AreEqual("\"Q\"", await driver.EvaluateAsync("() => editorState('code').text"));
            await driver.EvaluateAsync("() => { editors.code.set('one\\ntwo\\nthree', 5, 5); }");
            await driver.CallAsync("fill", $$"""{"uid":"{{code}}","value":""}""");
            Assert.AreEqual("""{"text":"","selection":[0,0],"events":["own a","own Backspace"]}""", await driver.EvaluateAsync("() => editorState('code')"));

            // A text of the editor names the editor, and an editor that is editable content as well is filled through its EditContext.
            await driver.CallAsync("fill", $$"""{"uid":"{{driver.Uid("\"Bare editor\"")}}","value":"bare"}""");
            await driver.CallAsync("fill", $$"""{"uid":"{{driver.Uid("textbox \"Editable editor\"")}}","value":"both"}""");
            Assert.AreEqual("""["bare","both",false]""", await driver.EvaluateAsync("() => [editorState('bare').text, editorState('both').text, editorState('both').events.includes('input')]"));

            var form = $$"""{"elements":[{"uid":"{{plain}}","value":"one"},{"uid":"{{code}}","value":"two\nlines"}]}""";
            Assert.AreEqual("Successfully filled out the form", await driver.CallAsync("fill_form", form));
            Assert.AreEqual("""["one","two\nlines"]""", await driver.EvaluateAsync("() => [editorState('plain').text, editorState('code').text]"));

            StringAssert.Contains(await driver.CallAsync("fill", $$"""{"uid":"{{driver.Uid("textbox \"Locked editor\"")}}","value":"no"}""", expectError: true), "is read-only");
            Assert.AreEqual("\"\"", await driver.EvaluateAsync("() => editorState('locked').text"));
        });
    }

    [TestMethod]
    public async Task ADialogStopsAnActionUntilItIsHandled()
    {
        await RunAsync(async driver =>
        {
            await driver.CallAsync("navigate_page", """{"url":"app://neoastra/automation.html"}""");
            await driver.CallAsync("take_snapshot");
            var ask = driver.Uid("button \"Ask\"");
            var page = driver.Automation.SelectedPage!;

            Assert.AreEqual(
                "The element was clicked and it opened a dialog.\n# Open dialog\nconfirm: Proceed?.\nCall handle_dialog to handle it before continuing.",
                await driver.CallAsync("click", $$"""{"uid":"{{ask}}"}"""));
            Assert.AreEqual(new NeoAutomationDialog(NeoScriptDialogKind.Confirm, "Proceed?", null), page.Dialog);

            // The page runs no script while the dialog is open, so the tools that need it say so at once.
            Assert.AreEqual("A dialog is open (confirm: Proceed?). Call handle_dialog to handle it before continuing.", await driver.CallAsync("take_snapshot", expectError: true));
            StringAssert.Contains(await driver.CallAsync("evaluate_script", """{"function":"() => 1"}""", expectError: true), "A dialog is open");
            StringAssert.Contains(await driver.CallAsync("list_pages"), "# Open dialog\nconfirm: Proceed?.");

            StringAssert.StartsWith(await driver.CallAsync("handle_dialog", """{"action":"accept"}"""), "Successfully accepted the dialog\n## Pages\n");
            Assert.IsNull(page.Dialog);
            Assert.AreEqual("\"Confirmed\"", await driver.EvaluateAsync("() => document.getElementById('confirm').textContent"));
            Assert.AreEqual("No open dialog found", await driver.CallAsync("handle_dialog", """{"action":"accept"}""", expectError: true));

            await driver.CallAsync("click", $$"""{"uid":"{{ask}}"}""");
            StringAssert.StartsWith(await driver.CallAsync("handle_dialog", """{"action":"dismiss"}"""), "Successfully dismissed the dialog");
            Assert.AreEqual("\"Declined\"", await driver.EvaluateAsync("() => document.getElementById('confirm').textContent"));

            // A script answers the dialogs it opens itself: accepted by default, or as it says.
            Assert.AreEqual("true", await driver.EvaluateAsync("() => confirm('sure?')"));
            Assert.AreEqual("false", await driver.EvaluateAsync("() => confirm('sure?')", ""","dialogAction":"dismiss" """));
            Assert.AreEqual("\"typed\"", await driver.EvaluateAsync("() => prompt('Value?', 'initial')", ""","dialogAction":"typed" """));
            Assert.AreEqual("\"initial\"", await driver.EvaluateAsync("() => prompt('Value?', 'initial')"));
            Assert.IsNull(page.Dialog);

            // A prompt that an action opens is answered with a text, or with its default one.
            await driver.EvaluateAsync("() => { document.getElementById('hover-target').onclick = () => { window.answer = prompt('Name?', 'nobody'); }; }");
            StringAssert.Contains(await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("\"Hover me\"")}}"}"""), "# Open dialog\nprompt: Name? (default value: \"nobody\").");
            await driver.CallAsync("handle_dialog", """{"action":"accept","promptText":"NeoAstra"}""");
            Assert.AreEqual("\"NeoAstra\"", await driver.EvaluateAsync("() => window.answer"));
        });
    }

    [TestMethod]
    public async Task ConsoleAndNetworkLogsAreKeptForEachNavigation()
    {
        await RunAsync(async driver =>
        {
            await driver.CallAsync("navigate_page", """{"url":"app://neoastra/automation.html"}""");
            await driver.CallAsync("take_snapshot");
            StringAssert.Matches(await driver.CallAsync("list_console_messages"), new Regex("""^## Console messages\nShowing 1-1 of 1 \(Page 1 of 1\)\.\nmsgid=\d+ \[info\] fixture ready \(1 args\)$"""));

            await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("button \"Log\"")}}"}""");
            var messages = await driver.CallAsync("list_console_messages");
            StringAssert.Matches(messages, new Regex("""Showing 1-4 of 4 \(Page 1 of 1\)\.\nmsgid=\d+ \[info\] fixture ready \(1 args\)\nmsgid=\d+ \[log\] clicked log \{answer: 42\} \(3 args\)\nmsgid=\d+ \[warn\] careful \(1 args\)\nmsgid=\d+ \[error\] Error: boom \(1 args\)$"""));
            var logId = Regex.Match(messages, """msgid=(\d+) \[log\]""").Groups[1].Value;

            var errors = await driver.CallAsync("list_console_messages", """{"types":["error"],"includeStackTraces":true}""");
            StringAssert.Contains(errors, "Showing 1-1 of 1 (Page 1 of 1).");
            // The stack names the page, not the automation script that dispatched the click.
            StringAssert.Matches(errors, new Regex("""\[error\] Error: boom \(1 args\)\nat \S.* \(app://neoastra/automation\.html:\d+:\d+\)\nNote: stack trace line and column numbers use 1-based indexing$"""));
            Assert.DoesNotContain("neoastra-automation", errors);

            var detail = await driver.CallAsync("get_console_message", $$"""{"msgid":{{logId}}}""");
            StringAssert.Matches(detail, new Regex("""^ID: \d+\nMessage: log> clicked log \{answer: 42\}\nSource: app://neoastra/automation\.html:\d+:\d+\n### Arguments\nArg #0: clicked %s\nArg #1: log\nArg #2: \{answer: 42\}$"""));
            StringAssert.Contains(await driver.CallAsync("get_console_message", """{"msgid":9999}""", expectError: true), "No console message with msgid 9999");

            // Uncaught errors and rejections are messages too, and repeated ones are counted.
            await driver.EvaluateAsync("() => { setTimeout(() => { throw new RangeError('late failure'); }, 0); Promise.reject(new Error('rejected')); for (let i = 0; i < 3; i++) console.debug('again'); }");
            await driver.CallAsync("wait_for", """{"text":["Automation fixture"]}""");
            var more = await driver.CallAsync("list_console_messages", """{"types":["error","debug"]}""");
            StringAssert.Contains(more, "[error] Uncaught RangeError: late failure (0 args)");
            StringAssert.Contains(more, "[error] Uncaught (in promise) Error: rejected (0 args)");
            StringAssert.Matches(more, new Regex("""\[debug\] again \(1 args\) \[3 times\]"""));

            await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("button \"Fetch\"")}}"}""");
            await driver.CallAsync("wait_for", """{"text":["fetched 7"]}""");
            var requests = await driver.CallAsync("list_network_requests");
            StringAssert.Matches(requests, new Regex("""^## Network requests\nShowing 1-2 of 2 \(Page 1 of 1\)\.\nreqid=\d+ GET app://neoastra/automation\.html \[200\]\nreqid=\d+ GET app://neoastra/data\.json \[200\]$"""));
            var fetchId = Regex.Match(requests, """reqid=(\d+) GET \S+data\.json""").Groups[1].Value;
            var request = await driver.CallAsync("get_network_request", $$"""{"reqid":{{fetchId}}}""");
            StringAssert.StartsWith(request, "## Request app://neoastra/data.json\nStatus: 200\nResource type: fetch\n### Request Headers\n- x-test:1\n### Response Headers\n");
            StringAssert.Contains(request, "- content-type:application/json; charset=utf-8\n");
            StringAssert.EndsWith(request, "### Response Body\n{\"value\": 7}");
            StringAssert.Matches(await driver.CallAsync("list_network_requests", """{"resourceTypes":["fetch"]}"""), new Regex("""Showing 1-1 of 1 \(Page 1 of 1\)\.\nreqid=\d+ GET \S+data\.json \[200\]$"""));
            Assert.AreEqual("## Network requests\nNo requests found.", await driver.CallAsync("list_network_requests", """{"resourceTypes":["websocket"]}"""));

            // A request that fails and one that posts a body.
            await driver.EvaluateAsync("async () => { await fetch('missing.json').catch(() => 0); await fetch('data.json', { method: 'POST', body: JSON.stringify({ a: 1 }), headers: { 'content-type': 'application/json' } }).catch(() => 0); }");
            var posted = await driver.CallAsync("list_network_requests", """{"resourceTypes":["fetch"]}""");
            StringAssert.Matches(posted, new Regex("""reqid=\d+ GET app://neoastra/missing\.json \[404\]"""));
            var postId = Regex.Match(posted, """reqid=(\d+) POST""").Groups[1].Value;
            StringAssert.Contains(await driver.CallAsync("get_network_request", $$"""{"reqid":{{postId}}}"""), "### Request Body\n{\"a\":1}");

            // A new document starts a new list; the earlier ones stay available.
            await driver.CallAsync("take_snapshot");
            StringAssert.Contains(await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("link \"Second page\"")}}"}"""), "Page navigated to app://neoastra/second.html.");
            StringAssert.Matches(await driver.CallAsync("list_console_messages"), new Regex("""Showing 1-1 of 1 \(Page 1 of 1\)\.\nmsgid=\d+ \[log\] second ready \(1 args\)$"""));
            var preserved = await driver.CallAsync("list_console_messages", """{"includePreservedMessages":true}""");
            StringAssert.Contains(preserved, "[info] fixture ready (1 args)");
            StringAssert.Contains(preserved, "[log] second ready (1 args)");
            var paged = await driver.CallAsync("list_console_messages", """{"includePreservedMessages":true,"pageSize":2,"pageIdx":1}""");
            StringAssert.Matches(paged, new Regex("""Showing 3-4 of \d+ \(Page 2 of \d+\)\.\nNext page: 2\nPrevious page: 0\n"""));
            StringAssert.Contains(await driver.CallAsync("list_console_messages", """{"pageSize":2,"pageIdx":7}"""), "Invalid page number provided. Showing first page.");
            StringAssert.Matches(await driver.CallAsync("list_network_requests"), new Regex("""Showing 1-1 of 1 \(Page 1 of 1\)\.\nreqid=\d+ GET app://neoastra/second\.html \[200\]$"""));
            StringAssert.Contains(await driver.CallAsync("list_network_requests", """{"includePreservedRequests":true}"""), "data.json");

            // The typed interface gives the same messages and requests as objects.
            var page = driver.Automation.SelectedPage!;
            var typedMessages = await page.GetConsoleMessagesAsync(includePreserved: true, driver.CancellationToken);
            var warning = typedMessages.Single(static message => message.Type == "warn");
            Assert.AreEqual("careful", warning.Text);
            Assert.AreEqual("app://neoastra/automation.html", warning.Url);
            Assert.IsGreaterThan(0, warning.Line);
            Assert.AreSame(warning, await page.GetConsoleMessageAsync(warning.Id, driver.CancellationToken));
            var typedRequests = await page.GetNetworkRequestsAsync(includePreserved: true, driver.CancellationToken);
            var fetched = typedRequests.First(static request => request.Url.EndsWith("data.json", StringComparison.Ordinal) && request.Method == "GET");
            Assert.AreEqual((200, "200", "fetch", "application/json"), (fetched.StatusCode, fetched.Status, fetched.ResourceType, fetched.MimeType));
            Assert.AreEqual("1", fetched.RequestHeaders["X-Test"]);
            Assert.AreEqual("{\"value\": 7}", fetched.ResponseBody);
        });
    }

    [TestMethod]
    public async Task NetworkLogListsTheFilesAPageLoadsFromItsOwnScheme()
    {
        await RunAsync(async driver =>
        {
            // Resource timing reports no load from a scheme other than http and https; the elements of the page do.
            await driver.CallAsync("navigate_page", """{"url":"app://neoastra/resources.html"}""");
            await driver.CallAsync("wait_for", """{"text":["Resources"]}""");
            Assert.AreEqual("\"Resources loaded\"", await driver.EvaluateAsync("() => document.title"));
            var requests = await driver.CallAsync("list_network_requests");
            StringAssert.Matches(requests, new Regex("""^## Network requests\nShowing 1-4 of 4 \(Page 1 of 1\)\.\nreqid=\d+ GET app://neoastra/resources\.html \[200\]\n"""));
            StringAssert.Matches(requests, new Regex("""reqid=\d+ GET app://neoastra/style\.css \[(200|finished)\]"""));
            StringAssert.Matches(requests, new Regex("""reqid=\d+ GET app://neoastra/script\.js \[(200|finished)\]"""));
            // An address is listed once, however many elements ask for it.
            Assert.HasCount(1, Regex.Matches(requests, """reqid=\d+ GET app://neoastra/missing\.png \[(404|failed to load)\]"""));
            StringAssert.Matches(await driver.CallAsync("list_network_requests", """{"resourceTypes":["stylesheet","script"]}"""), new Regex("""Showing 1-2 of 2 \(Page 1 of 1\)\."""));
            StringAssert.Matches(await driver.CallAsync("list_network_requests", """{"resourceTypes":["image"]}"""), new Regex("""Showing 1-1 of 1 \(Page 1 of 1\)\.\nreqid=\d+ GET \S+missing\.png """));
            StringAssert.Contains(await driver.CallAsync("list_console_messages", """{"types":["error"]}"""), "[error] Failed to load resource: app://neoastra/missing.png (0 args)");

            var typed = await driver.Automation.SelectedPage!.GetNetworkRequestsAsync(includePreserved: false, driver.CancellationToken);
            Assert.AreEqual("stylesheet", typed.Single(static request => request.Url.EndsWith("style.css", StringComparison.Ordinal)).ResourceType);
            Assert.AreEqual("script", typed.Single(static request => request.Url.EndsWith("script.js", StringComparison.Ordinal)).ResourceType);
            Assert.AreEqual("image", typed.Single(static request => request.Url.EndsWith("missing.png", StringComparison.Ordinal)).ResourceType);
        });
    }

    [TestMethod]
    public async Task NavigationToolsFollowTheHistoryAndReportWhatHappened()
    {
        await RunAsync(async driver =>
        {
            await driver.CallAsync("navigate_page", """{"url":"app://neoastra/automation.html"}""");
            await driver.CallAsync("take_snapshot");

            // A link that an action follows is reported with the action, and the next snapshot is of the new document.
            var followed = await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("link \"Second page\"")}}","includeSnapshot":true}""");
            StringAssert.StartsWith(followed, "Successfully clicked on the element\nPage navigated to app://neoastra/second.html.\n## Latest page snapshot\n");
            StringAssert.Matches(followed, new Regex("""uid=\S+ RootWebArea "Second page" url="app://neoastra/second\.html"\n  uid=\S+ heading "Second" level="1"\n  uid=\S+ link "Back to first" url="app://neoastra/automation\.html"$"""));
            // An identifier of the document that is gone names nothing in the new one.
            StringAssert.Contains(await driver.CallAsync("click", """{"uid":"1_4"}""", expectError: true), "not found on the page");

            StringAssert.StartsWith(await driver.CallAsync("navigate_page", """{"type":"back"}"""), "Successfully navigated back to app://neoastra/automation.html.\n## Pages\n1: Automation fixture");
            StringAssert.StartsWith(await driver.CallAsync("navigate_page", """{"type":"forward"}"""), "Successfully navigated forward to app://neoastra/second.html.\n");
            StringAssert.StartsWith(await driver.CallAsync("navigate_page", """{"type":"forward"}"""), "Unable to navigate forward in the selected page: there is no such entry in the history.");
            await driver.EvaluateAsync("() => { window.marker = 'set'; }");
            StringAssert.StartsWith(await driver.CallAsync("navigate_page", """{"type":"reload"}"""), "Successfully reloaded the page.\n");
            Assert.AreEqual("undefined", await driver.EvaluateAsync("() => window.marker"));
            // A reload that leaves the cache out is a reload all the same.
            await driver.EvaluateAsync("() => { window.marker = 'set'; }");
            StringAssert.StartsWith(await driver.CallAsync("navigate_page", """{"type":"reload","ignoreCache":true}"""), "Successfully reloaded the page.\n");
            Assert.AreEqual("undefined", await driver.EvaluateAsync("() => window.marker"));

            // A script given with a navigation runs before the scripts of the document, for that navigation only.
            await driver.CallAsync("navigate_page", """{"url":"app://neoastra/second.html","initScript":"window.marker = 'early';"}""");
            Assert.AreEqual("\"early\"", await driver.EvaluateAsync("() => window.marker"));
            await driver.CallAsync("navigate_page", """{"type":"reload"}""");
            Assert.AreEqual("undefined", await driver.EvaluateAsync("() => window.marker"));

            // A move within the document is a navigation of the page, without a new document.
            await driver.CallAsync("navigate_page", """{"url":"app://neoastra/automation.html"}""");
            await driver.CallAsync("take_snapshot");
            StringAssert.Contains(await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("link \"Jump\"")}}"}"""), "Page navigated to app://neoastra/automation.html#section.");

            StringAssert.StartsWith(await driver.CallAsync("navigate_page", """{"url":"app://neoastra/missing.html"}"""), "Unable to navigate in the selected page: ");
            Assert.AreEqual("Either URL or a type is required.", await driver.CallAsync("navigate_page", "{}", expectError: true));
            StringAssert.StartsWith(await driver.CallAsync("navigate_page", """{"url":"not a url"}""", expectError: true), "Invalid URL: \"not a url\".");

            // A leave that the document asks to confirm is confirmed, unless the caller says otherwise.
            await driver.CallAsync("navigate_page", """{"url":"app://neoastra/automation.html"}""");
            await driver.CallAsync("take_snapshot");
            await driver.EvaluateAsync("() => { window.addEventListener('beforeunload', e => { e.preventDefault(); e.returnValue = ''; }); }");
            // The engine asks only once the user has interacted with the document.
            await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("button \"Clicked 0 times\"")}}"}""");
            var left = await driver.CallAsync("navigate_page", """{"url":"app://neoastra/second.html"}""");
            StringAssert.StartsWith(left, "Successfully navigated to app://neoastra/second.html.");

            // wait_for returns as soon as a text is there, and fails when none comes.
            await driver.CallAsync("navigate_page", """{"url":"app://neoastra/automation.html"}""");
            await driver.CallAsync("take_snapshot");
            await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("button \"Show later\"")}}"}""");
            var waited = await driver.CallAsync("wait_for", """{"text":["Never there","Late arrival"],"timeout":5000}""");
            StringAssert.StartsWith(waited, "Element matching one of [\"Never there\",\"Late arrival\"] found.\n## Latest page snapshot\n");
            StringAssert.Matches(waited, new Regex("""uid=\S+ "Late arrival"$"""));
            Assert.AreEqual("Timed out after 300 ms waiting for one of [\"Never there\"] to appear on the page.", await driver.CallAsync("wait_for", """{"text":["Never there"],"timeout":300}""", expectError: true));
            // A text that is the name of an element, not rendered text, is found as well.
            StringAssert.StartsWith(await driver.CallAsync("wait_for", """{"text":["Blue swatch"]}"""), "Element matching one of [\"Blue swatch\"] found.");

            // The typed interface reports a navigation as a result.
            var page = driver.Automation.SelectedPage!;
            var result = await page.NavigateAsync(new NeoAutomationNavigationOptions { Url = new Uri("app://neoastra/second.html") }, driver.CancellationToken);
            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(new Uri("app://neoastra/second.html"), result.Url);
            Assert.AreEqual(new Uri("app://neoastra/second.html"), page.Url);
            Assert.AreEqual("Second page", page.Title);
        });
    }

    [TestMethod]
    public async Task ScriptsReturnJsonAndSayWhyTheyFailed()
    {
        var directory = CreateDirectory();
        File.WriteAllText(Path.Combine(directory, "script.js"), "() => document.title");
        await RunAsync(async driver =>
        {
            await driver.CallAsync("navigate_page", """{"url":"app://neoastra/automation.html"}""");
            await driver.CallAsync("take_snapshot");
            var counter = driver.Uid("button \"Clicked 0 times\"");

            Assert.AreEqual("Script ran on page and returned:\n```json\n\"Automation fixture\"\n```", await driver.CallAsync("evaluate_script", """{"function":"() => document.title"}"""));
            Assert.AreEqual("""{"a":[1,2,3],"b":null}""", await driver.EvaluateAsync("() => ({ a: [1, 2, 3], b: null })"));
            Assert.AreEqual("undefined", await driver.EvaluateAsync("() => undefined"));
            // An asynchronous function is awaited, and elements of the snapshot are passed by their identifiers.
            Assert.AreEqual("\"Clicked 0 times\"", await driver.EvaluateAsync("async (el) => { await new Promise(resolve => setTimeout(resolve, 30)); return el.textContent; }", $$""","args":["{{counter}}"]"""));
            Assert.AreEqual("3", await driver.EvaluateAsync("1 + 2", ""","format":"script" """));
            Assert.AreEqual("\"Automation fixture\"", await driver.EvaluateAsync(null, ""","sourcePath":"script.js" """));

            Assert.AreEqual("TypeError: bad thing", await driver.CallAsync("evaluate_script", """{"function":"() => { throw new TypeError('bad thing'); }"}""", expectError: true));
            StringAssert.Contains(await driver.CallAsync("evaluate_script", """{"function":"async () => { await Promise.reject(new Error('later')); }"}""", expectError: true), "later");
            Assert.IsFalse(string.IsNullOrWhiteSpace(await driver.CallAsync("evaluate_script", """{"function":"() => { this is not javascript"}""", expectError: true)));
            Assert.AreEqual("Specify exactly one of function or sourcePath.", await driver.CallAsync("evaluate_script", "{}", expectError: true));
            Assert.AreEqual("args cannot be used when format is \"script\".", await driver.CallAsync("evaluate_script", $$"""{"function":"1","format":"script","args":["{{counter}}"]}""", expectError: true));
            StringAssert.Contains(await driver.CallAsync("evaluate_script", """{"function":"(el) => el","args":["77_1"]}""", expectError: true), "not found on the page");

            // The output can go to a file of an allowed directory instead.
            var saved = await driver.CallAsync("evaluate_script", """{"function":"() => [1, 2]","filePath":"out.json"}""");
            Assert.AreEqual($"Script ran on page. Output saved to {Path.Combine(directory, "out.json")}.", saved);
            Assert.AreEqual("[1,2]", File.ReadAllText(Path.Combine(directory, "out.json")));
            StringAssert.Contains(await driver.CallAsync("evaluate_script", """{"function":"() => 1","filePath":"../out.json"}""", expectError: true), "outside the directories");

            // A script that navigates is reported with where the page went.
            StringAssert.Contains(await driver.CallAsync("evaluate_script", """{"function":"() => { location.href = 'second.html'; }"}"""), "Page navigated to app://neoastra/second.html.");
        }, directory);
    }

    [TestMethod]
    public async Task ScreenshotsShowThePageAnElementOrTheWholeDocument()
    {
        var directory = CreateDirectory();
        await RunAsync(async driver =>
        {
            await driver.CallAsync("navigate_page", """{"url":"app://neoastra/automation.html"}""");
            await driver.CallAsync("take_snapshot");
            var scale = double.Parse(await driver.EvaluateAsync("() => devicePixelRatio"), System.Globalization.CultureInfo.InvariantCulture);
            var viewportWidth = int.Parse(await driver.EvaluateAsync("() => innerWidth"));

            var viewport = await driver.CallToolAsync("take_screenshot");
            Assert.AreEqual("Took a screenshot of the current page's viewport.", viewport.Text);
            Assert.HasCount(1, viewport.Images);
            Assert.AreEqual("image/png", viewport.Images[0].ContentType);
            Assert.AreEqual(viewportWidth * scale, TestPng.Decode(viewport.Images[0].Data.Span).Width, 2);

            // The screenshot of an element shows that element, here a blue box.
            var swatch = driver.Uid("image \"Blue swatch\"");
            var element = await driver.CallToolAsync("take_screenshot", $$"""{"uid":"{{swatch}}"}""");
            Assert.AreEqual($"Took a screenshot of node with uid \"{swatch}\".", element.Text);
            var pixels = TestPng.Decode(element.Images[0].Data.Span);
            Assert.AreEqual(80 * scale, pixels.Width, 2);
            Assert.AreEqual(40 * scale, pixels.Height, 2);
            var (red, green, blue) = pixels.GetPixel(pixels.Width / 2, pixels.Height / 2);
            Assert.IsTrue(red < 20 && green < 20 && blue > 235, $"Expected blue but found ({red}, {green}, {blue}).");

            // An element below the first screen is scrolled into view for its screenshot.
            var far = await driver.CallToolAsync("take_screenshot", $$"""{"uid":"{{driver.Uid("button \"Far button\"")}}","format":"jpeg","quality":60}""");
            Assert.AreEqual("image/jpeg", far.Images[0].ContentType);
            Assert.IsGreaterThan(0, far.Images[0].Width);

            var full = await driver.CallToolAsync("take_screenshot", """{"fullPage":true,"filePath":"full.png"}""");
            Assert.AreEqual($"Took a screenshot of the full current page.\nSaved screenshot to {Path.Combine(directory, "full.png")}.", full.Text);
            Assert.IsEmpty(full.Images);
            var documentHeight = int.Parse(await driver.EvaluateAsync("() => document.documentElement.scrollHeight"));
            Assert.AreEqual(documentHeight * scale, TestPng.Decode(File.ReadAllBytes(Path.Combine(directory, "full.png"))).Height, 2);

            Assert.AreEqual("Providing both \"uid\" and \"fullPage\" is not allowed.", await driver.CallAsync("take_screenshot", $$"""{"uid":"{{swatch}}","fullPage":true}""", expectError: true));
            StringAssert.Contains(await driver.CallAsync("take_screenshot", """{"format":"webp"}""", expectError: true), "Use \"png\" or \"jpeg\"");

            // The snapshot can go to a file as well.
            Assert.AreEqual($"Saved snapshot to {Path.Combine(directory, "page.txt")}.", await driver.CallAsync("take_snapshot", """{"filePath":"page.txt"}"""));
            StringAssert.StartsWith(File.ReadAllText(Path.Combine(directory, "page.txt")), "uid=");

            Assert.AreEqual("## Pages\n1: Automation fixture (app://neoastra/automation.html) [selected]", await driver.CallAsync("resize_page", """{"width":640,"height":480}"""));
            Assert.AreEqual("[640,480]", await driver.EvaluateAsync("() => [innerWidth, innerHeight]"));
        }, directory);
    }

    [TestMethod]
    public async Task ResizePageGivesThePageItsSizeWhereAWindowIsCountedInThePixelsOfItsDisplay()
    {
        await RunAsync(async driver =>
        {
            const string pages = "## Pages\n1: Second page (app://neoastra/second.html) [selected]";
            await driver.CallAsync("navigate_page", """{"url":"app://neoastra/second.html"}""");
            var view = driver.Automation.SelectedPage!.View;
            var scale = double.Parse(await driver.EvaluateAsync("() => devicePixelRatio"), System.Globalization.CultureInfo.InvariantCulture);

            // Asks for a size and returns the one the page has, which the answer names when it is another one.
            async Task<(int Width, int Height)> ResizeAsync(int width, int height)
            {
                var answer = await driver.CallAsync("resize_page", $$"""{"width":{{width}},"height":{{height}}}""");
                using var size = JsonDocument.Parse(await driver.EvaluateAsync("() => [innerWidth, innerHeight]"));
                var shown = (Width: size.RootElement[0].GetInt32(), Height: size.RootElement[1].GetInt32());
                Assert.AreEqual(shown == (width, height) ? pages : $"The page is {shown.Width}x{shown.Height}, not {width}x{height}: its window did not take the size for it.\n{pages}", answer);
                return shown;
            }

            // The page has the size, or the one next to it where the engine has no page of that size at this scale.
            async Task ExpectAsync(int width, int height, bool exact, string where)
            {
                var shown = await ResizeAsync(width, height);
                if (exact) Assert.AreEqual((width, height), shown, where);
                else Assert.IsTrue(Math.Abs(shown.Width - width) <= 1 && Math.Abs(shown.Height - height) <= 1, $"{shown.Width}x{shown.Height} for {width}x{height} {where}");
            }

            // On a display that scales, a pixel of the window is not a CSS pixel of the page. One call gives the page
            // its size, whether or not the size comes to a whole number of pixels. These are the scales at which
            // WebView2 was seen to have a page of each of these sizes; at 125 percent it has none of 4n + 1 pixels.
            var everySize = scale is 1 or 1.25 or 1.5 or 1.75 or 2 or 2.5;
            foreach (var (width, height) in new[] { (640, 480), (803, 603), (500, 375), (335, 446) })
            {
                await ExpectAsync(width, height, everySize, $"at a scale of {scale}");
            }

            // The zoom of a view changes what a CSS pixel takes of the window, on every display. The sizes leave the
            // window inside a small screen at a zoom of 200 percent.
            foreach (var zoom in new[] { 1.25, 2 })
            {
                view.ZoomFactor = zoom;
                await ExpectAsync(320, 240, scale is 1 or 1.5, $"at a zoom of {zoom}");
                await ExpectAsync(401, 301, scale is 1 or 1.5, $"at a zoom of {zoom}");
                Assert.AreEqual(scale * zoom, double.Parse(await driver.EvaluateAsync("() => devicePixelRatio"), System.Globalization.CultureInfo.InvariantCulture), 0.001);
            }

            // Zoomed out, a pixel of the window is more than a CSS pixel, and not every size has a window.
            view.ZoomFactor = 0.8;
            await ExpectAsync(501, 401, exact: false, "at a zoom of 0.8");
            view.ZoomFactor = 1;

            // A window stops at its screen and at the least size of its frame, and the answer says so.
            Assert.AreNotEqual((30000, 20000), await ResizeAsync(30000, 20000));
            Assert.AreNotEqual((20, 10), await ResizeAsync(20, 10));

            // A window that is maximized is restored for the size.
            view.OwnedWindow!.State = NeoWindowState.Maximized;
            await ExpectAsync(640, 480, everySize, "from a maximized window");
        }, perMonitorDpiAware: true);
    }

    [TestMethod]
    public async Task PagesAreOpenedSelectedAndClosed()
    {
        await RunAsync(async driver =>
        {
            var automation = driver.Automation;
            await driver.CallAsync("navigate_page", """{"url":"app://neoastra/automation.html"}""");
            Assert.AreEqual("The last open page cannot be closed. It is fine to keep it open.\n## Pages\n1: Automation fixture (app://neoastra/automation.html) [selected]",
                await driver.CallAsync("close_page", """{"pageId":1}"""));

            Assert.AreEqual("## Pages\n1: Automation fixture (app://neoastra/automation.html)\n2: Second page (app://neoastra/second.html) [selected]",
                await driver.CallAsync("new_page", """{"url":"app://neoastra/second.html"}"""));
            Assert.HasCount(2, automation.Pages);
            Assert.AreEqual(2, automation.SelectedPage!.Id);
            // A tool acts on the selected page, or on the one it names.
            Assert.AreEqual("\"Second page\"", await driver.EvaluateAsync("() => document.title"));
            Assert.AreEqual("\"Automation fixture\"", await driver.EvaluateAsync("() => document.title", ""","pageId":1"""));
            StringAssert.StartsWith(await driver.CallAsync("take_snapshot", """{"pageId":1}"""), "## Latest page snapshot\nuid=1_0 RootWebArea \"Automation fixture\"");

            Assert.AreEqual("## Pages\n1: Automation fixture (app://neoastra/automation.html) [selected]\n2: Second page (app://neoastra/second.html)",
                await driver.CallAsync("select_page", """{"pageId":1,"bringToFront":true}"""));
            Assert.AreEqual("\"Automation fixture\"", await driver.EvaluateAsync("() => document.title"));
            Assert.AreEqual("No page found", await driver.CallAsync("select_page", """{"pageId":42}""", expectError: true));

            // Pages of an isolated context do not share storage with the others.
            await driver.EvaluateAsync("() => { localStorage.setItem('neo-automation', 'shared'); }");
            var isolated = await driver.CallAsync("new_page", """{"url":"app://neoastra/second.html","isolatedContext":"clean","background":true}""");
            StringAssert.EndsWith(isolated, "3: Second page (app://neoastra/second.html) [selected] isolatedContext=clean");
            Assert.AreEqual("null", await driver.EvaluateAsync("() => localStorage.getItem('neo-automation')"));
            Assert.AreEqual("\"shared\"", await driver.EvaluateAsync("() => localStorage.getItem('neo-automation')", ""","pageId":2"""));
            await driver.EvaluateAsync("() => { localStorage.removeItem('neo-automation'); }", ""","pageId":1""");

            // Closing the selected page selects another one and says so.
            var closed = await driver.CallAsync("close_page", """{"pageId":3}""");
            Assert.AreEqual("Note: the previously selected page was closed. Page 1 is now selected.\n## Pages\n1: Automation fixture (app://neoastra/automation.html) [selected]\n2: Second page (app://neoastra/second.html)", closed);
            StringAssert.Contains(await driver.CallAsync("take_snapshot", """{"pageId":3}""", expectError: true), "No page found");
            await driver.CallAsync("close_page", """{"pageId":2}""");
            Assert.HasCount(1, automation.Pages);

            Assert.AreEqual("Unknown tool \"no_such_tool\". Call a tool of the list of tools.", await driver.CallAsync("no_such_tool", expectError: true));
            Assert.AreEqual("The argument \"uid\" is required.", await driver.CallAsync("click", "{}", expectError: true));
            Assert.AreEqual("The argument \"uid\" must be a string.", await driver.CallAsync("click", """{"uid":5}""", expectError: true));
            Assert.AreEqual("The arguments of a tool must be a JSON object.", await driver.CallAsync("click", "[]", expectError: true));
        });
    }

    [TestMethod]
    public async Task ToolsRefuseFilesWithoutAnAllowedDirectoryAndScriptsWhenTheyAreOff()
    {
        await RunAsync(async driver =>
        {
            CollectionAssert.DoesNotContain(driver.Automation.Tools.Select(static tool => tool.Name).ToArray(), "evaluate_script");
            await driver.CallAsync("navigate_page", """{"url":"app://neoastra/automation.html"}""");
            await driver.CallAsync("take_snapshot");
            StringAssert.Contains(await driver.CallAsync("take_screenshot", """{"filePath":"shot.png"}""", expectError: true), "File access is turned off for this application");
            StringAssert.Contains(await driver.CallAsync("upload_file", $$"""{"uid":"{{driver.Uid("button \"Attachment\"")}}","filePaths":["C:/Windows/win.ini"]}""", expectError: true), "File access is turned off");
            StringAssert.Contains(await driver.CallAsync("evaluate_script", """{"function":"() => 1"}""", expectError: true), "Unknown tool");
            StringAssert.Contains(await driver.CallAsync("navigate_page", """{"url":"app://neoastra/second.html","initScript":"1"}""", expectError: true), "Script evaluation is turned off");
            StringAssert.Contains(await driver.CallAsync("navigate_page", """{"url":"javascript:alert(1)"}""", expectError: true), "not allowed when JavaScript evaluation is disabled");
            var page = driver.Automation.SelectedPage!;
            Assert.AreEqual("not-allowed", (await Assert.ThrowsExactlyAsync<NeoAutomationException>(async () => await page.EvaluateScriptAsync("() => 1", cancellationToken: driver.CancellationToken))).Code);
            // The other tools still work.
            Assert.AreEqual("Successfully clicked on the element", await driver.CallAsync("click", $$"""{"uid":"{{driver.Uid("button \"Clicked 0 times\"")}}"}"""));
        }, configure: static options => options.AllowScriptEvaluation = false);
    }

    [TestMethod]
    public async Task AutomationLeavesAViewAsItFoundIt()
    {
        await LiveBrowser.RunAsync(AutomationFixture.Pages, async session =>
        {
            var view = session.View;
            var answered = 0;
            view.ScriptDialogRequested = _ =>
            {
                answered++;
                return ValueTask.FromResult(NeoScriptDialogDecision.Accept);
            };
            await session.NavigateAsync("automation.html");
            Assert.AreEqual("\"undefined\"", await view.EvaluateScriptAsync("typeof window.__neoastraAutomation", session.CancellationToken));

            var automation = new NeoAutomation(session.Application, new NeoAutomationOptions { PageFilter = candidate => ReferenceEquals(candidate, view) });
            var page = automation.SelectedPage!;
            Assert.AreSame(view, page.View);
            Assert.AreSame(page, automation.FindPage(view));
            Assert.AreSame(page, automation.GetPage(page.Id));
            await page.TakeSnapshotAsync(cancellationToken: session.CancellationToken);
            Assert.AreEqual("\"object\"", await view.EvaluateScriptAsync("typeof window.__neoastraAutomation", session.CancellationToken));

            // While automation drives the view, it answers the dialogs; afterwards the application does again.
            var result = await page.EvaluateScriptAsync("() => confirm('automation?')", new NeoAutomationEvaluateOptions { DialogAction = "dismiss" }, session.CancellationToken);
            Assert.AreEqual("false", result.Json);
            Assert.IsTrue(result.Action.DialogHandled);
            Assert.AreEqual(0, answered);

            await automation.DisposeAsync();
            Assert.IsTrue(page.IsClosed);
            Assert.IsEmpty(automation.Pages);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await page.TakeSnapshotAsync(cancellationToken: session.CancellationToken));
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await automation.CallToolAsync("list_pages", default(JsonElement), session.CancellationToken));
            Assert.AreEqual("true", await view.EvaluateScriptAsync("confirm('application?')", session.CancellationToken));
            Assert.AreEqual(1, answered);

            // A document loaded after automation was turned off has no trace of it.
            await session.NavigateAsync("second.html");
            Assert.AreEqual("\"undefined\"", await view.EvaluateScriptAsync("typeof window.__neoastraAutomation", session.CancellationToken));
        }, new NeoAstraOptions { ViewLabel = "main" });
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "neoastra-automation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static Task RunAsync(Func<AutomationDriver, Task> body, string? allowedDirectory = null, Action<NeoAutomationOptions>? configure = null, bool perMonitorDpiAware = false)
        => LiveBrowser.RunAsync(AutomationFixture.Pages, async session =>
        {
            var options = new NeoAutomationOptions();
            if (allowedDirectory is not null) options.AllowedDirectories.Add(allowedDirectory);
            configure?.Invoke(options);
            await using var automation = new NeoAutomation(session.Application, options);
            await body(new AutomationDriver(session, automation));
        }, timeout: TimeSpan.FromSeconds(90), perMonitorDpiAware: perMonitorDpiAware);

    /// <summary>Calls the tools of an automation as a Model Context Protocol server would, from their names and JSON arguments.</summary>
    private sealed class AutomationDriver(LiveBrowserSession session, NeoAutomation automation)
    {
        private string _lastSnapshot = string.Empty;

        internal NeoAutomation Automation { get; } = automation;

        internal CancellationToken CancellationToken => session.CancellationToken;

        internal async Task<NeoAutomationToolResult> CallToolAsync(string name, string? arguments = null, bool expectError = false)
        {
            session.Stage = $"{name} {arguments}";
            var result = await Automation.CallToolAsync(name, arguments, session.CancellationToken);
            Assert.AreEqual(expectError, result.IsError, $"{name} {arguments}: {result.Text}");
            if (result.Text.Contains("## Latest page snapshot", StringComparison.Ordinal)) _lastSnapshot = result.Text;
            return result;
        }

        internal async Task<string> CallAsync(string name, string? arguments = null, bool expectError = false)
            => (await CallToolAsync(name, arguments, expectError)).Text;

        /// <summary>Runs a function in the page and returns the JSON text of its result.</summary>
        internal async Task<string> EvaluateAsync(string? function, string moreArguments = "")
        {
            var source = function is null ? string.Empty : $"\"function\":{JsonSerializer.Serialize(function, AutomationTestsJsonContext.Default.String)}";
            if (function is null) moreArguments = moreArguments.TrimStart().TrimStart(',');
            var text = await CallAsync("evaluate_script", $"{{{source}{moreArguments}}}");
            var match = Regex.Match(text, "^Script ran on page and returned:\n```json\n(.*)\n```", RegexOptions.Singleline);
            Assert.IsTrue(match.Success, text);
            return match.Groups[1].Value;
        }

        /// <summary>Finds the identifier of the node of the latest snapshot whose line continues with the given text.</summary>
        internal string Uid(string description)
        {
            var match = Regex.Match(_lastSnapshot, @"uid=(\S+) " + Regex.Escape(description) + @"(?: |$)", RegexOptions.Multiline);
            Assert.IsTrue(match.Success, $"The latest snapshot has no node {description}:\n{_lastSnapshot}");
            return match.Groups[1].Value;
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class AutomationTestsJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
