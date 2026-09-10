import { act, renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { createElement, type ReactNode } from "react";
import { beforeEach, describe, expect, it, vi, type Mock } from "vitest";

const hub = vi.hoisted(() => ({
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
const buffered = vi.hoisted(() => vi.fn());

vi.mock("@/lib/api-client", () => ({
  default: {
    GET: vi.fn(),
    POST: vi.fn(),
    PUT: vi.fn(),
    DELETE: vi.fn(),
  },
}));
vi.mock("./useDebouncedValue", () => ({
  useDebouncedValue: (value: string) => value,
}));
vi.mock("@/lib/signalr-toast-buffer", () => ({
  bufferToast: buffered,
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
      return hub;
    }
  },
  LogLevel: { Debug: 1, None: 6 },
  HubConnectionState: {
    Disconnected: "Disconnected",
    Connecting: "Connecting",
    Connected: "Connected",
    Reconnecting: "Reconnecting",
    Disconnecting: "Disconnecting",
  },
}));

import client from "@/lib/api-client";
import { useSignalR } from "./useSignalR";
import { useSimilarItems } from "./useSimilarItems";

function wrapper(queryClient: QueryClient) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return createElement(
      QueryClientProvider,
      { client: queryClient },
      children,
    );
  };
}

describe("item embedding realtime repair", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    hub.state = "Disconnected";
    hub.start.mockImplementation(async () => {
      hub.state = "Connected";
    });
    hub.stop.mockImplementation(async () => {
      hub.state = "Disconnected";
    });
  });

  it("refreshes a rendered stale similarity result after a remote embedding commit", async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: Infinity } },
    });
    let revision = "stale";
    (client.GET as Mock).mockImplementation(async () => ({
      data: [{ name: revision }],
      error: undefined,
    }));
    const { result, unmount } = renderHook(
      () => {
        useSignalR(true);
        return useSimilarItems("milk");
      },
      { wrapper: wrapper(queryClient) },
    );
    await waitFor(() =>
      expect(result.current.data).toEqual([{ name: "stale" }]),
    );
    await waitFor(() => expect(hub.start).toHaveBeenCalledTimes(1));
    queryClient.setQueryData(["ynab", "budgets"], { untouched: true });
    expect(queryClient.getQueryState(["ynab", "budgets"])?.isInvalidated).toBe(
      false,
    );
    const readsBeforeEvent = (client.GET as Mock).mock.calls.length;
    expect(readsBeforeEvent).toBeGreaterThan(0);
    const handler = hub.on.mock.calls.find(
      ([eventName]) => eventName === "EntityChanged",
    )?.[1] as ((event: unknown) => Promise<void>) | undefined;
    expect(handler).toBeDefined();
    revision = "fresh";

    await act(async () => {
      await handler!({
        entityType: "item-embedding",
        changeType: "updated",
        connectionId: "remote-connection",
        suppressToast: true,
      });
    });

    await waitFor(() =>
      expect(result.current.data).toEqual([{ name: "fresh" }]),
    );
    expect(client.GET).toHaveBeenCalledTimes(readsBeforeEvent + 1);
    expect(queryClient.getQueryData(["ynab", "budgets"])).toEqual({
      untouched: true,
    });
    expect(queryClient.getQueryState(["ynab", "budgets"])?.isInvalidated).toBe(
      false,
    );
    expect(buffered).not.toHaveBeenCalled();

    unmount();
    queryClient.clear();
  });
});
