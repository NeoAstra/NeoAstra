(() => {
  const post = value => {
    if (globalThis.chrome?.webview) {
      globalThis.chrome.webview.postMessage(value);
      return;
    }
    if (globalThis.webkit?.messageHandlers?.neoastra) {
      globalThis.webkit.messageHandlers.neoastra.postMessage(value);
      return;
    }
    throw new Error("The NeoAstra message bridge is unavailable.");
  };

  globalThis.__benchmarkSend = (token, count, payloadSize) => {
    const payload = "x".repeat(payloadSize);
    for (let index = 0; index < count; index++) {
      post({ kind: "benchmark", token, index, payload });
    }
    return count;
  };
})();
