import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";
import { renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { createElement, type ReactNode } from "react";

vi.mock("@/lib/api-client", () => ({
  default: {
    GET: vi.fn(),
    POST: vi.fn(),
    PUT: vi.fn(),
    DELETE: vi.fn(),
  },
}));

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));

import client from "@/lib/api-client";
import { toast } from "sonner";
import {
  useReceipts,
  useAllReceipts,
  useReceipt,
  useCreateReceipt,
  useUpdateReceipt,
  useDeleteReceipts,
  useDeletedReceipts,
  useRestoreReceipt,
  useLocationSuggestions,
} from "./useReceipts";

function createWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  return function Wrapper({ children }: { children: ReactNode }) {
    return createElement(QueryClientProvider, { client: queryClient }, children);
  };
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe("useReceipts", () => {
  it("list query returns data on success", async () => {
    const receipts = [
      { id: "1", location: "Walmart", date: "2025-01-01", taxAmount: 5.0 },
    ];
    (client.GET as Mock).mockResolvedValue({ data: { data: receipts, total: 1, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useReceipts(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(receipts);
    expect(client.GET).toHaveBeenCalledWith("/api/receipts", {
      params: { query: { offset: 0, limit: 50 } },
    });
  });

  it("single query is disabled when id is null", () => {
    const { result } = renderHook(() => useReceipt(null), {
      wrapper: createWrapper(),
    });

    expect(result.current.data).toBeUndefined();
    expect(result.current.fetchStatus).toBe("idle");
    expect(client.GET).not.toHaveBeenCalled();
  });

  it("single query fetches data when id is provided", async () => {
    const receipt = { id: "1", location: "Walmart", date: "2025-01-01", taxAmount: 5.0 };
    (client.GET as Mock).mockResolvedValue({ data: receipt, error: undefined });

    const { result } = renderHook(() => useReceipt("1"), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(receipt);
  });

  it("create mutation calls POST and shows toast on success", async () => {
    const newReceipt = {
      location: "Target",
      date: "2025-02-01",
      taxAmount: 3.5,
      description: null,
    };
    const created = { id: "2", ...newReceipt };
    (client.POST as Mock).mockResolvedValue({ data: created, error: undefined });

    const { result } = renderHook(() => useCreateReceipt(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync(newReceipt);

    expect(client.POST).toHaveBeenCalledWith("/api/receipts", { body: newReceipt });
    expect(toast.success).toHaveBeenCalledWith("Receipt created");
  });

  it("update mutation calls PUT and shows toast on success", async () => {
    const updated = {
      id: "1",
      location: "Walmart Updated",
      date: "2025-01-02",
      taxAmount: 6.0,
    };
    (client.PUT as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useUpdateReceipt(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync(updated);

    expect(client.PUT).toHaveBeenCalledWith("/api/receipts/{id}", {
      params: { path: { id: "1" } },
      body: updated,
    });
    expect(toast.success).toHaveBeenCalledWith("Receipt updated");
  });

  it("delete mutation calls DELETE", async () => {
    (client.DELETE as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useDeleteReceipts(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync(["1", "2"]);

    expect(client.DELETE).toHaveBeenCalledWith("/api/receipts", {
      body: ["1", "2"],
    });
    expect(toast.success).toHaveBeenCalledWith("Receipt(s) deleted");
  });

  it("deleted receipts query returns data on success", async () => {
    const deleted = [{ id: "3", location: "Old Store", date: "2024-01-01", taxAmount: 0 }];
    (client.GET as Mock).mockResolvedValue({ data: { data: deleted, total: 1, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useDeletedReceipts(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(deleted);
    expect(client.GET).toHaveBeenCalledWith("/api/receipts/deleted", {
      params: { query: { offset: 0, limit: 50 } },
    });
  });

  it("restore mutation calls POST and shows toast on success", async () => {
    (client.POST as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useRestoreReceipt(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync("1");

    expect(client.POST).toHaveBeenCalledWith("/api/receipts/{id}/restore", {
      params: { path: { id: "1" } },
    });
    expect(toast.success).toHaveBeenCalledWith("Receipt restored");
  });

  // --- Branch coverage: error callbacks ---

  it("create mutation does not toast on failure (surfaced by the global handler)", async () => {
    (client.POST as Mock).mockResolvedValue({ error: { message: "Server error" } });

    const { result } = renderHook(() => useCreateReceipt(), {
      wrapper: createWrapper(),
    });

    result.current.mutate({ location: "Store", date: "2025-01-01", taxAmount: 1.0 });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("update mutation does not toast on failure (surfaced by the global handler)", async () => {
    (client.PUT as Mock).mockResolvedValue({ error: { message: "Server error" } });

    const { result } = renderHook(() => useUpdateReceipt(), {
      wrapper: createWrapper(),
    });

    result.current.mutate({ id: "1", location: "Store", date: "2025-01-01", taxAmount: 1.0 });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("delete mutation rolls back cache on failure (error surfaced by the global handler)", async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: 0 } },
    });
    const setQueryDataSpy = vi.spyOn(queryClient, "setQueryData");

    function Wrapper({ children }: { children: ReactNode }) {
      return createElement(QueryClientProvider, { client: queryClient }, children);
    }

    const receipts = [
      { id: "1", location: "A" },
      { id: "2", location: "B" },
    ];
    const cacheKey = ["receipts", "list", 0, 50, undefined, undefined];
    const cacheValue = { data: receipts, total: 2, offset: 0, limit: 50 };
    queryClient.setQueryData(cacheKey, cacheValue);
    setQueryDataSpy.mockClear();

    (client.DELETE as Mock).mockResolvedValue({ error: { message: "Server error" } });

    const { result } = renderHook(() => useDeleteReceipts(), {
      wrapper: Wrapper,
    });

    result.current.mutate(["1"]);

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(toast.error).not.toHaveBeenCalled();

    // Verify rollback restored the original data (not just the optimistic update from onMutate)
    expect(setQueryDataSpy).toHaveBeenCalledWith(cacheKey, cacheValue);
  });

  it("delete optimistic update handles undefined cache gracefully", async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: 0 } },
    });

    function Wrapper({ children }: { children: ReactNode }) {
      return createElement(QueryClientProvider, { client: queryClient }, children);
    }

    queryClient.setQueryData(["receipts", "list", 0, 50, undefined, undefined], undefined);

    (client.DELETE as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useDeleteReceipts(), {
      wrapper: Wrapper,
    });

    result.current.mutate(["1"]);

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(toast.success).toHaveBeenCalledWith("Receipt(s) deleted");
  });

  it("restore mutation does not toast on failure (surfaced by the global handler)", async () => {
    (client.POST as Mock).mockResolvedValue({ error: { message: "Server error" } });

    const { result } = renderHook(() => useRestoreReceipt(), {
      wrapper: createWrapper(),
    });

    result.current.mutate("1");

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("list query throws on API error", async () => {
    (client.GET as Mock).mockResolvedValue({ data: undefined, error: { message: "Server error" } });

    const { result } = renderHook(() => useReceipts(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
  });

  it("list query passes sort parameters", async () => {
    (client.GET as Mock).mockResolvedValue({ data: { data: [], total: 0, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useReceipts(0, 50, "location", "desc"), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenCalledWith("/api/receipts", {
      params: { query: { offset: 0, limit: 50, sortBy: "location", sortDirection: "desc" } },
    });
  });

  it("list query passes trimmed q to the server when provided", async () => {
    (client.GET as Mock).mockResolvedValue({ data: { data: [], total: 0, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useReceipts(0, 50, null, null, null, null, "  Walmart  "), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenCalledWith("/api/receipts", {
      params: { query: { offset: 0, limit: 50, q: "Walmart" } },
    });
  });

  it("list query omits q when the value is blank", async () => {
    (client.GET as Mock).mockResolvedValue({ data: { data: [], total: 0, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useReceipts(0, 50, null, null, null, null, "   "), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    const call = (client.GET as Mock).mock.calls[0];
    expect(call[1].params.query.q).toBeUndefined();
  });

  it("list query passes location to the server verbatim, including surrounding whitespace", async () => {
    // Unlike q, location is an exact-match drill-down key matched byte-for-byte against the raw
    // Location value the Spending by Location report grouped on, so it must NOT be trimmed.
    (client.GET as Mock).mockResolvedValue({ data: { data: [], total: 0, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(
      () => useReceipts(0, 50, null, null, null, null, null, { location: "  Target  " }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenCalledWith("/api/receipts", {
      params: { query: { offset: 0, limit: 50, location: "  Target  " } },
    });
  });

  it("list query forwards a whitespace-only location as a real filter value", async () => {
    (client.GET as Mock).mockResolvedValue({ data: { data: [], total: 0, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(
      () => useReceipts(0, 50, null, null, null, null, null, { location: "   " }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    const call = (client.GET as Mock).mock.calls[0];
    expect(call[1].params.query.location).toBe("   ");
  });

  it("list query omits location when it is an empty string", async () => {
    (client.GET as Mock).mockResolvedValue({ data: { data: [], total: 0, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(
      () => useReceipts(0, 50, null, null, null, null, null, { location: "" }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    const call = (client.GET as Mock).mock.calls[0];
    expect(call[1].params.query.location).toBeUndefined();
  });

  it("list query returns total of 0 when data is undefined", async () => {
    (client.GET as Mock).mockResolvedValue({ data: undefined, error: { message: "err" } });

    const { result } = renderHook(() => useReceipts(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.total).toBe(0);
  });

  it("delete mutation invalidates both list and deleted query keys on settled", async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: 0 } },
    });
    const invalidateSpy = vi.spyOn(queryClient, "invalidateQueries");

    function Wrapper({ children }: { children: ReactNode }) {
      return createElement(QueryClientProvider, { client: queryClient }, children);
    }

    (client.DELETE as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useDeleteReceipts(), {
      wrapper: Wrapper,
    });

    result.current.mutate(["1"]);

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ["receipts"] });
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ["receipts", "deleted"] });
  });
});

describe("useAllReceipts", () => {
  it("does not fetch when disabled", () => {
    const { result } = renderHook(
      () => useAllReceipts({ enabled: false }),
      { wrapper: createWrapper() },
    );

    expect(result.current.fetchStatus).toBe("idle");
    expect(client.GET).not.toHaveBeenCalled();
  });

  it("auto-paginates across multiple pages in descending date order", async () => {
    const pageOne = Array.from({ length: 500 }, (_, index) => ({
      id: `receipt-${index}`,
      location: `Store ${index}`,
      date: "2025-01-02",
    }));
    const pageTwo = Array.from({ length: 100 }, (_, index) => ({
      id: `receipt-${500 + index}`,
      location: `Store ${500 + index}`,
      date: "2025-01-01",
    }));
    (client.GET as Mock).mockImplementation((_path, options) => {
      const offset = options.params.query.offset;
      return Promise.resolve({
        data: {
          data: offset === 0 ? pageOne : pageTwo,
          total: 600,
          offset,
          limit: 500,
        },
        error: undefined,
      });
    });

    const { result } = renderHook(() => useAllReceipts(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toHaveLength(600);
    expect(client.GET).toHaveBeenCalledTimes(2);
    expect(client.GET).toHaveBeenLastCalledWith("/api/receipts", {
      params: {
        query: {
          offset: 500,
          limit: 500,
          sortBy: "date",
          sortDirection: "desc",
        },
      },
      signal: expect.any(AbortSignal),
    });
  });
});

describe("useLocationSuggestions", () => {
  it("returns location strings on success", async () => {
    const locations = ["Walmart", "Target", "Costco"];
    (client.GET as Mock).mockResolvedValue({
      data: { locations },
      error: undefined,
    });

    const { result } = renderHook(() => useLocationSuggestions(""), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(locations);
    expect(client.GET).toHaveBeenCalledWith("/api/receipts/locations", {
      params: { query: { q: undefined, limit: 20 } },
    });
  });

  it("passes query parameter when provided", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: { locations: ["Walmart"] },
      error: undefined,
    });

    const { result } = renderHook(() => useLocationSuggestions("Wal"), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenCalledWith("/api/receipts/locations", {
      params: { query: { q: "Wal", limit: 20 } },
    });
  });

  it("returns empty array when response has no locations", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: undefined,
      error: undefined,
    });

    const { result } = renderHook(() => useLocationSuggestions(""), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual([]);
  });

  it("throws on error", async () => {
    const error = { message: "Server error" };
    (client.GET as Mock).mockResolvedValue({ data: undefined, error });

    const { result } = renderHook(() => useLocationSuggestions(""), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
  });
});
