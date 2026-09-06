// @vitest-environment node
import { createServer, type Server } from "node:http";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { toast } from "sonner";
vi.mock("sonner", () => ({ toast: { error: vi.fn() } }));

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
let policies: typeof import("./request-error-policy");
let queryClient: ReturnType<
  typeof import("./query-client").createAppQueryClient
>;
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
  vi.clearAllMocks();
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
  policies = await import("./request-error-policy");
  unsubscribe = (await import("./server-error-bus")).addServerErrorListener(
    (status) => errors.push(status),
  );
  auth.setTokens("expired-access", "original-refresh");
  queryClient = (await import("./query-client")).createAppQueryClient();
});
afterEach(async () => {
  queryClient.clear();
  gate?.resolve();
  unsubscribe();
  auth.clearTokens();
  server.closeAllConnections();
  await new Promise<void>((resolve) => server.close(() => resolve()));
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
});

function execute(presentation: "toast" | "local" | "global" = "toast") {
  const policy =
    presentation === "toast"
      ? policies.toastErrorPolicy
      : presentation === "local"
        ? policies.localErrorPolicy
        : undefined;
  return queryClient
    .getMutationCache()
    .build(queryClient, {
      ...policy?.mutation,
      mutationFn: async () => {
        const { data, error } = await client.POST("/api/receipts", {
          ...policy?.request,
          body,
        });
        if (error) throw error;
        return data;
      },
    })
    .execute(undefined);
}

describe("native replay and the actual session mutation-cache presenter", () => {
  it("replays exact bytes once after401, presents503 once and never retries the mutation", async () => {
    await expect(execute()).rejects.toMatchObject({
      status: 503,
      detail: "Optional service unavailable",
    });
    expect(requests.map((request) => request.path)).toEqual([
      "/api/receipts",
      "/api/auth/refresh",
      "/api/receipts",
    ]);
    const business = requests.filter(
      (request) => request.path === "/api/receipts",
    );
    expect(business.map((request) => request.body)).toEqual([
      JSON.stringify(body),
      JSON.stringify(body),
    ]);
    expect(business.map((request) => request.authorization)).toEqual([
      "Bearer expired-access",
      "Bearer renewed-access",
    ]);
    expect(
      requests
        .flatMap((request) => request.headers)
        .filter((name) => /presentation|error-policy/i.test(name)),
    ).toEqual([]);
    expect(toast.error).toHaveBeenCalledExactlyOnceWith(
      "Optional service unavailable",
    );
    expect(errors).toEqual([]);
    expect(auth.getAccessToken()).toBe("renewed-access");
    expect(auth.getRefreshToken()).toBe("renewed-refresh");
  });

  it.each(["local", "global"] as const)(
    "preserves the existing%s ownership control",
    async (presentation) => {
      await expect(execute(presentation)).rejects.toMatchObject({
        status: 503,
      });
      expect(toast.error).not.toHaveBeenCalled();
      expect(errors).toEqual(presentation === "global" ? [503] : []);
      expect(requests).toHaveLength(3);
    },
  );

  it("keeps ordinary successful refresh and result behavior", async () => {
    finalStatus = 200;
    await expect(execute()).resolves.toMatchObject({ id: "receipt" });
    expect(toast.error).not.toHaveBeenCalled();
    expect(errors).toEqual([]);
    expect(requests).toHaveLength(3);
  });

  it("suppresses the old cache and native response after a session replacement", async () => {
    auth.setTokens("current-access", "current-refresh");
    queryClient = (await import("./query-client")).createAppQueryClient();
    gate = deferred();
    const result = execute().catch((error: unknown) => error);
    await vi.waitFor(() => expect(requests).toHaveLength(1));
    auth.setTokens("bob-access", "bob-refresh");
    gate.resolve();
    expect(await result).toMatchObject({ name: "AbortError" });
    expect(errors).toEqual([]);
    expect(toast.error).not.toHaveBeenCalled();
    expect(auth.getAccessToken()).toBe("bob-access");
    expect(auth.getRefreshToken()).toBe("bob-refresh");
  });
});
