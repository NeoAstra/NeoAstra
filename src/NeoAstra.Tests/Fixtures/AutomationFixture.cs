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
    };
}
