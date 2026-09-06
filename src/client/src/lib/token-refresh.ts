import {
  getAccessToken, getRefreshToken, getSessionVersion, getSessionSignal,
  assertSessionCurrent, setRefreshedTokens, notifyTokenRefresh,
} from "@/lib/auth";
import { apiUrl, API_TIMEOUT_MS } from "@/lib/api-config";

interface PendingRefresh {
  sessionVersion: number;
  refreshToken: string;
  promise: Promise<boolean>;
}

let pendingRefresh: PendingRefresh | null = null;

async function refreshTokens(sessionVersion: number, refreshToken: string): Promise<boolean> {
  try {
    const res = await fetch(apiUrl("/api/auth/refresh"), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ refreshToken }),
      signal: AbortSignal.any([AbortSignal.timeout(API_TIMEOUT_MS), getSessionSignal()]),
    });
    if (!res.ok) return false;

    const data = await res.json();
    if (
      typeof data?.accessToken !== "string" || !data.accessToken ||
      typeof data?.refreshToken !== "string" || !data.refreshToken ||
      !setRefreshedTokens(sessionVersion, refreshToken, data.accessToken, data.refreshToken)
    ) return false;

    notifyTokenRefresh();
    return true;
  } catch {
    return false;
  }
}

export function attemptTokenRefresh(): Promise<boolean> {
  const sessionVersion = getSessionVersion();
  const refreshToken = getRefreshToken();
  if (!refreshToken) return Promise.resolve(false);

  if (pendingRefresh?.sessionVersion === sessionVersion && pendingRefresh.refreshToken === refreshToken) {
    return pendingRefresh.promise;
  }

  const pending: PendingRefresh = {
    sessionVersion,
    refreshToken,
    promise: refreshTokens(sessionVersion, refreshToken).finally(() => {
      if (pendingRefresh === pending) pendingRefresh = null;
    }),
  };
  pendingRefresh = pending;
  return pending.promise;
}

// Cancelling one request must not cancel the shared refresh needed by others.
export function waitForRefresh(promise: Promise<boolean>, signal: AbortSignal): Promise<boolean> {
  signal.throwIfAborted();
  return new Promise((resolve, reject) => {
    const onAbort = () => reject(signal.reason);
    signal.addEventListener("abort", onAbort, { once: true });
    promise.then(
      (value) => {
        signal.removeEventListener("abort", onAbort);
        resolve(value);
      },
      (error: unknown) => {
        signal.removeEventListener("abort", onAbort);
        reject(error);
      },
    );
  });
}

// Expiry is only a refresh hint. The server remains the authority for JWT validity.
function hasUsableExpiry(token: string | null): token is string {
  if (!token) return false;
  try {
    const parts = token.split(".");
    if (parts.length !== 3) return false;
    const encoded = parts[1].replace(/-/g, "+").replace(/_/g, "/");
    const bytes = Uint8Array.from(atob(encoded), (character) => character.charCodeAt(0));
    const payload: unknown = JSON.parse(new TextDecoder().decode(bytes));
    if (!payload || typeof payload !== "object" || !("exp" in payload)) return false;
    return typeof payload.exp === "number" && Number.isFinite(payload.exp) &&
      payload.exp > Date.now() / 1000 + 30;
  } catch {
    return false;
  }
}

/** A connection's cancellation stops its wait, never another caller's refresh. */
export async function getConnectionAccessToken(
  sessionVersion: number,
  signal: AbortSignal,
): Promise<string> {
  signal.throwIfAborted();
  assertSessionCurrent(sessionVersion);
  let token = getAccessToken();
  if (hasUsableExpiry(token)) return token;

  const refreshed = await waitForRefresh(attemptTokenRefresh(), signal);
  signal.throwIfAborted();
  assertSessionCurrent(sessionVersion);
  token = getAccessToken();
  if (!refreshed || !hasUsableExpiry(token)) {
    // A network outage is not proof of invalid credentials. Retry the connection;
    // do not terminate this session from the background transport.
    throw new Error("Unable to obtain an unexpired connection token");
  }
  return token;
}
