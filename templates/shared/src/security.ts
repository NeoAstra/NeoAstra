export function openExternal(url: string): void {
  const target = new URL(url);
  if (target.protocol !== "https:") throw new Error("Only reviewed HTTPS links may open externally.");
  // Route through an explicitly permissioned generated backend command in a real app.
  throw new Error(`No external opener grant is configured for ${target.origin}.`);
}

export function installNavigationGuard(): void {
  document.addEventListener("click", event => {
    const anchor = (event.target as Element | null)?.closest("a[href]") as HTMLAnchorElement | null;
    if (anchor !== null && new URL(anchor.href).origin !== location.origin) event.preventDefault();
  });
}
