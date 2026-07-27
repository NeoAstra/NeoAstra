import React from "react";
import { tour } from "./generated/neoastra";
import { FeatureCard, ResultPanel } from "./FeatureCard";
import { describeError } from "./tour-api";

interface RpcTourProps {
  readonly report: (source: string, message: string) => void;
}

export function RpcTour({ report }: RpcTourProps) {
  const [name, setName] = React.useState("desktop developer");
  const [greeting, setGreeting] = React.useState("Call C# to receive a typed response.");
  const [delayStatus, setDelayStatus] = React.useState("No operation is running.");
  const [streamMessages, setStreamMessages] = React.useState<string[]>([]);
  const delayController = React.useRef<AbortController | undefined>(undefined);

  async function greet() {
    try {
      const response = await tour.hello({ name });
      setGreeting(`${response.message}\nView: ${response.viewLabel}`);
      report("typed-rpc", "Received a generated typed C# response.");
    } catch (error) {
      setGreeting(describeError(error));
    }
  }

  async function startCancelableCall() {
    const controller = new AbortController();
    delayController.current = controller;
    setDelayStatus("C# is waiting for five seconds. You can cancel it.");
    try {
      const response = await tour.delay(
        { milliseconds: 5_000 },
        { signal: controller.signal },
      );
      setDelayStatus(response.message);
      report("rpc-cancellation", `Completed after ${response.milliseconds} ms.`);
    } catch (error) {
      setDelayStatus(describeError(error));
      report("rpc-cancellation", "The renderer canceled the managed operation.");
    } finally {
      if (delayController.current === controller) delayController.current = undefined;
    }
  }

  async function startStream() {
    setStreamMessages([]);
    try {
      const stream = await tour.stream({ count: 6 });
      for await (const item of stream) {
        setStreamMessages(current => [...current, item.message]);
      }
      report("rpc-channel", "Consumed an ordered bounded channel from C#.");
    } catch (error) {
      report("rpc-channel", describeError(error));
    }
  }

  return (
    <FeatureCard
      eyebrow="Generated contract"
      title="Typed RPC, cancellation, and channels"
      description={
        "The source generator creates the C# dispatcher, JSON metadata, and this TypeScript API. " +
        "No handwritten WebView bridge code is used."
      }
    >
      <label className="field">
        Name sent to C#
        <input value={name} onChange={event => setName(event.target.value)} />
      </label>
      <div className="button-row">
        <button type="button" onClick={() => void greet()}>Call typed RPC</button>
        <button type="button" onClick={() => void startCancelableCall()}>
          Start cancelable call
        </button>
        <button
          type="button"
          className="secondary"
          onClick={() => delayController.current?.abort()}
        >
          Cancel call
        </button>
        <button type="button" onClick={() => void startStream()}>Stream channel</button>
      </div>
      <ResultPanel label="Typed response">{greeting}</ResultPanel>
      <ResultPanel label="Cancellation">{delayStatus}</ResultPanel>
      <ResultPanel label="Ordered channel">
        {streamMessages.length === 0
          ? "No channel items yet."
          : streamMessages.join("\n")}
      </ResultPanel>
    </FeatureCard>
  );
}
