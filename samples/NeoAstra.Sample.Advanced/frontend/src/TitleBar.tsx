import React from "react";
import type { DesktopWindowSnapshot } from "@neoastra/client";
import markUrl from "./advanced-mark.svg";
import { desktop } from "./tour-api";

/**
 * Keeps the document in step with the native title bar. The client publishes the layout as
 * `--neoastra-titlebar-*` custom properties and `data-neoastra-*` attributes on the root element.
 */
export function useTitleBar(): DesktopWindowSnapshot | undefined {
  const [snapshot, setSnapshot] = React.useState<DesktopWindowSnapshot>();

  React.useEffect(() => {
    let disposed = false;
    let dispose: (() => Promise<void>) | undefined;

    void desktop.window.attachTitleBar({ onChange: setSnapshot }).then(binding => {
      if (disposed) void binding.dispose();
      else dispose = binding.dispose;
    }).catch(() => {
      // A view without window-management authority keeps its standard title bar.
    });
    return () => {
      disposed = true;
      if (dispose !== undefined) void dispose();
    };
  }, []);

  return snapshot;
}

interface TitleBarProps {
  readonly snapshot: DesktopWindowSnapshot | undefined;
  readonly title: string;
}

export function hasNativeWindowControls(snapshot: DesktopWindowSnapshot) {
  return snapshot.titleBar.leftInset > 0 || snapshot.titleBar.rightInset > 0;
}

export function TitleBar({ snapshot, title }: TitleBarProps) {
  if (snapshot === undefined || snapshot.titleBar.style === "Default") return null;

  const fullscreen = snapshot.state === "Fullscreen";
  const maximized = snapshot.state === "Maximized";
  const expanded = fullscreen || maximized;

  function toggleExpanded() {
    if (fullscreen) void desktop.window.setFullscreen(false);
    else if (maximized) void desktop.window.restore();
    else void desktop.window.maximize();
  }

  return (
    <header className="titlebar" data-neoastra-drag-region>
      <img className="titlebar-mark" src={markUrl} alt="" />
      <span className="titlebar-title">{title}</span>
      <button
        type="button"
        className="titlebar-action"
        onClick={() => void desktop.window.setFullscreen(!fullscreen)}
      >{fullscreen ? "Exit fullscreen" : "Fullscreen"}</button>
      {/* Native controls are overlaid by the platform; the page draws its own only when none are shown. */}
      {hasNativeWindowControls(snapshot) ? null : (
        <div className="titlebar-controls">
          <button type="button" aria-label="Minimize" onClick={() => void desktop.window.minimize()}>
            <svg viewBox="0 0 10 10" aria-hidden="true"><path d="M0 5h10" /></svg>
          </button>
          <button
            type="button"
            aria-label={expanded ? "Restore" : "Maximize"}
            disabled={!snapshot.resizable && !expanded}
            onClick={toggleExpanded}
          >
            <svg viewBox="0 0 10 10" aria-hidden="true">
              {expanded
                ? <path d="M2.5 2.5v-2h7v7h-2M.5 2.5h7v7h-7z" />
                : <path d="M.5.5h9v9h-9z" />}
            </svg>
          </button>
          <button type="button" className="close" aria-label="Close" onClick={() => void desktop.window.close()}>
            <svg viewBox="0 0 10 10" aria-hidden="true"><path d="M0 0l10 10M10 0L0 10" /></svg>
          </button>
        </div>
      )}
    </header>
  );
}
