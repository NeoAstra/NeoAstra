import React from "react";
import { tour } from "#neoastra";
import { FeatureCard, ResultPanel } from "./FeatureCard";
import { describe, describeError, desktop, lifecycleTrayMenu } from "./tour-api";

const lifecycleTrayId = "lifecycle-tour";

interface LifecycleTourProps {
  readonly report: (source: string, message: string) => void;
}

export function LifecycleTour({ report }: LifecycleTourProps) {
  const [dirty, setDirty] = React.useState(false);
  const [closeToTray, setCloseToTray] = React.useState(false);
  const [confirmBeforeQuit, setConfirmBeforeQuit] = React.useState(true);
  const [status, setStatus] = React.useState(
    "Enable the lifecycle tray to keep this document session alive while its window is hidden.",
  );
  const dirtyRef = React.useRef(dirty);
  const closeToTrayRef = React.useRef(closeToTray);
  const confirmBeforeQuitRef = React.useRef(confirmBeforeQuit);

  React.useEffect(() => { dirtyRef.current = dirty; }, [dirty]);
  React.useEffect(() => { closeToTrayRef.current = closeToTray; }, [closeToTray]);
  React.useEffect(() => { confirmBeforeQuitRef.current = confirmBeforeQuit; }, [confirmBeforeQuit]);

  React.useEffect(() => {
    let disposed = false;
    let unsubscribeClose: (() => Promise<void>) | undefined;
    let unsubscribeTray: (() => Promise<void>) | undefined;

    async function subscribe() {
      unsubscribeTray = await desktop.tray.onActivated(async activation => {
        if (activation.id !== lifecycleTrayId || activation.secondary) return;
        await desktop.window.show();
        await desktop.window.focus();
        const message = "Primary tray activation restored and focused the existing browser session.";
        setStatus(message);
        report("window-lifecycle", message);
      });
      unsubscribeClose = await desktop.window.onCloseRequested(async event => {
        if (event.reason === "User" && closeToTrayRef.current) {
          event.preventDefault();
          await desktop.window.hide();
          report("window-lifecycle", "Canceled the native close and hid the browser window in the tray.");
          return;
        }
        if (!event.canCancel || !confirmBeforeQuitRef.current && !dirtyRef.current) return;

        await desktop.window.show();
        await desktop.window.focus();
        const detail = dirtyRef.current
          ? "The feature tour also contains simulated unsaved work."
          : "Cancel keeps the application and browser session running.";
        const answer = await desktop.dialogs.message({
          title: "Quit NeoAstra?",
          message: "Do you want to close the application?",
          detail,
          icon: "Question",
          buttons: ["Accept", "Cancel"],
        });
        if (answer.status !== "Success" || answer.value !== "Accept") {
          event.preventDefault();
          const message = "Application close was canceled by the renderer.";
          setStatus(message);
          report("close-negotiation", message);
          return;
        }
        if (dirtyRef.current) {
          const result = await tour.setDirty({ value: false });
          dirtyRef.current = result.hasUnsavedChanges;
          setDirty(result.hasUnsavedChanges);
        }
      });
      if (disposed) {
        await unsubscribeClose();
        await unsubscribeTray();
      }
    }

    void subscribe().catch(error => setStatus(describeError(error)));
    return () => {
      disposed = true;
      if (closeToTrayRef.current) {
        closeToTrayRef.current = false;
        void desktop.tray.remove(lifecycleTrayId);
      }
      if (unsubscribeClose !== undefined) void unsubscribeClose();
      if (unsubscribeTray !== undefined) void unsubscribeTray();
    };
  }, [report]);

  async function updateDirty(value: boolean) {
    try {
      const result = await tour.setDirty({ value });
      dirtyRef.current = result.hasUnsavedChanges;
      setDirty(result.hasUnsavedChanges);
      const message = result.hasUnsavedChanges
        ? "The next real application quit also reports simulated unsaved work."
        : "The backend now considers the tour saved.";
      setStatus(message);
      report("close-negotiation", message);
    } catch (error) {
      setStatus(describeError(error));
    }
  }

  async function updateCloseToTray(value: boolean) {
    try {
      if (!value) {
        closeToTrayRef.current = false;
        setCloseToTray(false);
        const result = await desktop.tray.remove(lifecycleTrayId);
        setStatus(`Close-to-tray disabled: ${describe(result)}`);
        return;
      }
      const result = await desktop.tray.create({
        id: lifecycleTrayId,
        toolTip: "NeoAstra lifecycle tour — click to restore",
        isTemplateImage: false,
        menu: lifecycleTrayMenu,
      });
      const enabled = result.status === "Success";
      closeToTrayRef.current = enabled;
      setCloseToTray(enabled);
      setStatus(enabled
        ? "Close-to-tray is active. Close the native window, then left-click the tray item to restore it."
        : `The platform could not create a recovery tray: ${describe(result)}`);
    } catch (error) {
      closeToTrayRef.current = false;
      setCloseToTray(false);
      setStatus(describeError(error));
    }
  }

  async function requestQuit() {
    try {
      const result = await desktop.application.requestQuit();
      const message = result.status === "Canceled"
        ? "The negotiated application quit was canceled."
        : `Application quit result: ${describe(result)}`;
      setStatus(message);
      report("application-quit", message);
    } catch (error) {
      setStatus(describeError(error));
    }
  }

  async function inspectWindow() {
    try {
      const result = await desktop.window.state();
      const message = describe(result.value);
      setStatus(message);
      report("window-state", message.replaceAll("\n", " "));
    } catch (error) {
      setStatus(describeError(error));
    }
  }

  async function showPreview() {
    try {
      await tour.showPreview({});
      setStatus("Opened a second view with an explicit restricted desktop policy.");
    } catch (error) {
      setStatus(describeError(error));
    }
  }

  return (
    <FeatureCard
      eyebrow="Application lifecycle"
      title="Close to tray, negotiated quit, and view identity"
      description={
        "The renderer uses registered window and application APIs to distinguish hiding " +
        "the browser from quitting the process, while close requests remain asynchronous and bounded."
      }
    >
      <label className="toggle-row">
        <input
          type="checkbox"
          checked={closeToTray}
          onChange={event => void updateCloseToTray(event.target.checked)}
        />
        Close the main window to a recovery tray
      </label>
      <label className="toggle-row">
        <input
          type="checkbox"
          checked={confirmBeforeQuit}
          onChange={event => setConfirmBeforeQuit(event.target.checked)}
        />
        Confirm before application quit
      </label>
      <label className="toggle-row">
        <input
          type="checkbox"
          checked={dirty}
          onChange={event => void updateDirty(event.target.checked)}
        />
        Simulate unsaved work
      </label>
      <div className="button-row">
        <button type="button" onClick={() => void requestQuit()}>
          Request negotiated quit
        </button>
        <button type="button" className="secondary" onClick={() => void inspectWindow()}>
          Read current window state
        </button>
        <button type="button" onClick={() => void showPreview()}>
          Open restricted preview view
        </button>
        <button type="button" className="secondary" onClick={() => location.reload()}>
          Reload document session
        </button>
      </div>
      <ResultPanel label="Lifecycle state">{status}</ResultPanel>
      <div className="instruction">
        <strong>Try close-to-tray</strong>
        <span>Enable the recovery tray, close this native window, then left-click the tray item.</span>
        <span>Use its Quit command or “Request negotiated quit” to exercise renderer confirmation.</span>
      </div>
      <div className="instruction">
        <strong>Try single-instance routing</strong>
        <code>dotnet run --project samples/NeoAstra.Sample.Advanced -- second-launch</code>
        <span>The existing window activates and receives a typed lifecycle event.</span>
      </div>
    </FeatureCard>
  );
}
