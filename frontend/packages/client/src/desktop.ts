import type { NeoRpcCallOptions, NeoRpcUnsubscribe } from "./rpc.js";

export type DesktopStatus = "Success" | "Canceled" | "Unsupported" | "Denied" | "NotFound" | "NoHandler" | "Conflict" | "Locked" | "Corrupt" | "LimitExceeded" | "Failed";
export interface DesktopResult { readonly status: DesktopStatus; readonly code?: string; }
export interface DesktopValueResult<T> extends DesktopResult { readonly value?: T; }
export interface DesktopBytesResult extends DesktopResult { readonly base64?: string; }
export interface DesktopPathResult extends DesktopResult { readonly path?: string; }
export interface DesktopPathsResult extends DesktopResult { readonly paths?: readonly string[]; }
export interface DesktopRpc {
  invoke<TRequest, TResult>(command: string, args: TRequest, options?: NeoRpcCallOptions): Promise<TResult>;
  subscribe<T>(event: string, handler: (value: T) => void, options?: NeoRpcCallOptions): Promise<NeoRpcUnsubscribe>;
}

export interface DesktopDialogFilter { readonly name: string; readonly extensions: readonly string[]; readonly mimeTypes: readonly string[]; }
export interface DesktopFileDialogRequest {
  readonly initialLocation: string; readonly initialRelativePath?: string; readonly extensions: readonly string[];
  readonly title?: string; readonly suggestedFileName?: string; readonly allowMultiple: boolean; readonly filters: readonly DesktopDialogFilter[];
}
export interface DesktopMessageRequest { readonly title?: string; readonly message: string; readonly detail?: string; readonly icon: "None" | "Information" | "Warning" | "Error" | "Question"; readonly buttons: readonly string[]; }
export interface DesktopMenuItem {
  readonly id: string; readonly kind: "Command" | "Submenu" | "Separator" | "Role"; readonly text?: string; readonly commandId?: string;
  readonly accelerator?: string; readonly enabled: boolean; readonly visible: boolean; readonly checked: boolean; readonly role?: string; readonly children: readonly DesktopMenuItem[];
}
export interface DesktopTrayRequest { readonly id: string; readonly toolTip?: string; readonly isTemplateImage: boolean; readonly menu: readonly DesktopMenuItem[]; }
export interface DesktopNotificationRequest {
  readonly appIdentity: string; readonly category: string; readonly urgency: string; readonly persistent: boolean; readonly payload?: string;
  readonly id: string; readonly title: string; readonly body: string; readonly actions: readonly { readonly id: string; readonly title: string }[];
}
export interface DesktopScopedPath { readonly root: string; readonly relativePath: string; }
export interface DesktopOutboundDragItem { readonly kind: "Text" | "File" | "Url"; readonly value?: string; readonly root?: string; readonly relativePath?: string; }
export type DesktopWindowState = "Normal" | "Minimized" | "Maximized" | "Fullscreen";
export type DesktopWindowCloseReason = "User" | "Owner" | "ApplicationQuit" | "SessionEnd" | "System" | "Programmatic";
export interface DesktopWindowSnapshot {
  readonly title: string; readonly position: { readonly x: number; readonly y: number }; readonly size: { readonly width: number; readonly height: number };
  readonly minimumSize: { readonly width: number; readonly height: number }; readonly maximumSize: { readonly width: number; readonly height: number };
  readonly visible: boolean; readonly focused: boolean; readonly closed: boolean; readonly scaleFactor: number; readonly state: DesktopWindowState;
  readonly decorations: boolean; readonly resizable: boolean; readonly alwaysOnTop: boolean; readonly taskbarVisible?: boolean; readonly modal: boolean;
}
export interface DesktopWindowCloseRequestedEvent {
  readonly reason: DesktopWindowCloseReason; readonly canCancel: boolean;
  preventDefault(): void;
}

export const desktopCommands = Object.freeze({
  dialogs: Object.freeze({ openFile: "desktop.dialogs.open-file", openFolder: "desktop.dialogs.open-folder", saveFile: "desktop.dialogs.save-file", message: "desktop.dialogs.message" }),
  menus: Object.freeze({ activate: "desktop.menus.activate", set: "desktop.menus.set", popup: "desktop.menus.popup" }),
  tray: Object.freeze({ create: "desktop.tray.create", update: "desktop.tray.update", remove: "desktop.tray.remove", activated: "desktop.tray.activated" }),
  clipboard: Object.freeze({ readText: "desktop.clipboard.read-text", writeText: "desktop.clipboard.write-text", readRich: "desktop.clipboard.read-rich", writeRich: "desktop.clipboard.write-rich", clear: "desktop.clipboard.clear" }),
  notifications: Object.freeze({ status: "desktop.notifications.status", show: "desktop.notifications.show", remove: "desktop.notifications.remove", activated: "desktop.notifications.activated" }),
  shortcuts: Object.freeze({ register: "desktop.shortcuts.register", unregister: "desktop.shortcuts.unregister", activated: "desktop.shortcuts.activated" }),
  system: Object.freeze({ theme: "desktop.system.theme", displays: "desktop.system.displays", metadata: "desktop.system.metadata", themeChanged: "desktop.system.theme-changed", displaysChanged: "desktop.system.displays-changed" }),
  opener: Object.freeze({ url: "desktop.opener.url", file: "desktop.opener.file", reveal: "desktop.opener.reveal" }),
  dragDrop: Object.freeze({ outbound: "desktop.drag-drop.outbound", resolveFile: "desktop.drag-drop.resolve-file", inbound: "desktop.drag-drop.inbound" }),
  safeStorage: Object.freeze({ store: "desktop.safe-storage.store", retrieve: "desktop.safe-storage.retrieve", delete: "desktop.safe-storage.delete", contains: "desktop.safe-storage.contains" }),
  window: Object.freeze({
    getState: "desktop.window.get-state", setTitle: "desktop.window.set-title", setPosition: "desktop.window.set-position", setSize: "desktop.window.set-size", setMinimumSize: "desktop.window.set-minimum-size", setMaximumSize: "desktop.window.set-maximum-size",
    show: "desktop.window.show", hide: "desktop.window.hide", focus: "desktop.window.focus", maximize: "desktop.window.maximize", minimize: "desktop.window.minimize", restore: "desktop.window.restore", setFullscreen: "desktop.window.set-fullscreen",
    setDecorations: "desktop.window.set-decorations", setResizable: "desktop.window.set-resizable", setAlwaysOnTop: "desktop.window.set-always-on-top", setTaskbarVisible: "desktop.window.set-taskbar-visible", close: "desktop.window.close",
    interceptClose: "desktop.window.intercept-close", completeClose: "desktop.window.complete-close", closeRequested: "desktop.window.close-requested",
    setIcon: "desktop.window.set-icon", setRepresentedFile: "desktop.window.set-represented-file", requestAttention: "desktop.window.request-attention", setProgress: "desktop.window.set-progress", setBadge: "desktop.window.set-badge", setDocumentEdited: "desktop.window.set-document-edited", setContentProtection: "desktop.window.set-content-protection", setTitleBarTheme: "desktop.window.set-titlebar-theme",
  }),
  application: Object.freeze({ requestQuit: "desktop.application.request-quit" }),
} as const);

export function createDesktopClient(rpc: DesktopRpc) {
  if (rpc === null || typeof rpc !== "object" || typeof rpc.invoke !== "function" || typeof rpc.subscribe !== "function") throw new TypeError("A desktop RPC client is required.");
  const invoke = <T>(command: string, args: unknown, options?: NeoRpcCallOptions): Promise<T> => rpc.invoke(command, args, options);
  const subscribe = <T>(event: string, handler: (value: T) => void, options?: NeoRpcCallOptions) => rpc.subscribe(event, handler, options);
  const dialog = (kind: string, value: DesktopFileDialogRequest) => ({ kind, ...value });
  type CloseWireEvent = { readonly requestId: number; readonly reason: DesktopWindowCloseReason; readonly canCancel: boolean };
  const closeHandlers = new Set<(event: DesktopWindowCloseRequestedEvent) => void | Promise<void>>();
  let closeSubscription: Promise<NeoRpcUnsubscribe> | undefined;
  let closeSetup: Promise<void> | undefined;
  let closeOptions: NeoRpcCallOptions | undefined;
  async function dispatchClose(value: CloseWireEvent) {
    let prevented = false;
    const event: DesktopWindowCloseRequestedEvent = Object.freeze({
      reason: value.reason,
      canCancel: value.canCancel,
      preventDefault: () => { if (value.canCancel) prevented = true; },
    });
    for (const handler of [...closeHandlers]) {
      try { await handler(event); } catch { prevented = value.canCancel; }
    }
    await invoke<DesktopResult>(desktopCommands.window.completeClose, { requestId: value.requestId, preventDefault: prevented }, closeOptions);
  }
  async function onCloseRequested(handler: (event: DesktopWindowCloseRequestedEvent) => void | Promise<void>, options?: NeoRpcCallOptions): Promise<NeoRpcUnsubscribe> {
    if (typeof handler !== "function") throw new TypeError("A window close-request handler is required.");
    closeHandlers.add(handler);
    let setup: Promise<void> | undefined;
    try {
      if (closeSetup === undefined) {
        closeOptions = options;
        closeSubscription = subscribe<CloseWireEvent>(desktopCommands.window.closeRequested, value => { void dispatchClose(value).catch(() => { /* The host deadline preserves the window. */ }); }, options);
        closeSetup = (async () => {
          await closeSubscription;
          const enabled = await invoke<DesktopResult>(desktopCommands.window.interceptClose, { value: true }, options);
          if (enabled.status !== "Success") throw new Error(`Could not intercept window close requests: ${enabled.status}`);
        })();
      }
      setup = closeSetup;
      await setup;
    } catch (error) {
      closeHandlers.delete(handler);
      if (closeSetup === setup) {
        const failedSubscription = closeSubscription;
        closeSubscription = undefined;
        closeSetup = undefined;
        closeOptions = undefined;
        if (failedSubscription !== undefined) {
          try { await (await failedSubscription)(); } catch { /* Preserve the setup failure. */ }
        }
      }
      throw error;
    }
    let listening = true;
    return async () => {
      if (!listening) return;
      listening = false;
      closeHandlers.delete(handler);
      if (closeHandlers.size !== 0 || closeSubscription === undefined) return;
      await closeSetup;
      const unsubscribe = await closeSubscription;
      closeSubscription = undefined;
      closeSetup = undefined;
      try { await invoke<DesktopResult>(desktopCommands.window.interceptClose, { value: false }, closeOptions); }
      finally { await unsubscribe(); closeOptions = undefined; }
    };
  }
  return Object.freeze({
    dialogs: Object.freeze({
      openFile: (value: DesktopFileDialogRequest, options?: NeoRpcCallOptions) => invoke<DesktopPathsResult>(desktopCommands.dialogs.openFile, dialog("openFile", value), options),
      openFolder: (value: DesktopFileDialogRequest, options?: NeoRpcCallOptions) => invoke<DesktopPathsResult>(desktopCommands.dialogs.openFolder, dialog("openFolder", value), options),
      saveFile: (value: DesktopFileDialogRequest, options?: NeoRpcCallOptions) => invoke<DesktopPathResult>(desktopCommands.dialogs.saveFile, dialog("saveFile", value), options),
      message: (value: DesktopMessageRequest, options?: NeoRpcCallOptions) => invoke<DesktopValueResult<string>>(desktopCommands.dialogs.message, { kind: "message", ...value }, options),
    }),
    menus: Object.freeze({
      activate: (id: string, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.menus.activate, { id }, options),
      set: (targetId: string, items: readonly DesktopMenuItem[], options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.menus.set, { targetId, items }, options),
      popup: (targetId: string, x: number, y: number, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.menus.popup, { targetId, x, y }, options),
    }),
    tray: Object.freeze({
      create: (value: DesktopTrayRequest, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.tray.create, value, options),
      update: (value: DesktopTrayRequest, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.tray.update, value, options),
      remove: (id: string, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.tray.remove, { id }, options),
      onActivated: (handler: (value: { readonly id: string; readonly secondary: boolean }) => void, options?: NeoRpcCallOptions) => subscribe(desktopCommands.tray.activated, handler, options),
    }),
    clipboard: Object.freeze({
      read: (format: "text" | "html" | "image" | "files", options?: NeoRpcCallOptions) => invoke<DesktopBytesResult>(format === "text" ? desktopCommands.clipboard.readText : desktopCommands.clipboard.readRich, { format, operation: "read" }, options),
      write: (format: "text" | "html" | "image" | "files", base64: string, options?: NeoRpcCallOptions) => invoke<DesktopResult>(format === "text" ? desktopCommands.clipboard.writeText : desktopCommands.clipboard.writeRich, { format, operation: "write", base64 }, options),
      clear: (options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.clipboard.clear, { format: "all", operation: "write" }, options),
    }),
    notifications: Object.freeze({
      status: (options?: NeoRpcCallOptions) => invoke<{ readonly status: string }>(desktopCommands.notifications.status, {}, options),
      show: (value: DesktopNotificationRequest, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.notifications.show, value, options),
      remove: (id: string, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.notifications.remove, { id }, options),
      onActivated: (handler: (value: { readonly id: string; readonly actionId?: string; readonly payload?: string }) => void, options?: NeoRpcCallOptions) => subscribe(desktopCommands.notifications.activated, handler, options),
    }),
    shortcuts: Object.freeze({
      register: (id: string, accelerator: string, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.shortcuts.register, { id, accelerator }, options),
      unregister: (id: string, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.shortcuts.unregister, { id }, options),
      onActivated: (handler: (value: { readonly id: string }) => void, options?: NeoRpcCallOptions) => subscribe(desktopCommands.shortcuts.activated, handler, options),
    }),
    system: Object.freeze({
      theme: (options?: NeoRpcCallOptions) => invoke<unknown>(desktopCommands.system.theme, {}, options),
      displays: (options?: NeoRpcCallOptions) => invoke<unknown>(desktopCommands.system.displays, {}, options),
      metadata: (options?: NeoRpcCallOptions) => invoke<unknown>(desktopCommands.system.metadata, {}, options),
      onThemeChanged: (handler: (value: unknown) => void, options?: NeoRpcCallOptions) => subscribe(desktopCommands.system.themeChanged, handler, options),
      onDisplaysChanged: (handler: (value: unknown) => void, options?: NeoRpcCallOptions) => subscribe(desktopCommands.system.displaysChanged, handler, options),
    }),
    opener: Object.freeze({
      url: (url: string, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.opener.url, { url }, options),
      file: (path: DesktopScopedPath, intent: "OpenDocument" | "OpenContainingApplication", options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.opener.file, { ...path, operation: "open", intent }, options),
      reveal: (path: DesktopScopedPath, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.opener.reveal, { ...path, operation: "reveal" }, options),
    }),
    dragDrop: Object.freeze({
      outbound: (viewLabel: string, items: readonly DesktopOutboundDragItem[], options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.dragDrop.outbound, { viewLabel, items }, options),
      resolveFile: (token: string, options?: NeoRpcCallOptions) => invoke<DesktopPathResult>(desktopCommands.dragDrop.resolveFile, { token }, options),
      onInbound: (handler: (value: unknown) => void, options?: NeoRpcCallOptions) => subscribe(desktopCommands.dragDrop.inbound, handler, options),
    }),
    safeStorage: Object.freeze({
      store: (id: string, base64: string, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.safeStorage.store, { id, base64 }, options),
      retrieve: (id: string, options?: NeoRpcCallOptions) => invoke<DesktopBytesResult>(desktopCommands.safeStorage.retrieve, { id }, options),
      contains: (id: string, options?: NeoRpcCallOptions) => invoke<DesktopValueResult<boolean>>(desktopCommands.safeStorage.contains, { id }, options),
      delete: (id: string, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.safeStorage.delete, { id }, options),
    }),
    window: Object.freeze({
      state: (options?: NeoRpcCallOptions) => invoke<{ readonly value: DesktopWindowSnapshot }>(desktopCommands.window.getState, {}, options),
      setTitle: (value: string, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setTitle, { value }, options),
      setPosition: (x: number, y: number, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setPosition, { x, y }, options),
      setSize: (width: number, height: number, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setSize, { width, height }, options),
      setMinimumSize: (width: number, height: number, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setMinimumSize, { width, height }, options),
      setMaximumSize: (width: number, height: number, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setMaximumSize, { width, height }, options),
      show: (options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.show, {}, options),
      hide: (options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.hide, {}, options),
      focus: (options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.focus, {}, options),
      maximize: (options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.maximize, {}, options),
      minimize: (options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.minimize, {}, options),
      restore: (options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.restore, {}, options),
      setFullscreen: (value: boolean, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setFullscreen, { value }, options),
      setDecorations: (value: boolean, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setDecorations, { value }, options),
      setResizable: (value: boolean, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setResizable, { value }, options),
      setAlwaysOnTop: (value: boolean, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setAlwaysOnTop, { value }, options),
      setTaskbarVisible: (value: boolean, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setTaskbarVisible, { value }, options),
      close: (options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.close, {}, options),
      onCloseRequested,
      setIcon: (path: DesktopScopedPath, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setIcon, { ...path, operation: "read" }, options),
      setRepresentedFile: (path?: DesktopScopedPath, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setRepresentedFile, path === undefined ? { operation: "read" } : { ...path, operation: "read" }, options),
      requestAttention: (critical = false, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.requestAttention, { value: critical }, options),
      setProgress: (state: "None" | "Normal" | "Paused" | "Error" | "Indeterminate", value: number, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setProgress, { state, value }, options),
      setBadge: (value?: string, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setBadge, { value }, options),
      setDocumentEdited: (value: boolean, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setDocumentEdited, { value }, options),
      setContentProtection: (value: boolean, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setContentProtection, { value }, options),
      setTitleBarTheme: (theme: "System" | "Light" | "Dark", options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setTitleBarTheme, { theme }, options),
    }),
    application: Object.freeze({
      requestQuit: (options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.application.requestQuit, {}, options),
    }),
  });
}

export type NeoDesktopClient = ReturnType<typeof createDesktopClient>;
