import {
  createDesktopClient,
  invoke,
  subscribe,
  type DesktopFileDialogRequest,
  type DesktopMenuItem,
  type DesktopNotificationRequest,
  type DesktopRpc,
  type NeoRpcCallOptions,
} from "@neoastra/client";
import { neoRpcContractHash } from "#neoastra";

export function withAdvancedContract(rpc: DesktopRpc): DesktopRpc {
  return {
    invoke<TRequest, TResult>(
      command: string,
      args: TRequest,
      options?: NeoRpcCallOptions,
    ): Promise<TResult> {
      return rpc.invoke<TRequest, TResult>(command, args, {
        ...options,
        contractHash: neoRpcContractHash,
      });
    },
    subscribe<T>(
      event: string,
      handler: (value: T) => void,
      options?: NeoRpcCallOptions,
    ) {
      return rpc.subscribe(event, handler, {
        ...options,
        contractHash: neoRpcContractHash,
      });
    },
  };
}

export const desktop = createDesktopClient(withAdvancedContract({ invoke, subscribe }));

export const menuItems: readonly DesktopMenuItem[] = [
  {
    id: "say-hello",
    kind: "Command",
    text: "Send managed greeting",
    commandId: "tour.say-hello",
    accelerator: "Ctrl+Shift+H",
    enabled: true,
    visible: true,
    checked: false,
    children: [],
  },
  {
    id: "show-preview",
    kind: "Command",
    text: "Open restricted preview",
    commandId: "tour.show-preview",
    accelerator: "Ctrl+Shift+P",
    enabled: true,
    visible: true,
    checked: false,
    children: [],
  },
];

export const lifecycleTrayMenu: readonly DesktopMenuItem[] = [
  {
    id: "quit",
    kind: "Role",
    text: "Quit NeoAstra…",
    enabled: true,
    visible: true,
    checked: false,
    role: "Quit",
    children: [],
  },
];

// Keep the primary tour request within the portable notification baseline. Windows
// notification-area balloons cannot represent actions without packaged toast identity.
export const tourNotification: DesktopNotificationRequest = {
  appIdentity: "org.neoastra.sample.advanced",
  category: "tour",
  urgency: "normal",
  persistent: false,
  payload: "feature-tour",
  id: "feature-tour",
  title: "NeoAstra feature tour",
  body: "A renderer request reached the native notification adapter.",
  actions: [],
};

export const fileDialog: DesktopFileDialogRequest = {
  initialLocation: "assets",
  initialRelativePath: ".",
  extensions: ["svg", "txt"],
  title: "Choose a feature-tour asset",
  allowMultiple: true,
  filters: [
    {
      name: "Tour assets",
      extensions: ["svg", "txt"],
      mimeTypes: [],
    },
  ],
};

export const folderDialog: DesktopFileDialogRequest = {
  initialLocation: "assets",
  initialRelativePath: ".",
  extensions: [],
  title: "Choose a folder",
  allowMultiple: false,
  filters: [],
};

export const saveDialog: DesktopFileDialogRequest = {
  initialLocation: "assets",
  initialRelativePath: ".",
  extensions: ["txt"],
  title: "Choose where the tour would save",
  suggestedFileName: "neoastra-tour.txt",
  allowMultiple: false,
  filters: [
    {
      name: "Text",
      extensions: ["txt"],
      mimeTypes: [],
    },
  ],
};

export function encodeText(value: string): string {
  const bytes = new TextEncoder().encode(value);
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary);
}

export function decodeText(value: string): string {
  const binary = atob(value);
  const bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
  return new TextDecoder().decode(bytes);
}

export function describe(value: unknown): string {
  if (value === undefined) return "Completed";
  if (typeof value === "string") return value;
  return JSON.stringify(value, null, 2);
}

export function describeError(error: unknown): string {
  if (error instanceof Error) {
    const code = "code" in error && typeof error.code === "string"
      ? ` (${error.code})`
      : "";
    return `${error.message}${code}`;
  }
  return String(error);
}
