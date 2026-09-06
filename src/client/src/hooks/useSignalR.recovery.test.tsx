import { type ReactNode } from "react";
import { act, cleanup, render, renderHook, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { HttpResponse, http } from "msw";
import { setupServer } from "msw/node";
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { clearTokens, getAccessToken, getRefreshToken, setTokens } from "@/lib/auth";
import { getConnectionId, setConnectionId } from "@/lib/signalr-connection";

// This SDK boundary fake enforces native start states and creates a distinct
// connection for every builder, including StrictMode's discarded first effect.
const sdk = vi.hoisted(() => {
  type Callback = (...args: unknown[]) => void;
  type Options = { accessTokenFactory: () => string | Promise<string> };
  class Connection {
    state = "Disconnected";
    connectionId: string | null = null;
    url = "";
    options!: Options;
    handlers = new Map<string, Callback>();
    reconnecting: Callback = () => {};
    reconnected: Callback = () => {};
    closed: Callback = () => {};
    startsInFlight = 0;
    maxStartsInFlight = 0;
    invalidStarts = 0;
    startCount = 0;
    afterConnect: () => void = () => {};
    startPlan: () => Promise<void> = async () => {};
    start = vi.fn(async () => {
      if (this.state !== "Disconnected") {
        this.invalidStarts++;
        throw new Error(`Cannot start from ${this.state}`);
      }
      this.state = "Connecting";
      this.startCount++;
      this.startsInFlight++;
      this.maxStartsInFlight = Math.max(this.maxStartsInFlight, this.startsInFlight);
      try {
        await this.options.accessTokenFactory();
        if (this.state !== "Connecting") throw new Error("Start interrupted during token acquisition");
        await this.startPlan();
        // stop() while negotiation is pending must prevent native publication.
        if (this.state !== "Connecting") throw new Error("Start interrupted by stop");
        this.state = "Connected";
        this.connectionId = `connection-${connections.indexOf(this)}-${this.startCount}`;
        this.afterConnect();
      } catch (error) {
        this.state = "Disconnected";
        throw error;
      } finally {
        this.startsInFlight--;
      }
    });
    stop = vi.fn(async () => {
      this.state = "Disconnected";
      this.connectionId = null;
    });
    on(name: string, callback: Callback) { this.handlers.set(name, callback); }
    off(name: string, callback: Callback) {
      if (this.handlers.get(name) === callback) this.handlers.delete(name);
    }
    onreconnecting(callback: Callback) { this.reconnecting = callback; }
    onreconnected(callback: Callback) { this.reconnected = callback; }
    onclose(callback: Callback) { this.closed = callback; }
    disconnect() {
      this.state = "Disconnected";
      this.connectionId = null;
      this.closed(new Error("Automatic reconnect exhausted"));
    }
    beginReconnect() { this.state = "Reconnecting"; this.reconnecting(); }
    finishReconnect(id: string) {
      this.state = "Connected";
      this.connectionId = id;
      this.reconnected(id);
    }
  }
  const connections: Connection[] = [];
  const plans: Array<() => Promise<void>> = [];
  class Builder {
    connection = new Connection();
    withUrl(url: string, options: Options) {
      this.connection.url = url;
      this.connection.options = options;
      return this;
    }
    withAutomaticReconnect() { return this; }
    configureLogging() { return this; }
    build() {
      this.connection.startPlan = plans.shift() ?? (async () => {});
      connections.push(this.connection);
      return this.connection;
    }
  }
  return { connections, plans, Builder };
});
vi.mock("@microsoft/signalr", () => ({
  HubConnectionBuilder: sdk.Builder,
  HubConnectionState: { Disconnected: "Disconnected", Connecting: "Connecting", Connected: "Connected", Disconnecting: "Disconnecting", Reconnecting: "Reconnecting" },
  LogLevel: { Debug: 1, None: 6 },
}));
vi.mock("sonner", () => ({ toast: { info: vi.fn(), error: vi.fn(), success: vi.fn() } }));

const apiBase = "https://api.receipts.test/gateway";
const server = setupServer();
let useSignalR: typeof import("./useSignalR").useSignalR;
let dashboard: typeof import("./useDashboard");
let api: typeof import("@/lib/api-client");
let queryClient: QueryClient;
let refreshRequests = 0;
const freshToken = () => token(Math.floor(Date.now() / 1000) + 3600);
function token(exp: number) {
  return `eyJhbGciOiJub25lIn0.${btoa(JSON.stringify({ userId: "alice", email: "alice@example.test", roles: ["Admin"], exp }))}.signature`;
}
function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => { resolve = done; });
  return { promise, resolve };
}
function Wrapper({ children }: { children: ReactNode }) {
  return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}
async function flush() { await act(async () => {}); }

beforeAll(async () => {
  vi.stubEnv("VITE_API_URL", apiBase);
  ({ useSignalR } = await import("./useSignalR"));
  dashboard = await import("./useDashboard");
  api = await import("@/lib/api-client");
  server.listen({ onUnhandledRequest: "error" });
});
beforeEach(() => {
  sdk.connections.length = 0;
  sdk.plans.length = 0;
  queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  refreshRequests = 0;
  setConnectionId(null);
  setTokens(freshToken(), "alice-refresh");
  server.use(http.post(`${apiBase}/api/auth/refresh`, () => {
    refreshRequests++;
    return HttpResponse.json({ accessToken: freshToken(), refreshToken: "alice-rotated" });
  }));
});
afterEach(() => {
  cleanup();
  queryClient.clear();
  clearTokens();
  setConnectionId(null);
  server.resetHandlers();
  vi.useRealTimers();
});
afterAll(() => { server.close(); vi.unstubAllEnvs(); });

describe("realtime recovery with native lifecycle state", () => {
  it("retries an initial outage without remounting the owner", async () => {
    vi.useFakeTimers();
    let available = false;
    sdk.plans.push(async () => { if (!available) throw new Error("offline"); });
    const { result } = renderHook(() => useSignalR(true), { wrapper: Wrapper });
    await flush();
    expect(result.current.connectionState).toBe("disconnected");
    available = true;
    await act(async () => { await vi.advanceTimersByTimeAsync(1000); });
    expect(sdk.connections[0].start).toHaveBeenCalledTimes(2);
    expect(result.current.connectionState).toBe("connected");
    expect(getConnectionId()).toBe(sdk.connections[0].connectionId);
    expect(sdk.connections[0].invalidStarts).toBe(0);
  });

  it("uses the configured separate API origin and path prefix for the hub", async () => {
    renderHook(() => useSignalR(true), { wrapper: Wrapper });
    await flush();
    expect(sdk.connections[0].url).toBe(`${apiBase}/hubs/entities`);
  });

  it("refreshes expired access credentials for the hub without an HTTP business request", async () => {
    setTokens(token(Math.floor(Date.now() / 1000) - 60), "alice-refresh");
    renderHook(() => useSignalR(true), { wrapper: Wrapper });
    let returned = "";
    await act(async () => { returned = await sdk.connections[0].options.accessTokenFactory(); });
    expect(refreshRequests).toBe(1);
    expect(returned).toBe(getAccessToken());
    expect(JSON.parse(atob(returned.split(".")[1])).exp).toBeGreaterThan(Date.now() / 1000);
  });

  it("refreshes actual rendered totals and the infinitely fresh earliest year after reconnection", async () => {
    let total = 10;
    let year = 2026;
    server.use(
      http.get(`${apiBase}/api/dashboard/summary`, () => HttpResponse.json({ totalReceipts: 1, totalSpent: total, averageTripAmount: total })),
      http.get(`${apiBase}/api/dashboard/earliest-receipt-year`, () => HttpResponse.json({ year })),
    );
    function ReadProjections() {
      useSignalR(true);
      const summary = dashboard.useDashboardSummary({});
      const earliest = dashboard.useDashboardEarliestReceiptYear();
      return <><output data-testid="total">{summary.data?.totalSpent}</output><output data-testid="year">{earliest.data?.year}</output></>;
    }
    render(<ReadProjections />, { wrapper: Wrapper });
    await waitFor(() => expect(screen.getByTestId("total")).toHaveTextContent("10"));
    expect(screen.getByTestId("year")).toHaveTextContent("2026");
    act(() => { sdk.connections[0].beginReconnect(); });
    total = 25;
    year = 2020;
    act(() => { sdk.connections[0].finishReconnect("recovered-id"); });
    await waitFor(() => expect(screen.getByTestId("total")).toHaveTextContent("25"));
    expect(screen.getByTestId("year")).toHaveTextContent("2020");
  });

  it("retains947 protection when a disposed start settles after its replacement", async () => {
    const oldStart = deferred();
    sdk.plans.push(() => oldStart.promise);
    const oldOwner = renderHook(() => useSignalR(true), { wrapper: Wrapper });
    await flush();
    oldOwner.unmount();
    renderHook(() => useSignalR(true), { wrapper: Wrapper });
    await flush();
    expect(sdk.connections).toHaveLength(2);
    expect(sdk.connections[0].stop).toHaveBeenCalled();
    const currentId = sdk.connections[1].connectionId;
    expect(getConnectionId()).toBe(currentId);
    await act(async () => { oldStart.resolve(); });
    expect(getConnectionId()).toBe(currentId);
    expect(sdk.connections[1].state).toBe("Connected");
  });
});


describe("recovery ownership and scheduling", () => {
  it("continues capped retry delays and resets the delay after successful recovery", async () => {
    vi.useFakeTimers();
    let available = false;
    sdk.plans.push(async () => { if (!available) throw new Error("offline"); });
    const { result } = renderHook(() => useSignalR(true), { wrapper: Wrapper });
    await flush();
    const connection = sdk.connections[0];
    for (const delay of [1000, 2000, 4000, 8000, 16000, 30000, 30000]) {
      const before = connection.start.mock.calls.length;
      await act(async () => { await vi.advanceTimersByTimeAsync(delay - 1); });
      expect(connection.start).toHaveBeenCalledTimes(before);
      await act(async () => { await vi.advanceTimersByTimeAsync(1); });
      expect(connection.start).toHaveBeenCalledTimes(before + 1);
    }
    available = true;
    await act(async () => { await vi.advanceTimersByTimeAsync(30000); });
    expect(result.current.connectionState).toBe("connected");
    act(() => connection.disconnect());
    const beforeRestart = connection.start.mock.calls.length;
    await act(async () => { await vi.advanceTimersByTimeAsync(1000); });
    expect(connection.start).toHaveBeenCalledTimes(beforeRestart + 1);
    expect(result.current.connectionState).toBe("connected");
    expect(connection.maxStartsInFlight).toBe(1);
    expect(connection.invalidStarts).toBe(0);
  });

  it("does not manually start during native reconnecting and clears the unusable origin ID", async () => {
    vi.useFakeTimers();
    const { result } = renderHook(() => useSignalR(true), { wrapper: Wrapper });
    await flush();
    const connection = sdk.connections[0];
    expect(getConnectionId()).not.toBeNull();
    act(() => connection.beginReconnect());
    expect(getConnectionId()).toBeNull();
    await act(async () => { await vi.advanceTimersByTimeAsync(60000); });
    expect(connection.start).toHaveBeenCalledTimes(1);
    expect(result.current.connectionState).toBe("reconnecting");
    act(() => connection.finishReconnect("native-recovered"));
    expect(getConnectionId()).toBe("native-recovered");
    expect(result.current.connectionState).toBe("connected");
  });

  it("does not overlap a pending manual start with a close-triggered retry", async () => {
    vi.useFakeTimers();
    const gate = deferred();
    sdk.plans.push(() => gate.promise);
    renderHook(() => useSignalR(true), { wrapper: Wrapper });
    await flush();
    const connection = sdk.connections[0];
    act(() => connection.disconnect());
    await act(async () => { await vi.advanceTimersByTimeAsync(60000); });
    expect(connection.start).toHaveBeenCalledTimes(1);
    connection.startPlan = async () => {};
    await act(async () => { gate.resolve(); });
    await act(async () => { await vi.advanceTimersByTimeAsync(1000); });
    expect(connection.start).toHaveBeenCalledTimes(2);
    expect(connection.maxStartsInFlight).toBe(1);
    expect(connection.invalidStarts).toBe(0);
  });

  it("does not publish connected when native reconnection begins before the start continuation", async () => {
    sdk.plans.push(async () => {
      sdk.connections[0].afterConnect = () => sdk.connections[0].beginReconnect();
    });
    const { result } = renderHook(() => useSignalR(true), { wrapper: Wrapper });
    await flush();
    expect(sdk.connections[0].state).toBe("Reconnecting");
    expect(result.current.connectionState).toBe("reconnecting");
    expect(getConnectionId()).toBeNull();
  });

  it("cancels retry ownership on logout and ignores late old callbacks after replacement", async () => {
    vi.useFakeTimers();
    sdk.plans.push(async () => { throw new Error("offline"); });
    const oldOwner = renderHook(() => useSignalR(true), { wrapper: Wrapper });
    await flush();
    const old = sdk.connections[0];
    const queuedEntity = old.handlers.get("EntityChanged")!;
    act(() => { clearTokens(); setTokens(freshToken(), "bob-refresh"); });
    oldOwner.unmount();
    renderHook(() => useSignalR(true), { wrapper: Wrapper });
    await flush();
    const replacementId = getConnectionId();
    queryClient.setQueryData(["receipts", "all"], []);
    act(() => {
      old.finishReconnect("old-id");
      old.disconnect();
      queuedEntity({ entityType: "receipt", changeType: "updated", id: "old-receipt" });
    });
    await act(async () => { await vi.advanceTimersByTimeAsync(60000); });
    expect(old.start).toHaveBeenCalledTimes(1);
    expect(getConnectionId()).toBe(replacementId);
    expect(queryClient.getQueryState(["receipts", "all"])?.isInvalidated).toBe(false);
  });

  it("constructs separate native connections for StrictMode setup cleanup and setup", async () => {
    renderHook(() => useSignalR(true), { wrapper: Wrapper, reactStrictMode: true });
    await flush();
    expect(sdk.connections).toHaveLength(2);
    expect(sdk.connections[0].stop).toHaveBeenCalled();
    expect(sdk.connections[0].state).toBe("Disconnected");
    expect(sdk.connections[1].state).toBe("Connected");
    expect(getConnectionId()).toBe(sdk.connections[1].connectionId);
  });

  it("does not clear credentials when an expired-token refresh fails during an outage", async () => {
    const expired = token(Math.floor(Date.now() / 1000) - 60);
    setTokens(expired, "alice-refresh");
    server.use(http.post(`${apiBase}/api/auth/refresh`, () => {
      refreshRequests++;
      return HttpResponse.json({ detail: "Unavailable" }, { status: 503 });
    }));
    const { result } = renderHook(() => useSignalR(true), { wrapper: Wrapper });
    await expect(sdk.connections[0].options.accessTokenFactory()).rejects.toThrow();
    await flush();
    expect(refreshRequests).toBe(1);
    expect(getAccessToken()).toBe(expired);
    expect(getRefreshToken()).toBe("alice-refresh");
    expect(result.current.connectionState).toBe("disconnected");
  });
});


describe("one refresh owner for HTTP and realtime", () => {
  it("cancels only a disposed hub waiter while an HTTP401 still completes the shared refresh", async () => {
    const refreshGate = deferred();
    let businessCalls = 0;
    const expired = token(Math.floor(Date.now() / 1000) - 60);
    setTokens(expired, "alice-refresh");
    server.use(
      http.post(`${apiBase}/api/auth/refresh`, async () => {
        refreshRequests++;
        await refreshGate.promise;
        return HttpResponse.json({ accessToken: freshToken(), refreshToken: "shared-rotated" });
      }),
      http.get(`${apiBase}/api/accounts`, ({ request }) => {
        businessCalls++;
        return request.headers.get("Authorization") === `Bearer ${expired}`
          ? HttpResponse.json({ status: 401 }, { status: 401 })
          : HttpResponse.json({ data: [], total: 0, offset: 0, limit: 50 });
      }),
    );
    const owner = renderHook(() => useSignalR(true), { wrapper: Wrapper });
    const tokenWait = Promise.resolve(sdk.connections[0].options.accessTokenFactory());
    // Observe rejection before disposal so the test never leaks a rejected waiter.
    const cancelled = tokenWait.then((value) => ({ value, error: null }), (error: unknown) => ({ value: null, error }));
    const request = api.default.GET("/api/accounts");
    try {
      await waitFor(() => expect(refreshRequests).toBe(1));
      await waitFor(() => expect(businessCalls).toBe(1));
      owner.unmount();
      await expect(cancelled).resolves.toMatchObject({ error: { name: "AbortError" } });
      expect(getRefreshToken()).toBe("alice-refresh");
      refreshGate.resolve();
      const response = await request;
      expect(response.response.status).toBe(200);
      expect(response.data?.total).toBe(0);
      expect(refreshRequests).toBe(1);
      expect(businessCalls).toBe(2);
      expect(getRefreshToken()).toBe("shared-rotated");
      expect(getConnectionId()).toBeNull();
    } finally {
      refreshGate.resolve();
      await request.catch(() => {});
    }
  });

  it("does not install an old refresh response or return Alice's token after replacement login", async () => {
    const refreshGate = deferred();
    setTokens(token(Math.floor(Date.now() / 1000) - 60), "alice-refresh");
    server.use(http.post(`${apiBase}/api/auth/refresh`, async () => {
      refreshRequests++;
      await refreshGate.promise;
      return HttpResponse.json({ accessToken: freshToken(), refreshToken: "alice-late" });
    }));
    const owner = renderHook(() => useSignalR(true), { wrapper: Wrapper });
    const wait = Promise.resolve(sdk.connections[0].options.accessTokenFactory());
    const cancelled = wait.then((value) => ({ value, error: null }), (error: unknown) => ({ value: null, error }));
    try {
      await waitFor(() => expect(refreshRequests).toBe(1));
      const bob = token(Math.floor(Date.now() / 1000) + 7200);
      act(() => { setTokens(bob, "bob-refresh"); });
      await expect(cancelled).resolves.toMatchObject({ error: { name: "AbortError" } });
      await act(async () => { refreshGate.resolve(); });
      expect(getAccessToken()).toBe(bob);
      expect(getRefreshToken()).toBe("bob-refresh");
      expect(getConnectionId()).toBeNull();
    } finally {
      refreshGate.resolve();
      owner.unmount();
    }
  });
});


describe("first successful connection catch-up", () => {
  it("repairs reads cached before registration and publishes the origin ID before HTTP repair", async () => {
    const startGate = deferred();
    sdk.plans.push(() => startGate.promise);
    queryClient.setDefaultOptions({ queries: { retry: false, staleTime: Infinity } });
    queryClient.setQueryData(["dashboard", "summary", undefined, undefined], { totalSpent: 10 });
    queryClient.setQueryData(["dashboard", "earliest-receipt-year"], { year: 2026 });
    queryClient.setQueryData(["users", "list"], { data: [] });
    queryClient.setQueryData(["similarItems", "bread"], []);
    const origins: Array<string | null> = [];
    server.use(
      http.get(`${apiBase}/api/dashboard/summary`, ({ request }) => {
        origins.push(request.headers.get("X-SignalR-Connection-Id"));
        return HttpResponse.json({ totalReceipts: 1, totalSpent: 25, averageTripAmount: 25 });
      }),
      http.get(`${apiBase}/api/dashboard/earliest-receipt-year`, ({ request }) => {
        origins.push(request.headers.get("X-SignalR-Connection-Id"));
        return HttpResponse.json({ year: 2020 });
      }),
    );
    function ReadProjections() {
      useSignalR(true);
      const summary = dashboard.useDashboardSummary({});
      const earliest = dashboard.useDashboardEarliestReceiptYear();
      return <><output data-testid="catchup-total">{summary.data?.totalSpent}</output><output data-testid="catchup-year">{earliest.data?.year}</output></>;
    }
    render(<ReadProjections />, { wrapper: Wrapper });
    expect(screen.getByTestId("catchup-total")).toHaveTextContent("10");
    expect(origins).toEqual([]);
    await act(async () => { startGate.resolve(); });
    await waitFor(() => expect(screen.getByTestId("catchup-total")).toHaveTextContent("25"));
    await waitFor(() => expect(screen.getByTestId("catchup-year")).toHaveTextContent("2020"));
    expect(origins).toEqual([getConnectionId(), getConnectionId()]);
    expect(getConnectionId()).not.toBeNull();
    expect(queryClient.getQueryState(["similarItems", "bread"])?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(["users", "list"])?.isInvalidated).toBe(false);
  });
});

it("replaces an in-flight initial earliest-year read instead of treating its stale response as catch-up", async () => {
  const startGate = deferred();
  const firstResponse = deferred();
  const firstReadSeen = deferred();
  sdk.plans.push(() => startGate.promise);
  let year = 2026;
  let reads = 0;
  server.use(http.get(`${apiBase}/api/dashboard/earliest-receipt-year`, async () => {
    const snapshot = year;
    reads++;
    if (reads === 1) {
      firstReadSeen.resolve();
      await firstResponse.promise;
    }
    return HttpResponse.json({ year: snapshot });
  }));
  function InitialRead() {
    useSignalR(true);
    const earliest = dashboard.useDashboardEarliestReceiptYear();
    return <output data-testid="initial-year">{earliest.data?.year ?? "loading"}</output>;
  }
  render(<InitialRead />, { wrapper: Wrapper });
  try {
    await act(async () => { await firstReadSeen.promise; });
    expect(screen.getByTestId("initial-year")).toHaveTextContent("loading");
    year = 2020;
    await act(async () => { startGate.resolve(); });
    await act(async () => { firstResponse.resolve(); });
    await waitFor(() => expect(screen.getByTestId("initial-year")).toHaveTextContent("2020"));
    expect(reads).toBe(2);
  } finally {
    startGate.resolve();
    firstResponse.resolve();
  }
});

it("does not invalidate the old cache if its session changes during initial-read cancellation", async () => {
  const startGate = deferred();
  const firstResponse = deferred();
  const firstReadSeen = deferred();
  sdk.plans.push(() => startGate.promise);
  let reads = 0;
  let armed = false;
  queryClient.setQueryData(["receipts", "all"], []);
  server.use(http.get(`${apiBase}/api/dashboard/earliest-receipt-year`, async () => {
    reads++;
    firstReadSeen.resolve();
    await firstResponse.promise;
    return HttpResponse.json({ year: 2026 });
  }));
  // Observe actual Query cancellation, not a mocked QueryClient promise. A session
  // replacement at this point must stop catch-up's continuation after its await.
  const unsubscribe = queryClient.getQueryCache().subscribe((event) => {
    if (armed && event.type === "updated" && event.query.queryKey[0] === "dashboard" &&
      event.query.state.data === undefined && event.query.state.fetchStatus === "idle") {
      armed = false;
      setTokens(freshToken(), "bob-refresh");
    }
  });
  function PendingRead() {
    useSignalR(true);
    dashboard.useDashboardEarliestReceiptYear();
    return null;
  }
  render(<PendingRead />, { wrapper: Wrapper });
  try {
    await act(async () => { await firstReadSeen.promise; });
    armed = true;
    await act(async () => { startGate.resolve(); });
    expect(getRefreshToken()).toBe("bob-refresh");
    await act(async () => { firstResponse.resolve(); });
    expect(queryClient.getQueryState(["receipts", "all"])?.isInvalidated).toBe(false);
    expect(reads).toBe(1);
    expect(getConnectionId()).toBeNull();
  } finally {
    unsubscribe();
    startGate.resolve();
    firstResponse.resolve();
  }
});
