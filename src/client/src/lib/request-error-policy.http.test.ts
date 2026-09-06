// @vitest-environment node
import { createServer, type Server } from "node:http";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

interface Received {
  path: string;
  body: string;
  authorization: string | undefined;
  headers: string[];
}
let server: Server;
let requests: Received[];
let finalStatus: number;
let gate: { promise: Promise<void>; resolve: () => void } | undefined;
let client: typeof import("./api-client").default;
let auth: typeof import("./auth");
let localErrorPolicy: typeof import("./request-error-policy").localErrorPolicy;
let unsubscribe: () => void;
let errors: number[];
function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}
const body = { location: 'Café "North"', date: "2026-09-01", taxAmount: 1.23 };

beforeEach(async () => {
  vi.resetModules();
  localStorage.clear();
  vi.stubGlobal(
    "window",
    Object.assign(new EventTarget(), {
      location: { href: "/receipts" },
      sessionStorage: localStorage,
    }),
  );
  requests = [];
  errors = [];
  gate = undefined;
  finalStatus = 503;
  server = createServer(async (request, response) => {
    let receivedBody = "";
    for await (const chunk of request) receivedBody += chunk;
    requests.push({
      path: request.url!,
      body: receivedBody,
      authorization: request.headers.authorization,
      headers: Object.keys(request.headers),
    });
    let status = finalStatus;
    let responseBody: object = {
      status,
      detail: "Optional service unavailable",
    };
    if (request.url === "/api/auth/refresh") {
      status = 200;
      responseBody = {
        accessToken: "renewed-access",
        refreshToken: "renewed-refresh",
      };
    } else if (request.headers.authorization === "Bearer expired-access") {
      status = 401;
      responseBody = { status };
    } else {
      await gate?.promise;
      if (status === 200) responseBody = { id: "receipt" };
    }
    response.writeHead(status, { "Content-Type": "application/json" });
    response.end(JSON.stringify(responseBody));
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  if (!address || typeof address === "string")
    throw new Error("TCP listener required");
  vi.stubEnv("VITE_API_URL", `http://127.0.0.1:${address.port}`);
  ({ default: client } = await import("./api-client"));
  auth = await import("./auth");
  ({ localErrorPolicy } = await import("./request-error-policy"));
  unsubscribe = (await import("./server-error-bus")).addServerErrorListener(
    (status) => errors.push(status),
  );
  auth.setTokens("expired-access", "original-refresh");
});
afterEach(async () => {
  gate?.resolve();
  unsubscribe();
  auth.clearTokens();
  server.closeAllConnections();
  await new Promise<void>((resolve) => server.close(() => resolve()));
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
});

describe("local error ownership on native authenticated replay", () => {
  it.each([503, 200])(
    "retains request policy, body and authorization across401 refresh to%s",
    async (status) => {
      finalStatus = status;
      const result = await client.POST("/api/receipts", {
        ...localErrorPolicy.request,
        body,
      });
      expect(result.response.status).toBe(status);
      if (status === 503)
        expect(result.error).toMatchObject({
          status: 503,
          detail: "Optional service unavailable",
        });
      else expect(result.error).toBeUndefined();
      const business = requests.filter(
        (request) => request.path === "/api/receipts",
      );
      expect(business).toHaveLength(2);
      expect(business.map((request) => request.body)).toEqual([
        JSON.stringify(body),
        JSON.stringify(body),
      ]);
      expect(business.map((request) => request.authorization)).toEqual([
        "Bearer expired-access",
        "Bearer renewed-access",
      ]);
      expect(
        requests.filter((request) => request.path === "/api/auth/refresh"),
      ).toHaveLength(1);
      expect(
        requests
          .flatMap((request) => request.headers)
          .filter((name) => /presentation|error-policy/i.test(name)),
      ).toEqual([]);
      expect(errors).toEqual([]);
      expect(auth.getAccessToken()).toBe("renewed-access");
      expect(auth.getRefreshToken()).toBe("renewed-refresh");
    },
  );

  it("retains the default global503 event after the same successful refresh", async () => {
    const result = await client.POST("/api/receipts", { body });
    expect(result.response.status).toBe(503);
    expect(errors).toEqual([503]);
    expect(auth.getAccessToken()).toBe("renewed-access");
  });

  it("does not publish a late old-session503 into replacement credentials", async () => {
    auth.setTokens("current-access", "current-refresh");
    gate = deferred();
    const result = client
      .POST("/api/receipts", { ...localErrorPolicy.request, body })
      .catch((error: unknown) => error);
    await vi.waitFor(() => expect(requests).toHaveLength(1));
    auth.setTokens("bob-access", "bob-refresh");
    gate.resolve();
    expect(await result).toMatchObject({ name: "AbortError" });
    expect(errors).toEqual([]);
    expect(auth.getAccessToken()).toBe("bob-access");
    expect(auth.getRefreshToken()).toBe("bob-refresh");
  });
});
