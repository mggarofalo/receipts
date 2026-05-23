/**
 * Decouples the API client (which can't call React Router's `navigate`) from
 * the React shell (which can). The middleware fires `notifyServerError()` on
 * a 5xx response; a subscriber inside the shell decides whether to navigate
 * to /error/500 or show a toast.
 *
 * Listed in lib/ rather than hooks/ because the publisher is non-React.
 */
type ServerErrorListener = (status: number) => void;
const serverErrorListeners = new Set<ServerErrorListener>();

export function addServerErrorListener(cb: ServerErrorListener): () => void {
  serverErrorListeners.add(cb);
  return () => serverErrorListeners.delete(cb);
}

export function notifyServerError(status: number): void {
  serverErrorListeners.forEach((cb) => cb(status));
}

/**
 * Session-scoped flag: have we already navigated to the dedicated 500 page
 * during this tab session? If so, subsequent 5xx responses are toasted
 * instead of navigating again. sessionStorage clears with the tab so a
 * fresh tab gets the full editorial treatment again.
 */
const SESSION_KEY = "receipts:server-error-shown";

export function hasShownServerErrorPage(): boolean {
  try {
    return window.sessionStorage.getItem(SESSION_KEY) === "1";
  } catch {
    return false;
  }
}

export function markServerErrorPageShown(): void {
  try {
    window.sessionStorage.setItem(SESSION_KEY, "1");
  } catch {
    // Private mode / disabled storage — degrade silently. We'll just keep
    // navigating to the page; users in private mode have bigger problems.
  }
}

export function clearServerErrorPageFlag(): void {
  try {
    window.sessionStorage.removeItem(SESSION_KEY);
  } catch {
    /* see above */
  }
}

/**
 * sessionStorage-backed flash message shown on the Login page after a 401
 * triggers an automatic redirect. Set by the api-client middleware right
 * before navigating to /login; consumed and cleared by the Login page.
 */
const LOGIN_FLASH_KEY = "receipts:login-flash";

export function setLoginFlash(message: string): void {
  try {
    window.sessionStorage.setItem(LOGIN_FLASH_KEY, message);
  } catch {
    /* ignore */
  }
}

export function consumeLoginFlash(): string | null {
  try {
    const v = window.sessionStorage.getItem(LOGIN_FLASH_KEY);
    if (v) window.sessionStorage.removeItem(LOGIN_FLASH_KEY);
    return v;
  } catch {
    return null;
  }
}
