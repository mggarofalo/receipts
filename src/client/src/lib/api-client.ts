import createClient from "openapi-fetch";
import type { Middleware } from "openapi-fetch";
import type { paths } from "@/generated/api";
import {
  getAccessToken,
  getRefreshToken,
  getSessionVersion,
  setRefreshedTokens,
  clearTokens,
  notifyTokenRefresh,
  notifyPasswordChangeRequired,
} from "@/lib/auth";
import { getConnectionId } from "@/lib/signalr-connection";
import {
  notifyServerError,
  setLoginFlash,
} from "@/lib/server-error-bus";
import { toApiError } from "@/lib/problem-details";

const baseUrl = import.meta.env.VITE_API_URL ?? "";
const API_TIMEOUT_MS = 30_000;

interface PendingRefresh {
  sessionVersion: number;
  refreshToken: string;
  promise: Promise<boolean>;
}

let pendingRefresh: PendingRefresh | null = null;
const requestSessions = new WeakMap<Request, number>();

export function isTimeoutError(error: unknown): boolean {
  return error instanceof DOMException && error.name === "TimeoutError";
}

export function isNetworkError(error: unknown): boolean {
  return error instanceof TypeError && error.message.includes("fetch");
}

async function refreshTokens(sessionVersion: number, refreshToken: string): Promise<boolean> {
  try {
    const res = await fetch(`${baseUrl}/api/auth/refresh`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ refreshToken }),
      signal: AbortSignal.timeout(API_TIMEOUT_MS),
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

function requireCurrentSession(sessionVersion: number): void {
  if (getSessionVersion() !== sessionVersion) {
    throw new DOMException("The authenticated session changed", "AbortError");
  }
}

// Cancelling one request must not cancel the shared refresh needed by others.
function waitForRefresh(promise: Promise<boolean>, signal: AbortSignal): Promise<boolean> {
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

async function fetchWithTokenRefresh(request: Request): Promise<Response> {
  const signal = AbortSignal.any([AbortSignal.timeout(API_TIMEOUT_MS), request.signal]);
  const isAuthRequest = new URL(request.url).pathname.startsWith("/api/auth/");
  if (isAuthRequest) return fetch(request, { signal });

  const sessionVersion = requestSessions.get(request) ?? getSessionVersion();
  requireCurrentSession(sessionVersion);
  // Fetch consumes body streams. Save the replay before the first dispatch,
  // after request middleware has applied all headers and serialization.
  const replay = request.clone();
  try {
    const response = await fetch(request, { signal });
    signal.throwIfAborted();
    requireCurrentSession(sessionVersion);
    if (response.status !== 401) return response;

    // A concurrent request may already have rotated the credentials by the
    // time this response arrives. Reuse them instead of rotating again.
    const token = getAccessToken();
    const alreadyRefreshed = token !== null && request.headers.get("Authorization") !== `Bearer ${token}`;
    const refreshed = alreadyRefreshed || await waitForRefresh(attemptTokenRefresh(), signal);
    signal.throwIfAborted();
    requireCurrentSession(sessionVersion);
    if (!refreshed) {
      clearTokens();
      setLoginFlash("Your session expired. Please sign in again.");
      window.location.href = "/login";
      return response;
    }

    if (response.body) void response.body.cancel().catch(() => {});
    const newToken = getAccessToken();
    if (newToken) replay.headers.set("Authorization", `Bearer ${newToken}`);
    // This transport retries once. The final response then traverses every
    // ordinary response middleware, including password-change and 5xx policy.
    const retriedResponse = await fetch(replay, { signal });
    signal.throwIfAborted();
    requireCurrentSession(sessionVersion);
    return retriedResponse;
  } finally {
    // Do not retain the unused side of a cloned upload after a normal response.
    if (replay.body && !replay.bodyUsed) void replay.body.cancel().catch(() => {});
  }
}

const client = createClient<paths>({ baseUrl, fetch: fetchWithTokenRefresh });

const authMiddleware: Middleware = {
  async onRequest({ request }) {
    // Bind identity before openapi-fetch yields between request middleware.
    requestSessions.set(request, getSessionVersion());
    const token = getAccessToken();
    if (token) request.headers.set("Authorization", `Bearer ${token}`);
    return request;
  },
  async onResponse({ response }) {
    if (response.status === 403) {
      try {
        const body = await response.clone().json();
        if (body?.detail === "Password change required") notifyPasswordChangeRequired();
      } catch {
        // A bodiless authorization rejection has no password-change reason.
      }
    }
    return response;
  },
};

const signalRConnectionMiddleware: Middleware = {
  async onRequest({ request }) {
    const connId = getConnectionId();
    if (connId) {
      request.headers.set("X-SignalR-Connection-Id", connId);
    }
    return request;
  },
};

// Surfaces 5xx responses to the React shell so it can navigate to the
// dedicated /error/500 page on the first occurrence of a session, then
// fall back to toasts (RECEIPTS-740). The "first vs subsequent" decision
// lives in the subscriber (RootLayout) — this middleware only publishes.
const serverErrorMiddleware: Middleware = {
  async onResponse({ response }) {
    if (response.status >= 500 && response.status < 600) {
      notifyServerError(response.status);
    }
    return response;
  },
};

// Statuses the Fetch spec forbids from carrying a body. Constructing a
// `Response` with a body and one of these statuses throws a TypeError, so the
// normaliser has to leave them alone. 304 is the one that actually reaches
// here: it is not `ok`, but it is also not an error worth rewriting.
const NULL_BODY_STATUSES = new Set([204, 205, 304]);

/**
 * Guarantees that every failed response carries a ProblemDetails-shaped JSON
 * body, so `error` is always a truthy object with a numeric `status`.
 *
 * openapi-fetch derives `error` from the raw body of a non-ok response
 * (`error = await response.text()`, then a best-effort `JSON.parse`). This API
 * produces three different failure bodies, and two of them break callers:
 *
 *   1. ProblemDetails — has `status`. Handled correctly everywhere already.
 *      Since RECEIPTS-886 this is what every rejection-with-a-reason sends.
 *   2. A bare JSON string, from `TypedResults.BadRequest("some reason")`.
 *      Parses to a JS string, so `handleGlobalError`'s `typeof === "object"`
 *      test fails and the user is shown *nothing*. The server no longer
 *      produces this shape, but the branch stays: it is the backstop for any
 *      endpoint that regresses, and it costs one `typeof` check.
 *   3. No body at all — an authorization 403 or a bodiless `NotFound()`.
 *      openapi-fetch yields `""` here, or `undefined` when the response
 *      carries `Content-Length: 0`. Both are falsy, so the ubiquitous
 *      `if (error) throw error` treats the failure as a *success*: the
 *      success toast fires and dialogs close (RECEIPTS-885). This one is
 *      still live — ASP.NET's authorization failures have no body to give.
 *
 * Normalising here rather than at each of the ~150 call sites means the
 * existing `if (error) throw error` becomes correct everywhere, and no future
 * hook can reintroduce the bug by forgetting a status check.
 *
 * Registered FIRST so it runs LAST: openapi-fetch invokes `onResponse` in
 * reverse registration order, and this must observe the final response, after
 * the transport has resolved any token refresh and retry.
 */
const errorNormalizationMiddleware: Middleware = {
  async onResponse({ response }) {
    if (response.ok) return undefined;
    if (NULL_BODY_STATUSES.has(response.status)) return undefined;

    const raw = await response.clone().text();

    let body: unknown = undefined;
    if (raw.trim()) {
      try {
        body = JSON.parse(raw);
      } catch {
        // Not JSON — keep the raw text so it can become `detail`.
        body = raw;
      }
    }

    // Shape 1: already ProblemDetails. Leave the response untouched so the
    // server's own `status`, `errors` map and `detail` survive verbatim.
    // The status must be a *number*: `handleGlobalError` keys off that, so a
    // body carrying `status: "error"` would otherwise pass through and still
    // toast nothing. Stamping the real HTTP status over it is the fix.
    if (
      body &&
      typeof body === "object" &&
      !Array.isArray(body) &&
      typeof (body as Record<string, unknown>).status === "number"
    ) {
      return undefined;
    }

    // An array body is already truthy, so neither bug applies. Rewriting it
    // would mangle it (`{...[]}` spreads indices), so leave it be.
    if (Array.isArray(body)) return undefined;

    return new Response(JSON.stringify(toApiError(response.status, body)), {
      status: response.status,
      statusText: response.statusText,
      headers: { "Content-Type": "application/json" },
    });
  },
};

client.use(errorNormalizationMiddleware);
client.use(authMiddleware);
client.use(signalRConnectionMiddleware);
client.use(serverErrorMiddleware);

export default client;
