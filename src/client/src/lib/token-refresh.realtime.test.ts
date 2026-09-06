// @vitest-environment node
import { createServer, type Server } from "node:http";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

function jwt(payload: object) {
  return `header.${Buffer.from(JSON.stringify(payload)).toString("base64url")}.signature`;
}
function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => { resolve = done; });
  return { promise, resolve };
}
let server: Server;
let origin: string;
let paths: string[];
let refreshStatus: number;
let refreshedToken: string;
let responseGate: ReturnType<typeof deferred> | undefined;
let auth: typeof import("./auth");
let refresh: typeof import("./token-refresh");
let api: typeof import("./api-client");

beforeEach(async () => {
  vi.resetModules();
  localStorage.clear();
  paths = [];
  responseGate = undefined;
  refreshStatus = 200;
  refreshedToken = jwt({ exp: Math.floor(Date.now() / 1000) + 3600, userId: "alice" });
  vi.stubGlobal("window", Object.assign(new EventTarget(), { location: { href: "/receipts" }, sessionStorage: localStorage }));
  server = createServer(async (request, response) => {
    for await (const chunk of request) void chunk;
    paths.push(request.url!);
    if (request.url === "/proxy/api/auth/refresh") {
      response.writeHead(refreshStatus, { "Content-Type": "application/json" });
      response.end(JSON.stringify({ accessToken: refreshedToken, refreshToken: "rotated" }));
    } else if (request.url === "/proxy/api/auth/login") {
      response.writeHead(401, { "Content-Type": "application/json" });
      response.end(JSON.stringify({ status: 401, detail: "Credentials rejected" }));
    } else if (request.url === "/proxy/api/auth/logout") {
      await responseGate?.promise;
      response.writeHead(204);
      response.end();
    } else if (request.url === "/proxy/api/accounts") {
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end(JSON.stringify({ data: [], total: 0, offset: 0, limit: 50 }));
    } else {
      response.writeHead(404);
      response.end();
    }
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  if (!address || typeof address === "string") throw new Error("TCP listener required");
  origin = `http://127.0.0.1:${address.port}`;
  vi.stubEnv("VITE_API_URL", `${origin}/proxy///`);
  auth = await import("./auth");
  refresh = await import("./token-refresh");
  api = await import("./api-client");
  auth.setTokens(refreshedToken, "refresh");
});
afterEach(async () => {
  responseGate?.resolve();
  auth.clearTokens();
  server.closeAllConnections();
  await new Promise<void>((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
});

describe("connection expiry hints over native refresh HTTP", () => {
  it.each([
    ["missing expiry", () => jwt({ userId: "alice" })],
    ["string expiry", () => jwt({ exp: String(Math.floor(Date.now() / 1000) + 3600) })],
    ["null expiry", () => jwt({ exp: null })],
    ["malformed payload", () => "header.not-json.signature"],
    ["malformed token", () => "not-a-jwt"],
    ["expired", () => jwt({ exp: Math.floor(Date.now() / 1000) - 1 })],
    ["inside clock margin", () => jwt({ exp: Math.floor(Date.now() / 1000) + 20 })],
    ["nonfinite expiry", () => `header.${Buffer.from('{"exp":1e309}').toString("base64url")}.signature`],
  ])("refreshes %s rather than treating it as indefinitely valid", async (_name, makeToken) => {
    auth.setTokens(makeToken(), "refresh");
    const result = await refresh.getConnectionAccessToken(auth.getSessionVersion(), new AbortController().signal);
    expect(result).toBe(refreshedToken);
    expect(auth.getRefreshToken()).toBe("rotated");
    expect(paths).toEqual(["/proxy/api/auth/refresh"]);
  });

  it("reuses an unexpired base64url JWT with Unicode payload without rotating", async () => {
    const access = jwt({ exp: Math.floor(Date.now() / 1000) + 3600, name: "Café 🧾" });
    auth.setTokens(access, "refresh");
    const result = await refresh.getConnectionAccessToken(auth.getSessionVersion(), new AbortController().signal);
    expect(result).toBe(access);
    expect(paths).toEqual([]);
    expect(auth.getRefreshToken()).toBe("refresh");
  });

  it("does not accept a successful refresh response whose JWT is already expired", async () => {
    refreshedToken = jwt({ exp: Math.floor(Date.now() / 1000) - 60 });
    auth.setTokens("invalid", "refresh");
    await expect(refresh.getConnectionAccessToken(auth.getSessionVersion(), new AbortController().signal)).rejects.toThrow("unexpired");
    expect(paths).toEqual(["/proxy/api/auth/refresh"]);
    expect(auth.getRefreshToken()).toBe("rotated");
  });

  it("does not issue a refresh after its local lifecycle was already cancelled", async () => {
    auth.setTokens("invalid", "refresh");
    const lifetime = new AbortController();
    lifetime.abort();
    await expect(refresh.getConnectionAccessToken(auth.getSessionVersion(), lifetime.signal)).rejects.toMatchObject({ name: "AbortError" });
    expect(paths).toEqual([]);
  });
});

describe("API base path and authentication endpoint ownership", () => {
  it("uses the normalized prefix for actual HTTP reads and rejects login without attempting refresh", async () => {
    const accounts = await api.default.GET("/api/accounts");
    const login = await api.default.POST("/api/auth/login", { body: { email: "alice@example.test", password: "incorrect" } });
    expect(accounts.response.status).toBe(200);
    expect(login.response.status).toBe(401);
    expect(paths).toEqual(["/proxy/api/accounts", "/proxy/api/auth/login"]);
    expect(auth.getRefreshToken()).toBe("refresh");
  });

  it("lets captured prefixed logout finish after immediate local clearing without replay or replacement-token mutation", async () => {
    responseGate = deferred();
    const request = api.default.POST("/api/auth/logout");
    await vi.waitFor(() => expect(paths).toEqual(["/proxy/api/auth/logout"]));
    auth.clearTokens();
    auth.setTokens("bob-access", "bob-refresh");
    responseGate.resolve();
    expect((await request).response.status).toBe(204);
    expect(auth.getRefreshToken()).toBe("bob-refresh");
    expect(paths).toEqual(["/proxy/api/auth/logout"]);
  });

  it.each([
    ["", "/hubs/entities"],
    ["/gateway///", "/gateway/hubs/entities"],
    ["https://other-api.example.test/", "https://other-api.example.test/hubs/entities"],
    ["https://other-api.example.test/gateway///", "https://other-api.example.test/gateway/hubs/entities"],
  ])("preserves endpoint joining for configured base %s", async (base, expected) => {
    vi.resetModules();
    vi.stubEnv("VITE_API_URL", base);
    const { apiUrl } = await import("./api-config");
    expect(apiUrl("/hubs/entities")).toBe(expected);
  });
});
