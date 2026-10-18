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
  useCreateAdjustment,
  useUpdateAdjustment,
  useDeleteAdjustments,
} from "./useAdjustments";

function createWrapper(
  queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: Infinity } },
  }),
) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return createElement(
      QueryClientProvider,
      { client: queryClient },
      children,
    );
  };
}

const cachedAdjustmentDependents = [
  ["receipts"],
  ["receipts-with-items"],
  ["trips"],
  ["reports"],
  ["ynab", "split-comparison"],
  ["ynab", "receipt-sync-statuses"],
  ["adjustments"],
  ["adjustments", "deleted"],
];

function expectReceiptDependentsInvalidated(queryClient: QueryClient) {
  expect(queryClient.getQueryState(["receipts"])?.isInvalidated).toBe(true);
  expect(
    queryClient.getQueryState(["receipts-with-items"])?.isInvalidated,
  ).toBe(true);
  expect(queryClient.getQueryState(["trips"])?.isInvalidated).toBe(true);
  expect(queryClient.getQueryState(["reports"])?.isInvalidated).toBe(true);
  expect(
    queryClient.getQueryState(["ynab", "split-comparison"])?.isInvalidated,
  ).toBe(true);
  expect(
    queryClient.getQueryState(["ynab", "receipt-sync-statuses"])?.isInvalidated,
  ).toBe(true);
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe("useAdjustments", () => {
  it("create mutation calls POST and shows toast on success", async () => {
    const body = { type: "surcharge", amount: 2.0, description: "Service fee" };
    const created = { id: "2", ...body };
    (client.POST as Mock).mockResolvedValue({
      data: created,
      error: undefined,
    });

    const queryClient = new QueryClient();
    for (const key of cachedAdjustmentDependents)
      queryClient.setQueryData(key, {
        data: [],
        total: 0,
        offset: 0,
        limit: 50,
      });
    const { result } = renderHook(() => useCreateAdjustment(), {
      wrapper: createWrapper(queryClient),
    });

    await result.current.mutateAsync({ receiptId: "r-1", body });

    expect(client.POST).toHaveBeenCalledWith(
      "/api/receipts/{receiptId}/adjustments",
      {
        middleware: expect.any(Array),
        params: { path: { receiptId: "r-1" } },
        body,
      },
    );
    expect(toast.success).toHaveBeenCalledWith("Adjustment created");
    expectReceiptDependentsInvalidated(queryClient);
  });

  it("update mutation calls PUT and shows toast on success", async () => {
    const body = {
      id: "1",
      type: "discount",
      amount: 10.0,
      description: "Updated coupon",
    };
    (client.PUT as Mock).mockResolvedValue({ error: undefined });

    const queryClient = new QueryClient();
    for (const key of cachedAdjustmentDependents)
      queryClient.setQueryData(key, {
        data: [],
        total: 0,
        offset: 0,
        limit: 50,
      });
    const { result } = renderHook(() => useUpdateAdjustment(), {
      wrapper: createWrapper(queryClient),
    });

    await result.current.mutateAsync({ body });

    expect(client.PUT).toHaveBeenCalledWith("/api/adjustments/{id}", {
      middleware: expect.any(Array),
      params: { path: { id: body.id } },
      body,
    });
    expect(toast.success).toHaveBeenCalledWith("Adjustment updated");
    expectReceiptDependentsInvalidated(queryClient);
  });

  it("delete mutation calls DELETE", async () => {
    (client.DELETE as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useDeleteAdjustments(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync(["1"]);

    expect(client.DELETE).toHaveBeenCalledWith("/api/adjustments", {
      middleware: expect.any(Array),
      body: ["1"],
    });
    expect(toast.success).toHaveBeenCalledWith("Adjustment(s) deleted");
  });

  // --- Branch coverage: error callbacks ---

  it("create mutation does not toast on failure (surfaced by the global handler)", async () => {
    (client.POST as Mock).mockResolvedValue({
      error: { message: "Server error" },
    });

    const { result } = renderHook(() => useCreateAdjustment(), {
      wrapper: createWrapper(),
    });

    result.current.mutate({
      receiptId: "r-1",
      body: { type: "discount", amount: 5.0 },
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("update mutation does not toast on failure (surfaced by the global handler)", async () => {
    (client.PUT as Mock).mockResolvedValue({
      error: { message: "Server error" },
    });

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
      return createElement(
        QueryClientProvider,
        { client: queryClient },
        children,
      );
    }

    const adjustments = [
      { id: "1", type: "discount" },
      { id: "2", type: "surcharge" },
    ];
    const cacheKey = ["adjustments", "list", 0, 50, undefined, undefined];
    const cacheValue = { data: adjustments, total: 2, offset: 0, limit: 50 };
    queryClient.setQueryData(cacheKey, cacheValue);
    setQueryDataSpy.mockClear();

    (client.DELETE as Mock).mockResolvedValue({
      error: { message: "Server error" },
    });

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
      return createElement(
        QueryClientProvider,
        { client: queryClient },
        children,
      );
    }

    queryClient.setQueryData(
      ["adjustments", "list", 0, 50, undefined, undefined],
      undefined,
    );

    (client.DELETE as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useDeleteAdjustments(), {
      wrapper: Wrapper,
    });

    result.current.mutate(["1"]);

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(toast.success).toHaveBeenCalledWith("Adjustment(s) deleted");
  });

  it("delete mutation invalidates adjustments, deleted, receipts-with-items, and trips on settled", async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: Infinity } },
    });
    for (const key of cachedAdjustmentDependents)
      queryClient.setQueryData(key, {
        data: [],
        total: 0,
        offset: 0,
        limit: 50,
      });

    function Wrapper({ children }: { children: ReactNode }) {
      return createElement(
        QueryClientProvider,
        { client: queryClient },
        children,
      );
    }

    (client.DELETE as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useDeleteAdjustments(), {
      wrapper: Wrapper,
    });

    result.current.mutate(["1"]);

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(queryClient.getQueryState(["adjustments"])?.isInvalidated).toBe(
      true,
    );
    expect(
      queryClient.getQueryState(["adjustments", "deleted"])?.isInvalidated,
    ).toBe(true);
    expect(
      queryClient.getQueryState(["receipts-with-items"])?.isInvalidated,
    ).toBe(true);
    expect(queryClient.getQueryState(["trips"])?.isInvalidated).toBe(true);
    expectReceiptDependentsInvalidated(queryClient);
  });
});
