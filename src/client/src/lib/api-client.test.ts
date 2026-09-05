import { describe, it, expect, vi, beforeEach } from "vitest";
import type { Middleware, MiddlewareCallbackParams } from "openapi-fetch";

// Capture middleware registered via client.use()
const registeredMiddleware: Middleware[] = [];

vi.mock("openapi-fetch", () => ({
  default: () => ({
    GET: vi.fn(),
    POST: vi.fn(),
    PUT: vi.fn(),
    DELETE: vi.fn(),
    use: (mw: Middleware) => {
      registeredMiddleware.push(mw);
    },
  }),
}));

vi.mock("@/lib/auth", () => ({
  getAccessToken: vi.fn(() => null),
  getRefreshToken: vi.fn(() => null),
  getSessionVersion: vi.fn(() => 0),
  setTokens: vi.fn(),
  clearTokens: vi.fn(),
  notifyTokenRefresh: vi.fn(),
  notifyPasswordChangeRequired: vi.fn(),
}));

vi.mock("@/lib/signalr-connection", () => ({
  getConnectionId: vi.fn(() => null),
}));

// Must import after mocks are set up
import * as auth from "@/lib/auth";
import * as signalr from "@/lib/signalr-connection";

const mockedAuth = vi.mocked(auth);
const mockedSignalR = vi.mocked(signalr);

let errorNormalizationMiddleware: Middleware;
let authMiddleware: Middleware;
let signalRMiddleware: Middleware;

// Helper to build middleware callback params with correct types
function makeParams(
  request: Request,
  response?: Response,
): MiddlewareCallbackParams & { response: Response } {
  return {
    request,
    schemaPath: "/api/items",
    params: {},
    id: "test",
    options: {} as MiddlewareCallbackParams["options"],
    response: response ?? new Response(),
  };
}

// Helper to create a minimal Request
function makeRequest(url = "https://api.test/api/items"): Request {
  return new Request(url);
}

// Helper to create a minimal Response
function makeResponse(status: number, body?: unknown): Response {
  return new Response(body ? JSON.stringify(body) : null, {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

beforeEach(async () => {
  registeredMiddleware.length = 0;
  vi.resetModules();

  // Re-import to trigger module-level registration
  const mod = await import("./api-client");
  // Extract the isTimeoutError and isNetworkError from the module
  Object.assign(globalThis, { __apiClientModule: mod });

  // Registration order matters: the error normaliser is registered first so
  // that openapi-fetch — which runs `onResponse` in reverse order — invokes it
  // last, after authMiddleware has had its chance to retry a 401.
  [errorNormalizationMiddleware, authMiddleware, signalRMiddleware] =
    registeredMiddleware;

  vi.clearAllMocks();
});

describe("isTimeoutError", () => {
  it("returns true for DOMException with TimeoutError name", async () => {
    const { isTimeoutError } = await import("./api-client");
    const err = new DOMException("The operation timed out", "TimeoutError");
    expect(isTimeoutError(err)).toBe(true);
  });

  it("returns false for DOMException with different name", async () => {
    const { isTimeoutError } = await import("./api-client");
    const err = new DOMException("Aborted", "AbortError");
    expect(isTimeoutError(err)).toBe(false);
  });

  it("returns false for non-DOMException", async () => {
    const { isTimeoutError } = await import("./api-client");
    expect(isTimeoutError(new Error("timeout"))).toBe(false);
    expect(isTimeoutError("timeout")).toBe(false);
    expect(isTimeoutError(null)).toBe(false);
  });
});

describe("isNetworkError", () => {
  it("returns true for TypeError with fetch in message", async () => {
    const { isNetworkError } = await import("./api-client");
    const err = new TypeError("Failed to fetch");
    expect(isNetworkError(err)).toBe(true);
  });

  it("returns false for TypeError without fetch in message", async () => {
    const { isNetworkError } = await import("./api-client");
    const err = new TypeError("Cannot read property 'x'");
    expect(isNetworkError(err)).toBe(false);
  });

  it("returns false for non-TypeError", async () => {
    const { isNetworkError } = await import("./api-client");
    expect(isNetworkError(new Error("fetch failed"))).toBe(false);
    expect(isNetworkError(null)).toBe(false);
  });
});

describe("authMiddleware.onRequest", () => {
  it("attaches Authorization header when token exists", async () => {
    mockedAuth.getAccessToken.mockReturnValue("my-token");
    const request = makeRequest();

    const result = await authMiddleware.onRequest!(makeParams(request));

    expect((result as Request).headers.get("Authorization")).toBe(
      "Bearer my-token",
    );
  });

  it("does not attach Authorization header when no token", async () => {
    mockedAuth.getAccessToken.mockReturnValue(null);
    const request = makeRequest();

    const result = await authMiddleware.onRequest!(makeParams(request));

    expect((result as Request).headers.has("Authorization")).toBe(false);
  });
});

describe("authMiddleware.onResponse", () => {
  const callOnResponse = (request: Request, response: Response) =>
    authMiddleware.onResponse!(makeParams(request, response));

  it("passes through non-401 non-403 responses", async () => {
    const request = makeRequest();
    const response = makeResponse(200);

    const result = await callOnResponse(request, response);

    expect(result).toBe(response);
    expect(mockedAuth.clearTokens).not.toHaveBeenCalled();
  });

  it("handles 403 with password change required", async () => {
    const request = makeRequest();
    const response = makeResponse(403, {
      detail: "Password change required",
    });

    const result = await callOnResponse(request, response);

    expect(result).toBe(response);
    expect(mockedAuth.notifyPasswordChangeRequired).toHaveBeenCalled();
  });

  it("handles 403 with different detail (no password change notification)", async () => {
    const request = makeRequest();
    const response = makeResponse(403, { detail: "Access denied" });

    const result = await callOnResponse(request, response);

    expect(result).toBe(response);
    expect(mockedAuth.notifyPasswordChangeRequired).not.toHaveBeenCalled();
  });

  it("handles 403 with non-JSON body", async () => {
    const request = makeRequest();
    const response = new Response("Forbidden", {
      status: 403,
      headers: { "Content-Type": "text/plain" },
    });

    const result = await callOnResponse(request, response);

    expect(result).toBe(response);
    expect(mockedAuth.notifyPasswordChangeRequired).not.toHaveBeenCalled();
  });


});

describe("errorNormalizationMiddleware.onResponse", () => {
  const callOnResponse = (response: Response) =>
    errorNormalizationMiddleware.onResponse!(
      makeParams(makeRequest(), response),
    );

  /**
   * Reads the middleware's output the way openapi-fetch does for a non-ok
   * response: `await response.text()` followed by a best-effort `JSON.parse`.
   * Whatever this returns is what a call site sees as `error`.
   */
  async function errorAsSeenByCallers(response: Response): Promise<unknown> {
    const result = await callOnResponse(response);
    const final = (result as Response | undefined) ?? response;
    const raw = await final.text();
    if (!raw) return undefined;
    try {
      return JSON.parse(raw);
    } catch {
      return raw;
    }
  }

  it("leaves successful responses untouched", async () => {
    expect(await callOnResponse(makeResponse(200, { id: 1 }))).toBeUndefined();
  });

  // RECEIPTS-885: the bug that reported a rejected merge as "Cards merged".
  it("gives a bodiless 403 a truthy error carrying the status", async () => {
    const error = await errorAsSeenByCallers(makeResponse(403));

    expect(error).toBeTruthy();
    expect(error).toMatchObject({ status: 403 });
  });

  it("gives a bodiless 403 sent with Content-Length: 0 the same treatment", async () => {
    const response = new Response(null, {
      status: 403,
      headers: { "Content-Length": "0" },
    });

    expect(await errorAsSeenByCallers(response)).toMatchObject({ status: 403 });
  });

  it("gives a bodiless 404 a truthy error carrying the status", async () => {
    expect(await errorAsSeenByCallers(makeResponse(404))).toMatchObject({
      status: 404,
    });
  });

  // RECEIPTS-886: TypedResults.BadRequest("...") serialises a bare JSON string,
  // which handleGlobalError's `typeof === "object"` test used to discard.
  it("promotes a bare-string 400 body to ProblemDetails detail", async () => {
    const message =
      "Source account would be partially merged: all of its cards must be included in the merge, or none.";
    const response = new Response(JSON.stringify(message), {
      status: 400,
      headers: { "Content-Type": "application/json" },
    });

    expect(await errorAsSeenByCallers(response)).toEqual({
      status: 400,
      detail: message,
    });
  });

  it("promotes a non-JSON text body to ProblemDetails detail", async () => {
    const response = new Response("Forbidden", {
      status: 403,
      headers: { "Content-Type": "text/plain" },
    });

    expect(await errorAsSeenByCallers(response)).toEqual({
      status: 403,
      detail: "Forbidden",
    });
  });

  it("leaves a real ProblemDetails body untouched", async () => {
    const problem = {
      status: 400,
      title: "One or more validation errors occurred.",
      errors: { Date: ["Date cannot be in the future"] },
    };

    expect(await callOnResponse(makeResponse(400, problem))).toBeUndefined();
    expect(await errorAsSeenByCallers(makeResponse(400, problem))).toEqual(
      problem,
    );
  });

  it("preserves a non-ProblemDetails object body and stamps the status on it", async () => {
    // The 409 shape useMergeCards depends on to render its conflict dialog.
    const conflict = {
      message: "YNAB mapping conflict",
      conflicts: [{ cardId: "abc", accountName: "Checking" }],
    };

    expect(await errorAsSeenByCallers(makeResponse(409, conflict))).toEqual({
      ...conflict,
      status: 409,
    });
  });

  it("stamps the real status over a body whose status is not a number", async () => {
    // `handleGlobalError` keys off a *numeric* status, so a body carrying
    // `status: "error"` must not be mistaken for ProblemDetails.
    const response = makeResponse(400, { status: "error", detail: "nope" });

    expect(await errorAsSeenByCallers(response)).toEqual({
      status: 400,
      detail: "nope",
    });
  });

  it("leaves an array body untouched rather than spreading its indices", async () => {
    const body = ["first", "second"];

    expect(await callOnResponse(makeResponse(422, body))).toBeUndefined();
    expect(await errorAsSeenByCallers(makeResponse(422, body))).toEqual(body);
  });

  it("leaves a 304 alone, since a null-body status cannot carry one", async () => {
    const response = new Response(null, { status: 304 });

    // Constructing a body-bearing 304 throws, so the guard must short-circuit.
    expect(await callOnResponse(response)).toBeUndefined();
  });

  it("does not consume the body, so later readers still see it", async () => {
    const response = makeResponse(400, { status: 400, detail: "nope" });

    await callOnResponse(response);

    expect(response.bodyUsed).toBe(false);
    await expect(response.json()).resolves.toMatchObject({ detail: "nope" });
  });
});

describe("signalRConnectionMiddleware.onRequest", () => {
  it("attaches X-SignalR-Connection-Id header when connected", async () => {
    mockedSignalR.getConnectionId.mockReturnValue("conn-abc");
    const request = makeRequest();

    const result = await signalRMiddleware.onRequest!(makeParams(request));

    expect((result as Request).headers.get("X-SignalR-Connection-Id")).toBe(
      "conn-abc",
    );
  });

  it("does not attach header when no connection ID", async () => {
    mockedSignalR.getConnectionId.mockReturnValue(null);
    const request = makeRequest();

    const result = await signalRMiddleware.onRequest!(makeParams(request));

    expect(
      (result as Request).headers.has("X-SignalR-Connection-Id"),
    ).toBe(false);
  });
});
