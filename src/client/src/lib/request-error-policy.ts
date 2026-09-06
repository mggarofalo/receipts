import type { Middleware } from "openapi-fetch";
import { parseProblemDetails } from "@/lib/problem-details";

export type ErrorPresentation = "global" | "local";
export interface ErrorMetadata extends Record<string, unknown> {
  errorPresentation?: ErrorPresentation;
}

const requestPresentations = new WeakMap<Request, ErrorPresentation>();

export function getRequestErrorPresentation(request: Request): ErrorPresentation {
  return requestPresentations.get(request) ?? "global";
}

export function getCacheErrorPresentation(meta?: Record<string, unknown>): ErrorPresentation {
  return meta?.errorPresentation === "local" ? "local" : "global";
}

export function isHttpServerError(error: unknown): boolean {
  const status = parseProblemDetails(error)?.status;
  return typeof status === "number" && status >= 500 && status < 600;
}

const localRequestMiddleware: Middleware = {
  onRequest({ request }) {
    // This stays on the original request used by response middleware after
    // native refresh/replay. No presentation metadata is sent to the server.
    requestPresentations.set(request, "local");
    return request;
  },
};
const localMetadata: ErrorMetadata = { errorPresentation: "local" };

/** Select both request and cache options when a query/mutation owns its error UI. */
export const localErrorPolicy = {
  request: { middleware: [localRequestMiddleware] },
  query: { meta: localMetadata, retry: false as const },
  mutation: { meta: localMetadata },
};
