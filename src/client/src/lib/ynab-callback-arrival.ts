/**
 * YNAB returns the browser to this route with a single-use authorization code
 * and state in the query string (or `error=…` when the request was declined).
 */
export const YNAB_CALLBACK_PATH = "/settings/ynab/callback";

export interface YnabCallbackArrival {
  code: string | null;
  state: string | null;
  /** YNAB sent `error=…`: the request was not approved. */
  declined: boolean;
}

export function parseYnabCallbackArrival(search: string): YnabCallbackArrival {
  const params = new URLSearchParams(search);
  return {
    code: params.get("code"),
    state: params.get("state"),
    declined: params.has("error"),
  };
}

let captured: YnabCallbackArrival | null = null;

/**
 * Runs once at startup, before error monitoring and the router are created:
 * when the document was loaded at the callback address, remember the values in
 * memory and rewrite the address without its query and fragment. Everything
 * that later reads `window.location` (session replay's page metadata, trace
 * and error context, the router) then sees an address with no code or state.
 */
export function captureYnabCallbackArrival(
  win: Pick<Window, "location" | "history"> | undefined = typeof window ===
  "undefined"
    ? undefined
    : window,
): void {
  if (!win) return;
  const { pathname, search, hash } = win.location;
  if (pathname.replace(/\/+$/, "") !== YNAB_CALLBACK_PATH) return;
  if (!search && !hash) return;
  captured = parseYnabCallbackArrival(search);
  win.history.replaceState(win.history.state, "", YNAB_CALLBACK_PATH);
}

/** The values captured at startup, if the document was loaded at the callback address. */
export function peekYnabCallbackArrival(): YnabCallbackArrival | null {
  return captured;
}

/** Forget the captured values once the callback page holds them in its own state. */
export function clearYnabCallbackArrival(): void {
  captured = null;
}
