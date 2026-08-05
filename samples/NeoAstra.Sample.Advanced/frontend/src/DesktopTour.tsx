import React from "react";
import type {
  DesktopSupportInfo,
  DesktopWindowExtraSupport,
  NeoAstraRuntimeInfo,
} from "@neoastra/client";
import { tour } from "#neoastra";
import { FeatureCard, ResultPanel } from "./FeatureCard";
import {
  decodeText,
  describe,
  describeError,
  desktop,
  encodeText,
  fileDialog,
  folderDialog,
  menuItems,
  saveDialog,
  tourNotification,
} from "./tour-api";

interface DesktopTourProps {
  readonly platform: NeoAstraRuntimeInfo["platform"];
  readonly report: (source: string, message: string) => void;
}

type ResultGroup = "dialogs" | "shell" | "system" | "storage" | "window";

export function isWindowExtraAvailable(support: DesktopSupportInfo | undefined) {
  return support !== undefined && support.supportLevel !== "None";
}

export function isNativeMenuVisibleByDefault(platform: NeoAstraRuntimeInfo["platform"]) {
  return platform === "macos";
}

export function DesktopTour({ platform, report }: DesktopTourProps) {
  const [results, setResults] = React.useState<Record<ResultGroup, string>>({
    dialogs: "Choose an action to invoke a native dialog.",
    shell: "Registered native surfaces report platform support explicitly.",
    system: "Query immutable OS and application snapshots.",
    storage: "Secrets are never displayed or logged by this tour.",
    window: "Window extras report unsupported features instead of pretending success.",
  });
  const [clipboardText, setClipboardText] = React.useState("Hello from NeoAstra clipboard");
  const [secret, setSecret] = React.useState("small sample secret");
  const [contentProtected, setContentProtected] = React.useState(false);
  const [extraSupport, setExtraSupport] = React.useState<DesktopWindowExtraSupport>();
  const [nativeMenuVisible, setNativeMenuVisible] = React.useState(
    isNativeMenuVisibleByDefault(platform),
  );
  const [nativeMenuPending, setNativeMenuPending] = React.useState(false);

  React.useEffect(() => {
    const unsubscribers: Array<() => Promise<void>> = [];
    let disposed = false;

    async function subscribeToDesktopEvents() {
      const subscriptions = await Promise.all([
        desktop.tray.onActivated(value =>
          report("tray-event", `Tray item activated: ${value.id}`)),
        desktop.notifications.onActivated(value =>
          report("notification-event", `Notification activated: ${value.id}`)),
        desktop.shortcuts.onActivated(value =>
          report("shortcut-event", `Global shortcut activated: ${value.id}`)),
        desktop.system.onThemeChanged(value =>
          report("theme-event", describe(value))),
        desktop.system.onDisplaysChanged(value =>
          report("display-event", describe(value))),
        desktop.dragDrop.onInbound(value => {
          const message = describe(value);
          setResults(current => ({ ...current, storage: message }));
          report("drop-event", message);
        }),
      ]);
      if (disposed) {
        await Promise.all(subscriptions.map(unsubscribe => unsubscribe()));
      } else {
        unsubscribers.push(...subscriptions);
      }
    }

    void subscribeToDesktopEvents().catch(error =>
      report("desktop-events", describeError(error)));
    return () => {
      disposed = true;
      void Promise.all(unsubscribers.map(unsubscribe => unsubscribe()));
    };
  }, [report]);

  React.useEffect(() => {
    let disposed = false;
    void tour.nativeMenuState({}).then(value => {
      if (!disposed) setNativeMenuVisible(value.visible);
    }).catch(error => {
      if (!disposed) report("native-menu-state", describeError(error));
    });
    return () => { disposed = true; };
  }, [report]);

  React.useEffect(() => {
    let disposed = false;
    void desktop.window.extraSupport().then(value => {
      if (!disposed) setExtraSupport(value);
    }).catch(error => {
      if (disposed) return;
      const message = describeError(error);
      setResults(current => ({ ...current, window: message }));
      report("window-extra-support", message);
    });
    return () => { disposed = true; };
  }, [report]);

  async function run(
    group: ResultGroup,
    source: string,
    action: () => Promise<unknown>,
  ) {
    try {
      const value = await action();
      const message = describe(value);
      setResults(current => ({ ...current, [group]: message }));
      report(source, message.replaceAll("\n", " "));
      return value;
    } catch (error) {
      const message = describeError(error);
      setResults(current => ({ ...current, [group]: message }));
      report(source, message);
      return undefined;
    }
  }

  async function readClipboard() {
    const result = await desktop.clipboard.read("text");
    if (result.base64 !== undefined) setClipboardText(decodeText(result.base64));
    return result;
  }

  async function showContextMenu(event: React.MouseEvent) {
    event.preventDefault();
    await run("shell", "context-menu", async () => {
      const configured = await desktop.menus.set("main", menuItems);
      if (configured.status !== "Success" && configured.status !== "Conflict") {
        return configured;
      }
      return desktop.menus.popup("main", event.clientX, event.clientY);
    });
  }

  async function updateNativeMenu(visible: boolean) {
    setNativeMenuPending(true);
    try {
      const value = await tour.setNativeMenuVisible({ visible });
      setNativeMenuVisible(value.visible);
      const message = `Native application menu ${value.visible ? "shown" : "hidden"}.`;
      setResults(current => ({ ...current, shell: message }));
      report("native-menu", message);
    } catch (error) {
      const message = describeError(error);
      setResults(current => ({ ...current, shell: message }));
      report("native-menu", message);
    } finally {
      setNativeMenuPending(false);
    }
  }

  async function beginOutboundDrag(event: React.DragEvent) {
    event.dataTransfer.setData("text/plain", "NeoAstra renderer drag payload");
    await run("shell", "outbound-drag", () =>
      desktop.dragDrop.outbound("main", [
        {
          kind: "Text",
          value: "NeoAstra renderer drag payload",
        },
      ]));
  }

  function supports(feature: keyof DesktopWindowExtraSupport) {
    return isWindowExtraAvailable(extraSupport?.[feature]);
  }

  function extraLabel(label: string, feature: keyof DesktopWindowExtraSupport) {
    return extraSupport?.[feature].supportLevel === "None" ? `${label} (unsupported)` : label;
  }

  const unavailableExtras = extraSupport === undefined
    ? []
    : [
        ["attention", "attention"],
        ["progress", "taskbar progress"],
        ["badge", "badges"],
        ["titleBarTheme", "title-bar themes"],
        ["contentProtection", "content protection"],
      ].filter(([feature]) => !supports(feature as keyof DesktopWindowExtraSupport)).map(([, label]) => label);

  return (
    <>
      <FeatureCard
        eyebrow="Desktop essentials"
        title="Dialogs, menus, tray, and notifications"
        description={
          "Each call crosses typed RPC, bounded host validation, application policy, and a " +
          "platform adapter before displaying native UI."
        }
      >
        <div className="button-row">
          <button type="button" onClick={() => void run("dialogs", "message-dialog", () =>
            desktop.dialogs.message({
              title: "NeoAstra",
              message: "This is a native message dialog.",
              detail: "It has an explicit owner and portable button roles.",
              icon: "Information",
              buttons: ["Accept"],
            }))}
          >Message dialog</button>
          <button type="button" onClick={() => void run("dialogs", "open-file", () =>
            desktop.dialogs.openFile(fileDialog))}
          >Open files</button>
          <button type="button" onClick={() => void run("dialogs", "open-folder", () =>
            desktop.dialogs.openFolder(folderDialog))}
          >Open folder</button>
          <button type="button" onClick={() => void run("dialogs", "save-file", () =>
            desktop.dialogs.saveFile(saveDialog))}
          >Save dialog</button>
        </div>
        <div className="button-row">
          <button type="button" onClick={() => void run("shell", "tray-create", () =>
            desktop.tray.create({
              id: "feature-tour",
              toolTip: "NeoAstra feature tour",
              isTemplateImage: false,
              menu: menuItems,
            }))}
          >Create tray item</button>
          <button type="button" className="secondary" onClick={() => void run(
            "shell",
            "tray-remove",
            () => desktop.tray.remove("feature-tour"),
          )}>Remove tray item</button>
          <button type="button" onClick={() => void run("shell", "notification-status", () =>
            desktop.notifications.status())}
          >Notification status</button>
          <button type="button" onClick={() => void run("shell", "notification-show", () =>
            desktop.notifications.show(tourNotification))}
          >Show notification</button>
          <button type="button" className="secondary" onClick={() => void run(
            "shell",
            "notification-remove",
            () => desktop.notifications.remove("feature-tour"),
          )}>Remove notification</button>
        </div>
        <label className="toggle-row">
          <input
            type="checkbox"
            checked={nativeMenuVisible}
            disabled={nativeMenuPending}
            onChange={event => void updateNativeMenu(event.target.checked)}
          />
          {platform === "macos"
            ? "Show native application menu (shown by default on macOS)"
            : "Show native application menu (hidden by default on Windows and Linux)"}
        </label>
        <button type="button" className="context-target" onContextMenu={showContextMenu}>
          Right-click for a renderer-owned native context menu
        </button>
        <ResultPanel label="Dialogs">{results.dialogs}</ResultPanel>
        <ResultPanel label="Native surface result">{results.shell}</ResultPanel>
      </FeatureCard>

      <FeatureCard
        eyebrow="OS integration"
        title="Clipboard, shortcuts, system information, and opener"
        description={
          "Sensitive operations use narrow typed APIs and bounded inputs. Restricted views can " +
          "layer explicit permissions and scopes; unsupported behavior remains visible."
        }
      >
        <label className="field">
          Clipboard text
          <input
            value={clipboardText}
            onChange={event => setClipboardText(event.target.value)}
          />
        </label>
        <div className="button-row">
          <button type="button" onClick={() => void run("system", "clipboard-write", () =>
            desktop.clipboard.write("text", encodeText(clipboardText)))}
          >Write clipboard</button>
          <button type="button" onClick={() => void run("system", "clipboard-read", readClipboard)}>
            Read clipboard
          </button>
          <button type="button" className="secondary" onClick={() => void run(
            "system",
            "clipboard-clear",
            () => desktop.clipboard.clear(),
          )}>Clear clipboard</button>
          <button type="button" onClick={() => void run("system", "shortcut-register", () =>
            desktop.shortcuts.register("feature-tour", "Ctrl+Shift+R"))}
          >Register Ctrl+Shift+R</button>
          <button type="button" className="secondary" onClick={() => void run(
            "system",
            "shortcut-unregister",
            () => desktop.shortcuts.unregister("feature-tour"),
          )}>Unregister shortcut</button>
        </div>
        <div className="button-row">
          <button type="button" onClick={() => void run("system", "theme", () =>
            desktop.system.theme())}
          >Theme</button>
          <button type="button" onClick={() => void run("system", "displays", () =>
            desktop.system.displays())}
          >Displays</button>
          <button type="button" onClick={() => void run("system", "metadata", () =>
            desktop.system.metadata())}
          >Application metadata</button>
          <button type="button" onClick={() => void run("system", "external-opener", () =>
            desktop.opener.url("https://github.com/NeoAstra/NeoAstra"))}
          >Open scoped URL</button>
        </div>
        <ResultPanel label="System result">{results.system}</ResultPanel>
      </FeatureCard>

      <FeatureCard
        eyebrow="Owned resources"
        title="Safe storage and drag-and-drop brokering"
        description={
          "Safe storage uses the OS credential facility without a plaintext fallback. " +
          "Drop files are represented by document-session-owned tokens rather than ambient paths."
        }
      >
        <label className="field">
          Secret used for the demo
          <input
            type="password"
            value={secret}
            onChange={event => setSecret(event.target.value)}
          />
        </label>
        <div className="button-row">
          <button type="button" onClick={() => void run("storage", "secret-store", () =>
            desktop.safeStorage.store("feature-tour-secret", encodeText(secret)))}
          >Store secret</button>
          <button type="button" onClick={() => void run("storage", "secret-contains", () =>
            desktop.safeStorage.contains("feature-tour-secret"))}
          >Check secret</button>
          <button type="button" onClick={() => void run("storage", "secret-retrieve", async () => {
            const value = await desktop.safeStorage.retrieve("feature-tour-secret");
            return {
              status: value.status,
              bytes: value.base64 === undefined ? 0 : atob(value.base64).length,
              code: value.code,
            };
          })}
          >Retrieve size</button>
          <button type="button" className="secondary" onClick={() => void run(
            "storage",
            "secret-delete",
            () => desktop.safeStorage.delete("feature-tour-secret"),
          )}>Delete secret</button>
        </div>
        <div
          className="drop-zone"
          draggable
          onDragStart={event => void beginOutboundDrag(event)}
        >
          <strong>Drag this card out</strong>
          <span>or drop a file/text/URL here to observe its brokered event below.</span>
        </div>
        <ResultPanel label="Resource result">{results.storage}</ResultPanel>
      </FeatureCard>

      <FeatureCard
        eyebrow="Native window"
        title="Window extras and platform support"
        description={
          "Attention, taskbar progress, badges, title-bar theme, document state, " +
          "and content protection are enabled only when the current platform supports them."
        }
      >
        <div className="button-row">
          <button type="button" disabled={!supports("attention")} title={extraSupport?.attention.details} onClick={() => void run("window", "attention", () =>
            desktop.window.requestAttention(true))}
          >{extraLabel("Request attention", "attention")}</button>
          <button type="button" disabled={!supports("progress")} title={extraSupport?.progress.details} onClick={() => void run("window", "progress", () =>
            desktop.window.setProgress("Normal", 0.65))}
          >{extraLabel("Set progress", "progress")}</button>
          <button type="button" disabled={!supports("progress")} className="secondary" title={extraSupport?.progress.details} onClick={() => void run(
            "window",
            "progress-clear",
            () => desktop.window.setProgress("None", 0),
          )}>{extraLabel("Clear progress", "progress")}</button>
          <button type="button" disabled={!supports("badge")} title={extraSupport?.badge.details} onClick={() => void run("window", "badge", () =>
            desktop.window.setBadge("2"))}
          >{extraLabel("Set badge", "badge")}</button>
          <button type="button" disabled={!supports("badge")} className="secondary" title={extraSupport?.badge.details} onClick={() => void run(
            "window",
            "badge-clear",
            () => desktop.window.setBadge(),
          )}>{extraLabel("Clear badge", "badge")}</button>
        </div>
        <div className="button-row">
          <button type="button" disabled={!supports("titleBarTheme")} title={extraSupport?.titleBarTheme.details} onClick={() => void run("window", "theme-dark", () =>
            desktop.window.setTitleBarTheme("Dark"))}
          >{extraLabel("Dark title bar", "titleBarTheme")}</button>
          <button type="button" disabled={!supports("titleBarTheme")} title={extraSupport?.titleBarTheme.details} onClick={() => void run("window", "theme-light", () =>
            desktop.window.setTitleBarTheme("Light"))}
          >{extraLabel("Light title bar", "titleBarTheme")}</button>
          <button type="button" disabled={!supports("titleBarTheme")} className="secondary" title={extraSupport?.titleBarTheme.details} onClick={() => void run(
            "window",
            "theme-system",
            () => desktop.window.setTitleBarTheme("System"),
          )}>{extraLabel("System title bar", "titleBarTheme")}</button>
          <button type="button" disabled={!supports("contentProtection")} title={extraSupport?.contentProtection.details} onClick={() => {
            const next = !contentProtected;
            setContentProtected(next);
            void run("window", "content-protection", () =>
              desktop.window.setContentProtection(next));
          }}
          >{extraLabel(`${contentProtected ? "Disable" : "Enable"} content protection`, "contentProtection")}</button>
        </div>
        <div className="instruction">
          <strong>Platform support</strong>
          <span>{extraSupport === undefined
            ? "Loading available native window features…"
            : unavailableExtras.length === 0
              ? "All window-extra controls in this tour are available."
              : `Unavailable controls are disabled: ${unavailableExtras.join(", ")}.`}</span>
        </div>
        <ResultPanel label="Window result">{results.window}</ResultPanel>
      </FeatureCard>
    </>
  );
}
