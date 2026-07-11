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
  useReceiptItems,
  useReceiptItem,
  useReceiptItemsByReceiptId,
  useCreateReceiptItem,
  useCreateReceiptItemsBatch,
  useUpdateReceiptItem,
  useDeleteReceiptItems,
  useDeletedReceiptItems,
  useRestoreReceiptItem,
} from "./useReceiptItems";

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

describe("useReceiptItems", () => {
  it("list query returns data on success", async () => {
    const items = [
      {
        id: "1",
        receiptItemCode: "RI-1",
        description: "Apples",
        quantity: 3,
        unitPrice: 1.5,
        category: "Food",
        subcategory: "Produce",
      },
    ];
    (client.GET as Mock).mockResolvedValue({ data: { data: items, total: 1, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useReceiptItems(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(items);
    expect(client.GET).toHaveBeenCalledWith("/api/receipt-items", {
      params: { query: { offset: 0, limit: 50 } },
    });
  });

  it("list query passes trimmed q to the server when provided", async () => {
    (client.GET as Mock).mockResolvedValue({ data: { data: [], total: 0, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useReceiptItems(0, 50, null, null, "  Apples  "), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenCalledWith("/api/receipt-items", {
      params: { query: { offset: 0, limit: 50, q: "Apples" } },
    });
  });

  it("list query omits q when the value is blank", async () => {
    (client.GET as Mock).mockResolvedValue({ data: { data: [], total: 0, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useReceiptItems(0, 50, null, null, "   "), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    const call = (client.GET as Mock).mock.calls[0];
    expect(call[1].params.query.q).toBeUndefined();
  });

  it("single query is disabled when id is null", () => {
    const { result } = renderHook(() => useReceiptItem(null), {
      wrapper: createWrapper(),
    });

    expect(result.current.data).toBeUndefined();
    expect(result.current.fetchStatus).toBe("idle");
    expect(client.GET).not.toHaveBeenCalled();
  });

  it("by-receipt query returns data when receiptId is provided", async () => {
    const items = [{ id: "1", receiptItemCode: "RI-1", description: "Apples" }];
    (client.GET as Mock).mockResolvedValue({ data: { data: items, total: 1, offset: 0, limit: 200 }, error: undefined });

    const { result } = renderHook(() => useReceiptItemsByReceiptId("r-1"), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(items);
    expect(client.GET).toHaveBeenCalledWith(
      "/api/receipt-items",
      { params: { query: { receiptId: "r-1", offset: 0, limit: 200 } } },
    );
  });

  it("by-receipt query is disabled when receiptId is null", () => {
    const { result } = renderHook(() => useReceiptItemsByReceiptId(null), {
      wrapper: createWrapper(),
    });

    expect(result.current.data).toBeUndefined();
    expect(result.current.fetchStatus).toBe("idle");
  });

  it("create mutation calls POST and shows toast on success", async () => {
    const body = {
      receiptItemCode: "RI-2",
      description: "Bananas",
      quantity: 6,
      unitPrice: 0.5,
      category: "Food",
      subcategory: "Produce",
    };
    const created = { id: "2", ...body };
    (client.POST as Mock).mockResolvedValue({ data: created, error: undefined });

    const { result } = renderHook(() => useCreateReceiptItem(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync({ receiptId: "r-1", body });

    expect(client.POST).toHaveBeenCalledWith("/api/receipts/{receiptId}/receipt-items", {
      params: { path: { receiptId: "r-1" } },
      body,
    });
    expect(toast.success).toHaveBeenCalledWith("Receipt item created");
  });

  it("update mutation calls PUT and shows toast on success", async () => {
    const body = {
      id: "1",
      receiptItemCode: "RI-1",
      description: "Apples (updated)",
      quantity: 5,
      unitPrice: 1.75,
      category: "Food",
      subcategory: "Produce",
    };
    (client.PUT as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useUpdateReceiptItem(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync({ body });

    expect(client.PUT).toHaveBeenCalledWith("/api/receipt-items/{id}", {
      params: { path: { id: body.id } },
      body,
    });
    expect(toast.success).toHaveBeenCalledWith("Receipt item updated");
  });

  it("delete mutation calls DELETE", async () => {
    (client.DELETE as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useDeleteReceiptItems(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync(["1"]);

    expect(client.DELETE).toHaveBeenCalledWith("/api/receipt-items", {
      body: ["1"],
    });
    expect(toast.success).toHaveBeenCalledWith("Receipt item(s) deleted");
  });

  it("deleted receipt items query returns data on success", async () => {
    const deleted = [{ id: "3", receiptItemCode: "DEL", description: "Old item" }];
    (client.GET as Mock).mockResolvedValue({ data: { data: deleted, total: 1, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useDeletedReceiptItems(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(deleted);
    expect(client.GET).toHaveBeenCalledWith("/api/receipt-items/deleted", {
      params: { query: { offset: 0, limit: 50 } },
    });
  });

  it("restore mutation calls POST and shows toast on success", async () => {
    (client.POST as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useRestoreReceiptItem(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync("1");

    expect(client.POST).toHaveBeenCalledWith("/api/receipt-items/{id}/restore", {
      params: { path: { id: "1" } },
    });
    expect(toast.success).toHaveBeenCalledWith("Receipt item restored");
  });

  // --- Branch coverage: error callbacks ---

  it("create mutation does not toast on failure (surfaced by the global handler)", async () => {
    (client.POST as Mock).mockResolvedValue({ error: { message: "Server error" } });

    const { result } = renderHook(() => useCreateReceiptItem(), {
      wrapper: createWrapper(),
    });

    result.current.mutate({ receiptId: "r-1", body: { receiptItemCode: "RI-1", description: "X", quantity: 1, unitPrice: 1, category: "C", subcategory: "S" } });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("batch create mutation does not toast on failure (surfaced by the global handler)", async () => {
    (client.POST as Mock).mockResolvedValue({ error: { message: "Server error" } });

    const { result } = renderHook(() => useCreateReceiptItemsBatch(), {
      wrapper: createWrapper(),
    });

    result.current.mutate({ receiptId: "r-1", body: [{ receiptItemCode: "RI-1", description: "X", quantity: 1, unitPrice: 1, category: "C", subcategory: "S" }] });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("batch create mutation invalidates cache on success", async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: 0 } },
    });
    const invalidateSpy = vi.spyOn(queryClient, "invalidateQueries");

    function Wrapper({ children }: { children: ReactNode }) {
      return createElement(QueryClientProvider, { client: queryClient }, children);
    }

    (client.POST as Mock).mockResolvedValue({ data: [{ id: "1" }], error: undefined });

    const { result } = renderHook(() => useCreateReceiptItemsBatch(), {
      wrapper: Wrapper,
    });

    result.current.mutate({ receiptId: "r-1", body: [{ receiptItemCode: "RI-1", description: "X", quantity: 1, unitPrice: 1, category: "C", subcategory: "S" }] });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ["receipt-items"] });
  });

  it("update mutation does not toast on failure (surfaced by the global handler)", async () => {
    (client.PUT as Mock).mockResolvedValue({ error: { message: "Server error" } });

    const { result } = renderHook(() => useUpdateReceiptItem(), {
      wrapper: createWrapper(),
    });

    result.current.mutate({ body: { id: "1", receiptItemCode: "RI-1", description: "X", quantity: 1, unitPrice: 1, category: "C", subcategory: "S" } });

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

    // Pre-populate cache with list data
    const items = [
      { id: "1", description: "A" },
      { id: "2", description: "B" },
    ];
    const cacheKey = ["receipt-items", "list", 0, 50, undefined, undefined];
    const cacheValue = { data: items, total: 2, offset: 0, limit: 50 };
    queryClient.setQueryData(cacheKey, cacheValue);
    setQueryDataSpy.mockClear();

    (client.DELETE as Mock).mockResolvedValue({ error: { message: "Server error" } });

    const { result } = renderHook(() => useDeleteReceiptItems(), {
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

    // Set cache to undefined (simulating no prior fetch)
    queryClient.setQueryData(["receipt-items", "list", 0, 50, undefined, undefined], undefined);

    (client.DELETE as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useDeleteReceiptItems(), {
      wrapper: Wrapper,
    });

    result.current.mutate(["1"]);

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(toast.success).toHaveBeenCalledWith("Receipt item(s) deleted");
  });

  it("restore mutation does not toast on failure (surfaced by the global handler)", async () => {
    (client.POST as Mock).mockResolvedValue({ error: { message: "Server error" } });

    const { result } = renderHook(() => useRestoreReceiptItem(), {
      wrapper: createWrapper(),
    });

    result.current.mutate("1");

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("list query throws on API error", async () => {
    (client.GET as Mock).mockResolvedValue({ data: undefined, error: { message: "Server error" } });

    const { result } = renderHook(() => useReceiptItems(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
  });

  it("list query passes sort parameters", async () => {
    (client.GET as Mock).mockResolvedValue({ data: { data: [], total: 0, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useReceiptItems(0, 50, "description", "asc"), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenCalledWith("/api/receipt-items", {
      params: { query: { offset: 0, limit: 50, sortBy: "description", sortDirection: "asc" } },
    });
  });

  it("list query returns total of 0 when data is undefined", async () => {
    (client.GET as Mock).mockResolvedValue({ data: undefined, error: { message: "err" } });

    const { result } = renderHook(() => useReceiptItems(), {
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

    const { result } = renderHook(() => useDeleteReceiptItems(), {
      wrapper: Wrapper,
    });

    result.current.mutate(["1"]);

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ["receipt-items"] });
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ["receipt-items", "deleted"] });
  });
});
