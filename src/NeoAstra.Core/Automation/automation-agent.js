// NeoAstra automation agent.
//
// NeoAutomation injects this script into every document of a view it drives. It is the part of browser automation that
// has to run inside the page: the accessibility snapshot and its element ids, input synthesized as DOM events, and the
// capture of console messages and network requests. It uses only standard DOM APIs, so that WebView2, WKWebView, and
// WebKitGTK behave the same. The host calls it through script evaluation and reads JSON text back; nothing here
// evaluates a string as code, so a content security policy without 'unsafe-eval' does not affect it.
(function () {
  'use strict';

  const KEY = '__neoastraAutomation';
  if (window[KEY]) return;

  const MAX_CONSOLE_ENTRIES = 1000;
  const MAX_NETWORK_ENTRIES = 1000;
  const MAX_TEXT = 8000;
  const MAX_BODY = 64 * 1024;
  const MAX_SNAPSHOT_NODES = 50000;

  const documentId = Date.now().toString(36) + Math.random().toString(36).slice(2, 10);
  let sequence = 0;

  // ---------------------------------------------------------------------------------------------------------------
  // Text helpers
  // ---------------------------------------------------------------------------------------------------------------

  function clip(text, limit) {
    text = String(text);
    return text.length > limit ? text.slice(0, limit) + '…' : text;
  }

  function squash(text) {
    return String(text == null ? '' : text).replace(/\s+/g, ' ').trim();
  }

  function describe(element) {
    if (!element || element.nodeType !== 1) return String(element);
    let text = '<' + element.localName;
    if (element.id) text += ' id="' + element.id + '"';
    const classes = typeof element.className === 'string' ? element.className.trim() : '';
    if (classes) text += ' class="' + clip(classes, 60) + '"';
    return text + '>';
  }

  function preview(value, depth) {
    depth = depth || 0;
    try {
      if (value === null) return 'null';
      const type = typeof value;
      if (type === 'string') return depth ? JSON.stringify(clip(value, 200)) : value;
      if (type === 'undefined' || type === 'number' || type === 'boolean') return String(value);
      if (type === 'bigint') return String(value) + 'n';
      if (type === 'symbol') return value.toString();
      if (type === 'function') return 'function ' + (value.name || '(anonymous)') + '()';
      if (value instanceof Error) return value.name + ': ' + value.message;
      if (value.nodeType === 1) return describe(value);
      if (value.nodeType === 3) return JSON.stringify(clip(value.nodeValue, 200));
      if (value.nodeType === 9) return '#document';
      if (value instanceof Date) return isNaN(value) ? 'Invalid Date' : value.toISOString();
      if (value instanceof RegExp) return String(value);
      if (depth >= 2) return Array.isArray(value) ? 'Array(' + value.length + ')' : (value.constructor && value.constructor.name) || 'Object';
      if (Array.isArray(value)) {
        const items = value.slice(0, 20).map(function (item) { return preview(item, depth + 1); });
        if (value.length > 20) items.push('…');
        return '[' + items.join(', ') + ']';
      }
      const name = value.constructor && value.constructor.name && value.constructor.name !== 'Object' ? value.constructor.name + ' ' : '';
      const keys = Object.keys(value);
      const parts = keys.slice(0, 20).map(function (key) {
        let item;
        try { item = preview(value[key], depth + 1); } catch (error) { item = '<unavailable>'; }
        return key + ': ' + item;
      });
      if (keys.length > 20) parts.push('…');
      return name + '{' + parts.join(', ') + '}';
    } catch (error) {
      return '<unavailable>';
    }
  }

  // Formats console arguments as the browser console does, including the printf-like substitutions.
  function formatArguments(args) {
    if (!args.length) return '';
    const rest = Array.prototype.slice.call(args);
    let text;
    if (typeof rest[0] === 'string' && /%[sdifoOc%]/.test(rest[0])) {
      const format = rest.shift();
      text = format.replace(/%([sdifoOc%])/g, function (match, specifier) {
        if (specifier === '%') return '%';
        if (!rest.length) return match;
        const value = rest.shift();
        switch (specifier) {
          case 's': return typeof value === 'string' ? value : preview(value, 1);
          case 'd':
          case 'i': return typeof value === 'bigint' ? String(value) : String(parseInt(value, 10));
          case 'f': return String(parseFloat(value));
          case 'c': return '';
          default: return preview(value, 0);
        }
      });
    } else {
      text = preview(rest.shift(), 0);
    }
    for (const value of rest) text += ' ' + preview(value, 0);
    return text;
  }

  // Parses every frame of an Error stack, in the layout of V8 ("at name (url:line:column)") or of JavaScriptCore
  // ("name@url:line:column").
  function parseAllFrames(stack) {
    const frames = [];
    if (!stack) return frames;
    const lines = String(stack).split('\n');
    const v8 = lines.some(function (line) { return /^\s+at /.test(line); });
    for (const raw of lines) {
      const line = raw.trim();
      if (!line) continue;
      if (v8) {
        if (!/^at /.test(line)) continue;
        const match = /^at (?:(.*?) \()?(.*?):(\d+):(\d+)\)?$/.exec(line);
        if (match) frames.push({ function: match[1] || '', url: match[2] || '', line: Number(match[3]), column: Number(match[4]) });
        else frames.push({ function: line.replace(/^at /, ''), url: '', line: 0, column: 0 });
        continue;
      }
      const match = /^(?:(.*?)@)?(.*?):(\d+):(\d+)$/.exec(line);
      // JavaScriptCore names the code outside of any function; V8 leaves that name empty.
      const name = function (text) { return /^(global|eval|module) code$/.test(text || '') ? '' : text || ''; };
      if (match) frames.push({ function: name(match[1]), url: match[2] || '', line: Number(match[3]), column: Number(match[4]) });
      else frames.push({ function: name(line.replace(/@.*$/, '')), url: '', line: 0, column: 0 });
    }
    return frames;
  }

  // The agent and the scripts that call it name themselves with a sourceURL comment that starts with this. An engine
  // that does not apply the comment to an injected script gives the frames of the agent another address, which is
  // read here from a stack of the agent itself.
  const OWN_SOURCE = 'neoastra-automation-';
  const ownUrl = (function () {
    try {
      const frame = parseAllFrames(new Error().stack)[0];
      return frame && frame.url && frame.url.indexOf(OWN_SOURCE) < 0 && frame.url !== String(document.URL) ? frame.url : '';
    } catch (error) {
      return '';
    }
  })();

  function isOwnFrame(frame) {
    return frame.url.indexOf(OWN_SOURCE) >= 0 || (ownUrl !== '' && frame.url === ownUrl);
  }

  // Keeps the frames of the page: an event that the agent dispatches runs the handlers of the page on top of the
  // agent's own calls, and a wrapper of the agent is on top of the page when the page logs a message.
  function pageFrames(frames) {
    let start = 0;
    while (start < frames.length && isOwnFrame(frames[start])) start++;
    let end = start;
    while (end < frames.length && !isOwnFrame(frames[end])) end++;
    // What an engine reports between the page and the agent is its own dispatching, not a frame of the page.
    while (end > start && !frames[end - 1].url) end--;
    return frames.slice(start, end);
  }

  function parseStack(stack) {
    return pageFrames(parseAllFrames(stack));
  }

  function formatFrames(frames) {
    return frames.slice(0, 30).map(function (frame) {
      const name = frame.function || '<anonymous>';
      return frame.url ? 'at ' + name + ' (' + frame.url + ':' + frame.line + ':' + frame.column + ')' : 'at ' + name;
    }).join('\n');
  }

  // ---------------------------------------------------------------------------------------------------------------
  // Console capture
  // ---------------------------------------------------------------------------------------------------------------

  const consoleEntries = [];
  let consoleDropped = 0;

  function pushConsole(entry) {
    entry.seq = ++sequence;
    entry.time = Date.now();
    entry.text = clip(entry.text, MAX_TEXT);
    if (entry.stack) entry.stack = clip(entry.stack, MAX_TEXT);
    consoleEntries.push(entry);
    if (consoleEntries.length > MAX_CONSOLE_ENTRIES) {
      consoleEntries.shift();
      consoleDropped++;
    }
  }

  // The message types are the ones Chrome DevTools reports for each console method.
  const consoleMethods = {
    log: 'log', debug: 'debug', info: 'info', error: 'error', warn: 'warn', dir: 'dir', dirxml: 'dirxml', table: 'table',
    trace: 'trace', clear: 'clear', group: 'startGroup', groupCollapsed: 'startGroupCollapsed', groupEnd: 'endGroup',
    assert: 'assert', count: 'count', timeEnd: 'timeEnd', profile: 'profile', profileEnd: 'profileEnd',
  };

  function installConsoleCapture() {
    const target = window.console;
    if (!target) return;
    Object.keys(consoleMethods).forEach(function (method) {
      const original = target[method];
      if (typeof original !== 'function') return;
      const type = consoleMethods[method];
      const replacement = function () {
        try {
          const args = arguments;
          if (method !== 'assert' || !args[0]) {
            const values = method === 'assert' ? Array.prototype.slice.call(args, 1) : Array.prototype.slice.call(args);
            // The first frame is this wrapper, whatever address the engine gives it; the first one left is the caller in
            // the page.
            const frames = pageFrames(parseAllFrames(new Error().stack).slice(1));
            const site = frames[0];
            const thrown = values.length && values[0] instanceof Error && values[0].stack ? parseStack(values[0].stack) : null;
            let text = formatArguments(values);
            if (method === 'assert') text = 'Assertion failed' + (text ? ': ' + text : '');
            const entry = {
              type: type, text: text, args: values.slice(0, 20).map(function (value) { return clip(preview(value, 0), 1000); }),
              url: site ? site.url : '', line: site ? site.line : 0, column: site ? site.column : 0,
            };
            // An error that is logged brings the stack of where it was created, which says more than where it was logged.
            if (thrown && thrown.length) entry.stack = formatFrames(thrown);
            else if (method === 'trace' || method === 'error' || method === 'warn' || method === 'assert') entry.stack = formatFrames(frames);
            pushConsole(entry);
          }
        } catch (error) {
          // Capturing a message must never break the page's own logging.
        }
        return original.apply(this, arguments);
      };
      try { target[method] = replacement; } catch (error) { /* a frozen console is left alone */ }
    });

    window.addEventListener('error', function (event) {
      try {
        const source = event.target;
        if (source && source !== window && source.nodeType === 1) {
          const url = source.currentSrc || source.src || source.href || '';
          pushConsole({ type: 'error', text: 'Failed to load resource' + (url ? ': ' + url : ' for ' + describe(source)), args: [], url: url, line: 0, column: 0 });
          return;
        }
        const error = event.error;
        const message = error instanceof Error ? error.name + ': ' + error.message : (event.message || preview(error, 0));
        pushConsole({
          type: 'error', text: /^Uncaught/.test(message) ? message : 'Uncaught ' + message, args: [],
          url: event.filename || '', line: event.lineno || 0, column: event.colno || 0,
          stack: error && error.stack ? formatFrames(parseStack(error.stack)) : '',
        });
      } catch (failure) { /* see above */ }
    }, true);

    window.addEventListener('unhandledrejection', function (event) {
      try {
        const reason = event.reason;
        const message = reason instanceof Error ? reason.name + ': ' + reason.message : preview(reason, 0);
        const frames = reason && reason.stack ? parseStack(reason.stack) : [];
        pushConsole({
          type: 'error', text: 'Uncaught (in promise) ' + message, args: [],
          url: frames[0] ? frames[0].url : '', line: frames[0] ? frames[0].line : 0, column: frames[0] ? frames[0].column : 0,
          stack: formatFrames(frames),
        });
      } catch (failure) { /* see above */ }
    });
  }

  // ---------------------------------------------------------------------------------------------------------------
  // Network capture
  // ---------------------------------------------------------------------------------------------------------------

  // A request is known from the fetch and XMLHttpRequest wrappers, which see methods, headers, and bodies, and from
  // resource timing, which sees every load of the document but only its address, kind, timing, and sizes.
  const networkEntries = [];
  const networkUpdates = [];
  let networkDropped = 0;
  const pendingScripted = [];

  function pushNetwork(entry) {
    entry.seq = ++sequence;
    networkEntries.push(entry);
    if (networkEntries.length > MAX_NETWORK_ENTRIES) {
      networkEntries.shift();
      networkDropped++;
    }
    return entry;
  }

  // An entry that changes after the host has read it is sent again; the host replaces it by its sequence number.
  function touchNetwork(entry) {
    if (networkEntries.indexOf(entry) < 0 && networkUpdates.indexOf(entry) < 0) {
      networkUpdates.push(entry);
      if (networkUpdates.length > MAX_NETWORK_ENTRIES) networkUpdates.shift();
    }
  }

  function absoluteUrl(url) {
    try { return new URL(String(url), document.baseURI).href; } catch (error) { return String(url); }
  }

  function isTextual(contentType) {
    return /^(text\/|application\/(json|xml|javascript|x-www-form-urlencoded|[\w.+-]*\+(json|xml)))/i.test(contentType || '');
  }

  function bodyText(body) {
    try {
      if (body == null) return undefined;
      if (typeof body === 'string') return clip(body, MAX_BODY);
      if (typeof URLSearchParams !== 'undefined' && body instanceof URLSearchParams) return clip(body.toString(), MAX_BODY);
      if (typeof FormData !== 'undefined' && body instanceof FormData) {
        const parts = [];
        body.forEach(function (value, key) { parts.push(key + '=' + (typeof value === 'string' ? value : '<file ' + (value && value.name) + '>')); });
        return clip(parts.join('&'), MAX_BODY);
      }
      if (typeof Blob !== 'undefined' && body instanceof Blob) return '<' + body.size + ' bytes of ' + (body.type || 'binary data') + '>';
      if (body.byteLength !== undefined) return '<' + body.byteLength + ' bytes of binary data>';
      return '<stream>';
    } catch (error) {
      return undefined;
    }
  }

  function headerList(headers) {
    const result = {};
    try {
      if (!headers) return result;
      if (typeof Headers !== 'undefined' && headers instanceof Headers) headers.forEach(function (value, key) { result[key] = value; });
      else if (Array.isArray(headers)) headers.forEach(function (pair) { result[String(pair[0]).toLowerCase()] = String(pair[1]); });
      else Object.keys(headers).forEach(function (key) { result[key.toLowerCase()] = String(headers[key]); });
    } catch (error) { /* an unreadable header set is reported empty */ }
    return result;
  }

  // Reads at most MAX_BODY bytes of a text response through a clone, then stops reading so that a stream that
  // never ends is not buffered.
  function readResponseBody(response, entry) {
    try {
      if (!isTextual(response.headers.get('content-type')) || !response.body || !response.clone) return;
      const reader = response.clone().body.getReader();
      const decoder = new TextDecoder();
      let text = '';
      let size = 0;
      const pump = function () {
        return reader.read().then(function (chunk) {
          if (chunk.done) { entry.responseBody = text + decoder.decode(); touchNetwork(entry); return undefined; }
          size += chunk.value.byteLength;
          text += decoder.decode(chunk.value, { stream: true });
          if (size >= MAX_BODY) {
            entry.responseBody = clip(text, MAX_BODY);
            entry.responseBodyTruncated = true;
            touchNetwork(entry);
            return reader.cancel();
          }
          return pump();
        });
      };
      pump().catch(function () { /* the page aborted the response */ });
    } catch (error) { /* a response that cannot be cloned keeps no body */ }
  }

  function installNetworkCapture() {
    if (typeof window.fetch === 'function') {
      const originalFetch = window.fetch;
      window.fetch = function (input, init) {
        let entry = null;
        try {
          const request = typeof Request !== 'undefined' && input instanceof Request ? input : null;
          entry = pushNetwork({
            url: absoluteUrl(request ? request.url : input), method: String((init && init.method) || (request && request.method) || 'GET').toUpperCase(),
            kind: 'fetch', state: 'pending', start: performance.now(), wallTime: Date.now(),
            requestHeaders: Object.assign(headerList(request && request.headers), headerList(init && init.headers)),
            requestBody: bodyText(init && init.body),
          });
          pendingScripted.push(entry);
        } catch (error) { entry = null; }
        const result = originalFetch.apply(this, arguments);
        if (entry) {
          result.then(function (response) {
            entry.state = 'finished';
            entry.status = response.status;
            entry.statusText = response.statusText;
            entry.responseHeaders = headerList(response.headers);
            entry.mimeType = (response.headers.get('content-type') || '').split(';')[0];
            entry.end = performance.now();
            if (response.url) entry.finalUrl = response.url;
            touchNetwork(entry);
            readResponseBody(response, entry);
          }, function (error) {
            entry.state = 'failed';
            entry.error = error && error.name === 'AbortError' ? 'aborted' : squash(error && error.message) || 'failed';
            entry.end = performance.now();
            touchNetwork(entry);
          });
        }
        return result;
      };
    }

    if (typeof window.XMLHttpRequest === 'function') {
      const prototype = window.XMLHttpRequest.prototype;
      const originalOpen = prototype.open;
      const originalSend = prototype.send;
      const originalSetHeader = prototype.setRequestHeader;
      const records = new WeakMap();
      prototype.open = function (method, url) {
        try { records.set(this, { method: String(method || 'GET').toUpperCase(), url: absoluteUrl(url), headers: {} }); } catch (error) { /* ignore */ }
        return originalOpen.apply(this, arguments);
      };
      prototype.setRequestHeader = function (name, value) {
        try { const record = records.get(this); if (record) record.headers[String(name).toLowerCase()] = String(value); } catch (error) { /* ignore */ }
        return originalSetHeader.apply(this, arguments);
      };
      prototype.send = function (body) {
        try {
          const record = records.get(this);
          if (record) {
            const xhr = this;
            const entry = pushNetwork({
              url: record.url, method: record.method, kind: 'xhr', state: 'pending', start: performance.now(), wallTime: Date.now(),
              requestHeaders: record.headers, requestBody: bodyText(body),
            });
            pendingScripted.push(entry);
            const finish = function (state, error) {
              if (entry.state !== 'pending') return;
              entry.state = state;
              entry.end = performance.now();
              if (error) entry.error = error;
              try {
                entry.status = xhr.status;
                entry.statusText = xhr.statusText;
                const headers = {};
                String(xhr.getAllResponseHeaders() || '').split(/\r?\n/).forEach(function (line) {
                  const separator = line.indexOf(':');
                  if (separator > 0) headers[line.slice(0, separator).trim().toLowerCase()] = line.slice(separator + 1).trim();
                });
                entry.responseHeaders = headers;
                entry.mimeType = (headers['content-type'] || '').split(';')[0];
                if (state === 'finished') {
                  if (xhr.responseType === '' || xhr.responseType === 'text') {
                    const text = xhr.responseText || '';
                    entry.responseBody = clip(text, MAX_BODY);
                    if (text.length > MAX_BODY) entry.responseBodyTruncated = true;
                  } else if (xhr.responseType === 'json') {
                    entry.responseBody = clip(JSON.stringify(xhr.response), MAX_BODY);
                  }
                }
              } catch (failure) { /* the response is not readable */ }
              touchNetwork(entry);
            };
            xhr.addEventListener('load', function () { finish('finished'); });
            xhr.addEventListener('error', function () { finish('failed', 'failed'); });
            xhr.addEventListener('abort', function () { finish('failed', 'aborted'); });
            xhr.addEventListener('timeout', function () { finish('failed', 'timed out'); });
          }
        } catch (error) { /* ignore */ }
        return originalSend.apply(this, arguments);
      };
    }

    const initiatorKinds = {
      navigation: 'document', script: 'script', img: 'image', image: 'image', input: 'image', css: 'other', link: 'other',
      fetch: 'fetch', xmlhttprequest: 'xhr', audio: 'media', video: 'media', track: 'texttrack', iframe: 'document',
      frame: 'document', embed: 'other', object: 'other', beacon: 'ping', other: 'other',
    };

    function kindOf(timing) {
      let kind = initiatorKinds[timing.initiatorType] || 'other';
      if (kind === 'other') {
        const path = timing.name.split(/[?#]/)[0];
        if (/\.css$/i.test(path)) kind = 'stylesheet';
        else if (/\.(png|jpe?g|gif|webp|avif|svg|ico|bmp)$/i.test(path)) kind = 'image';
        else if (/\.(woff2?|ttf|otf|eot)$/i.test(path)) kind = 'font';
        else if (/\.m?js$/i.test(path)) kind = 'script';
        else if (/\.(webmanifest)$/i.test(path) || /manifest\.json$/i.test(path)) kind = 'manifest';
      }
      return kind;
    }

    function applyTiming(entry, timing) {
      entry.duration = Math.round(timing.duration * 10) / 10;
      if (timing.transferSize) entry.transferSize = timing.transferSize;
      if (timing.encodedBodySize) entry.encodedBodySize = timing.encodedBodySize;
      if (timing.decodedBodySize) entry.decodedBodySize = timing.decodedBodySize;
      if (timing.nextHopProtocol) entry.protocol = timing.nextHopProtocol;
      if (timing.responseStatus && entry.status === undefined) entry.status = timing.responseStatus;
    }

    // The loads of elements by address, and the ones among them that resource timing has described.
    const loads = new Map();
    const timed = new WeakSet();

    function onTiming(timing) {
      try {
        const scripted = timing.initiatorType === 'fetch' || timing.initiatorType === 'xmlhttprequest';
        if (scripted) {
          // The wrapper already reported this request; the timing only completes it.
          for (let index = 0; index < pendingScripted.length; index++) {
            const candidate = pendingScripted[index];
            if (candidate.url === timing.name && Math.abs(candidate.start - timing.startTime) < 250) {
              pendingScripted.splice(index, 1);
              applyTiming(candidate, timing);
              touchNetwork(candidate);
              return;
            }
          }
        }
        const known = scripted ? undefined : loads.get(timing.name);
        if (known && !timed.has(known)) {
          // The element reported this load first; the timing completes it.
          timed.add(known);
          known.start = timing.startTime;
          known.wallTime = Math.round(performance.timeOrigin + timing.startTime);
          applyTiming(known, timing);
          touchNetwork(known);
          return;
        }
        const entry = pushNetwork({
          url: timing.name, method: 'GET', kind: timing.entryType === 'navigation' ? 'document' : kindOf(timing), state: 'finished',
          start: timing.startTime, wallTime: Math.round(performance.timeOrigin + timing.startTime),
        });
        applyTiming(entry, timing);
        timed.add(entry);
        if (!scripted && timing.entryType !== 'navigation' && !loads.has(timing.name)) loads.set(timing.name, entry);
      } catch (error) { /* ignore */ }
    }

    function elementKind(element) {
      switch (element.localName) {
        case 'script': return 'script';
        case 'img': case 'image': case 'input': return 'image';
        case 'iframe': case 'frame': return 'document';
        case 'video': case 'audio': case 'source': return 'media';
        case 'track': return 'texttrack';
        case 'link': {
          const rel = ' ' + String(element.getAttribute('rel') || '').toLowerCase() + ' ';
          if (rel.indexOf(' stylesheet ') >= 0) return 'stylesheet';
          if (rel.indexOf(' icon ') >= 0) return 'image';
          if (rel.indexOf(' manifest ') >= 0) return 'manifest';
          if (rel.indexOf(' modulepreload ') >= 0) return 'script';
          const as = String(element.getAttribute('as') || '').toLowerCase();
          return { script: 'script', style: 'stylesheet', font: 'font', image: 'image', fetch: 'fetch', document: 'document' }[as] || 'other';
        }
        default: return 'other';
      }
    }

    // Resource timing leaves out what is not loaded over HTTP, which is every file that an application serves from a
    // scheme of its own. Such a load is known from the element that asked for it: its address, its kind, and whether
    // it succeeded. An address is listed once, however many elements load it.
    function onElementLoad(event) {
      try {
        const element = event.target;
        if (!element || element.nodeType !== 1) return;
        let address = element.localName === 'object' ? element.data : (element.currentSrc || element.src || element.href);
        if (address && typeof address !== 'string') address = address.baseVal;
        if (!address || typeof address !== 'string' || /^(about|data|blob|javascript):/i.test(address)) return;
        const url = absoluteUrl(address);
        const failed = event.type === 'error';
        const known = loads.get(url);
        if (!known) {
          const entry = pushNetwork({ url: url, method: 'GET', kind: elementKind(element), state: failed ? 'failed' : 'finished', start: performance.now(), wallTime: Date.now() });
          if (failed) entry.error = 'failed to load';
          loads.set(url, entry);
        } else if (failed && known.state !== 'failed' && known.status === undefined) {
          known.state = 'failed';
          known.error = 'failed to load';
          touchNetwork(known);
        }
      } catch (error) { /* ignore */ }
    }

    // These events do not bubble; they are seen on their way down to the element. The load event of an element does
    // not pass through the window, so the document is where both are listened for.
    document.addEventListener('load', onElementLoad, true);
    document.addEventListener('error', onElementLoad, true);

    try {
      if (typeof PerformanceObserver === 'function') {
        // An observer of several entry types can report a buffered entry more than once, so each type has its own.
        let navigationSeen = false;
        try {
          new PerformanceObserver(function (list) {
            list.getEntries().forEach(function (timing) {
              if (navigationSeen) return;
              navigationSeen = true;
              onTiming(timing);
            });
          }).observe({ type: 'navigation', buffered: true });
        } catch (error) { /* not every engine reports navigations */ }
        new PerformanceObserver(function (list) { list.getEntries().forEach(onTiming); }).observe({ type: 'resource', buffered: true });
      }
    } catch (error) { /* resource timing is unavailable */ }
  }

  // ---------------------------------------------------------------------------------------------------------------
  // DOM helpers
  // ---------------------------------------------------------------------------------------------------------------

  function viewOf(node) {
    return (node.ownerDocument || node).defaultView || window;
  }

  function parentOf(node) {
    return node.assignedSlot || node.parentNode || node.host || null;
  }

  function composedContains(ancestor, node) {
    for (let current = node; current; current = parentOf(current)) if (current === ancestor) return true;
    return false;
  }

  function styleOf(element) {
    try { return viewOf(element).getComputedStyle(element); } catch (error) { return null; }
  }

  const neverRendered = { script: 1, style: 1, template: 1, noscript: 1, head: 1, meta: 1, link: 1, title: 1, base: 1, param: 1, source: 1, track: 1 };

  // Hidden from assistive technology: not rendered, or removed with aria-hidden.
  function isHidden(element) {
    if (neverRendered[element.localName]) return true;
    if (element.getAttribute('aria-hidden') === 'true') return true;
    if (element.hidden && element.localName !== 'body') return true;
    const style = styleOf(element);
    if (!style) return false;
    if (style.display === 'none') return true;
    // display:contents has no box of its own but its children are rendered.
    return style.visibility === 'hidden' || style.visibility === 'collapse';
  }

  function isDisabled(element) {
    if (element.getAttribute('aria-disabled') === 'true') return true;
    if ('disabled' in element && element.disabled) return true;
    const fieldset = element.closest ? element.closest('fieldset[disabled]') : null;
    return !!fieldset && /^(button|input|select|textarea)$/.test(element.localName) && !composedContains(fieldset.querySelector('legend'), element);
  }

  // Element.matches throws for a selector the engine does not know, such as :modal on an older one.
  function matchesSafely(element, selector) {
    try { return element.matches(selector); } catch (error) { return false; }
  }

  function isEditableContent(element) {
    return !!element.isContentEditable;
  }

  function isTextControl(element) {
    if (element.localName === 'textarea') return true;
    if (element.localName !== 'input') return false;
    return !/^(button|checkbox|color|file|hidden|image|radio|range|reset|submit)$/.test(element.type);
  }

  function isFocusable(element) {
    if (isDisabled(element) && element.getAttribute('aria-disabled') !== 'true') return false;
    if (element.hasAttribute('tabindex')) return !isNaN(parseInt(element.getAttribute('tabindex'), 10));
    switch (element.localName) {
      case 'a':
      case 'area': return element.hasAttribute('href');
      case 'input': return element.type !== 'hidden';
      case 'button':
      case 'select':
      case 'textarea':
      case 'iframe': return true;
      case 'summary': return !!element.parentElement && element.parentElement.localName === 'details';
      case 'audio':
      case 'video': return element.hasAttribute('controls');
      default: return isEditableContent(element) && (!element.parentElement || !element.parentElement.isContentEditable);
    }
  }

  function deepActiveElement(root) {
    let active = (root || document).activeElement;
    for (;;) {
      if (active && active.shadowRoot && active.shadowRoot.activeElement) active = active.shadowRoot.activeElement;
      else if (active && active.localName === 'iframe') {
        let inner = null;
        try { inner = active.contentDocument && active.contentDocument.activeElement; } catch (error) { inner = null; }
        if (!inner) break;
        active = inner;
      } else break;
    }
    return active;
  }

  // ---------------------------------------------------------------------------------------------------------------
  // Accessibility: roles, names, and properties computed from the DOM
  // ---------------------------------------------------------------------------------------------------------------

  // The role names follow what Chrome reports in its accessibility tree, so that a snapshot reads like the one of
  // Chrome DevTools: ARIA role names where they exist, and Chrome's own names for the rest.
  const inputRoles = {
    button: 'button', submit: 'button', reset: 'button', image: 'button', file: 'button', checkbox: 'checkbox', radio: 'radio',
    range: 'slider', number: 'spinbutton', search: 'searchbox', color: 'ColorWell', date: 'Date', 'datetime-local': 'DateTime',
    month: 'DateTime', week: 'DateTime', time: 'InputTime', hidden: 'none',
  };

  const tagRoles = {
    article: 'article', aside: 'complementary', blockquote: 'blockquote', button: 'button', caption: 'caption', code: 'code',
    datalist: 'listbox', dd: 'definition', del: 'deletion', details: 'Details', dfn: 'term', dialog: 'dialog', dt: 'term',
    em: 'emphasis', fieldset: 'group', figcaption: 'Figcaption', figure: 'figure', h1: 'heading', h2: 'heading', h3: 'heading',
    h4: 'heading', h5: 'heading', h6: 'heading', hr: 'separator', iframe: 'Iframe', ins: 'insertion', label: 'LabelText',
    legend: 'Legend', li: 'listitem', main: 'main', mark: 'mark', math: 'math', menu: 'list', meter: 'meter', nav: 'navigation',
    ol: 'list', optgroup: 'group', option: 'option', output: 'status', p: 'paragraph', progress: 'progressbar', search: 'search',
    strong: 'strong', sub: 'subscript', summary: 'DisclosureTriangle', sup: 'superscript', table: 'table', tbody: 'rowgroup',
    textarea: 'textbox', tfoot: 'rowgroup', thead: 'rowgroup', time: 'time', tr: 'row', ul: 'list', canvas: 'Canvas',
    video: 'Video', audio: 'Audio', address: 'group', hgroup: 'group', dl: 'DescriptionList',
  };

  const controlRoles = {
    button: 1, checkbox: 1, ColorWell: 1, combobox: 1, DisclosureTriangle: 1, listbox: 1, menu: 1, menubar: 1, menuitem: 1,
    menuitemcheckbox: 1, menuitemradio: 1, radio: 1, scrollbar: 1, searchbox: 1, slider: 1, spinbutton: 1, switch: 1, tab: 1,
    textbox: 1, tree: 1, treeitem: 1,
  };

  const leafRoles = { image: 1, img: 1, meter: 1, scrollbar: 1, slider: 1, separator: 1, progressbar: 1, 'graphics-symbol': 1, 'doc-cover': 1 };

  const nameFromContent = {
    button: 1, cell: 1, checkbox: 1, columnheader: 1, gridcell: 1, heading: 1, link: 1, menuitem: 1, menuitemcheckbox: 1,
    menuitemradio: 1, option: 1, radio: 1, row: 1, rowheader: 1, switch: 1, tab: 1, tooltip: 1, treeitem: 1,
    DisclosureTriangle: 1, LabelText: 1, Legend: 1, caption: 1, Figcaption: 1, term: 1,
  };

  const sectioning = 'article,aside,main,nav,section';

  function explicitRole(element) {
    const attribute = element.getAttribute('role');
    if (!attribute) return '';
    const role = attribute.trim().split(/\s+/)[0].toLowerCase();
    if (role === 'img') return 'image';
    return role;
  }

  function implicitRole(element) {
    const tag = element.localName;
    switch (tag) {
      case 'a':
      case 'area': return element.hasAttribute('href') ? 'link' : 'generic';
      case 'img': return element.getAttribute('alt') === '' && !element.hasAttribute('aria-label') && !element.hasAttribute('aria-labelledby') ? 'none' : 'image';
      case 'svg': return 'image';
      case 'input': {
        const type = (element.getAttribute('type') || 'text').toLowerCase();
        if (inputRoles[type]) return inputRoles[type];
        return element.hasAttribute('list') ? 'combobox' : 'textbox';
      }
      case 'select': return element.multiple || element.size > 1 ? 'listbox' : 'combobox';
      case 'header': return element.closest(sectioning) ? 'generic' : 'banner';
      case 'footer': return element.closest(sectioning) ? 'generic' : 'contentinfo';
      case 'section': return element.hasAttribute('aria-label') || element.hasAttribute('aria-labelledby') || element.hasAttribute('title') ? 'region' : 'generic';
      case 'form': return element.hasAttribute('aria-label') || element.hasAttribute('aria-labelledby') || element.hasAttribute('title') ? 'form' : 'generic';
      case 'td': {
        const table = element.closest('table');
        const role = table ? explicitRole(table) : '';
        return role === 'grid' || role === 'treegrid' ? 'gridcell' : 'cell';
      }
      case 'th': return element.getAttribute('scope') === 'row' ? 'rowheader' : 'columnheader';
      default: return tagRoles[tag] || 'generic';
    }
  }

  function roleOf(element) {
    const explicit = explicitRole(element);
    if (explicit === 'presentation' || explicit === 'none') return isFocusable(element) ? implicitRole(element) : 'none';
    return explicit || implicitRole(element);
  }

  function referenced(element, attribute) {
    const ids = (element.getAttribute(attribute) || '').trim();
    if (!ids) return [];
    const root = element.getRootNode ? element.getRootNode() : element.ownerDocument;
    const result = [];
    ids.split(/\s+/).forEach(function (id) {
      const target = root.getElementById ? root.getElementById(id) : element.ownerDocument.getElementById(id);
      if (target) result.push(target);
    });
    return result;
  }

  function pseudoText(element, pseudo) {
    try {
      const content = viewOf(element).getComputedStyle(element, pseudo).content;
      const match = /^"((?:[^"\\]|\\.)*)"$/.exec(content || '');
      return match ? match[1].replace(/\\(.)/g, '$1') : '';
    } catch (error) {
      return '';
    }
  }

  const blockDisplays = /^(block|flex|grid|list-item|table|table-row|table-cell|flow-root)$/;

  // Text an element contributes to a name computed from content: its own alternative when it has one, else its text.
  function contentText(node, state) {
    if (node.nodeType === 3) return node.nodeValue;
    if (node.nodeType !== 1) return '';
    const element = node;
    if (state.visited.has(element)) return '';
    state.visited.add(element);
    if (!state.hidden && isHidden(element)) return '';
    const label = (element.getAttribute('aria-label') || '').trim();
    if (label) return label;
    const tag = element.localName;
    if (tag === 'img' || tag === 'area') return element.getAttribute('alt') || element.getAttribute('title') || '';
    if (tag === 'br') return '\n';
    if (tag === 'input') {
      const type = element.type;
      if (type === 'hidden') return '';
      if (/^(button|submit|reset)$/.test(type)) return element.value || (type === 'submit' ? 'Submit' : type === 'reset' ? 'Reset' : '');
      if (type === 'image') return element.getAttribute('alt') || '';
      if (/^(checkbox|radio|file|color)$/.test(type)) return '';
      return type === 'password' ? '' : element.value;
    }
    if (tag === 'textarea') return element.value;
    if (tag === 'select') {
      return Array.prototype.map.call(element.selectedOptions || [], function (option) { return option.textContent; }).join(' ');
    }
    if (tag === 'svg') {
      const title = element.querySelector(':scope > title');
      return title ? title.textContent : '';
    }
    let text = pseudoText(element, '::before');
    const children = element.shadowRoot ? element.shadowRoot.childNodes : tag === 'slot' && element.assignedNodes && element.assignedNodes().length ? element.assignedNodes() : element.childNodes;
    for (let index = 0; index < children.length; index++) text += contentText(children[index], state);
    text += pseudoText(element, '::after');
    const style = styleOf(element);
    return style && blockDisplays.test(style.display) ? ' ' + text + ' ' : text;
  }

  function labelsOf(element) {
    try { return element.labels ? Array.prototype.slice.call(element.labels) : []; } catch (error) { return []; }
  }

  // The accessible name: aria-labelledby, aria-label, the native label of the element, its content, then its title.
  function nameOf(element, role) {
    const labelledBy = referenced(element, 'aria-labelledby');
    if (labelledBy.length) {
      const text = squash(labelledBy.map(function (target) { return contentText(target, { visited: new Set(), hidden: true }); }).join(' '));
      if (text) return text;
    }
    const ariaLabel = squash(element.getAttribute('aria-label'));
    if (ariaLabel) return ariaLabel;

    const tag = element.localName;
    if (tag === 'input' || tag === 'select' || tag === 'textarea' || tag === 'meter' || tag === 'progress' || tag === 'output' || tag === 'button') {
      const labels = labelsOf(element);
      if (labels.length) {
        const text = squash(labels.map(function (label) {
          const state = { visited: new Set(), hidden: false };
          state.visited.add(element);
          return contentText(label, state);
        }).join(' '));
        if (text) return text;
      }
    }
    if (tag === 'input') {
      const type = element.type;
      if (/^(button|submit|reset)$/.test(type)) {
        const value = squash(element.value) || (type === 'submit' ? 'Submit' : type === 'reset' ? 'Reset' : '');
        if (value) return value;
      }
      if (type === 'image') { const alt = squash(element.getAttribute('alt')); if (alt) return alt; }
    }
    if (tag === 'img' || tag === 'area') { const alt = squash(element.getAttribute('alt')); if (alt) return alt; }
    if (tag === 'fieldset') { const legend = element.querySelector(':scope > legend'); if (legend) return squash(contentText(legend, { visited: new Set(), hidden: false })); }
    if (tag === 'figure') { const caption = element.querySelector(':scope > figcaption'); if (caption) return squash(contentText(caption, { visited: new Set(), hidden: false })); }
    if (tag === 'table') { const caption = element.querySelector(':scope > caption'); if (caption) return squash(contentText(caption, { visited: new Set(), hidden: false })); }
    if (tag === 'svg') { const title = element.querySelector(':scope > title'); if (title) return squash(title.textContent); }

    if (nameFromContent[role]) {
      const state = { visited: new Set(), hidden: false };
      let text = pseudoText(element, '::before');
      const children = element.shadowRoot ? element.shadowRoot.childNodes : element.childNodes;
      for (let index = 0; index < children.length; index++) text += contentText(children[index], state);
      text = squash(text + pseudoText(element, '::after'));
      if (text) return text;
    }
    const title = squash(element.getAttribute('title'));
    if (title) return title;
    if ((role === 'textbox' || role === 'searchbox' || role === 'combobox' || role === 'spinbutton') && element.getAttribute('placeholder')) return squash(element.getAttribute('placeholder'));
    if (tag === 'iframe') return squash(element.getAttribute('name'));
    return '';
  }

  function descriptionOf(element, name) {
    const describedBy = referenced(element, 'aria-describedby');
    if (describedBy.length) {
      const text = squash(describedBy.map(function (target) { return contentText(target, { visited: new Set(), hidden: true }); }).join(' '));
      if (text) return text;
    }
    const description = squash(element.getAttribute('aria-description'));
    if (description) return description;
    const title = squash(element.getAttribute('title'));
    return title && title !== name ? title : '';
  }

  function tristate(value) {
    return value === 'true' ? true : value === 'mixed' ? 'mixed' : value === 'false' ? false : undefined;
  }

  // The properties of a node, with the names Puppeteer's serialized accessibility nodes use.
  function propertiesOf(element, role, focused) {
    const result = {};
    const tag = element.localName;
    const aria = function (name) { return element.getAttribute('aria-' + name); };

    if (isDisabled(element)) result.disabled = true;
    if (element === focused) result.focused = true;

    const expanded = aria('expanded');
    if (expanded === 'true' || expanded === 'false') result.expanded = expanded === 'true';
    else if (tag === 'details') result.expanded = element.open;
    else if (tag === 'summary' && element.parentElement && element.parentElement.localName === 'details') result.expanded = element.parentElement.open;

    if (tag === 'option') { if (element.selected) result.selected = true; }
    else if (aria('selected') === 'true') result.selected = true;

    if (tag === 'input' && (element.type === 'checkbox' || element.type === 'radio')) result.checked = element.indeterminate ? 'mixed' : element.checked;
    else if (role === 'checkbox' || role === 'radio' || role === 'switch' || role === 'menuitemcheckbox' || role === 'menuitemradio') {
      const checked = tristate(aria('checked'));
      result.checked = checked === undefined ? false : checked;
    }
    const pressed = tristate(aria('pressed'));
    if (pressed !== undefined) result.pressed = pressed;

    if (tag === 'dialog' ? matchesSafely(element, ':modal') : aria('modal') === 'true') result.modal = true;
    if (tag === 'textarea' || aria('multiline') === 'true') result.multiline = true;
    if ((tag === 'select' && element.multiple) || aria('multiselectable') === 'true') result.multiselectable = true;
    if ((isTextControl(element) && element.readOnly) || aria('readonly') === 'true') result.readonly = true;
    if (element.required === true || aria('required') === 'true') result.required = true;

    if (role === 'heading') {
      const level = parseInt(aria('level') || (/^h([1-6])$/.exec(tag) || [])[1] || '2', 10);
      if (!isNaN(level)) result.level = level;
    } else if (aria('level')) {
      const level = parseInt(aria('level'), 10);
      if (!isNaN(level)) result.level = level;
    }

    if (tag === 'input' || tag === 'textarea') {
      const type = element.type;
      if (isTextControl(element) && type !== 'password' && element.value) result.value = clip(element.value, 1000);
      else if ((type === 'range' || type === 'number' || type === 'color') && element.value !== '') result.value = element.value;
      if (type === 'range' || type === 'number') {
        if (element.min !== '') result.valuemin = Number(element.min);
        if (element.max !== '') result.valuemax = Number(element.max);
      }
    } else if (tag === 'select') {
      const selected = element.selectedOptions && element.selectedOptions[0];
      if (selected) result.value = squash(selected.textContent);
    } else if (tag === 'option') {
      // The option value shown is its text, which is also what fill() matches.
      result.value = squash(element.textContent);
    } else if (tag === 'progress' || tag === 'meter') {
      result.value = element.value;
      if (tag === 'meter') result.valuemin = element.min;
      result.valuemax = element.max;
    } else if (isEditableContent(element) && role === 'textbox') {
      const text = squash(element.textContent);
      if (text) result.value = clip(text, 1000);
    }
    ['valuemin', 'valuemax', 'valuenow'].forEach(function (name) {
      const value = aria(name);
      if (value !== null && value !== '' && !isNaN(Number(value))) result[name === 'valuenow' ? 'value' : name] = Number(value);
    });
    if (aria('valuetext')) result.valuetext = aria('valuetext');

    const hasPopup = aria('haspopup');
    if (hasPopup && hasPopup !== 'false') result.haspopup = hasPopup === 'true' ? 'menu' : hasPopup;
    else if (tag === 'select' && role === 'combobox') result.haspopup = 'menu';
    const invalid = aria('invalid');
    if (invalid && invalid !== 'false') result.invalid = invalid;
    const autocomplete = aria('autocomplete');
    if (autocomplete && autocomplete !== 'none') result.autocomplete = autocomplete;
    const orientation = aria('orientation');
    if (orientation) result.orientation = orientation;
    if (aria('keyshortcuts')) result.keyshortcuts = aria('keyshortcuts');
    if (aria('roledescription')) result.roledescription = aria('roledescription');
    if (role === 'link' && element.href) result.url = String(element.href.baseVal !== undefined ? element.href.baseVal : element.href);
    return result;
  }

  // ---------------------------------------------------------------------------------------------------------------
  // Snapshot: the accessibility tree with stable element ids
  // ---------------------------------------------------------------------------------------------------------------

  // An id stays with its node across snapshots; the host passes a snapshot number that never repeats for a view, so an
  // id of an earlier document can never name a node of this one.
  const uidOfNode = new WeakMap();
  const nodeOfUid = new Map();
  const hasWeakRef = typeof WeakRef === 'function';

  function remember(node, snapshotId, counter) {
    let uid = uidOfNode.get(node);
    if (!uid) {
      uid = snapshotId + '_' + counter.value++;
      uidOfNode.set(node, uid);
    }
    nodeOfUid.set(uid, hasWeakRef ? new WeakRef(node) : node);
    return uid;
  }

  function nodeForUid(uid) {
    const reference = nodeOfUid.get(uid);
    if (!reference) return null;
    const node = hasWeakRef ? reference.deref() : reference;
    return node || null;
  }

  function fail(code, message) {
    const error = new Error(message);
    error.code = code;
    return error;
  }

  // Resolves an id to the element to act on; a text node stands for its parent element.
  function elementForUid(uid) {
    if (!nodeOfUid.size) throw fail('no-snapshot', 'No snapshot found. Use take_snapshot to capture one.');
    const node = nodeForUid(String(uid));
    // Both say what to do next: the caller is often a language model that only has this text to go by.
    const gone = function () { return fail('stale', 'Element with uid ' + uid + ' no longer exists on the page. Take a new snapshot with take_snapshot.'); };
    if (!node) {
      throw nodeOfUid.has(String(uid)) ? gone() : fail('unknown', 'Element uid "' + uid + '" not found on the page. Take a new snapshot with take_snapshot.');
    }
    const element = node.nodeType === 1 ? node : node.nodeType === 9 ? node.documentElement : node.parentElement;
    if (!element || !element.isConnected) throw gone();
    return element;
  }

  const landmarkRoles = { banner: 1, complementary: 1, contentinfo: 1, form: 1, main: 1, navigation: 1, region: 1, search: 1 };
  const containerRoles = { dialog: 1, alertdialog: 1, alert: 1, status: 1, log: 1, Iframe: 1 };

  function takeSnapshot(options) {
    const verbose = !!options.verbose;
    const snapshotId = String(options.snapshotId);
    const counter = { value: 0 };
    const focused = deepActiveElement(document);
    let count = 0;
    let truncated = false;

    function childNodesOf(element) {
      if (element.shadowRoot) return element.shadowRoot.childNodes;
      if (element.localName === 'slot' && element.assignedNodes && element.assignedNodes().length) return element.assignedNodes();
      return element.childNodes;
    }

    // Returns the nodes an element contributes to the tree: itself, or its children alone when it is left out, and
    // whether anything below it can take the focus.
    function build(element, insideControl) {
      const result = { nodes: [], focusable: false };
      if (count >= MAX_SNAPSHOT_NODES) { truncated = true; return result; }
      if (isHidden(element)) return result;
      const tag = element.localName;
      let role = roleOf(element);
      const editableRoot = isEditableContent(element) && !(element.parentElement && element.parentElement.isContentEditable);
      if (editableRoot && role === 'generic') role = 'textbox';
      const focusable = isFocusable(element) || role === 'option';
      const name = role === 'none' ? '' : clip(nameOf(element, role), 2000);
      const control = !!controlRoles[role];
      const plainTextField = isTextControl(element) || editableRoot || ((role === 'textbox' || role === 'searchbox') && !element.firstElementChild);
      const leafByRole = plainTextField || !!leafRoles[role] || tag === 'svg' || tag === 'canvas' || tag === 'video' || tag === 'audio' || tag === 'math';

      const children = [];
      let focusableBelow = false;
      if (!leafByRole) {
        let frameDocument = null;
        if (tag === 'iframe' || tag === 'frame') {
          try { frameDocument = element.contentDocument; } catch (error) { frameDocument = null; }
        }
        if (frameDocument && frameDocument.documentElement) {
          const inner = build(frameDocument.body || frameDocument.documentElement, false);
          focusableBelow = inner.focusable;
          children.push({
            node: frameDocument, r: 'RootWebArea', n: clip(squash(frameDocument.title), 2000),
            p: { url: String(frameDocument.URL) }, c: inner.nodes,
          });
        } else {
          const nodes = childNodesOf(element);
          for (let index = 0; index < nodes.length; index++) {
            const child = nodes[index];
            if (child.nodeType === 3) {
              const text = squash(child.nodeValue);
              // Text inside a control is part of the control, not a node of its own.
              if (text && (verbose || !(insideControl || control))) children.push({ node: child, r: 'StaticText', n: clip(text, 2000) });
            } else if (child.nodeType === 1) {
              const built = build(child, insideControl || control);
              if (built.focusable) focusableBelow = true;
              for (let inner = 0; inner < built.nodes.length; inner++) children.push(built.nodes[inner]);
            }
          }
        }
      }
      result.focusable = focusable || focusableBelow;

      // A node is a leaf when it has nothing below it, or when what is below only spells out its name: a named
      // control, link, or heading without anything focusable inside. A leaf is listed without children.
      const leaf = leafByRole || !children.length || (!focusableBelow && !!name && (focusable || role === 'heading'));
      let interesting = verbose;
      if (!interesting && role !== 'none') {
        // A landmark, a dialog, or a live region is listed for what it holds; an empty one says nothing.
        const container = (!!landmarkRoles[role] || !!containerRoles[role]) && (children.length !== 0 || !!name);
        interesting = focusable || control || editableRoot || container || (!insideControl && leaf && !!name);
      }
      if (!interesting) { result.nodes = children; return result; }

      count++;
      const node = { node: element, r: role };
      if (name) node.n = name;
      const properties = propertiesOf(element, role, focused);
      const description = descriptionOf(element, name);
      if (description) properties.description = clip(description, 1000);
      if (Object.keys(properties).length) node.p = properties;
      if (children.length && (verbose || !leaf)) node.c = children;
      result.nodes = [node];
      return result;
    }

    // Forget the ids of nodes that are gone, then number the tree.
    nodeOfUid.forEach(function (reference, uid) {
      const node = hasWeakRef ? reference.deref() : reference;
      if (!node || node.isConnected === false) nodeOfUid.delete(uid);
    });

    const body = document.body || document.documentElement;
    const root = {
      node: document, r: 'RootWebArea', n: clip(squash(document.title), 2000),
      p: { url: String(document.URL) }, c: body ? build(body, false).nodes : [],
    };

    // Number the nodes that made it into the tree, in document order. A node seen in an earlier snapshot keeps its id.
    (function number(node) {
      node.i = remember(node.node, snapshotId, counter);
      delete node.node;
      if (node.c) node.c.forEach(number);
    })(root);
    return { snapshotId: snapshotId, root: root, truncated: truncated, nodes: count };
  }

  // ---------------------------------------------------------------------------------------------------------------
  // Synthesized input
  // ---------------------------------------------------------------------------------------------------------------

  const modifiers = { shift: false, ctrl: false, alt: false, meta: false };
  let hovered = null;

  function ancestorsOf(node) {
    const chain = [];
    for (let current = node; current && current.nodeType === 1; current = parentOf(current)) chain.push(current);
    return chain;
  }

  function scrollIntoViewIfNeeded(element) {
    const view = viewOf(element);
    const rect = element.getBoundingClientRect();
    const inside = rect.top >= 0 && rect.left >= 0 && rect.bottom <= view.innerHeight && rect.right <= view.innerWidth;
    if (!inside) {
      try { element.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' }); }
      catch (error) { element.scrollIntoView(); }
    }
  }

  // The point a click on the element lands on: the middle of its first box that has an area, inside the viewport.
  function clickPoint(element) {
    const view = viewOf(element);
    let rect = null;
    const rects = element.getClientRects();
    for (let index = 0; index < rects.length; index++) {
      if (rects[index].width > 0 && rects[index].height > 0) { rect = rects[index]; break; }
    }
    if (!rect) rect = element.getBoundingClientRect();
    if (!(rect.width > 0 && rect.height > 0)) throw fail('not-visible', 'The element ' + describe(element) + ' has no visible box to interact with.');
    const left = Math.max(rect.left, 0), right = Math.min(rect.right, view.innerWidth);
    const top = Math.max(rect.top, 0), bottom = Math.min(rect.bottom, view.innerHeight);
    return { x: right > left ? (left + right) / 2 : rect.left + rect.width / 2, y: bottom > top ? (top + bottom) / 2 : rect.top + rect.height / 2 };
  }

  function elementAt(root, x, y) {
    let element = root.elementFromPoint ? root.elementFromPoint(x, y) : null;
    while (element && element.shadowRoot && element.shadowRoot.elementFromPoint) {
      const inner = element.shadowRoot.elementFromPoint(x, y);
      if (!inner || inner === element) break;
      element = inner;
    }
    return element;
  }

  function mouseInit(target, point, extra) {
    const view = viewOf(target);
    return Object.assign({
      bubbles: true, cancelable: true, composed: true, view: view, detail: 0,
      clientX: point.x, clientY: point.y, screenX: point.x + (view.screenX || 0), screenY: point.y + (view.screenY || 0),
      button: 0, buttons: 0, ctrlKey: modifiers.ctrl, shiftKey: modifiers.shift, altKey: modifiers.alt, metaKey: modifiers.meta,
    }, extra || {});
  }

  function fireMouse(target, type, init) {
    return target.dispatchEvent(new (viewOf(target).MouseEvent)(type, init));
  }

  function firePointer(target, type, init) {
    const view = viewOf(target);
    if (typeof view.PointerEvent !== 'function') return true;
    return target.dispatchEvent(new view.PointerEvent(type, Object.assign({ pointerId: 1, pointerType: 'mouse', isPrimary: true, width: 1, height: 1, pressure: init.buttons ? 0.5 : 0 }, init)));
  }

  // Moves the pointer onto a target: out and leave events for what it left, over and enter events for what it entered.
  function movePointer(target, point) {
    if (hovered !== target) {
      const previous = hovered && hovered.isConnected ? hovered : null;
      const left = previous ? ancestorsOf(previous) : [];
      const entered = ancestorsOf(target);
      if (previous) {
        const init = mouseInit(previous, point, { relatedTarget: target });
        firePointer(previous, 'pointerout', init);
        fireMouse(previous, 'mouseout', init);
        left.forEach(function (element) {
          if (entered.indexOf(element) >= 0) return;
          const leave = mouseInit(element, point, { relatedTarget: target, bubbles: false, cancelable: false, composed: false });
          firePointer(element, 'pointerleave', leave);
          fireMouse(element, 'mouseleave', leave);
        });
      }
      const init = mouseInit(target, point, { relatedTarget: previous });
      firePointer(target, 'pointerover', init);
      fireMouse(target, 'mouseover', init);
      entered.slice().reverse().forEach(function (element) {
        if (left.indexOf(element) >= 0) return;
        const enter = mouseInit(element, point, { relatedTarget: previous, bubbles: false, cancelable: false, composed: false });
        firePointer(element, 'pointerenter', enter);
        fireMouse(element, 'mouseenter', enter);
      });
      hovered = target;
    }
    const move = mouseInit(target, point);
    firePointer(target, 'pointermove', move);
    fireMouse(target, 'mousemove', move);
  }

  // Focuses an element. A document that does not have the system focus, such as one in a background or hidden
  // window, moves its active element without telling the page, so the focus events are dispatched here in that case.
  function focusElement(element) {
    const doc = element.ownerDocument;
    const view = viewOf(element);
    const previous = deepActiveElement(doc);
    if (previous === element) return;
    let notified = false;
    const listener = function () { notified = true; };
    element.addEventListener('focus', listener, true);
    try { element.focus({ preventScroll: true }); } catch (error) { try { element.focus(); } catch (failure) { /* not focusable */ } }
    element.removeEventListener('focus', listener, true);
    if (notified || deepActiveElement(doc) !== element) return;
    if (previous && previous !== doc.body && previous.isConnected) {
      previous.dispatchEvent(new view.FocusEvent('blur', { relatedTarget: element, composed: true }));
      previous.dispatchEvent(new view.FocusEvent('focusout', { relatedTarget: element, bubbles: true, composed: true }));
    }
    element.dispatchEvent(new view.FocusEvent('focus', { relatedTarget: previous, composed: true }));
    element.dispatchEvent(new view.FocusEvent('focusin', { relatedTarget: previous, bubbles: true, composed: true }));
  }

  function focusForPress(target) {
    for (let current = target; current && current.nodeType === 1; current = parentOf(current)) {
      if (isFocusable(current)) { focusElement(current); return; }
    }
    const active = deepActiveElement(target.ownerDocument);
    if (active && active !== target.ownerDocument.body && typeof active.blur === 'function') active.blur();
  }

  // A full click at a point, sent to whatever is on top there, as a real pointer would.
  function clickTarget(target, point, count) {
    movePointer(target, point);
    for (let click = 1; click <= count; click++) {
      const down = mouseInit(target, point, { detail: click, buttons: 1 });
      firePointer(target, 'pointerdown', down);
      if (fireMouse(target, 'mousedown', down)) focusForPress(target);
      const up = mouseInit(target, point, { detail: click });
      firePointer(target, 'pointerup', up);
      fireMouse(target, 'mouseup', up);
      // A dispatched click runs the activation behavior of links, buttons, labels, and form controls.
      fireMouse(target, 'click', up);
      if (click === 2) fireMouse(target, 'dblclick', up);
    }
  }

  function selectOption(option) {
    const select = option.closest('select');
    if (!select || select.multiple || isDisabled(select) || option.disabled || (option.parentElement && option.parentElement.localName === 'optgroup' && option.parentElement.disabled)) return false;
    focusElement(select);
    if (select.value !== option.value || !option.selected) {
      option.selected = true;
      select.dispatchEvent(new (viewOf(select).Event)('input', { bubbles: true, composed: true }));
      select.dispatchEvent(new (viewOf(select).Event)('change', { bubbles: true }));
    }
    return true;
  }

  function click(options) {
    const element = elementForUid(options.uid);
    const count = options.dblClick ? 2 : 1;
    if (element.localName === 'option' && count === 1 && selectOption(element)) return { selected: true };
    if (isDisabled(element) && element.getAttribute('aria-disabled') !== 'true') throw fail('disabled', 'The element ' + describe(element) + ' is disabled.');
    scrollIntoViewIfNeeded(element);
    const point = clickPoint(element);
    const hit = elementAt(element.ownerDocument, point.x, point.y);
    let target = element;
    let obscuredBy;
    if (hit && composedContains(element, hit)) target = hit;
    else if (hit && !composedContains(hit, element)) { target = hit; obscuredBy = describe(hit); }
    clickTarget(target, point, count);
    return obscuredBy ? { obscuredBy: obscuredBy } : {};
  }

  // Finds the element at viewport coordinates of the top-level document, descending into same-origin frames.
  function targetAt(x, y) {
    let doc = document;
    let point = { x: x, y: y };
    for (;;) {
      const element = elementAt(doc, point.x, point.y);
      if (!element) return { target: doc.body || doc.documentElement, point: point };
      if (element.localName === 'iframe' || element.localName === 'frame') {
        let inner = null;
        try { inner = element.contentDocument; } catch (error) { inner = null; }
        if (inner) {
          const rect = element.getBoundingClientRect();
          point = { x: point.x - rect.left - element.clientLeft, y: point.y - rect.top - element.clientTop };
          doc = inner;
          continue;
        }
      }
      return { target: element, point: point };
    }
  }

  function clickAt(options) {
    const found = targetAt(Number(options.x), Number(options.y));
    clickTarget(found.target, found.point, options.dblClick ? 2 : 1);
    return {};
  }

  function hover(options) {
    const element = elementForUid(options.uid);
    scrollIntoViewIfNeeded(element);
    const point = clickPoint(element);
    const hit = elementAt(element.ownerDocument, point.x, point.y);
    const obscured = hit && !composedContains(element, hit) && !composedContains(hit, element);
    movePointer(hit && (composedContains(element, hit) || obscured) ? hit : element, point);
    return obscured ? { obscuredBy: describe(hit) } : {};
  }

  function setNativeValue(element, value) {
    const view = viewOf(element);
    const prototype = element.localName === 'textarea' ? view.HTMLTextAreaElement.prototype : element.localName === 'select' ? view.HTMLSelectElement.prototype : view.HTMLInputElement.prototype;
    // The prototype setter bypasses the value tracking that frameworks install on the element itself, so that the
    // following input event is seen as a change.
    const descriptor = Object.getOwnPropertyDescriptor(prototype, 'value');
    if (descriptor && descriptor.set) descriptor.set.call(element, value);
    else element.value = value;
  }

  function fireInput(element, inputType, data) {
    const view = viewOf(element);
    const init = { bubbles: true, cancelable: false, composed: true, inputType: inputType, data: data == null ? null : data };
    element.dispatchEvent(typeof view.InputEvent === 'function' ? new view.InputEvent('input', init) : new view.Event('input', init));
  }

  function fireBeforeInput(element, inputType, data) {
    const view = viewOf(element);
    if (typeof view.InputEvent !== 'function') return true;
    return element.dispatchEvent(new view.InputEvent('beforeinput', { bubbles: true, cancelable: true, composed: true, inputType: inputType, data: data == null ? null : data }));
  }

  function fireChange(element) {
    element.dispatchEvent(new (viewOf(element).Event)('change', { bubbles: true }));
  }

  function selectContents(element) {
    const selection = viewOf(element).getSelection();
    if (!selection) return;
    const range = element.ownerDocument.createRange();
    range.selectNodeContents(element);
    selection.removeAllRanges();
    selection.addRange(range);
  }

  function isToggle(element) {
    if (element.localName === 'input') return element.type === 'checkbox' || element.type === 'radio';
    const role = element.getAttribute('role');
    return role === 'checkbox' || role === 'radio' || role === 'switch';
  }

  function isChecked(element) {
    return element.localName === 'input' ? element.checked : element.getAttribute('aria-checked') === 'true';
  }

  function fillOne(uid, value) {
    let element = elementForUid(uid);
    value = String(value);
    if (element.localName === 'label' && element.control) element = element.control;
    if (element.localName === 'option') {
      const owner = element.closest('select');
      if (owner) element = owner;
    }
    if (isDisabled(element) && element.getAttribute('aria-disabled') !== 'true') throw fail('disabled', 'The element ' + describe(element) + ' is disabled.');

    if (element.localName === 'select') {
      // An option is chosen by its text, as the snapshot shows it, or by its value.
      let match = null;
      for (let index = 0; index < element.options.length; index++) {
        const option = element.options[index];
        if (squash(option.textContent) === value || squash(option.label) === value) { match = option; break; }
      }
      if (!match) for (let index = 0; index < element.options.length; index++) if (element.options[index].value === value) { match = element.options[index]; break; }
      if (!match) throw fail('no-option', 'Could not find option with text "' + value + '"');
      focusElement(element);
      if (!match.selected || element.value !== match.value) {
        if (element.multiple) match.selected = true; else setNativeValue(element, match.value);
        fireInput(element, 'insertReplacementText', null);
        fireChange(element);
      }
      return;
    }

    if (isToggle(element)) {
      if (value !== 'true' && value !== 'false') throw fail('bad-value', 'Checkboxes, radio boxes and toggles require "true" or "false" value, but ' + value + ' was used');
      if (isChecked(element) !== (value === 'true')) {
        scrollIntoViewIfNeeded(element);
        let point;
        try { point = clickPoint(element); } catch (error) { point = { x: 0, y: 0 }; }
        // A visually hidden native control is still toggled, as a click on its label would.
        clickTarget(element, point, 1);
      }
      return;
    }

    if (element.localName === 'input' || element.localName === 'textarea') {
      if (element.type === 'file') throw fail('file-input', 'Use upload_file for a file input.');
      if (element.readOnly) throw fail('readonly', 'The element ' + describe(element) + ' is read-only.');
      scrollIntoViewIfNeeded(element);
      focusElement(element);
      let text = value;
      if (isTextControl(element) && element.maxLength >= 0 && text.length > element.maxLength) text = text.slice(0, element.maxLength);
      if (!fireBeforeInput(element, 'insertReplacementText', text)) return;
      setNativeValue(element, text);
      try { if (isTextControl(element)) element.setSelectionRange(element.value.length, element.value.length); } catch (error) { /* this input type has no caret */ }
      fireInput(element, 'insertReplacementText', text);
      fireChange(element);
      return;
    }

    if (isEditableContent(element)) {
      scrollIntoViewIfNeeded(element);
      focusElement(element);
      selectContents(element);
      const doc = element.ownerDocument;
      let inserted = false;
      try { inserted = value === '' ? doc.execCommand('delete', false) : doc.execCommand('insertText', false, value); } catch (error) { inserted = false; }
      if (!inserted) {
        element.textContent = value;
        fireInput(element, 'insertReplacementText', value);
      }
      return;
    }

    throw fail('not-fillable', 'The element ' + describe(element) + ' is not an input, a text area, a select, a toggle, or editable content.');
  }

  function fill(options) {
    const elements = options.elements || [{ uid: options.uid, value: options.value }];
    for (let index = 0; index < elements.length; index++) {
      try { fillOne(elements[index].uid, elements[index].value); }
      catch (error) { error.uid = elements[index].uid; throw error; }
    }
    return { filled: elements.length };
  }

  // ---- Keyboard ----

  function keyTarget() {
    const active = deepActiveElement(document);
    return active || document.body || document.documentElement;
  }

  function fireKey(target, type, definition) {
    const view = viewOf(target);
    const init = {
      key: definition.key, code: definition.code || '', location: definition.location || 0, repeat: false, bubbles: true, cancelable: true,
      composed: true, view: view, ctrlKey: modifiers.ctrl, shiftKey: modifiers.shift, altKey: modifiers.alt, metaKey: modifiers.meta,
      keyCode: type === 'keypress' ? definition.charCode || 0 : definition.keyCode || 0, charCode: type === 'keypress' ? definition.charCode || 0 : 0,
      which: type === 'keypress' ? definition.charCode || 0 : definition.keyCode || 0,
    };
    const event = new view.KeyboardEvent(type, init);
    // The legacy codes are not part of the standard initializer in every engine.
    ['keyCode', 'charCode', 'which'].forEach(function (name) {
      if (event[name] !== init[name]) {
        try { Object.defineProperty(event, name, { get: function () { return init[name]; } }); } catch (error) { /* read-only in this engine */ }
      }
    });
    return target.dispatchEvent(event);
  }

  function setModifier(key, down) {
    if (key === 'Shift') modifiers.shift = down;
    else if (key === 'Control') modifiers.ctrl = down;
    else if (key === 'Alt') modifiers.alt = down;
    else if (key === 'Meta') modifiers.meta = down;
    else return false;
    return true;
  }

  function insertText(target, text) {
    if (target.localName === 'input' || target.localName === 'textarea') {
      if (!isTextControl(target) || target.readOnly || isDisabled(target)) return false;
      if (!fireBeforeInput(target, 'insertText', text)) return false;
      let start = null, end = null;
      try { start = target.selectionStart; end = target.selectionEnd; } catch (error) { start = null; }
      const current = target.value;
      if (typeof start !== 'number' || typeof end !== 'number') { start = current.length; end = current.length; }
      if (target.maxLength >= 0) {
        const room = target.maxLength - (current.length - (end - start));
        if (room <= 0) return false;
        text = text.slice(0, room);
      }
      setNativeValue(target, current.slice(0, start) + text + current.slice(end));
      try { target.setSelectionRange(start + text.length, start + text.length); } catch (error) { /* this input type has no caret */ }
      fireInput(target, 'insertText', text);
      return true;
    }
    if (isEditableContent(target)) {
      const doc = target.ownerDocument;
      let inserted = false;
      // execCommand raises the beforeinput and input events itself and keeps the undo history.
      try { inserted = doc.execCommand(text === '\n' ? 'insertLineBreak' : 'insertText', false, text); } catch (error) { inserted = false; }
      if (!inserted && fireBeforeInput(target, 'insertText', text)) {
        const selection = viewOf(target).getSelection();
        if (selection && selection.rangeCount) {
          const range = selection.getRangeAt(0);
          range.deleteContents();
          range.insertNode(doc.createTextNode(text));
          range.collapse(false);
        } else target.appendChild(doc.createTextNode(text));
        fireInput(target, 'insertText', text);
      }
      return true;
    }
    return false;
  }

  function deleteText(target, forward) {
    if (target.localName === 'input' || target.localName === 'textarea') {
      if (!isTextControl(target) || target.readOnly || isDisabled(target)) return;
      let start = null, end = null;
      try { start = target.selectionStart; end = target.selectionEnd; } catch (error) { start = null; }
      const current = target.value;
      if (typeof start !== 'number' || typeof end !== 'number') { start = current.length; end = current.length; }
      if (start === end) { if (forward) end = Math.min(end + 1, current.length); else start = Math.max(start - 1, 0); }
      if (start === end) return;
      const inputType = forward ? 'deleteContentForward' : 'deleteContentBackward';
      if (!fireBeforeInput(target, inputType, null)) return;
      setNativeValue(target, current.slice(0, start) + current.slice(end));
      try { target.setSelectionRange(start, start); } catch (error) { /* this input type has no caret */ }
      fireInput(target, inputType, null);
    } else if (isEditableContent(target)) {
      try { target.ownerDocument.execCommand(forward ? 'forwardDelete' : 'delete', false); } catch (error) { /* nothing to delete */ }
    }
  }

  function tabbableElements(doc) {
    const all = doc.querySelectorAll('a[href],area[href],button,input,select,textarea,iframe,summary,audio[controls],video[controls],[tabindex],[contenteditable]');
    const positive = [], natural = [];
    for (let index = 0; index < all.length; index++) {
      const element = all[index];
      if (!isFocusable(element) || element.tabIndex < 0 || isHidden(element) || element.closest('[inert]')) continue;
      const rect = element.getBoundingClientRect();
      if (!(rect.width > 0 || rect.height > 0)) continue;
      if (element.localName === 'input' && element.type === 'radio' && element.name && !element.checked) {
        // Only the checked radio button of a group, or its first one, takes part in the tab order.
        const group = doc.querySelectorAll('input[type=radio][name="' + (window.CSS && CSS.escape ? CSS.escape(element.name) : element.name) + '"]');
        const checked = Array.prototype.some.call(group, function (radio) { return radio.checked && radio.form === element.form; });
        if (checked || group[0] !== element) continue;
      }
      (element.tabIndex > 0 ? positive : natural).push(element);
    }
    positive.sort(function (left, right) { return left.tabIndex - right.tabIndex; });
    return positive.concat(natural);
  }

  function moveFocus(target, backward) {
    const doc = target.ownerDocument || document;
    const order = tabbableElements(doc);
    if (!order.length) return;
    let index = order.indexOf(target);
    if (index < 0) {
      // From a place outside the tab order, continue with what follows it in the document.
      index = backward ? order.length : -1;
      for (let position = 0; position < order.length; position++) {
        if (target.compareDocumentPosition && (target.compareDocumentPosition(order[position]) & 4)) { index = backward ? position : position - 1; break; }
      }
    }
    const next = order[(index + (backward ? -1 : 1) + order.length) % order.length];
    focusElement(next);
    if (isTextControl(next)) { try { next.select(); } catch (error) { /* this input type has no selection */ } }
  }

  function moveCaret(target, key) {
    let start = null, end = null;
    try { start = target.selectionStart; end = target.selectionEnd; } catch (error) { return; }
    if (typeof start !== 'number') return;
    const length = target.value.length;
    let position = key === 'Home' ? 0 : key === 'End' ? length : key === 'ArrowLeft' ? (start === end || modifiers.shift ? start - 1 : start) : (start === end || modifiers.shift ? end + 1 : end);
    position = Math.max(0, Math.min(length, position));
    try {
      if (!modifiers.shift) target.setSelectionRange(position, position);
      else if (key === 'ArrowLeft' || key === 'Home') target.setSelectionRange(position, end, 'backward');
      else target.setSelectionRange(start, position, 'forward');
    } catch (error) { /* this input type has no caret */ }
  }

  function stepSelection(target, key) {
    const tag = target.localName;
    const delta = key === 'ArrowDown' || key === 'ArrowRight' ? 1 : -1;
    if (tag === 'select' && !target.multiple) {
      let index = target.selectedIndex;
      for (;;) {
        index += delta;
        if (index < 0 || index >= target.options.length) return true;
        if (!target.options[index].disabled) break;
      }
      target.selectedIndex = index;
      fireInput(target, 'insertReplacementText', null);
      fireChange(target);
      return true;
    }
    if (tag === 'input' && target.type === 'radio' && target.name) {
      const group = Array.prototype.filter.call(target.ownerDocument.querySelectorAll('input[type=radio]'), function (radio) { return radio.name === target.name && radio.form === target.form && !radio.disabled; });
      const next = group[(group.indexOf(target) + delta + group.length) % group.length];
      if (next && next !== target) { focusElement(next); fireMouse(next, 'click', mouseInit(next, { x: 0, y: 0 })); }
      return true;
    }
    if (tag === 'input' && (target.type === 'number' || target.type === 'range') && !target.readOnly) {
      const before = target.value;
      const up = target.type === 'range' ? key === 'ArrowUp' || key === 'ArrowRight' : key === 'ArrowUp';
      const down = target.type === 'range' ? key === 'ArrowDown' || key === 'ArrowLeft' : key === 'ArrowDown';
      try { if (up) target.stepUp(); else if (down) target.stepDown(); else return false; } catch (error) { return true; }
      if (target.value !== before) { fireInput(target, 'insertReplacementText', target.value); fireChange(target); }
      return true;
    }
    return false;
  }

  function activates(target, key) {
    const tag = target.localName;
    const role = target.getAttribute('role');
    if (key === 'Enter') return tag === 'button' || tag === 'summary' || (tag === 'a' && target.hasAttribute('href')) || (tag === 'input' && /^(button|submit|reset|image)$/.test(target.type));
    return tag === 'button' || tag === 'summary' || (tag === 'input' && /^(button|submit|reset|image|checkbox|radio)$/.test(target.type)) ||
      role === 'button' || role === 'checkbox' || role === 'radio' || role === 'switch' || role === 'tab' || role === 'menuitem';
  }

  function submitImplicitly(input) {
    const form = input.form;
    if (!form) return;
    const submitter = form.querySelector('button:not([type]),button[type=submit],input[type=submit],input[type=image]');
    if (submitter) { if (!submitter.disabled) fireMouse(submitter, 'click', mouseInit(submitter, { x: 0, y: 0 })); return; }
    // Without a submit button a form is submitted from its only text field.
    const fields = Array.prototype.filter.call(form.elements, function (field) { return field.localName === 'input' && isTextControl(field); });
    if (fields.length !== 1) return;
    if (typeof form.requestSubmit === 'function') form.requestSubmit();
    else if (form.dispatchEvent(new (viewOf(form).Event)('submit', { bubbles: true, cancelable: true }))) { leaving = true; form.submit(); }
  }

  // What the engine itself does for a real key press and does not do for a synthesized one.
  function defaultKeyAction(target, definition) {
    const key = definition.key;
    const editing = (target.localName === 'input' && isTextControl(target)) || target.localName === 'textarea' || isEditableContent(target);
    const command = modifiers.ctrl || modifiers.meta;
    if (command && !modifiers.alt) {
      if (key.toLowerCase() === 'a' && editing) {
        if (isEditableContent(target)) selectContents(target);
        else { try { target.select(); } catch (error) { /* this input type has no selection */ } }
      }
      return;
    }
    if (modifiers.alt) return;
    switch (key) {
      case 'Enter':
        if (target.localName === 'textarea' || isEditableContent(target)) { fireKey(target, 'keypress', definition); insertText(target, '\n'); }
        else if (activates(target, key)) fireMouse(target, 'click', mouseInit(target, { x: 0, y: 0 }));
        else if (target.localName === 'input') { fireKey(target, 'keypress', definition); submitImplicitly(target); }
        return;
      case 'Tab': moveFocus(target, modifiers.shift); return;
      case 'Backspace': deleteText(target, false); return;
      case 'Delete': deleteText(target, true); return;
      case 'Escape': {
        const dialog = target.closest ? target.closest('dialog[open]') : null;
        if (dialog && matchesSafely(dialog, ':modal') && dialog.dispatchEvent(new (viewOf(dialog).Event)('cancel', { cancelable: true }))) dialog.close();
        return;
      }
      case 'ArrowLeft':
      case 'ArrowRight':
      case 'ArrowUp':
      case 'ArrowDown':
      case 'Home':
      case 'End':
        if (stepSelection(target, key)) return;
        if ((target.localName === 'input' && isTextControl(target)) || target.localName === 'textarea') {
          if (key === 'ArrowUp') moveCaret(target, 'Home'); else if (key === 'ArrowDown') moveCaret(target, 'End'); else moveCaret(target, key);
        }
        return;
      default: break;
    }
    if (definition.text) {
      if (definition.text === ' ' && !editing) return;
      if (fireKey(target, 'keypress', definition)) insertText(target, definition.text);
    }
  }

  function pressOne(definition) {
    const target = keyTarget();
    if (setModifier(definition.key, true)) {
      fireKey(target, 'keydown', definition);
      return target;
    }
    if (fireKey(target, 'keydown', definition)) defaultKeyAction(target, definition);
    const after = target.isConnected ? target : keyTarget();
    fireKey(after, 'keyup', definition);
    // A button or a toggle is activated when the space bar is released.
    if (definition.key === ' ' && after === target && activates(target, ' ') && !modifiers.ctrl && !modifiers.meta && !modifiers.alt) fireMouse(target, 'click', mouseInit(target, { x: 0, y: 0 }));
    return target;
  }

  function releaseModifier(definition) {
    setModifier(definition.key, false);
    fireKey(keyTarget(), 'keyup', definition);
  }

  function pressKey(options) {
    const held = [];
    try {
      (options.modifiers || []).forEach(function (definition) { pressOne(definition); held.push(definition); });
      pressOne(options.key);
    } finally {
      held.reverse().forEach(releaseModifier);
      modifiers.shift = modifiers.ctrl = modifiers.alt = modifiers.meta = false;
    }
    return {};
  }

  function characterDefinition(character) {
    const upper = character.toUpperCase();
    let code = '';
    let keyCode = 0;
    if (/^[a-zA-Z]$/.test(character)) { code = 'Key' + upper; keyCode = upper.charCodeAt(0); }
    else if (/^[0-9]$/.test(character)) { code = 'Digit' + character; keyCode = character.charCodeAt(0); }
    else if (character === ' ') { code = 'Space'; keyCode = 32; }
    else if (character === '\n' || character === '\r') return { key: 'Enter', code: 'Enter', keyCode: 13, charCode: 13, text: '' };
    else if (character === '\t') return { key: 'Tab', code: 'Tab', keyCode: 9, text: '' };
    return { key: character, code: code, keyCode: keyCode, charCode: character.codePointAt(0), text: character };
  }

  function typeText(options) {
    const text = String(options.text);
    // Iterating by code point keeps a surrogate pair together.
    for (const character of text) {
      const definition = characterDefinition(character);
      const shifted = definition.text && character !== character.toLowerCase() && character === character.toUpperCase();
      if (shifted) modifiers.shift = true;
      try { pressOne(definition); } finally { if (shifted) modifiers.shift = false; }
    }
    if (options.submitKey) pressKey({ key: options.submitKey });
    return {};
  }

  // ---- Drag and drop ----

  function createDataTransfer(view) {
    try { return new view.DataTransfer(); }
    catch (error) {
      const data = {};
      return {
        dropEffect: 'none', effectAllowed: 'all', files: [], items: [], types: [],
        setData: function (type, value) { data[type] = String(value); if (this.types.indexOf(type) < 0) this.types.push(type); },
        getData: function (type) { return data[type] || ''; },
        clearData: function (type) { if (type) delete data[type]; else Object.keys(data).forEach(function (key) { delete data[key]; }); },
        setDragImage: function () { },
      };
    }
  }

  function fireDrag(target, type, point, dataTransfer, cancelable) {
    const view = viewOf(target);
    const init = mouseInit(target, point, { cancelable: cancelable !== false, buttons: type === 'drop' || type === 'dragend' ? 0 : 1 });
    let event;
    try {
      event = new view.DragEvent(type, Object.assign({ dataTransfer: dataTransfer }, init));
      if (event.dataTransfer !== dataTransfer) Object.defineProperty(event, 'dataTransfer', { value: dataTransfer });
    } catch (error) {
      event = new view.MouseEvent(type, init);
      Object.defineProperty(event, 'dataTransfer', { value: dataTransfer });
    }
    return target.dispatchEvent(event);
  }

  function isDraggable(element) {
    for (let current = element; current && current.nodeType === 1; current = parentOf(current)) {
      const attribute = current.getAttribute('draggable');
      if (attribute === 'true') return current;
      if (attribute === 'false') return null;
      if (current.localName === 'img' || (current.localName === 'a' && current.hasAttribute('href'))) return current;
    }
    return null;
  }

  // A drag is both a pointer gesture and an HTML drag-and-drop operation; pages listen to one or the other.
  function drag(options) {
    const source = elementForUid(options.from);
    const destination = elementForUid(options.to);
    scrollIntoViewIfNeeded(source);
    const from = clickPoint(source);
    const start = elementAt(source.ownerDocument, from.x, from.y);
    const origin = start && composedContains(source, start) ? start : source;
    movePointer(origin, from);
    const down = mouseInit(origin, from, { detail: 1, buttons: 1 });
    firePointer(origin, 'pointerdown', down);
    if (fireMouse(origin, 'mousedown', down)) focusForPress(origin);

    const draggable = isDraggable(origin);
    const dataTransfer = createDataTransfer(viewOf(source));
    const native = !!draggable && fireDrag(draggable, 'dragstart', from, dataTransfer, true);

    scrollIntoViewIfNeeded(destination);
    const to = clickPoint(destination);
    const end = elementAt(destination.ownerDocument, to.x, to.y);
    const target = end && composedContains(destination, end) ? end : destination;
    const steps = 4;
    for (let step = 1; step <= steps; step++) {
      const point = { x: from.x + (to.x - from.x) * step / steps, y: from.y + (to.y - from.y) * step / steps };
      const over = step === steps ? target : (elementAt(source.ownerDocument, point.x, point.y) || origin);
      if (native) {
        fireDrag(draggable, 'drag', point, dataTransfer, true);
      } else {
        const move = mouseInit(over, point, { buttons: 1 });
        firePointer(over, 'pointermove', move);
        fireMouse(over, 'mousemove', move);
      }
    }
    let dropped = false;
    if (native) {
      fireDrag(target, 'dragenter', to, dataTransfer, true);
      // A target accepts a drop by canceling dragover.
      const accepted = !fireDrag(target, 'dragover', to, dataTransfer, true);
      if (accepted) { fireDrag(target, 'drop', to, dataTransfer, true); dropped = true; }
      else fireDrag(target, 'dragleave', to, dataTransfer, false);
      fireDrag(draggable, 'dragend', to, dataTransfer, false);
    } else {
      movePointer(target, to);
      const up = mouseInit(target, to, { detail: 1 });
      firePointer(target, 'pointerup', up);
      fireMouse(target, 'mouseup', up);
      dropped = true;
    }
    return { dropped: dropped, native: native };
  }

  // ---- File upload ----

  const uploads = new Map();

  function uploadChunk(options) {
    const key = String(options.id);
    const parts = uploads.get(key) || [];
    const binary = atob(options.data);
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
    parts.push(bytes);
    uploads.set(key, parts);
    return {};
  }

  function uploadCommit(options) {
    try {
      let element = elementForUid(options.uid);
      if (!(element.localName === 'input' && element.type === 'file')) {
        // A page often shows a button or a label and keeps the real file input hidden next to or inside it.
        let input = element.localName === 'label' ? element.control : null;
        if (!(input && input.type === 'file')) input = element.querySelector ? element.querySelector('input[type=file]') : null;
        if (!input && element.id) input = element.ownerDocument.querySelector('input[type=file][aria-labelledby~="' + element.id + '"]');
        if (!input && element.closest) { const label = element.closest('label'); input = label && label.control && label.control.type === 'file' ? label.control : null; }
        if (!input) throw fail('not-file-input', 'Failed to upload file. The element is not a file input and has no file input of its own.');
        element = input;
      }
      if (isDisabled(element)) throw fail('disabled', 'The element ' + describe(element) + ' is disabled.');
      if (options.files.length > 1 && !element.multiple) throw fail('single-file', 'The file input accepts a single file.');
      const view = viewOf(element);
      const transfer = new view.DataTransfer();
      options.files.forEach(function (file) {
        transfer.items.add(new view.File(uploads.get(String(file.id)) || [], file.name, { type: file.type || '', lastModified: file.lastModified || Date.now() }));
      });
      element.files = transfer.files;
      fireInput(element, 'insertReplacementText', null);
      fireChange(element);
      return { files: element.files.length };
    } finally {
      options.files.forEach(function (file) { uploads.delete(String(file.id)); });
    }
  }

  // ---------------------------------------------------------------------------------------------------------------
  // Waiting
  // ---------------------------------------------------------------------------------------------------------------

  function findText(texts) {
    const body = document.body;
    if (!body) return null;
    const visible = body.innerText || body.textContent || '';
    for (let index = 0; index < texts.length; index++) if (texts[index] && visible.indexOf(texts[index]) >= 0) return texts[index];
    // A name that is not rendered text: a label, an alternative text, a placeholder, or the value of a button.
    const labelled = document.querySelectorAll('[aria-label],[alt],[title],[placeholder],input[type=button],input[type=submit],input[type=reset]');
    for (let index = 0; index < labelled.length; index++) {
      const element = labelled[index];
      if (isHidden(element)) continue;
      const names = [element.getAttribute('aria-label'), element.getAttribute('alt'), element.getAttribute('title'), element.getAttribute('placeholder'), element.localName === 'input' ? element.value : null];
      for (let position = 0; position < texts.length; position++) {
        for (let name = 0; name < names.length; name++) if (names[name] && texts[position] && names[name].indexOf(texts[position]) >= 0) return texts[position];
      }
    }
    return null;
  }

  // A wait is advanced by the host, which asks for its state on its own clock: an engine runs the timers of a page
  // that is not shown once a second at best, and far less often after a while, so nothing here waits on a timer.
  // `step` returns the result, or undefined while the wait goes on, or a number of milliseconds before which it
  // cannot end. `stop` releases what the wait holds, for a wait that the host gave up on.
  function Wait(step, stop, lifetime) {
    this.step = step;
    this.stop = stop;
    this.expires = performance.now() + lifetime + 10000;
  }

  function observeMutations(callback, characterData) {
    try {
      const observer = new MutationObserver(callback);
      observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, characterData: characterData });
      return observer;
    } catch (error) {
      return null;
    }
  }

  function waitForText(options) {
    const texts = (options.texts || []).map(String);
    const timeout = Number(options.timeout) || 5000;
    const started = performance.now();
    let found = findText(texts);
    let checked = started;
    // A text that shows for a moment only is caught as the page changes, between two questions of the host.
    const observer = found !== null ? null : observeMutations(function () {
      const now = performance.now();
      if (found !== null || now - checked < 30) return;
      checked = now;
      found = findText(texts);
    }, true);
    const stop = function () { if (observer) observer.disconnect(); };
    return new Wait(function () {
      if (found === null) found = findText(texts);
      if (found === null && performance.now() - started < timeout) return undefined;
      stop();
      if (found !== null) return { text: found };
      throw fail('timeout', 'Timed out after ' + timeout + ' ms waiting for one of ' + JSON.stringify(texts) + ' to appear on the page.');
    }, stop, timeout);
  }

  // Ends once the DOM has not changed for a moment, or after a deadline.
  function waitForStableDom(options) {
    const quiet = Number(options.quiet) || 100;
    const timeout = Number(options.timeout) || 3000;
    const started = performance.now();
    let changed = started;
    const observer = observeMutations(function () { changed = performance.now(); }, false);
    const stop = function () { if (observer) observer.disconnect(); };
    return new Wait(function () {
      const now = performance.now();
      const stable = now - changed >= quiet;
      if (!stable && now - started < timeout) return Math.min(quiet - (now - changed), timeout - (now - started));
      stop();
      return { stable: stable, elapsed: Math.round(now - started) };
    }, stop, timeout);
  }

  // Ends once the page has had a chance to draw, for a capture that follows a scroll. A page that is not being drawn
  // runs no animation frame, so a short deadline stands in for it.
  function nextFrame() {
    const started = performance.now();
    let drawn = false;
    try { requestAnimationFrame(function () { requestAnimationFrame(function () { drawn = true; }); }); } catch (error) { drawn = true; }
    return new Wait(function () { return drawn || performance.now() - started >= 250 ? {} : undefined; }, function () { }, 250);
  }

  // ---------------------------------------------------------------------------------------------------------------
  // Host interface
  // ---------------------------------------------------------------------------------------------------------------

  function info() {
    return {
      documentId: documentId, url: String(document.URL), title: String(document.title), readyState: document.readyState,
      focused: typeof document.hasFocus === 'function' ? document.hasFocus() : true,
      viewport: { width: window.innerWidth, height: window.innerHeight, scale: window.devicePixelRatio || 1, scrollX: window.scrollX, scrollY: window.scrollY },
    };
  }

  // The box of an element in the top-level viewport, after scrolling it into view, for a capture of the element.
  function elementRect(options) {
    const element = elementForUid(options.uid);
    scrollIntoViewIfNeeded(element);
    let rect = element.getBoundingClientRect();
    let left = rect.left, top = rect.top;
    for (let frame = viewOf(element).frameElement; frame; frame = viewOf(frame).frameElement) {
      const outer = frame.getBoundingClientRect();
      left += outer.left + frame.clientLeft;
      top += outer.top + frame.clientTop;
    }
    return { x: left, y: top, width: rect.width, height: rect.height, viewportWidth: window.innerWidth, viewportHeight: window.innerHeight };
  }

  // The requests the host has not read yet, in the order they started in, then the ones that changed since it read them.
  function takeNetwork() {
    return networkEntries.splice(0).sort(function (first, second) { return first.start - second.start; }).concat(networkUpdates.splice(0));
  }

  // Hands the captured messages and requests to the host and forgets them. Frames of the same origin are included.
  function drain() {
    const result = {
      documentId: documentId, url: String(document.URL), console: consoleEntries.splice(0), consoleDropped: consoleDropped,
      network: takeNetwork(), networkDropped: networkDropped,
    };
    consoleDropped = 0;
    networkDropped = 0;
    try {
      for (let index = 0; index < window.frames.length; index++) {
        let child = null;
        try { child = window.frames[index][KEY]; } catch (error) { child = null; }
        if (!child || typeof child.drainFrame !== 'function') continue;
        const inner = child.drainFrame();
        // Sequence numbers are per document; the frame prefix keeps them apart from the ones of this document.
        inner.console.forEach(function (entry) { entry.frame = inner.documentId; result.console.push(entry); });
        inner.network.forEach(function (entry) { entry.frame = inner.documentId; result.network.push(entry); });
      }
    } catch (error) { /* a frame went away while it was read */ }
    return result;
  }

  const operations = {
    info: info, snapshot: takeSnapshot, click: click, clickAt: clickAt, hover: hover, fill: fill, pressKey: pressKey, typeText: typeText,
    drag: drag, uploadChunk: uploadChunk, uploadCommit: uploadCommit, elementRect: elementRect, drain: drain,
    waitForText: waitForText, waitForStableDom: waitForStableDom, nextFrame: nextFrame,
  };

  function errorResult(error) {
    const message = error && error.message !== undefined ? String(error.message) : String(error);
    const result = { ok: false, error: { message: message, code: (error && error.code) || 'error', name: (error && error.name) || 'Error' } };
    if (error && error.uid !== undefined) result.error.uid = String(error.uid);
    if (error && error.stack && !(error.code)) result.error.stack = clip(formatFrames(parseStack(error.stack)), 2000);
    return result;
  }

  function serialize(value) {
    try { return JSON.stringify(value); }
    catch (error) { return JSON.stringify(errorResult(fail('unserializable', 'The result could not be serialized: ' + (error && error.message)))); }
  }

  // A job is an operation whose result the host reads later with poll(): a Promise that settles by itself, or a wait
  // that poll() advances.
  const jobs = new Map();

  function addJob(id, outcome) {
    const now = performance.now();
    // A wait that the host stopped asking about would keep watching the page for ever.
    jobs.forEach(function (job, key) {
      if (job.wait && job.result === null && now > job.wait.expires) {
        try { job.wait.stop(); } catch (error) { /* nothing to release */ }
        jobs.delete(key);
      }
    });
    const job = { result: null, wait: outcome instanceof Wait ? outcome : null };
    jobs.set(String(id), job);
    if (job.wait) return;
    Promise.resolve(outcome).then(
      function (value) { job.result = { ok: true, value: value === undefined ? null : value }; },
      function (error) { job.result = errorResult(error); });
  }

  // Whether the operation that is running has sent the page to another document. The engine tells the host about a
  // navigation a moment after the event that caused it; this tells the host that the moment is worth waiting for.
  let leaving = false;

  function leavesDocument(address) {
    try {
      const next = new URL(address, document.baseURI);
      const current = new URL(String(document.URL));
      next.hash = '';
      current.hash = '';
      return next.href !== current.href;
    } catch (error) {
      return false;
    }
  }

  function opensHere(target) {
    target = String(target || '').toLowerCase();
    return target === '' || target === '_self' || target === '_top' || target === '_parent';
  }

  function installNavigationIntent() {
    // These listeners are the last to run, so they see whether the page cancelled the event.
    window.addEventListener('click', function (event) {
      try {
        if (event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        const path = typeof event.composedPath === 'function' ? event.composedPath() : [];
        for (let index = 0; index < path.length; index++) {
          const node = path[index];
          if (!node || node.nodeType !== 1 || (node.localName !== 'a' && node.localName !== 'area') || !node.hasAttribute('href')) continue;
          // A link that the browser hands to another program, opens elsewhere, or downloads does not leave the page.
          const loads = /^(https?|file):$/.test(node.protocol) || node.protocol === window.location.protocol;
          if (loads && !node.hasAttribute('download') && opensHere(node.getAttribute('target')) && leavesDocument(node.href)) leaving = true;
          return;
        }
      } catch (error) { /* the click is the page's own business */ }
    });
    window.addEventListener('submit', function (event) {
      try {
        const form = event.target;
        const submitter = event.submitter;
        const target = (submitter && submitter.getAttribute('formtarget')) || (form && form.getAttribute && form.getAttribute('target'));
        const method = String((submitter && submitter.getAttribute('formmethod')) || (form && form.getAttribute && form.getAttribute('method')) || '').toLowerCase();
        if (!event.defaultPrevented && opensHere(target) && method !== 'dialog') leaving = true;
      } catch (error) { /* see above */ }
    });
    // Where the engine announces navigations to the page, a script that assigns to location is seen as well.
    const navigation = window.navigation;
    if (navigation && typeof navigation.addEventListener === 'function') {
      navigation.addEventListener('navigate', function (event) {
        try {
          if (!event.hashChange && !event.downloadRequest && !(event.destination && event.destination.sameDocument)) leaving = true;
        } catch (error) { /* see above */ }
      });
    }
  }

  // A navigation that the page took over keeps its document.
  function isLeaving() {
    if (!leaving) return false;
    try { return !(window.navigation && window.navigation.transition); } catch (error) { return true; }
  }

  const agent = {
    version: 1,
    documentId: documentId,

    // Runs an operation that completes at once and returns its result as JSON text, with the address of the document
    // before and after it, which tells the host whether the operation moved the page within the document.
    call: function (name, options) {
      const before = String(document.URL);
      let result;
      leaving = false;
      try {
        const operation = operations[name];
        if (!operation) throw fail('unknown-operation', 'Unknown automation operation ' + name + '.');
        const value = operation(options || {});
        result = { ok: true, value: value === undefined ? null : value };
      } catch (error) {
        result = errorResult(error);
      }
      result.before = before;
      result.url = String(document.URL);
      if (isLeaving()) result.leaving = true;
      return serialize(result);
    },

    // Starts an operation that completes later; the host reads its result with poll().
    start: function (id, name, options) {
      try {
        const operation = operations[name];
        if (!operation) throw fail('unknown-operation', 'Unknown automation operation ' + name + '.');
        addJob(id, operation(options || {}));
        return serialize({ ok: true, value: null });
      } catch (error) {
        return serialize(errorResult(error));
      }
    },

    // Runs a function supplied by the host with the elements of the given ids, awaits its result, and keeps the
    // JSON text of that result for poll(). The function arrives as code inside the evaluated script, never as a string.
    evaluate: function (id, factory, uids) {
      const toJson = function (result) {
        if (result === undefined) return { undefined: true };
        const json = JSON.stringify(result);
        return json === undefined ? { undefined: true } : { json: json };
      };
      leaving = false;
      const answer = function (result) {
        if (isLeaving()) result.leaving = true;
        return serialize(result);
      };
      try {
        const elements = (uids || []).map(elementForUid);
        const callable = factory();
        const value = typeof callable === 'function' ? callable.apply(undefined, elements) : callable;
        // A function that returns at once has its result now, even when it has just sent the page elsewhere; only a
        // Promise makes the host wait.
        if (value && typeof value.then === 'function') {
          addJob(id, Promise.resolve(value).then(toJson));
          return answer({ ok: true, value: null });
        }
        return answer({ ok: true, value: { done: true, result: toJson(value) } });
      } catch (error) {
        return answer(errorResult(error));
      }
    },

    poll: function (id) {
      const key = String(id);
      if (!jobs.has(key)) return serialize(errorResult(fail('lost', 'The operation is not known to this document.')));
      const job = jobs.get(key);
      let again;
      if (job.result === null && job.wait) {
        try {
          const value = job.wait.step();
          if (typeof value === 'number') again = value;
          else if (value !== undefined) job.result = { ok: true, value: value };
        } catch (error) {
          job.result = errorResult(error);
        }
      }
      if (job.result === null) return serialize(again === undefined ? { pending: true } : { pending: true, again: Math.max(0, Math.ceil(again)) });
      jobs.delete(key);
      job.result.url = String(document.URL);
      if (isLeaving()) job.result.leaving = true;
      return serialize(job.result);
    },

    drainFrame: function () {
      return { documentId: documentId, console: consoleEntries.splice(0), network: takeNetwork() };
    },
  };

  try { Object.defineProperty(window, KEY, { value: agent, enumerable: false, configurable: false, writable: false }); }
  catch (error) { window[KEY] = agent; }

  try { installConsoleCapture(); } catch (error) { /* the page keeps its console */ }
  try { installNetworkCapture(); } catch (error) { /* the page keeps its network functions */ }
  try { installNavigationIntent(); } catch (error) { /* the host then learns of a navigation from the engine alone */ }
})();
//# sourceURL=neoastra-automation-agent.js
