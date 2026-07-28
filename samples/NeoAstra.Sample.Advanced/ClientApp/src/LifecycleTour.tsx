import React from "react";
import { tour } from "./generated/neoastra";
import { FeatureCard, ResultPanel } from "./FeatureCard";
import { describeError } from "./tour-api";

interface LifecycleTourProps {
  readonly report: (source: string, message: string) => void;
}

export function LifecycleTour({ report }: LifecycleTourProps) {
  const [dirty, setDirty] = React.useState(false);
  const [status, setStatus] = React.useState(
    "The main window is single-instance and persists its placement.",
  );

  async function updateDirty(value: boolean) {
    try {
      const result = await tour.setDirty({ value });
      setDirty(result.hasUnsavedChanges);
      const message = result.hasUnsavedChanges
        ? "Close the window to exercise asynchronous close cancellation."
        : "The backend now considers the tour saved.";
      setStatus(message);
      report("close-negotiation", message);
    } catch (error) {
      setStatus(describeError(error));
    }
  }

  async function showPreview() {
    try {
      await tour.showPreview({});
      setStatus("Opened a second view with a deliberately smaller capability grant.");
    } catch (error) {
      setStatus(describeError(error));
    }
  }

  return (
    <FeatureCard
      eyebrow="Application lifecycle"
      title="Close negotiation, single instance, and view identity"
      description={
        "The backend owns stable window/view labels, securely routes second launches, " +
        "persists placement, and asks the renderer before discarding unsaved work."
      }
    >
      <label className="toggle-row">
        <input
          type="checkbox"
          checked={dirty}
          onChange={event => void updateDirty(event.target.checked)}
        />
        Simulate unsaved work
      </label>
      <div className="button-row">
        <button type="button" onClick={() => void showPreview()}>
          Open restricted preview view
        </button>
        <button type="button" className="secondary" onClick={() => location.reload()}>
          Reload document session
        </button>
      </div>
      <ResultPanel label="Lifecycle state">{status}</ResultPanel>
      <div className="instruction">
        <strong>Try single-instance routing</strong>
        <code>dotnet run --project samples/NeoAstra.Sample.Advanced -- second-launch</code>
        <span>The existing window activates and receives a typed lifecycle event.</span>
      </div>
    </FeatureCard>
  );
}
