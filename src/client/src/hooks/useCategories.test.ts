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
import { useAllCategories, useDeletedCategories } from "./useCategories";

function createWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  return function Wrapper({ children }: { children: ReactNode }) {
    return createElement(
      QueryClientProvider,
      { client: queryClient },
      children,
    );
  };
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe("useAllCategories", () => {
  it("useAllCategories returns the full list when it fits in one page", async () => {
    const categories = [
      { id: "1", name: "Apparel" },
      { id: "2", name: "Food" },
    ];
    (client.GET as Mock).mockResolvedValue({
      data: { data: categories, total: 2, offset: 0, limit: 500 },
      error: undefined,
    });

    const { result } = renderHook(() => useAllCategories(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(categories);
    expect(client.GET).toHaveBeenCalledTimes(1);
    expect(client.GET).toHaveBeenCalledWith("/api/categories", {
      params: {
        query: { offset: 0, limit: 500, sortBy: "name", sortDirection: "asc" },
      },
      middleware: expect.any(Array),
      signal: expect.any(AbortSignal),
    });
  });

  it("useAllCategories auto-paginates across multiple pages", async () => {
    const pageOne = Array.from({ length: 500 }, (_, i) => ({
      id: `${i}`,
      name: `Cat ${i}`,
    }));
    const pageTwo = Array.from({ length: 100 }, (_, i) => ({
      id: `${500 + i}`,
      name: `Cat ${500 + i}`,
    }));
    (client.GET as Mock).mockImplementation((_path, opts) => {
      const offset = opts.params.query.offset;
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

    const { result } = renderHook(() => useAllCategories(true), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toHaveLength(600);
    expect(client.GET).toHaveBeenCalledTimes(2);
    expect(client.GET).toHaveBeenLastCalledWith("/api/categories", {
      params: {
        query: {
          offset: 500,
          limit: 500,
          sortBy: "name",
          sortDirection: "asc",
          isActive: true,
        },
      },
      middleware: expect.any(Array),
      signal: expect.any(AbortSignal),
    });
  });
});

it("loads every deleted legacy category before trash can purge it", async () => {
  (client.GET as Mock)
    .mockResolvedValueOnce({
      data: {
        data: Array.from({ length: 500 }, (_, i) => ({
          id: String(i),
          name: `Category ${i}`,
        })),
        total: 501,
      },
      error: undefined,
    })
    .mockResolvedValueOnce({
      data: { data: [{ id: "500", name: "Last category" }], total: 501 },
      error: undefined,
    });
  const { result } = renderHook(() => useDeletedCategories(), {
    wrapper: createWrapper(),
  });
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(result.current.data).toHaveLength(501);
  expect(client.GET).toHaveBeenCalledTimes(2);
  expect(client.GET).toHaveBeenLastCalledWith("/api/categories/deleted", {
    params: { query: { offset: 500, limit: 500 } },
    signal: expect.any(AbortSignal),
  });
});
