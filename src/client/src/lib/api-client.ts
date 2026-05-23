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

client.use(authMiddleware);
client.use(signalRConnectionMiddleware);
client.use(serverErrorMiddleware);

export default client;
