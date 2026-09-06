// @vitest-environment node
import { createServer, type Server, type ServerResponse } from "node:http";
import { afterEach, beforeEach, expect, it, vi } from "vitest";

function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}
const binary = new Uint8Array([0, 83, 81, 76, 105, 116, 101, 0, 255, 128]);
let server: Server;
let client: typeof import("./api-client").default;
let auth: typeof import("./auth");
let timeout: typeof import("./request-timeout");
let policy: typeof import("./request-error-policy");
let requests: Array<{
  path: string;
  body: Buffer;
  contentType?: string;
  authorization?: string;
  connection?: string;
}>;
let gates: Array<ReturnType<typeof deferred>>;
let bodyStarted: ReturnType<typeof deferred>;
let bodyGate: ReturnType<typeof deferred> | undefined;
let unauthorizedGate: ReturnType<typeof deferred> | undefined;
let finalStatus: number;
let clock: number;
let deadlines: Array<{
  at: number;
  milliseconds: number;
  controller: AbortController;
}>;
let globalError: ReturnType<typeof vi.fn<(status: number) => void>>;
let unsubscribe: () => void;

function json(response: ServerResponse, status: number, body: unknown) {
  response.writeHead(status, { "Content-Type": "application/json" });
  response.end(JSON.stringify(body));
}
function advance(milliseconds: number) {
  clock += milliseconds;
  for (const entry of deadlines)
    if (entry.at <= clock)
      entry.controller.abort(
        new DOMException(
          "The operation was aborted due to timeout",
          "TimeoutError",
        ),
      );
}
beforeEach(async () => {
  vi.resetModules();
  localStorage.clear();
  vi.stubGlobal(
    "window",
    Object.assign(new EventTarget(), {
      location: { href: "/settings/backup" },
      sessionStorage: localStorage,
    }),
  );
  requests = [];
  gates = [];
  finalStatus = 200;
  clock = 0;
  deadlines = [];
  bodyStarted = deferred();
  bodyGate = undefined;
  unauthorizedGate = undefined;
  // Advance only timeout signals; fetch, sockets and response streams remain native.
  vi.spyOn(AbortSignal, "timeout").mockImplementation((milliseconds) => {
    const controller = new AbortController();
    deadlines.push({ at: clock + milliseconds, milliseconds, controller });
    return controller.signal;
  });
  server = createServer(async (request, response) => {
    const chunks: Buffer[] = [];
    for await (const chunk of request) chunks.push(Buffer.from(chunk));
    requests.push({
      path: request.url!,
      body: Buffer.concat(chunks),
      contentType: request.headers["content-type"],
      authorization: request.headers.authorization,
      connection: request.headers["x-signalr-connection-id"] as
        | string
        | undefined,
    });
    if (request.url?.endsWith("/auth/refresh")) {
      json(response, 200, {
        accessToken: "renewed-access",
        refreshToken: "renewed-refresh",
      });
      return;
    }
    if (request.headers.authorization === "Bearer expired-access") {
      await unauthorizedGate?.promise;
      json(response, 401, { status: 401 });
      return;
    }
    if (finalStatus !== 200) {
      json(response, finalStatus, {
        status: finalStatus,
        detail: "Transfer rejected",
      });
      return;
    }
    const body = request.url?.endsWith("/export")
      ? binary
      : Buffer.from(JSON.stringify({ totalCreated: 2, totalUpdated: 3 }));
    response.writeHead(200, {
      "Content-Type": request.url?.endsWith("/export")
        ? "application/octet-stream"
        : "application/json",
      "Content-Disposition": 'attachment; filename="exact.sqlite"',
    });
    response.write(body.slice(0, 1));
    bodyStarted.resolve();
    await bodyGate?.promise;
    response.end(body.slice(1));
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  if (!address || typeof address === "string")
    throw new Error("TCP server required");
  vi.stubEnv("VITE_API_URL", `http://127.0.0.1:${address.port}/gateway`);
  ({ default: client } = await import("./api-client"));
  auth = await import("./auth");
  timeout = await import("./request-timeout");
  policy = await import("./request-error-policy");
  globalError = vi.fn();
  unsubscribe = (await import("./server-error-bus")).addServerErrorListener(
    globalError,
  );
  (await import("./signalr-connection")).setConnectionId("current-connection");
  auth.setTokens("expired-access", "valid-refresh");
});
afterEach(async () => {
  gates.forEach((gate) => gate.resolve());
  bodyGate?.resolve();
  unauthorizedGate?.resolve();
  unsubscribe?.();
  auth?.clearTokens();
  server.closeAllConnections();
  await new Promise<void>((resolve, reject) =>
    server.close((error) => (error ? reject(error) : resolve())),
  );
  vi.restoreAllMocks();
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
});
function transfer(operation: "export" | "import") {
  const middleware = [
    ...policy.localErrorPolicy.request.middleware,
    timeout.requestTimeout(300_000),
  ];
  if (operation === "export")
    return client.POST("/api/backup/export", { middleware, parseAs: "blob" });
  const body = new FormData();
  body.append(
    "file",
    new File([binary], "exact.sqlite", { type: "application/octet-stream" }),
  );
  return client.POST("/api/backup/import", {
    middleware,
    body: {},
    bodySerializer: () => body,
  });
}

it.each(["export", "import"] as const)(
  "replays native %s once after refresh, retaining exact file bytes and request identity",
  async (operation) => {
    const result = await transfer(operation);
    expect(result.error).toBeUndefined();
    const business = requests.filter(
      (request) => !request.path.endsWith("/auth/refresh"),
    );
    expect(business).toHaveLength(2);
    expect(
      requests.filter((request) => request.path.endsWith("/auth/refresh")),
    ).toHaveLength(1);
    expect(business.map((request) => request.authorization)).toEqual([
      "Bearer expired-access",
      "Bearer renewed-access",
    ]);
    expect(business.map((request) => request.connection)).toEqual([
      "current-connection",
      "current-connection",
    ]);
    expect(business[1].body).toEqual(business[0].body);
    expect(business[1].contentType).toBe(business[0].contentType);
    expect(
      business.every(
        (request) => request.path === `/gateway/api/backup/${operation}`,
      ),
    ).toBe(true);
    if (operation === "import") {
      expect(business[0].contentType).toMatch(
        /^multipart\/form-data; boundary=/,
      );
      expect(business[0].body.includes(Buffer.from(binary))).toBe(true);
      expect(business[0].body.toString()).toContain('filename="exact.sqlite"');
      expect(result.data).toEqual({ totalCreated: 2, totalUpdated: 3 });
    } else {
      expect(new Uint8Array(await (result.data as Blob).arrayBuffer())).toEqual(
        binary,
      );
      expect(result.response.headers.get("Content-Disposition")).toContain(
        "exact.sqlite",
      );
    }
    expect(
      deadlines.filter((entry) => entry.milliseconds === 300_000),
    ).toHaveLength(1);
  },
);

it.each(["export", "import"] as const)(
  "keeps the final replayed %s failure local",
  async (operation) => {
    finalStatus = 503;
    const result = await transfer(operation);
    expect(result.error).toMatchObject({
      status: 503,
      detail: "Transfer rejected",
    });
    expect(globalError).not.toHaveBeenCalled();
    expect(requests).toHaveLength(3);
  },
);

it.each(["export", "import"] as const)(
  "allows native %s body consumption past30s but aborts at its single five-minute deadline",
  async (operation) => {
    auth.setTokens("current-access", "valid-refresh");
    bodyGate = deferred();
    const pending = transfer(operation).catch((error: unknown) => error);
    await bodyStarted.promise;
    advance(30_001);
    expect(
      deadlines.find((entry) => entry.milliseconds === 300_000)?.controller
        .signal.aborted,
    ).toBe(false);
    advance(269_999);
    expect(await pending).toMatchObject({ name: "TimeoutError" });
  },
);

it.each(["export", "import"] as const)(
  "cancels native %s body consumption when its session changes",
  async (operation) => {
    auth.setTokens("current-access", "valid-refresh");
    bodyGate = deferred();
    const pending = transfer(operation).catch((error: unknown) => error);
    await bodyStarted.promise;
    auth.clearTokens();
    auth.setTokens("Bob-access", "Bob-refresh");
    expect(await pending).toMatchObject({ name: "AbortError" });
    expect(auth.getAccessToken()).toBe("Bob-access");
  },
);

it("retains the ordinary30s deadline", async () => {
  auth.setTokens("current-access", "valid-refresh");
  bodyGate = deferred();
  const pending = client.GET("/api/cards").catch((error: unknown) => error);
  await bodyStarted.promise;
  advance(30_000);
  expect(await pending).toMatchObject({ name: "TimeoutError" });
  expect(deadlines.map((entry) => entry.milliseconds)).toEqual([30_000]);
});

it("does not restart the import deadline when a late401 refreshes and replays", async () => {
  unauthorizedGate = deferred();
  bodyGate = deferred();
  const pending = transfer("import").catch((error: unknown) => error);
  await vi.waitFor(() => expect(requests).toHaveLength(1));
  advance(290_000);
  unauthorizedGate.resolve();
  await bodyStarted.promise;
  advance(10_000);
  expect(await pending).toMatchObject({ name: "TimeoutError" });
  expect(
    deadlines.filter((entry) => entry.milliseconds === 300_000),
  ).toHaveLength(1);
});

it.each([0, -1, 0.5, Number.NaN, Number.POSITIVE_INFINITY, 2_147_483_648])(
  "rejects unsupported request timeout %s before dispatch",
  (milliseconds) => {
    expect(() => timeout.requestTimeout(milliseconds)).toThrow(RangeError);
    expect(requests).toEqual([]);
  },
);
