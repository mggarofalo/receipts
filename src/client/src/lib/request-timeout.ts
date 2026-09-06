import type { Middleware } from "openapi-fetch";
import { API_TIMEOUT_MS } from "@/lib/api-config";

const requestTimeouts = new WeakMap<Request, number>();

export function getRequestTimeoutMs(request: Request): number {
  return requestTimeouts.get(request) ?? API_TIMEOUT_MS;
}

/** Override this request's one transport deadline without changing refresh ownership. */
export function requestTimeout(milliseconds: number): Middleware {
  if (!Number.isInteger(milliseconds) || milliseconds <= 0 || milliseconds > 2_147_483_647) {
    throw new RangeError("Request timeout must be a positive supported timer duration.");
  }
  return {
    onRequest({ request }) {
      requestTimeouts.set(request, milliseconds);
      return request;
    },
  };
}
