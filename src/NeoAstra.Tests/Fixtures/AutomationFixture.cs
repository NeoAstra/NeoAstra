// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Tests;

/// <summary>The pages that the browser automation tests drive.</summary>
internal static class AutomationFixture
{
    internal static IReadOnlyDictionary<string, string> Pages { get; } = new Dictionary<string, string>
    {
        ["automation.html"] = """
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <title>Automation fixture</title>
            <style>
              body { font: 14px sans-serif; margin: 16px; }
              #spacer { height: 1400px; }
              .drop { width: 120px; height: 60px; border: 1px solid #888; }
              #swatch { width: 80px; height: 40px; background: #00f; }
            </style>
            </head>
            <body>
            <header><h1>Automation fixture</h1><nav aria-label="Main"><a href="second.html" id="next">Second page</a> <a href="#section">Jump</a></nav></header>
            <main>
              <p id="intro">Welcome to the <strong>fixture</strong> page.</p>
              <button id="counter" type="button">Clicked 0 times</button>
              <button id="disabled" disabled>Disabled</button>
              <form id="form">
                <label for="name">Name</label> <input id="name" name="name" placeholder="Your name">
                <label>Email <input id="email" type="email" name="email" required></label>
                <label for="color">Color</label>
                <select id="color" name="color"><option value="r">Red</option><option value="g" selected>Green</option><option value="b">Blue</option></select>
                <label><input id="agree" type="checkbox" name="agree"> I agree</label>
                <fieldset><legend>Size</legend>
                  <label><input type="radio" name="size" value="s"> Small</label>
                  <label><input type="radio" name="size" value="l"> Large</label>
                </fieldset>
                <label for="notes">Notes</label> <textarea id="notes" name="notes"></textarea>
                <div id="editor" contenteditable="true" role="textbox" aria-label="Editor"></div>
                <input id="file" type="file" aria-label="Attachment">
                <button id="submit" type="submit">Send</button>
              </form>
              <output id="result"></output>
              <div id="hover-target">Hover me</div><span id="hover-state"></span>
              <div id="drag-source" draggable="true">Drag me</div>
              <div id="drop-target" class="drop">Drop here</div>
              <div id="swatch" role="img" aria-label="Blue swatch"></div>
              <div aria-hidden="true">Hidden from the tree</div>
              <div style="display:none">Not rendered</div>
              <button id="confirm">Ask</button>
              <button id="later">Show later</button>
              <button id="log">Log</button>
              <button id="fetch">Fetch</button>
              <div id="spacer"></div>
              <h2 id="section">Far section</h2>
              <button id="far">Far button</button>
            </main>
            <script>
              let clicks = 0;
              const $ = id => document.getElementById(id);
              $('counter').addEventListener('click', e => { clicks++; e.currentTarget.textContent = 'Clicked ' + clicks + ' times'; });
              $('counter').addEventListener('dblclick', e => { e.currentTarget.dataset.double = 'yes'; });
              $('form').addEventListener('submit', e => {
                e.preventDefault();
                const out = {};
                for (const [key, value] of new FormData(e.target)) out[key] = typeof value === 'string' ? value : value.name;
                out.editor = $('editor').textContent;
                $('result').textContent = JSON.stringify(out);
              });
              $('hover-target').addEventListener('mouseenter', () => { $('hover-state').textContent = 'hovered'; });
              $('drag-source').addEventListener('dragstart', e => e.dataTransfer.setData('text/plain', 'payload'));
              $('drop-target').addEventListener('dragover', e => e.preventDefault());
              $('drop-target').addEventListener('drop', e => { e.preventDefault(); e.currentTarget.textContent = 'Dropped ' + e.dataTransfer.getData('text/plain'); });
              $('confirm').addEventListener('click', e => { e.currentTarget.textContent = confirm('Proceed?') ? 'Confirmed' : 'Declined'; });
              $('later').addEventListener('click', () => setTimeout(() => { const p = document.createElement('p'); p.textContent = 'Late arrival'; document.body.appendChild(p); }, 300));
              $('log').addEventListener('click', () => { console.log('clicked %s', 'log', { answer: 42 }); console.warn('careful'); console.error(new Error('boom')); });
              $('fetch').addEventListener('click', async () => {
                const response = await fetch('data.json', { headers: { 'X-Test': '1' } });
                $('result').textContent = 'fetched ' + (await response.json()).value;
              });
              $('file').addEventListener('change', e => { $('result').textContent = 'files ' + Array.from(e.target.files).map(f => f.name + ':' + f.size).join(','); });
              document.addEventListener('keydown', e => { if (e.key === 'k' && e.ctrlKey) $('result').textContent = 'shortcut'; });
              console.info('fixture ready');
            </script>
            </body></html>
            """,
        ["second.html"] = """
            <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Second page</title></head>
            <body><h1>Second</h1><a href="automation.html">Back to first</a>
            <script>console.log('second ready');</script></body></html>
            """,
        ["data.json"] = """{"value": 7}""",
        // A page that loads files of its own: a style sheet, a script, and a picture that is not there.
        ["resources.html"] = """
            <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Resources</title>
            <link rel="stylesheet" href="style.css"><script src="script.js" defer></script></head>
            <body><h1>Resources</h1><img src="missing.png" alt="Missing picture"><img src="missing.png" alt="Missing again"></body></html>
            """,
        ["style.css"] = "h1 { color: #036; }",
        ["script.js"] = "document.title = 'Resources loaded';",
        // Editors that take their text from an EditContext, as Monaco does: no input, no text area, and, but for one,
        // no editable content.
        ["editcontext.html"] = """
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <title>EditContext fixture</title>
            <style>
              body { font: 14px sans-serif; margin: 16px; }
              .editor { position: relative; width: 320px; min-height: 22px; border: 1px solid #888; white-space: pre-wrap; font: 14px monospace; }
              #code { position: absolute; left: 4px; top: 2px; width: 0; height: 18px; }
            </style>
            </head>
            <body>
            <h1>Editors</h1>
            <div id="plain" class="editor" role="textbox" aria-label="Plain editor" tabindex="0"></div>
            <div id="bare" class="editor">Bare editor</div>
            <div id="both" class="editor" role="textbox" aria-label="Editable editor" contenteditable="true"></div>
            <div id="code-view" class="editor"><div id="code-text" aria-hidden="true"></div><div id="code" role="textbox" aria-label="Code editor" aria-multiline="true" tabindex="0"></div></div>
            <div id="locked" class="editor" role="textbox" aria-label="Locked editor" aria-readonly="true" tabindex="0"></div>
            <script>
              const editors = {};
              const cancel = {};

              // An editor whose EditContext holds its whole text. The engine gives what is typed to the EditContext and
              // says so with a textupdate event; the editor draws the text and puts in the line breaks.
              function plainEditor(id) {
                const element = document.getElementById(id);
                const context = new EditContext({ text: element.textContent, selectionStart: element.textContent.length, selectionEnd: element.textContent.length });
                const events = [];
                element.editContext = context;
                const draw = () => { element.textContent = context.text; };
                context.addEventListener('textupdate', e => {
                  events.push(`textupdate ${JSON.stringify(e.text)} ${e.updateRangeStart}-${e.updateRangeEnd} ${e.selectionStart}-${e.selectionEnd}`);
                  draw();
                });
                for (const type of ['keydown', 'keypress', 'keyup']) {
                  element.addEventListener(type, e => { events.push(`${type} ${JSON.stringify(e.key)}`); if (cancel[type]) e.preventDefault(); });
                }
                element.addEventListener('input', () => events.push('input'));
                element.addEventListener('beforeinput', e => {
                  events.push('beforeinput ' + e.inputType + (e.data === null ? '' : ' ' + JSON.stringify(e.data)));
                  if (cancel.beforeinput) e.preventDefault();
                  else if (e.inputType === 'insertParagraph' || e.inputType === 'insertLineBreak') {
                    const start = Math.min(context.selectionStart, context.selectionEnd);
                    context.updateText(start, Math.max(context.selectionStart, context.selectionEnd), '\n');
                    context.updateSelection(start + 1, start + 1);
                    draw();
                  }
                });
                editors[id] = { text: () => context.text, selection: () => [context.selectionStart, context.selectionEnd], events, context };
              }

              // An editor that keeps its text in a model of its own, as Monaco does. The element with its EditContext has
              // no width and sits where the cursor is, in front of nothing: the editor draws its text next to it and gives
              // it the focus when the text is clicked. Its EditContext holds the lines of the selection only and is brought
              // up to date later, when the editor draws. The editor selects everything, deletes, and pastes by itself, and
              // puts the text that is typed where its own selection is.
              function codeEditor(id) {
                const element = document.getElementById(id);
                const context = new EditContext();
                const events = [];
                element.editContext = context;
                element.parentElement.addEventListener('mousedown', e => { e.preventDefault(); element.focus(); });
                let text = '', start = 0, end = 0, known = [0, 0], pending = false;
                const draw = () => {
                  pending = false;
                  const from = text.lastIndexOf('\n', start - 1) + 1;
                  const next = text.indexOf('\n', end);
                  context.updateText(0, context.text.length, text.slice(from, next < 0 ? text.length : next));
                  known = [start - from, end - from];
                  context.updateSelection(known[0], known[1]);
                  document.getElementById(id + '-text').textContent = text;
                };
                const changed = () => { if (!pending) { pending = true; setTimeout(draw, 0); } };
                const replace = (from, to, inserted) => { text = text.slice(0, from) + inserted + text.slice(to); start = end = from + inserted.length; changed(); };
                element.addEventListener('keydown', e => {
                  const command = e.ctrlKey || e.metaKey;
                  if (command && e.key.toLowerCase() === 'a') { start = 0; end = text.length; changed(); }
                  else if (!command && e.key === 'Backspace') replace(start === end ? Math.max(start - 1, 0) : start, end, '');
                  else return;
                  events.push('own ' + e.key);
                  e.preventDefault();
                });
                element.addEventListener('beforeinput', e => { if (e.inputType === 'insertParagraph' || e.inputType === 'insertLineBreak') replace(start, end, '\n'); });
                element.addEventListener('paste', e => { events.push('paste'); e.preventDefault(); replace(start, end, e.clipboardData.getData('text/plain')); });
                context.addEventListener('textupdate', e => {
                  // What the engine replaced is read from the selection the EditContext had, and done at the selection of the model.
                  const before = Math.max(known[0] - e.updateRangeStart, 0), after = Math.max(e.updateRangeEnd - known[1], 0);
                  known = [e.selectionStart, e.selectionEnd];
                  events.push('textupdate');
                  if (before === 0 && after === 0) replace(start, end, e.text);
                  // More than the selection is replaced on the line of a cursor only, and not at all over a selection.
                  else if (start === end) {
                    const from = text.lastIndexOf('\n', start - 1) + 1, next = text.indexOf('\n', start);
                    replace(Math.max(start - before, from), Math.min(start + after, next < 0 ? text.length : next), e.text);
                  }
                });
                editors[id] = { text: () => text, selection: () => [start, end], events, set: (value, from, to) => { text = value; start = from; end = to; draw(); } };
              }

              for (const id of ['plain', 'bare', 'both', 'locked']) plainEditor(id);
              codeEditor('code');
              window.editorState = id => ({ text: editors[id].text(), selection: editors[id].selection(), events: editors[id].events.splice(0) });
            </script>
            </body></html>
            """,
    };
}
