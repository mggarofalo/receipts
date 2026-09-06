// @vitest-environment node
import { createServer, type Server } from "node:http";
import { toast } from "sonner";
vi.mock("sonner", () => ({ toast: { error: vi.fn() } }));
let server: Server;
let requests: { url: string; authorization?: string }[];
let finalStatus: number;
let client: typeof import("./api-client").default;
let auth: typeof import("./auth");
let localPolicy: typeof import("./request-error-policy").localErrorPolicy;
let queryClient: ReturnType<
  typeof import("./query-client").createAppQueryClient
>;
let errors: number[];
let unsubscribe: () => void;
beforeEach(async () => {
  vi.resetModules();
  vi.clearAllMocks();
  localStorage.clear();
  vi.stubGlobal(
    "window",
    Object.assign(new EventTarget(), {
      location: { href: "/receipts/new" },
      sessionStorage: localStorage,
    }),
  );
  requests = [];
  errors = [];
  finalStatus = 503;
  server = createServer((request, response) => {
    requests.push({
      url: request.url!,
      authorization: request.headers.authorization,
    });
    const refresh = request.url === "/api/auth/refresh";
    const status = refresh
      ? 200
      : request.headers.authorization === "Bearer expired"
        ? 401
        : finalStatus;
    const body = refresh
      ? { accessToken: "renewed", refreshToken: "renewed-refresh" }
      : status === 200
        ? { locations: ["Café market"] }
        : { status, detail: "Location hints unavailable" };
    response.writeHead(status, { "Content-Type": "application/json" });
    response.end(JSON.stringify(body));
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  if (!address || typeof address === "string")
    throw new Error("TCP listener required");
  vi.stubEnv("VITE_API_URL", `http://127.0.0.1:${address.port}`);
  ({ default: client } = await import("./api-client"));
  auth = await import("./auth");
  ({ localErrorPolicy: localPolicy } = await import("./request-error-policy"));
  auth.setTokens("expired", "refresh");
  queryClient = (await import("./query-client")).createAppQueryClient();
  unsubscribe = (await import("./server-error-bus")).addServerErrorListener(
    (status) => errors.push(status),
  );
});
afterEach(async () => {
  queryClient.clear();
  unsubscribe();
  auth.clearTokens();
  server.closeAllConnections();
  await new Promise<void>((resolve) => server.close(() => resolve()));
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
});
function fetchLocations() {
  return queryClient.fetchQuery({
    ...localPolicy.query,
    queryKey: ["receipts", "locations", "Café & milk"],
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET("/api/receipts/locations", {
        ...localPolicy.request,
        params: { query: { q: "Café & milk", limit: 20 } },
        signal,
      });
      if (error) throw error;
      return data?.locations;
    },
  });
}
it("preserves the local query owner through native401 refresh and final503, then allows one manual retry", async () => {
  await expect(fetchLocations()).rejects.toMatchObject({
    status: 503,
    detail: "Location hints unavailable",
  });
  expect(requests).toHaveLength(3);
  expect(requests[1].url).toBe("/api/auth/refresh");
  expect(requests[0].url).toBe(requests[2].url);
  expect(
    new URL(requests[2].url, "http://localhost").searchParams.get("q"),
  ).toBe("Café & milk");
  expect(requests.map((request) => request.authorization)).toEqual([
    "Bearer expired",
    undefined,
    "Bearer renewed",
  ]);
  expect(errors).toEqual([]);
  expect(toast.error).not.toHaveBeenCalled();
  finalStatus = 200;
  await expect(fetchLocations()).resolves.toEqual(["Café market"]);
  expect(requests).toHaveLength(4);
  expect(errors).toEqual([]);
  expect(toast.error).not.toHaveBeenCalled();
});
it("returns successful native replay data to the same query without treating401 as an empty hint list", async () => {
  finalStatus = 200;
  await expect(fetchLocations()).resolves.toEqual(["Café market"]);
  expect(requests).toHaveLength(3);
  expect(
    queryClient.getQueryData(["receipts", "locations", "Café & milk"]),
  ).toEqual(["Café market"]);
  expect(errors).toEqual([]);
  expect(toast.error).not.toHaveBeenCalled();
});
