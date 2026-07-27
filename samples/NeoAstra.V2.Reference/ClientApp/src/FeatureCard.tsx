import type { ReactNode } from "react";

interface FeatureCardProps {
  readonly title: string;
  readonly description: string;
  readonly eyebrow?: string;
  readonly children: ReactNode;
}

export function FeatureCard({
  title,
  description,
  eyebrow = "NeoAstra v2",
  children,
}: FeatureCardProps) {
  return (
    <article className="feature-card">
      <header>
        <span className="eyebrow">{eyebrow}</span>
        <h2>{title}</h2>
        <p>{description}</p>
      </header>
      <div className="feature-content">{children}</div>
    </article>
  );
}

interface ResultPanelProps {
  readonly children: ReactNode;
  readonly label?: string;
}

export function ResultPanel({ children, label = "Result" }: ResultPanelProps) {
  return (
    <div className="result-panel">
      <strong>{label}</strong>
      <pre aria-live="polite">{children}</pre>
    </div>
  );
}
