(() => {
  globalThis.__fixtureExternalAsset = "loaded";
  globalThis.__fixtureSawDocumentStartScript = globalThis.__neoDocumentStart === "injected";
  globalThis.__fixtureHostMessages = [];

  const receive = value => globalThis.__fixtureHostMessages.push(value);
  if (globalThis.chrome?.webview) {
    globalThis.chrome.webview.addEventListener("message", event => receive(event.data));
  }
  globalThis.addEventListener("neowebviewmessage", event => receive(event.detail));

  globalThis.__fixturePostMessage = value => {
    if (globalThis.chrome?.webview) {
      globalThis.chrome.webview.postMessage(value);
      return;
    }
    if (globalThis.webkit?.messageHandlers?.neowebview) {
      globalThis.webkit.messageHandlers.neowebview.postMessage(value);
      return;
    }
    throw new Error("The NeoWebView message bridge is unavailable.");
  };
})();
