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

// Mock debounce to return immediately
vi.mock("./useDebouncedValue", () => ({
  useDebouncedValue: (value: string) => value,
}));

import client from "@/lib/api-client";
import { useSimilarItems, useCategoryRecommendations } from "./useSimilarItems";

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

describe("useSimilarItems", () => {
  it("does not fetch when query is too short", () => {
    const { result } = renderHook(() => useSimilarItems("a"), {
      wrapper: createWrapper(),
    });
    expect(result.current.fetchStatus).toBe("idle");
    expect(client.GET).not.toHaveBeenCalled();
  });

  it("fetches similar items when query is >= 2 chars", async () => {
    const items = [
      { name: "Milk", source: "template", combinedScore: 0.9, defaultCategory: "Food" },
    ];
    (client.GET as Mock).mockResolvedValue({ data: items, error: undefined });

    const { result } = renderHook(() => useSimilarItems("mi"), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(items);
    expect(client.GET).toHaveBeenCalledWith("/api/item-templates/similar", {
      params: { query: { q: "mi", limit: 5, threshold: 0.3 } },
      middleware: expect.any(Array),
      signal: expect.any(AbortSignal),
    });
  });

  it("does not fetch when enabled is false", () => {
    const { result } = renderHook(
      () => useSimilarItems("milk", { enabled: false }),
      { wrapper: createWrapper() },
    );
    expect(result.current.fetchStatus).toBe("idle");
    expect(client.GET).not.toHaveBeenCalled();
  });

  it("passes custom limit and threshold", async () => {
    (client.GET as Mock).mockResolvedValue({ data: [], error: undefined });

    const { result } = renderHook(
      () => useSimilarItems("milk", { limit: 10, threshold: 0.5 }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenCalledWith("/api/item-templates/similar", {
      params: { query: { q: "milk", limit: 10, threshold: 0.5 } },
      middleware: expect.any(Array),
      signal: expect.any(AbortSignal),
    });
  });
});

describe("useCategoryRecommendations", () => {
  it("does not fetch when description is too short", () => {
    const { result } = renderHook(
      () => useCategoryRecommendations("a"),
      { wrapper: createWrapper() },
    );
    expect(result.current.fetchStatus).toBe("idle");
    expect(client.GET).not.toHaveBeenCalled();
  });

  it("fetches recommendations when description is >= 2 chars", async () => {
    const recs = [{ category: "Food", subcategory: "Dairy" }];
    (client.GET as Mock).mockResolvedValue({ data: recs, error: undefined });

    const { result } = renderHook(
      () => useCategoryRecommendations("milk"),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(recs);
    expect(client.GET).toHaveBeenCalledWith(
      "/api/item-templates/category-suggestions",
      {
        params: { query: { q: "milk", limit: 5 } },
        middleware: expect.any(Array),
        signal: expect.any(AbortSignal),
      },
    );
  });

  it("does not fetch when enabled is false", () => {
    const { result } = renderHook(
      () => useCategoryRecommendations("milk", { enabled: false }),
      { wrapper: createWrapper() },
    );
    expect(result.current.fetchStatus).toBe("idle");
    expect(client.GET).not.toHaveBeenCalled();
  });
});
