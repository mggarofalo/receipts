import createClient from "openapi-fetch";
import type { Middleware } from "openapi-fetch";
import type { paths } from "@/generated/api";
import {
  getAccessToken,
  getRefreshToken,
  setTokens,
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

const client = createClient<paths>({
  baseUrl,
  fetch: (input: Request) => {
    const timeoutSignal = AbortSignal.timeout(API_TIMEOUT_MS);
    const signal = input.signal
      ? AbortSignal.any([timeoutSignal, input.signal])
      : timeoutSignal;
    return fetch(input, { signal });
  },
});

export function isTimeoutError(error: unknown): boolean {
  return error instanceof DOMException && error.name === "TimeoutError";
}

export function isNetworkError(error: unknown): boolean {
  return error instanceof TypeError && error.message.includes("fetch");
}

let refreshPromise: Promise<boolean> | null = null;

export async function attemptTokenRefresh(): Promise<boolean> {
  const refreshToken = getRefreshToken();
  if (!refreshToken) return false;

  try {
    const res = await fetch(`${baseUrl}/api/auth/refresh`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ refreshToken }),
      signal: AbortSignal.timeout(API_TIMEOUT_MS),
    });
    if (!res.ok) return false;

    const data = await res.json();
    setTokens(data.accessToken, data.refreshToken);
    notifyTokenRefresh();
    return true;
  } catch {
    return false;
  }
}

const authMiddleware: Middleware = {
  async onRequest({ request }) {
    const token = getAccessToken();
    if (token) {
      request.headers.set("Authorization", `Bearer ${token}`);
    }
    return request;
  },
  async onResponse({ request, response }) {
    if (response.status === 403) {
      const cloned = response.clone();
      try {
        const body = await cloned.json();
        if (body?.detail === "Password change required") {
          notifyPasswordChangeRequired();
        }
      } catch {
        // Not JSON — ignore
      }
      return response;
    }

    if (response.status !== 401) return response;

    // Avoid refresh loop for auth endpoints
    const url = new URL(request.url);
    if (url.pathname.startsWith("/api/auth/")) return response;

    // Deduplicate concurrent refresh attempts
    if (!refreshPromise) {
      refreshPromise = attemptTokenRefresh().finally(() => {
        refreshPromise = null;
      });
    }

    const refreshed = await refreshPromise;
    if (!refreshed) {
      clearTokens();
      // Surface a session-scoped flash on /login so the user sees *why*
      // they're back there rather than wondering. RECEIPTS-740.
      setLoginFlash("Your session expired. Please sign in again.");
      window.location.href = "/login";
      return response;
    }

    // Retry original request with new token
    const newToken = getAccessToken();
    const retryRequest = new Request(request, {
      headers: new Headers(request.headers),
    });
    if (newToken) {
      retryRequest.headers.set("Authorization", `Bearer ${newToken}`);
    }
    const timeoutSignal = AbortSignal.timeout(API_TIMEOUT_MS);
    const retrySignal = retryRequest.signal
      ? AbortSignal.any([timeoutSignal, retryRequest.signal])
      : timeoutSignal;
    return fetch(retryRequest, { signal: retrySignal });
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
 *   2. A bare JSON string, from `TypedResults.BadRequest("some reason")`.
 *      Parses to a JS string, so `handleGlobalError`'s `typeof === "object"`
 *      test fails and the user is shown *nothing* (RECEIPTS-886).
 *   3. No body at all — an authorization 403 or a bodiless `NotFound()`.
 *      openapi-fetch yields `""` here, or `undefined` when the response
 *      carries `Content-Length: 0`. Both are falsy, so the ubiquitous
 *      `if (error) throw error` treats the failure as a *success*: the
 *      success toast fires and dialogs close (RECEIPTS-885).
 *
 * Normalising here rather than at each of the ~150 call sites means the
 * existing `if (error) throw error` becomes correct everywhere, and no future
 * hook can reintroduce the bug by forgetting a status check.
 *
 * Registered FIRST so it runs LAST: openapi-fetch invokes `onResponse` in
 * reverse registration order, and this must observe the final response, after
 * `authMiddleware` has had its chance to replace a 401 with a successful retry.
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
