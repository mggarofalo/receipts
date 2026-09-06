import { type ReactNode } from "react";
import { act, cleanup, renderHook, waitFor } from "@testing-library/react";
import { QueryClientProvider } from "@tanstack/react-query";
import { http, HttpResponse } from "msw";
import { clearTokens, setTokens } from "@/lib/auth";
import { createAppQueryClient } from "@/lib/query-client";
import { server } from "@/test/msw/server";
import { useDashboardEarliestReceiptYear } from "./useDashboard";
import { useSignalR } from "./useSignalR";

const sdk = vi.hoisted(() => ({
  state: "Disconnected",
  connectionId: "current-connection",
  start: vi.fn(),
  stop: vi.fn(),
  on: vi.fn(),
  off: vi.fn(),
  onreconnecting: vi.fn(),
  onreconnected: vi.fn(),
  onclose: vi.fn(),
}));
const buffer = vi.hoisted(() => vi.fn());
vi.mock("@/lib/signalr-toast-buffer", () => ({
  bufferToast: buffer,
  clearBufferedToasts: vi.fn(),
}));
vi.mock("@microsoft/signalr", () => ({
  HubConnectionBuilder: class {
    withUrl() {
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      return sdk;
    }
  },
  HubConnectionState: {
    Disconnected: "Disconnected",
    Connecting: "Connecting",
    Connected: "Connected",
    Reconnecting: "Reconnecting",
    Disconnecting: "Disconnecting",
  },
  LogLevel: { Debug: 1, None: 6 },
}));
vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://backup-repair.test"));
function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}
let client: ReturnType<typeof createAppQueryClient>;
let gate: ReturnType<typeof deferred>;
let started: ReturnType<typeof deferred>;
let heldRead: number;
let reads: number;
let year: number;
function Wrapper({ children }: { children: ReactNode }) {
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  vi.clearAllMocks();
  setTokens("Alice-access", "Alice-refresh");
  client = createAppQueryClient();
  gate = deferred();
  started = deferred();
  heldRead = 1;
  reads = 0;
  year = 2026;
  sdk.state = "Disconnected";
  sdk.start.mockImplementation(async () => {
    sdk.state = "Connected";
  });
  sdk.stop.mockImplementation(async () => {
    sdk.state = "Disconnected";
  });
  server.use(
    http.get("*/api/dashboard/earliest-receipt-year", async () => {
      const capturedYear = year;
      if (++reads === heldRead) {
        started.resolve();
        await gate.promise;
      }
      return HttpResponse.json({ year: capturedYear });
    }),
  );
});
afterEach(() => {
  gate.resolve();
  cleanup();
  client.clear();
  clearTokens();
  server.resetHandlers();
});
async function connect() {
  const connection = renderHook(() => useSignalR(true), { wrapper: Wrapper });
  await waitFor(() =>
    expect(connection.result.current.connectionState).toBe("connected"),
  );
  // Finish the initial reconnect repair before starting the read under test.
  await act(async () => {});
  return sdk.on.mock.calls.find(([name]) => name === "EntityChanged")![1] as (
    event: object,
  ) => void;
}
function event(connectionId = "remote-connection") {
  return {
    entityType: "backup-import",
    changeType: "updated",
    id: null,
    count: 1,
    connectionId,
  };
}

it.each(["remote-connection", "current-connection"])(
  "repairs an active pending first read for%s, with origin-appropriate feedback",
  async (origin) => {
    const notify = await connect();
    client.setQueryData(["api-keys", "Alice"], "private");
    const view = renderHook(() => useDashboardEarliestReceiptYear(), {
      wrapper: Wrapper,
    });
    await started.promise;
    year = 2020;
    await act(async () => notify(event(origin)));
    await waitFor(() => expect(view.result.current.data?.year).toBe(2020));
    await act(async () => gate.resolve());
    expect(view.result.current.data?.year).toBe(2020);
    expect(reads).toBe(2);
    expect(client.getQueryState(["api-keys", "Alice"])?.isInvalidated).toBe(
      false,
    );
    if (origin === "current-connection") expect(buffer).not.toHaveBeenCalled();
    else
      expect(buffer).toHaveBeenCalledWith("backup", "updated", 1, "other-user");
  },
);

it.each(["backup", "reconnect"])(
  "cancels an inactive cached refetch across %s so its old response cannot undo invalidation",
  async (kind) => {
    const notify = await connect();
    heldRead = 2;
    const view = renderHook(() => useDashboardEarliestReceiptYear(), {
      wrapper: Wrapper,
    });
    await waitFor(() => expect(view.result.current.data?.year).toBe(2026));
    view.unmount();
    const pending = client.refetchQueries({
      queryKey: ["dashboard", "earliest-receipt-year"],
    });
    await started.promise;
    year = 2020;
    await act(async () => {
      if (kind === "backup") notify(event());
      else sdk.onreconnected.mock.calls[0][0]();
    });
    await act(async () => {
      gate.resolve();
      await pending;
    });
    expect(
      client.getQueryState(["dashboard", "earliest-receipt-year"])
        ?.isInvalidated,
    ).toBe(true);
    const returned = renderHook(() => useDashboardEarliestReceiptYear(), {
      wrapper: Wrapper,
    });
    await waitFor(() => expect(returned.result.current.data?.year).toBe(2020));
    expect(reads).toBe(3);
  },
);

it("ignores a saved old-session event callback after identity replacement", async () => {
  const notify = await connect();
  client.setQueryData(["receipts"], "old-session-data");
  act(() => {
    clearTokens();
    setTokens("Bob-access", "Bob-refresh");
  });
  await act(async () => notify(event()));
  expect(client.getQueryState(["receipts"])?.isInvalidated).toBe(false);
  expect(buffer).not.toHaveBeenCalled();
});
