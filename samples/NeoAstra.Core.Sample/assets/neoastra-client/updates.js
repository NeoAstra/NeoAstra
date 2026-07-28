export const updateCommands = Object.freeze({
    status: "updates.status",
    check: "updates.check",
    download: "updates.download",
    install: "updates.install-restart",
    changed: "updates.changed",
});
/**
 * Creates the bounded renderer update surface. Feed URLs, keys, channels, versions, artifact paths,
 * helper commands, and rollback targets are deliberately not accepted from renderer code.
 */
export function createUpdateClient(rpc) {
    if (rpc === null || typeof rpc !== "object" || typeof rpc.invoke !== "function" || typeof rpc.subscribe !== "function")
        throw new TypeError("An update RPC client is required.");
    return Object.freeze({
        status: (options) => rpc.invoke(updateCommands.status, {}, options),
        check: (options) => rpc.invoke(updateCommands.check, {}, options),
        download: (options) => rpc.invoke(updateCommands.download, {}, options),
        /** Requires a separately granted high-risk backend permission and trusted user confirmation. */
        installAndRestart: (options) => rpc.invoke(updateCommands.install, {}, options),
        onChanged: (handler, options) => rpc.subscribe(updateCommands.changed, handler, options),
    });
}
//# sourceMappingURL=updates.js.map