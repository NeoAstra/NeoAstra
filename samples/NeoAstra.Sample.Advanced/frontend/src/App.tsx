import React from "react";
import {
  connect,
  onDiagnostic,
  type NeoAstraRuntimeInfo,
} from "@neoastra/client";
import markUrl from "./advanced-mark.svg";
import { DesktopTour } from "./DesktopTour";
import { FeatureCard, ResultPanel } from "./FeatureCard";
import { tour, type TourActivity } from "#neoastra";
import { LifecycleTour } from "./LifecycleTour";
import { RpcTour } from "./RpcTour";
import { describe, describeError, desktop } from "./tour-api";

interface ActivityEntry {
  readonly source: string;
  readonly message: string;
  readonly timestamp: string;
}

export function App() {
  const [runtime, setRuntime] = React.useState<NeoAstraRuntimeInfo>();
  const [connectionError, setConnectionError] = React.useState<string>();
  const [details, setDetails] = React.useState("Loading the dynamic Vite chunk.");
  const [workerMessage, setWorkerMessage] = React.useState("Waiting for the module worker.");
  const [activities, setActivities] = React.useState<ActivityEntry[]>([]);

  const report = React.useCallback((source: string, message: string) => {
    setActivities(current => [
      {
        source,
        message,
        timestamp: new Date().toLocaleTimeString(),
      },
      ...current,
    ].slice(0, 30));
  }, []);

  React.useEffect(() => {
    const worker = new Worker(
      new URL("./advanced.worker.ts", import.meta.url),
      { type: "module" },
    );
    worker.onmessage = (event: MessageEvent<string>) => setWorkerMessage(event.data);
    worker.postMessage("ready");
    void import("./details").then(module => setDetails(module.advancedDetails()));
    return () => worker.terminate();
  }, []);

  React.useEffect(() => onDiagnostic(diagnostic =>
    report(`transport:${diagnostic.level}`, `${diagnostic.code}: ${diagnostic.message}`)),
  [report]);

  React.useEffect(() => {
    let unsubscribe: (() => Promise<void>) | undefined;
    let disposed = false;

    async function start() {
      const connection = await connect();
      if (disposed) return;
      setRuntime(connection.runtimeInfo);
      const stop = await tour.onActivity((activity: TourActivity) =>
        report(activity.source, activity.message));
      if (disposed) await stop();
      else unsubscribe = stop;
    }

    void start().catch(error => setConnectionError(describeError(error)));
    return () => {
      disposed = true;
      if (unsubscribe !== undefined) void unsubscribe();
    };
  }, [report]);

  if (connectionError !== undefined) {
    return (
      <main className="connection-failure" tabIndex={-1}>
        <img className="mark" src={markUrl} alt="" />
        <h1>NeoAstra connection failed</h1>
        <p>{connectionError}</p>
      </main>
    );
  }

  if (runtime === undefined) {
    return (
      <main className="connection-failure" tabIndex={-1}>
        <img className="mark pulse" src={markUrl} alt="" />
        <h1>Connecting to NeoAstra</h1>
        <p>The authenticated renderer transport is negotiating a document session.</p>
      </main>
    );
  }

  if (runtime.viewLabel === "preview") {
    return <RestrictedPreview runtime={runtime} report={report} activities={activities} />;
  }

  return (
    <main tabIndex={-1}>
      <header className="hero">
        <div>
          <span className="eyebrow">Executable specification</span>
          <h1>NeoAstra Feature tour</h1>
          <p>
            A React application running in the operating system WebView, backed by
            generated C# RPC and explicitly registered native desktop services.
          </p>
          <div className="badge-row" aria-label="Active platform features">
            <span>React + Vite</span>
            <span>app:// assets</span>
            <span>{runtime.backend}</span>
            <span>View: {runtime.viewLabel}</span>
          </div>
        </div>
        <img className="hero-mark" src={markUrl} alt="" />
      </header>

      <section className="foundation-grid" aria-label="Runtime foundations">
        <FeatureCard
          eyebrow="Portable transport"
          title="Authenticated document session"
          description={
            "The frontend client hides backend-specific WebView globals and exposes " +
            "negotiated runtime metadata."
          }
        >
          <dl className="runtime-grid">
            <div><dt>Protocol</dt><dd>{runtime.protocolMajor}.{runtime.protocolMinor}</dd></div>
            <div><dt>Platform</dt><dd>{runtime.platform}</dd></div>
            <div><dt>Backend</dt><dd>{runtime.backend}</dd></div>
            <div><dt>Whole-view trust</dt><dd>{String(runtime.wholeViewTrust)}</dd></div>
          </dl>
          <ResultPanel label="Static frontend checks">
            {`${details}\n${workerMessage}`}
          </ResultPanel>
        </FeatureCard>

        <FeatureCard
          eyebrow="Release architecture"
          title="Secure assets, NativeAOT, and delivery"
          description={
            "Production uses a manifest-backed custom scheme and restrictive CSP. " +
            "The same project publishes under NativeAOT and feeds the inspectable bundle pipeline."
          }
        >
          <div className="instruction compact">
            <code>dotnet publish -c Release -r win-x64</code>
            <code>dotnet neoastra bundle --config neoastra.json</code>
            <span>Signing and updates remain explicit backend-owned release operations.</span>
          </div>
        </FeatureCard>
      </section>

      <section className="tour-grid" aria-label="NeoAstra feature demonstrations">
        <RpcTour report={report} />
        <LifecycleTour report={report} />
        <DesktopTour platform={runtime.platform} report={report} />
      </section>

      <ActivityLog activities={activities} />
    </main>
  );
}

interface RestrictedPreviewProps {
  readonly runtime: NeoAstraRuntimeInfo;
  readonly report: (source: string, message: string) => void;
  readonly activities: readonly ActivityEntry[];
}

function RestrictedPreview({ runtime, report, activities }: RestrictedPreviewProps) {
  const [result, setResult] = React.useState(
    "Application RPC is trusted by default; this view limits only selected desktop operations.",
  );

  async function callAllowedRpc() {
    try {
      const response = await tour.hello({ name: "restricted preview" });
      setResult(response.message);
      report("preview", "The trusted application RPC succeeded without a permission grant.");
    } catch (error) {
      setResult(describeError(error));
    }
  }

  async function callAllowedDesktop() {
    try {
      const value = await desktop.system.theme();
      setResult(describe(value));
      report("preview-security", "The explicitly allowed theme query succeeded.");
    } catch (error) {
      setResult(describeError(error));
    }
  }

  async function proveDesktopDenial() {
    try {
      const value = await desktop.system.metadata();
      setResult(describe(value));
    } catch (error) {
      setResult(`Expected denial: ${describeError(error)}`);
      report("preview-security", "A desktop command was denied before dispatch.");
    }
  }

  return (
    <main className="preview-page" tabIndex={-1}>
      <header className="hero compact-hero">
        <div>
          <span className="eyebrow">Capability isolation</span>
          <h1>Restricted preview</h1>
          <p>
            The same React bundle is running in view <strong>{runtime.viewLabel}</strong>,
            but one small capability record intentionally limits its desktop authority.
          </p>
        </div>
        <img className="hero-mark" src={markUrl} alt="" />
      </header>
      <FeatureCard
        title="Restrictions are opt-in"
        description={
          "Application RPC stays trusted without permission declarations. This view explicitly " +
          "allows the theme query while another desktop command remains denied."
        }
      >
        <div className="button-row">
          <button type="button" onClick={() => void callAllowedRpc()}>Allowed typed RPC</button>
          <button type="button" onClick={() => void callAllowedDesktop()}>Allowed theme query</button>
          <button type="button" className="danger" onClick={() => void proveDesktopDenial()}>
            Prove desktop denial
          </button>
        </div>
        <ResultPanel>{result}</ResultPanel>
      </FeatureCard>
      <ActivityLog activities={activities} />
    </main>
  );
}

function ActivityLog({ activities }: { readonly activities: readonly ActivityEntry[] }) {
  return (
    <aside className="activity-log" aria-live="polite">
      <header>
        <div>
          <span className="eyebrow">Events and diagnostics</span>
          <h2>Live activity</h2>
        </div>
        <span>{activities.length} entries</span>
      </header>
      {activities.length === 0
        ? <p>Waiting for an application pulse or native event.</p>
        : (
          <ol>
            {activities.map((activity, index) => (
              <li key={`${activity.timestamp}-${index}`}>
                <time>{activity.timestamp}</time>
                <strong>{activity.source}</strong>
                <span>{activity.message}</span>
              </li>
            ))}
          </ol>
        )}
    </aside>
  );
}
