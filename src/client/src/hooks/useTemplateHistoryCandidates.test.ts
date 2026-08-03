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

import client from "@/lib/api-client";
import { useTemplateHistoryCandidates } from "./useTemplateHistoryCandidates";

const candidates = [
  {
    name: "Orange Juice",
    occurrenceCount: 6,
    lastPurchasedAt: "2026-05-14",
    suggestedCategory: "Groceries",
    suggestedSubcategory: "Beverages",
    suggestedUnitPrice: 5.29,
    suggestedItemCode: "OJ-100",
  },
];

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

describe("useTemplateHistoryCandidates", () => {
  it("returns the candidate list and total on success", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: { data: candidates, total: 1, offset: 0, limit: 10 },
      error: undefined,
    });

    const { result } = renderHook(() => useTemplateHistoryCandidates(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(candidates);
    expect(result.current.total).toBe(1);
  });

  it("requests the default page and minimum occurrence count", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: { data: [], total: 0, offset: 0, limit: 10 },
      error: undefined,
    });

    const { result } = renderHook(() => useTemplateHistoryCandidates(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenCalledWith(
      "/api/item-templates/history-candidates",
      expect.objectContaining({
        params: { query: { offset: 0, limit: 10, minCount: 2 } },
      }),
    );
  });

  it("forwards explicit paging and minCount arguments", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: { data: [], total: 0, offset: 20, limit: 5 },
      error: undefined,
    });

    const { result } = renderHook(
      () => useTemplateHistoryCandidates(20, 5, 3),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenCalledWith(
      "/api/item-templates/history-candidates",
      expect.objectContaining({
        params: { query: { offset: 20, limit: 5, minCount: 3 } },
      }),
    );
  });

  it("does not fetch when disabled", () => {
    const { result } = renderHook(
      () => useTemplateHistoryCandidates(0, 10, 2, { enabled: false }),
      { wrapper: createWrapper() },
    );

    expect(result.current.fetchStatus).toBe("idle");
    expect(client.GET).not.toHaveBeenCalled();
  });

  it("throws on API error", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: undefined,
      error: { message: "Server error" },
    });

    const { result } = renderHook(() => useTemplateHistoryCandidates(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.data).toBeUndefined();
    expect(result.current.total).toBe(0);
  });

  it("keeps the previous page on screen while a wider page loads", async () => {
    (client.GET as Mock).mockResolvedValueOnce({
      data: { data: candidates, total: 12, offset: 0, limit: 10 },
      error: undefined,
    });

    const { result, rerender } = renderHook(
      ({ limit }: { limit: number }) => useTemplateHistoryCandidates(0, limit),
      { wrapper: createWrapper(), initialProps: { limit: 10 } },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // The wider page never settles during this assertion window.
    (client.GET as Mock).mockImplementation(() => new Promise(() => {}));
    rerender({ limit: 20 });

    // Without placeholderData the section would unmount here, dropping keyboard
    // focus from the "Show more" button that triggered the widening.
    expect(result.current.data).toEqual(candidates);
    expect(result.current.isLoading).toBe(false);
  });

  it("returns a referentially stable object across re-renders", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: { data: candidates, total: 1, offset: 0, limit: 10 },
      error: undefined,
    });

    const { result, rerender } = renderHook(
      () => useTemplateHistoryCandidates(),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    const first = result.current;
    rerender();

    expect(result.current).toBe(first);
  });
});
