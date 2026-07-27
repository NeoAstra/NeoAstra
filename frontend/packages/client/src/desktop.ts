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
  window: Object.freeze({ setIcon: "desktop.window.set-icon", setRepresentedFile: "desktop.window.set-represented-file", requestAttention: "desktop.window.request-attention", setProgress: "desktop.window.set-progress", setBadge: "desktop.window.set-badge", setDocumentEdited: "desktop.window.set-document-edited", setContentProtection: "desktop.window.set-content-protection", setTitleBarTheme: "desktop.window.set-titlebar-theme" }),
} as const);

export function createDesktopClient(rpc: DesktopRpc) {
  if (rpc === null || typeof rpc !== "object" || typeof rpc.invoke !== "function" || typeof rpc.subscribe !== "function") throw new TypeError("A desktop RPC client is required.");
  const invoke = <T>(command: string, args: unknown, options?: NeoRpcCallOptions): Promise<T> => rpc.invoke(command, args, options);
  const subscribe = <T>(event: string, handler: (value: T) => void, options?: NeoRpcCallOptions) => rpc.subscribe(event, handler, options);
  const dialog = (kind: string, value: DesktopFileDialogRequest) => ({ kind, ...value });
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
      onActivated: (handler: (value: { readonly id: string }) => void, options?: NeoRpcCallOptions) => subscribe(desktopCommands.tray.activated, handler, options),
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
      setIcon: (path: DesktopScopedPath, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setIcon, { ...path, operation: "read" }, options),
      setRepresentedFile: (path?: DesktopScopedPath, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setRepresentedFile, path === undefined ? { operation: "read" } : { ...path, operation: "read" }, options),
      requestAttention: (critical = false, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.requestAttention, { value: critical }, options),
      setProgress: (state: "None" | "Normal" | "Paused" | "Error" | "Indeterminate", value: number, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setProgress, { state, value }, options),
      setBadge: (value?: string, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setBadge, { value }, options),
      setDocumentEdited: (value: boolean, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setDocumentEdited, { value }, options),
      setContentProtection: (value: boolean, options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setContentProtection, { value }, options),
      setTitleBarTheme: (theme: "System" | "Light" | "Dark", options?: NeoRpcCallOptions) => invoke<DesktopResult>(desktopCommands.window.setTitleBarTheme, { theme }, options),
    }),
  });
}

export type NeoDesktopClient = ReturnType<typeof createDesktopClient>;
