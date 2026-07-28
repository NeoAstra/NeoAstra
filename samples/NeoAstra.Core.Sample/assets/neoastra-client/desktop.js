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
});
export function createDesktopClient(rpc) {
    if (rpc === null || typeof rpc !== "object" || typeof rpc.invoke !== "function" || typeof rpc.subscribe !== "function")
        throw new TypeError("A desktop RPC client is required.");
    const invoke = (command, args, options) => rpc.invoke(command, args, options);
    const subscribe = (event, handler, options) => rpc.subscribe(event, handler, options);
    const dialog = (kind, value) => ({ kind, ...value });
    return Object.freeze({
        dialogs: Object.freeze({
            openFile: (value, options) => invoke(desktopCommands.dialogs.openFile, dialog("openFile", value), options),
            openFolder: (value, options) => invoke(desktopCommands.dialogs.openFolder, dialog("openFolder", value), options),
            saveFile: (value, options) => invoke(desktopCommands.dialogs.saveFile, dialog("saveFile", value), options),
            message: (value, options) => invoke(desktopCommands.dialogs.message, { kind: "message", ...value }, options),
        }),
        menus: Object.freeze({
            activate: (id, options) => invoke(desktopCommands.menus.activate, { id }, options),
            set: (targetId, items, options) => invoke(desktopCommands.menus.set, { targetId, items }, options),
            popup: (targetId, x, y, options) => invoke(desktopCommands.menus.popup, { targetId, x, y }, options),
        }),
        tray: Object.freeze({
            create: (value, options) => invoke(desktopCommands.tray.create, value, options),
            update: (value, options) => invoke(desktopCommands.tray.update, value, options),
            remove: (id, options) => invoke(desktopCommands.tray.remove, { id }, options),
            onActivated: (handler, options) => subscribe(desktopCommands.tray.activated, handler, options),
        }),
        clipboard: Object.freeze({
            read: (format, options) => invoke(format === "text" ? desktopCommands.clipboard.readText : desktopCommands.clipboard.readRich, { format, operation: "read" }, options),
            write: (format, base64, options) => invoke(format === "text" ? desktopCommands.clipboard.writeText : desktopCommands.clipboard.writeRich, { format, operation: "write", base64 }, options),
            clear: (options) => invoke(desktopCommands.clipboard.clear, { format: "all", operation: "write" }, options),
        }),
        notifications: Object.freeze({
            status: (options) => invoke(desktopCommands.notifications.status, {}, options),
            show: (value, options) => invoke(desktopCommands.notifications.show, value, options),
            remove: (id, options) => invoke(desktopCommands.notifications.remove, { id }, options),
            onActivated: (handler, options) => subscribe(desktopCommands.notifications.activated, handler, options),
        }),
        shortcuts: Object.freeze({
            register: (id, accelerator, options) => invoke(desktopCommands.shortcuts.register, { id, accelerator }, options),
            unregister: (id, options) => invoke(desktopCommands.shortcuts.unregister, { id }, options),
            onActivated: (handler, options) => subscribe(desktopCommands.shortcuts.activated, handler, options),
        }),
        system: Object.freeze({
            theme: (options) => invoke(desktopCommands.system.theme, {}, options),
            displays: (options) => invoke(desktopCommands.system.displays, {}, options),
            metadata: (options) => invoke(desktopCommands.system.metadata, {}, options),
            onThemeChanged: (handler, options) => subscribe(desktopCommands.system.themeChanged, handler, options),
            onDisplaysChanged: (handler, options) => subscribe(desktopCommands.system.displaysChanged, handler, options),
        }),
        opener: Object.freeze({
            url: (url, options) => invoke(desktopCommands.opener.url, { url }, options),
            file: (path, intent, options) => invoke(desktopCommands.opener.file, { ...path, operation: "open", intent }, options),
            reveal: (path, options) => invoke(desktopCommands.opener.reveal, { ...path, operation: "reveal" }, options),
        }),
        dragDrop: Object.freeze({
            outbound: (viewLabel, items, options) => invoke(desktopCommands.dragDrop.outbound, { viewLabel, items }, options),
            resolveFile: (token, options) => invoke(desktopCommands.dragDrop.resolveFile, { token }, options),
            onInbound: (handler, options) => subscribe(desktopCommands.dragDrop.inbound, handler, options),
        }),
        safeStorage: Object.freeze({
            store: (id, base64, options) => invoke(desktopCommands.safeStorage.store, { id, base64 }, options),
            retrieve: (id, options) => invoke(desktopCommands.safeStorage.retrieve, { id }, options),
            contains: (id, options) => invoke(desktopCommands.safeStorage.contains, { id }, options),
            delete: (id, options) => invoke(desktopCommands.safeStorage.delete, { id }, options),
        }),
        window: Object.freeze({
            setIcon: (path, options) => invoke(desktopCommands.window.setIcon, { ...path, operation: "read" }, options),
            setRepresentedFile: (path, options) => invoke(desktopCommands.window.setRepresentedFile, path === undefined ? { operation: "read" } : { ...path, operation: "read" }, options),
            requestAttention: (critical = false, options) => invoke(desktopCommands.window.requestAttention, { value: critical }, options),
            setProgress: (state, value, options) => invoke(desktopCommands.window.setProgress, { state, value }, options),
            setBadge: (value, options) => invoke(desktopCommands.window.setBadge, { value }, options),
            setDocumentEdited: (value, options) => invoke(desktopCommands.window.setDocumentEdited, { value }, options),
            setContentProtection: (value, options) => invoke(desktopCommands.window.setContentProtection, { value }, options),
            setTitleBarTheme: (theme, options) => invoke(desktopCommands.window.setTitleBarTheme, { theme }, options),
        }),
    });
}
//# sourceMappingURL=desktop.js.map