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
        setIcon: "desktop.window.set-icon", setRepresentedFile: "desktop.window.set-represented-file", getExtraSupport: "desktop.window.get-extra-support", requestAttention: "desktop.window.request-attention", setProgress: "desktop.window.set-progress", setBadge: "desktop.window.set-badge", setDocumentEdited: "desktop.window.set-document-edited", setContentProtection: "desktop.window.set-content-protection", setTitleBarTheme: "desktop.window.set-titlebar-theme",
    }),
    application: Object.freeze({ requestQuit: "desktop.application.request-quit" }),
});
export function createDesktopClient(rpc) {
    if (rpc === null || typeof rpc !== "object" || typeof rpc.invoke !== "function" || typeof rpc.subscribe !== "function")
        throw new TypeError("A desktop RPC client is required.");
    const invoke = (command, args, options) => rpc.invoke(command, args, options);
    const subscribe = (event, handler, options) => rpc.subscribe(event, handler, options);
    const dialog = (kind, value) => ({ kind, ...value });
    const closeHandlers = new Set();
    let closeSubscription;
    let closeSetup;
    let closeOptions;
    async function dispatchClose(value) {
        let prevented = false;
        const event = Object.freeze({
            reason: value.reason,
            canCancel: value.canCancel,
            preventDefault: () => { if (value.canCancel)
                prevented = true; },
        });
        for (const handler of [...closeHandlers]) {
            try {
                await handler(event);
            }
            catch {
                prevented = value.canCancel;
            }
        }
        await invoke(desktopCommands.window.completeClose, { requestId: value.requestId, preventDefault: prevented }, closeOptions);
    }
    async function onCloseRequested(handler, options) {
        if (typeof handler !== "function")
            throw new TypeError("A window close-request handler is required.");
        closeHandlers.add(handler);
        let setup;
        try {
            if (closeSetup === undefined) {
                closeOptions = options;
                closeSubscription = subscribe(desktopCommands.window.closeRequested, value => { void dispatchClose(value).catch(() => { }); }, options);
                closeSetup = (async () => {
                    await closeSubscription;
                    const enabled = await invoke(desktopCommands.window.interceptClose, { value: true }, options);
                    if (enabled.status !== "Success")
                        throw new Error(`Could not intercept window close requests: ${enabled.status}`);
                })();
            }
            setup = closeSetup;
            await setup;
        }
        catch (error) {
            closeHandlers.delete(handler);
            if (closeSetup === setup) {
                const failedSubscription = closeSubscription;
                closeSubscription = undefined;
                closeSetup = undefined;
                closeOptions = undefined;
                if (failedSubscription !== undefined) {
                    try {
                        await (await failedSubscription)();
                    }
                    catch { /* Preserve the setup failure. */ }
                }
            }
            throw error;
        }
        let listening = true;
        return async () => {
            if (!listening)
                return;
            listening = false;
            closeHandlers.delete(handler);
            if (closeHandlers.size !== 0 || closeSubscription === undefined)
                return;
            await closeSetup;
            const unsubscribe = await closeSubscription;
            closeSubscription = undefined;
            closeSetup = undefined;
            try {
                await invoke(desktopCommands.window.interceptClose, { value: false }, closeOptions);
            }
            finally {
                await unsubscribe();
                closeOptions = undefined;
            }
        };
    }
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
            state: (options) => invoke(desktopCommands.window.getState, {}, options),
            setTitle: (value, options) => invoke(desktopCommands.window.setTitle, { value }, options),
            setPosition: (x, y, options) => invoke(desktopCommands.window.setPosition, { x, y }, options),
            setSize: (width, height, options) => invoke(desktopCommands.window.setSize, { width, height }, options),
            setMinimumSize: (width, height, options) => invoke(desktopCommands.window.setMinimumSize, { width, height }, options),
            setMaximumSize: (width, height, options) => invoke(desktopCommands.window.setMaximumSize, { width, height }, options),
            show: (options) => invoke(desktopCommands.window.show, {}, options),
            hide: (options) => invoke(desktopCommands.window.hide, {}, options),
            focus: (options) => invoke(desktopCommands.window.focus, {}, options),
            maximize: (options) => invoke(desktopCommands.window.maximize, {}, options),
            minimize: (options) => invoke(desktopCommands.window.minimize, {}, options),
            restore: (options) => invoke(desktopCommands.window.restore, {}, options),
            setFullscreen: (value, options) => invoke(desktopCommands.window.setFullscreen, { value }, options),
            setDecorations: (value, options) => invoke(desktopCommands.window.setDecorations, { value }, options),
            setResizable: (value, options) => invoke(desktopCommands.window.setResizable, { value }, options),
            setAlwaysOnTop: (value, options) => invoke(desktopCommands.window.setAlwaysOnTop, { value }, options),
            setTaskbarVisible: (value, options) => invoke(desktopCommands.window.setTaskbarVisible, { value }, options),
            close: (options) => invoke(desktopCommands.window.close, {}, options),
            onCloseRequested,
            setIcon: (path, options) => invoke(desktopCommands.window.setIcon, { ...path, operation: "read" }, options),
            setRepresentedFile: (path, options) => invoke(desktopCommands.window.setRepresentedFile, path === undefined ? { operation: "read" } : { ...path, operation: "read" }, options),
            extraSupport: (options) => invoke(desktopCommands.window.getExtraSupport, {}, options),
            requestAttention: (critical = false, options) => invoke(desktopCommands.window.requestAttention, { value: critical }, options),
            setProgress: (state, value, options) => invoke(desktopCommands.window.setProgress, { state, value }, options),
            setBadge: (value, options) => invoke(desktopCommands.window.setBadge, { value }, options),
            setDocumentEdited: (value, options) => invoke(desktopCommands.window.setDocumentEdited, { value }, options),
            setContentProtection: (value, options) => invoke(desktopCommands.window.setContentProtection, { value }, options),
            setTitleBarTheme: (theme, options) => invoke(desktopCommands.window.setTitleBarTheme, { theme }, options),
        }),
        application: Object.freeze({
            requestQuit: (options) => invoke(desktopCommands.application.requestQuit, {}, options),
        }),
    });
}
//# sourceMappingURL=desktop.js.map