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
import { useReceiptItemSuggestions } from "./useReceiptItemSuggestions";

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

describe("useReceiptItemSuggestions", () => {
  it("does not fetch when itemCode is empty", () => {
    const { result } = renderHook(() => useReceiptItemSuggestions(""), {
      wrapper: createWrapper(),
    });
    expect(result.current.fetchStatus).toBe("idle");
    expect(client.GET).not.toHaveBeenCalled();
  });

  it("fetches suggestions when itemCode is >= 1 char", async () => {
    const suggestions = [
      {
        itemCode: "MILK-GAL",
        description: "Whole Milk",
        category: "Groceries",
        subcategory: "Dairy",
        unitPrice: 3.99,
        matchType: "location",
      },
    ];
    (client.GET as Mock).mockResolvedValue({ data: suggestions, error: undefined });

    const { result } = renderHook(
      () => useReceiptItemSuggestions("M", "Walmart"),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(suggestions);
    expect(client.GET).toHaveBeenCalledWith("/api/receipt-items/suggestions", {
      params: { query: { itemCode: "M", location: "Walmart", limit: 10 } },
      middleware: expect.any(Array),
      signal: expect.any(AbortSignal),
    });
  });

  it("does not fetch when enabled is false", () => {
    const { result } = renderHook(
      () => useReceiptItemSuggestions("MILK", "Walmart", { enabled: false }),
      { wrapper: createWrapper() },
    );
    expect(result.current.fetchStatus).toBe("idle");
    expect(client.GET).not.toHaveBeenCalled();
  });

  it("passes null location as undefined in query params", async () => {
    (client.GET as Mock).mockResolvedValue({ data: [], error: undefined });

    const { result } = renderHook(
      () => useReceiptItemSuggestions("MILK", null),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenCalledWith("/api/receipt-items/suggestions", {
      params: { query: { itemCode: "MILK", location: undefined, limit: 10 } },
      middleware: expect.any(Array),
      signal: expect.any(AbortSignal),
    });
  });

  it("passes custom limit", async () => {
    (client.GET as Mock).mockResolvedValue({ data: [], error: undefined });

    const { result } = renderHook(
      () => useReceiptItemSuggestions("MILK", "Walmart", { limit: 5 }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenCalledWith("/api/receipt-items/suggestions", {
      params: { query: { itemCode: "MILK", location: "Walmart", limit: 5 } },
      middleware: expect.any(Array),
      signal: expect.any(AbortSignal),
    });
  });
});
