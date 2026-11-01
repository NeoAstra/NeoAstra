/** What a keyboard that lies over the page covers, as the native backend reports it. */
export interface NeoKeyboardState {
  /** The height of the view that the keyboard covers, in CSS pixels; 0 once it is hidden. */
  readonly height: number;
  /** The duration of the animation of the keyboard, in seconds. */
  readonly duration: number;
}

/**
 * Calls `handler` each time the keyboard of iOS or Android comes up, changes, or goes, while the application keeps it over the page
 * (`NeoAstraIOSKeyboardOverlay`, `NeoAstraAndroidKeyboardOverlay`); the page then keeps its fields clear of it itself.
 * Returns a function that stops the calls.
 */
export function onKeyboard(handler: (state: NeoKeyboardState) => void, target: EventTarget = globalThis): () => void {
  const listener = (event: Event) => {
    const detail = (event as CustomEvent<unknown>).detail as { height?: unknown; duration?: unknown } | null | undefined;
    if (typeof detail?.height !== "number" || typeof detail.duration !== "number" || !(detail.height >= 0)) return;
    handler({ height: detail.height, duration: detail.duration });
  };
  target.addEventListener("neoastrakeyboard", listener);
  return () => target.removeEventListener("neoastrakeyboard", listener);
}

/** Reads the height that the keyboard covers now from the `--neoastra-keyboard-height` variable of the root element, 0 without one. */
export function keyboardHeight(root: { readonly style: { getPropertyValue(name: string): string } } | undefined = globalThis.document?.documentElement): number {
  const value = Number.parseFloat(root?.style.getPropertyValue("--neoastra-keyboard-height") ?? "");
  return Number.isFinite(value) && value > 0 ? value : 0;
}
