// @vitest-environment node
import { createServer, type Server, type ServerResponse } from "node:http";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Use native fetch and a real loopback server: a fetch mock that does not consume
// the Request body cannot detect a broken authenticated mutation replay.
interface ReceivedRequest {
  method: string;
  path: string;
  authorization: string | undefined;
  contentType: string | undefined;
  body: string;
}

function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

let server: Server;
let requests: ReceivedRequest[];
let refreshGate: ReturnType<typeof deferred>;
let unauthorizedGate: ReturnType<typeof deferred> | undefined;
let refreshStatus: number;
let disconnectRefresh: boolean;
let finalStatus: number;
let finalBody: unknown;
let client: typeof import("./api-client").default;
let auth: typeof import("./auth");
let errorBus: typeof import("./server-error-bus");
let cleanupListeners: Array<() => void>;
const receiptId = "11111111-1111-1111-1111-111111111111";
const receipt = {
  location: 'Café "North"',
  date: "2026-01-02",
  taxAmount: 1.23,
};

function sendJson(response: ServerResponse, status: number, body: unknown) {
  response.writeHead(status, { "Content-Type": "application/json" });
  response.end(JSON.stringify(body));
}

const businessRequests = () =>
  requests.filter((r) => r.path !== "/api/auth/refresh");
const refreshRequests = () =>
  requests.filter((r) => r.path === "/api/auth/refresh");

beforeEach(async () => {
  vi.resetModules();
  requests = [];
  cleanupListeners = [];
  refreshGate = deferred();
  unauthorizedGate = undefined;
  refreshStatus = 200;
  disconnectRefresh = false;
  finalStatus = 200;
  finalBody = { data: [], total: 0, offset: 0, limit: 50 };
  localStorage.clear();
  vi.stubGlobal("window", {
    location: { href: "/receipts" },
    sessionStorage: localStorage,
  });

  server = createServer(async (request, response) => {
    let body = "";
    for await (const chunk of request) body += chunk;
    requests.push({
      method: request.method!,
      path: request.url!,
      authorization: request.headers.authorization,
      contentType: request.headers["content-type"],
      body,
    });
    if (request.url === "/api/auth/refresh") {
      await refreshGate.promise;
      if (disconnectRefresh) {
        response.destroy();
        return;
      }
      sendJson(
        response,
        refreshStatus,
        refreshStatus === 200
          ? { accessToken: "renewed-access", refreshToken: "renewed-refresh" }
          : { status: refreshStatus, detail: "Refresh token expired" },
      );
    } else if (request.headers.authorization === "Bearer expired-access") {
      if (request.url === "/api/receipts") await unauthorizedGate?.promise;
      sendJson(response, 401, { status: 401 });
    } else {
      sendJson(response, finalStatus, finalBody);
    }
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  if (!address || typeof address === "string")
    throw new Error("Expected TCP listener");
  vi.stubEnv("VITE_API_URL", `http://127.0.0.1:${address.port}`);
  ({ default: client } = await import("./api-client"));
  auth = await import("./auth");
  errorBus = await import("./server-error-bus");
  auth.setTokens("expired-access", "valid-refresh");
});

afterEach(async () => {
  refreshGate.resolve();
  unauthorizedGate?.resolve();
  cleanupListeners.forEach((unsubscribe) => unsubscribe());
  server.closeAllConnections();
  await new Promise<void>((resolve, reject) =>
    server.close((error) => (error ? reject(error) : resolve())),
  );
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
});

describe("authenticated replay over native HTTP", () => {
  it("does not dispatch a mutation if login changes before transport starts", async () => {
    const pending = client.POST("/api/receipts", { body: receipt }).then(
      (result) => ({ result, error: undefined }),
      (error: unknown) => ({ result: undefined, error }),
    );
    auth.clearTokens();
    auth.setTokens("bob-access", "bob-refresh");
    refreshGate.resolve();

    const outcome = await pending;

    expect(outcome.error).toMatchObject({ name: "AbortError" });
    expect(requests).toHaveLength(0);
    expect(auth.getAccessToken()).toBe("bob-access");
  });

  it.each(["POST", "PUT", "DELETE"] as const)(
    "replays the exact %s JSON body once after refreshing",
    async (method) => {
      finalStatus = method === "POST" ? 200 : 204;
      finalBody = { id: receiptId, ...receipt };
      refreshGate.resolve();
      const body =
        method === "DELETE"
          ? [receiptId]
          : method === "PUT"
            ? { id: receiptId, ...receipt }
            : receipt;

      const result =
        method === "POST"
          ? await client.POST("/api/receipts", { body: receipt })
          : method === "PUT"
            ? await client.PUT("/api/receipts/{id}", {
                params: { path: { id: receiptId } },
                body: { id: receiptId, ...receipt },
              })
            : await client.DELETE("/api/receipts", { body: [receiptId] });

      expect(result.response.status).toBe(finalStatus);
      expect(businessRequests()).toHaveLength(2);
      expect(businessRequests().map((r) => r.body)).toEqual([
        JSON.stringify(body),
        JSON.stringify(body),
      ]);
      expect(businessRequests().map((r) => r.method)).toEqual([method, method]);
      expect(businessRequests()[1]).toMatchObject({
        path: businessRequests()[0].path,
        authorization: "Bearer renewed-access",
        contentType: "application/json",
      });
      expect(refreshRequests()).toHaveLength(1);
      expect(JSON.parse(refreshRequests()[0].body)).toEqual({
        refreshToken: "valid-refresh",
      });
    },
  );

  it("retains GET refresh and deduplicates concurrent requests", async () => {
    const pending = Promise.all([
      client.GET("/api/cards"),
      client.GET("/api/cards"),
      client.GET("/api/cards"),
    ]);
    await vi.waitFor(() => {
      expect(businessRequests()).toHaveLength(3);
      expect(refreshRequests()).toHaveLength(1);
    });
    refreshGate.resolve();

    const results = await pending;

    expect(results.map((r) => r.data)).toEqual([
      finalBody,
      finalBody,
      finalBody,
    ]);
    expect(refreshRequests()).toHaveLength(1);
    expect(businessRequests()).toHaveLength(6);
    expect(
      businessRequests()
        .slice(3)
        .every((r) => r.authorization === "Bearer renewed-access"),
    ).toBe(true);
  });

  it("terminates a failed refresh without replay and expires the current session", async () => {
    refreshStatus = 401;
    refreshGate.resolve();

    const result = await client.GET("/api/cards");

    expect(result.error).toMatchObject({ status: 401 });
    expect(businessRequests()).toHaveLength(1);
    expect(refreshRequests()).toHaveLength(1);
    expect(auth.getAccessToken()).toBeNull();
    expect(auth.getRefreshToken()).toBeNull();
    expect(window.location.href).toBe("/login");
    expect(errorBus.consumeLoginFlash()).toBe(
      "Your session expired. Please sign in again.",
    );
  });

  it("terminates a disconnected refresh without replay", async () => {
    disconnectRefresh = true;
    refreshGate.resolve();

    const result = await client.GET("/api/cards");

    expect(result.error).toMatchObject({ status: 401 });
    expect(refreshRequests()).toHaveLength(1);
    expect(businessRequests()).toHaveLength(1);
    expect(auth.getAccessToken()).toBeNull();
    expect(window.location.href).toBe("/login");
  });

  it("does not refresh a rejected login", async () => {
    const result = await client.POST("/api/auth/login", {
      body: { email: "alice@example.com", password: "wrong" },
    });

    expect(result.error).toMatchObject({ status: 401 });
    expect(refreshRequests()).toHaveLength(0);
    expect(auth.getAccessToken()).toBe("expired-access");
    expect(window.location.href).toBe("/receipts");
  });

  it("shares one refresh between a proactive refresh and an unauthorized request", async () => {
    const { attemptTokenRefresh } = await import("./api-client");
    const proactive = attemptTokenRefresh();
    await vi.waitFor(() => expect(refreshRequests()).toHaveLength(1));
    const request = client.GET("/api/cards");
    await vi.waitFor(() => expect(businessRequests()).toHaveLength(1));
    refreshGate.resolve();

    expect(await proactive).toBe(true);
    expect((await request).response.status).toBe(200);
    expect(refreshRequests()).toHaveLength(1);
    expect(businessRequests()).toHaveLength(2);
  });

  it("reuses renewed credentials when an earlier request's 401 arrives after refresh", async () => {
    unauthorizedGate = deferred();
    const delayed = client.GET("/api/receipts");
    await vi.waitFor(() => expect(businessRequests()).toHaveLength(1));
    refreshGate.resolve();

    expect((await client.GET("/api/cards")).response.status).toBe(200);
    unauthorizedGate.resolve();

    expect((await delayed).response.status).toBe(200);
    expect(refreshRequests()).toHaveLength(1);
    expect(businessRequests()).toHaveLength(4);
    expect(businessRequests()[3].authorization).toBe("Bearer renewed-access");
  });

  it("does not refresh repeatedly when the replay is also unauthorized", async () => {
    finalStatus = 401;
    finalBody = { status: 401 };
    refreshGate.resolve();

    const result = await client.GET("/api/cards");

    expect(result.error).toMatchObject({ status: 401 });
    expect(businessRequests()).toHaveLength(2);
    expect(refreshRequests()).toHaveLength(1);
  });

  it("applies the password-change policy to a retried 403", async () => {
    const listener = vi.fn();
    cleanupListeners.push(auth.addPasswordChangeRequiredListener(listener));
    finalStatus = 403;
    finalBody = { status: 403, detail: "Password change required" };
    refreshGate.resolve();

    const result = await client.GET("/api/cards");

    expect(result.error).toEqual(finalBody);
    expect(listener).toHaveBeenCalledOnce();
    expect(businessRequests()).toHaveLength(2);
  });

  it("publishes a retried server failure exactly once and normalizes its body", async () => {
    const listener = vi.fn();
    cleanupListeners.push(errorBus.addServerErrorListener(listener));
    finalStatus = 503;
    finalBody = "Service temporarily unavailable";
    refreshGate.resolve();

    const result = await client.GET("/api/cards");

    expect(result.error).toEqual({ status: 503, detail: finalBody });
    expect(listener).toHaveBeenCalledExactlyOnceWith(503);
  });

  it("never submits an aborted mutation while another refresh waiter can still finish", async () => {
    const controller = new AbortController();
    const pending = client
      .POST("/api/receipts", { body: receipt, signal: controller.signal })
      .then(
        (result) => ({ result, error: undefined }),
        (error: unknown) => ({ result: undefined, error }),
      );
    await vi.waitFor(() => expect(refreshRequests()).toHaveLength(1));
    const otherRequest = client.GET("/api/cards");
    await vi.waitFor(() => expect(businessRequests()).toHaveLength(2));
    controller.abort();
    refreshGate.resolve();

    const outcome = await pending;

    expect(outcome.error).toMatchObject({ name: "AbortError" });
    expect((await otherRequest).response.status).toBe(200);
    expect(businessRequests().filter((r) => r.method === "POST")).toHaveLength(
      1,
    );
    expect(businessRequests()).toHaveLength(3);
    expect(refreshRequests()).toHaveLength(1);
  });

  it.each(["logout", "new login"] as const)(
    "does not restore tokens or replay when %s occurs during refresh",
    async (transition) => {
      const listener = vi.fn();
      cleanupListeners.push(auth.addTokenRefreshListener(listener));
      const pending = client.GET("/api/cards").then(
        (result) => ({ result, error: undefined }),
        (error: unknown) => ({ result: undefined, error }),
      );
      await vi.waitFor(() => expect(refreshRequests()).toHaveLength(1));
      auth.clearTokens();
      if (transition === "new login")
        auth.setTokens("bob-access", "bob-refresh");
      refreshGate.resolve();
      const outcome = await pending;

      expect(outcome.error).toMatchObject({ name: "AbortError" });
      expect(auth.getAccessToken()).toBe(
        transition === "new login" ? "bob-access" : null,
      );
      expect(auth.getRefreshToken()).toBe(
        transition === "new login" ? "bob-refresh" : null,
      );
      expect(businessRequests()).toHaveLength(1);
      expect(listener).not.toHaveBeenCalled();
      expect(errorBus.consumeLoginFlash()).toBeNull();
      expect(window.location.href).toBe("/receipts");
    },
  );

  it("does not expire a new login when an older session's refresh fails", async () => {
    refreshStatus = 401;
    const pending = client.GET("/api/cards").then(
      (result) => ({ result, error: undefined }),
      (error: unknown) => ({ result: undefined, error }),
    );
    await vi.waitFor(() => expect(refreshRequests()).toHaveLength(1));
    auth.clearTokens();
    auth.setTokens("bob-access", "bob-refresh");
    refreshGate.resolve();
    const outcome = await pending;

    expect(outcome.error).toMatchObject({ name: "AbortError" });
    expect(auth.getAccessToken()).toBe("bob-access");
    expect(auth.getRefreshToken()).toBe("bob-refresh");
    expect(businessRequests()).toHaveLength(1);
    expect(errorBus.consumeLoginFlash()).toBeNull();
    expect(window.location.href).toBe("/receipts");
  });

  it("does not replay or clear a session replaced by another tab during refresh", async () => {
    const pending = client.GET("/api/cards").then(
      (result) => ({ result, error: undefined }),
      (error: unknown) => ({ result: undefined, error }),
    );
    await vi.waitFor(() => expect(refreshRequests()).toHaveLength(1));
    localStorage.setItem("receipts_access_token", "bob-access");
    localStorage.setItem("receipts_refresh_token", "bob-refresh");
    refreshGate.resolve();

    const outcome = await pending;

    expect(outcome.error).toMatchObject({ name: "AbortError" });
    expect(auth.getAccessToken()).toBe("bob-access");
    expect(auth.getRefreshToken()).toBe("bob-refresh");
    expect(businessRequests()).toHaveLength(1);
    expect(errorBus.consumeLoginFlash()).toBeNull();
    expect(window.location.href).toBe("/receipts");
  });
});
