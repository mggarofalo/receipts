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
  useAdjustments,
  useAdjustment,
  useAdjustmentsByReceiptId,
  useCreateAdjustment,
  useUpdateAdjustment,
  useDeleteAdjustments,
  useDeletedAdjustments,
  useRestoreAdjustment,
} from "./useAdjustments";

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

describe("useAdjustments", () => {
  it("list query returns data on success", async () => {
    const adjustments = [
      { id: "1", type: "discount", amount: 5.0, description: "Coupon" },
    ];
    (client.GET as Mock).mockResolvedValue({ data: { data: adjustments, total: 1, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useAdjustments(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(adjustments);
    expect(client.GET).toHaveBeenCalledWith("/api/adjustments", {
      params: { query: { offset: 0, limit: 50 } },
    });
  });

  it("single query is disabled when id is null", () => {
    const { result } = renderHook(() => useAdjustment(null), {
      wrapper: createWrapper(),
    });

    expect(result.current.data).toBeUndefined();
    expect(result.current.fetchStatus).toBe("idle");
    expect(client.GET).not.toHaveBeenCalled();
  });

  it("by-receipt query returns data when receiptId is provided", async () => {
    const items = [{ id: "1", type: "discount", amount: 5.0 }];
    (client.GET as Mock).mockResolvedValue({ data: { data: items, total: 1, offset: 0, limit: 200 }, error: undefined });

    const { result } = renderHook(() => useAdjustmentsByReceiptId("r-1"), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(items);
    expect(client.GET).toHaveBeenCalledWith(
      "/api/adjustments",
      { params: { query: { receiptId: "r-1", offset: 0, limit: 200 } } },
    );
  });

  it("by-receipt query is disabled when receiptId is null", () => {
    const { result } = renderHook(() => useAdjustmentsByReceiptId(null), {
      wrapper: createWrapper(),
    });

    expect(result.current.data).toBeUndefined();
    expect(result.current.fetchStatus).toBe("idle");
  });

  it("create mutation calls POST and shows toast on success", async () => {
    const body = { type: "surcharge", amount: 2.0, description: "Service fee" };
    const created = { id: "2", ...body };
    (client.POST as Mock).mockResolvedValue({ data: created, error: undefined });

    const { result } = renderHook(() => useCreateAdjustment(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync({ receiptId: "r-1", body });

    expect(client.POST).toHaveBeenCalledWith("/api/receipts/{receiptId}/adjustments", {
      params: { path: { receiptId: "r-1" } },
      body,
    });
    expect(toast.success).toHaveBeenCalledWith("Adjustment created");
  });

  it("update mutation calls PUT and shows toast on success", async () => {
    const body = {
      id: "1",
      type: "discount",
      amount: 10.0,
      description: "Updated coupon",
    };
    (client.PUT as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useUpdateAdjustment(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync({ body });

    expect(client.PUT).toHaveBeenCalledWith("/api/adjustments/{id}", {
      params: { path: { id: body.id } },
      body,
    });
    expect(toast.success).toHaveBeenCalledWith("Adjustment updated");
  });

  it("delete mutation calls DELETE", async () => {
    (client.DELETE as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useDeleteAdjustments(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync(["1"]);

    expect(client.DELETE).toHaveBeenCalledWith("/api/adjustments", {
      body: ["1"],
    });
    expect(toast.success).toHaveBeenCalledWith("Adjustment(s) deleted");
  });

  it("deleted adjustments query returns data on success", async () => {
    const deleted = [{ id: "3", type: "discount", amount: 1.0 }];
    (client.GET as Mock).mockResolvedValue({ data: { data: deleted, total: 1, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useDeletedAdjustments(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(deleted);
    expect(client.GET).toHaveBeenCalledWith("/api/adjustments/deleted", {
      params: { query: { offset: 0, limit: 50 } },
    });
  });

  it("restore mutation calls POST and shows toast on success", async () => {
    (client.POST as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useRestoreAdjustment(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync("1");

    expect(client.POST).toHaveBeenCalledWith("/api/adjustments/{id}/restore", {
      params: { path: { id: "1" } },
    });
    expect(toast.success).toHaveBeenCalledWith("Adjustment restored");
  });

  // --- Branch coverage: error callbacks ---

  it("create mutation does not toast on failure (surfaced by the global handler)", async () => {
    (client.POST as Mock).mockResolvedValue({ error: { message: "Server error" } });

    const { result } = renderHook(() => useCreateAdjustment(), {
      wrapper: createWrapper(),
    });

    result.current.mutate({ receiptId: "r-1", body: { type: "discount", amount: 5.0 } });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("update mutation does not toast on failure (surfaced by the global handler)", async () => {
    (client.PUT as Mock).mockResolvedValue({ error: { message: "Server error" } });

    const { result } = renderHook(() => useUpdateAdjustment(), {
      wrapper: createWrapper(),
    });

    result.current.mutate({ body: { id: "1", type: "discount", amount: 5.0 } });

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

    const adjustments = [
      { id: "1", type: "discount" },
      { id: "2", type: "surcharge" },
    ];
    const cacheKey = ["adjustments", "list", 0, 50, undefined, undefined];
    const cacheValue = { data: adjustments, total: 2, offset: 0, limit: 50 };
    queryClient.setQueryData(cacheKey, cacheValue);
    setQueryDataSpy.mockClear();

    (client.DELETE as Mock).mockResolvedValue({ error: { message: "Server error" } });

    const { result } = renderHook(() => useDeleteAdjustments(), {
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

    queryClient.setQueryData(["adjustments", "list", 0, 50, undefined, undefined], undefined);

    (client.DELETE as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useDeleteAdjustments(), {
      wrapper: Wrapper,
    });

    result.current.mutate(["1"]);

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(toast.success).toHaveBeenCalledWith("Adjustment(s) deleted");
  });

  it("restore mutation does not toast on failure (surfaced by the global handler)", async () => {
    (client.POST as Mock).mockResolvedValue({ error: { message: "Server error" } });

    const { result } = renderHook(() => useRestoreAdjustment(), {
      wrapper: createWrapper(),
    });

    result.current.mutate("1");

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("list query throws on API error", async () => {
    (client.GET as Mock).mockResolvedValue({ data: undefined, error: { message: "Server error" } });

    const { result } = renderHook(() => useAdjustments(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
  });

  it("list query passes sort parameters", async () => {
    (client.GET as Mock).mockResolvedValue({ data: { data: [], total: 0, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useAdjustments(0, 50, "amount", "asc"), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenCalledWith("/api/adjustments", {
      params: { query: { offset: 0, limit: 50, sortBy: "amount", sortDirection: "asc" } },
    });
  });

  it("list query returns total of 0 when data is undefined", async () => {
    (client.GET as Mock).mockResolvedValue({ data: undefined, error: { message: "err" } });

    const { result } = renderHook(() => useAdjustments(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.total).toBe(0);
  });

  it("delete mutation invalidates adjustments, deleted, receipts-with-items, and trips on settled", async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: 0 } },
    });
    const invalidateSpy = vi.spyOn(queryClient, "invalidateQueries");

    function Wrapper({ children }: { children: ReactNode }) {
      return createElement(QueryClientProvider, { client: queryClient }, children);
    }

    (client.DELETE as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useDeleteAdjustments(), {
      wrapper: Wrapper,
    });

    result.current.mutate(["1"]);

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ["adjustments"] });
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ["adjustments", "deleted"] });
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ["receipts-with-items"] });
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ["trips"] });
  });
});
