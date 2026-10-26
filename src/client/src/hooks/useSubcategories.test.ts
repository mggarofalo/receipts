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
import {
  useAllSubcategoriesByCategoryId,
  useDeletedSubcategories,
} from "./useSubcategories";

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

describe("useAllSubcategoriesByCategoryId", () => {
  it("is disabled when categoryId is null", () => {
    const { result } = renderHook(() => useAllSubcategoriesByCategoryId(null), {
      wrapper: createWrapper(),
    });

    expect(result.current.data).toBeUndefined();
    expect(result.current.fetchStatus).toBe("idle");
    expect(client.GET).not.toHaveBeenCalled();
  });

  it("returns the full list when it fits in one page", async () => {
    const subcategories = [
      { id: "1", name: "Bakery", categoryId: "cat-1" },
      { id: "2", name: "Produce", categoryId: "cat-1" },
    ];
    (client.GET as Mock).mockResolvedValue({
      data: { data: subcategories, total: 2, offset: 0, limit: 500 },
      error: undefined,
    });

    const { result } = renderHook(
      () => useAllSubcategoriesByCategoryId("cat-1", true),
      {
        wrapper: createWrapper(),
      },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(subcategories);
    expect(client.GET).toHaveBeenCalledTimes(1);
    expect(client.GET).toHaveBeenCalledWith("/api/subcategories", {
      params: {
        query: {
          categoryId: "cat-1",
          offset: 0,
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

  it("auto-paginates across multiple pages", async () => {
    const pageOne = Array.from({ length: 500 }, (_, i) => ({
      id: `${i}`,
      name: `Sub ${i}`,
      categoryId: "cat-1",
    }));
    const pageTwo = Array.from({ length: 50 }, (_, i) => ({
      id: `${500 + i}`,
      name: `Sub ${500 + i}`,
      categoryId: "cat-1",
    }));
    (client.GET as Mock).mockImplementation((_path, opts) => {
      const offset = opts.params.query.offset;
      return Promise.resolve({
        data: {
          data: offset === 0 ? pageOne : pageTwo,
          total: 550,
          offset,
          limit: 500,
        },
        error: undefined,
      });
    });

    const { result } = renderHook(
      () => useAllSubcategoriesByCategoryId("cat-1"),
      {
        wrapper: createWrapper(),
      },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toHaveLength(550);
    expect(client.GET).toHaveBeenCalledTimes(2);
  });
});

it("loads every deleted legacy child category before trash can purge it", async () => {
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
  const { result } = renderHook(() => useDeletedSubcategories(), {
    wrapper: createWrapper(),
  });
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(result.current.data).toHaveLength(501);
  expect(client.GET).toHaveBeenCalledTimes(2);
  expect(client.GET).toHaveBeenLastCalledWith("/api/subcategories/deleted", {
    params: { query: { offset: 500, limit: 500 } },
    signal: expect.any(AbortSignal),
  });
});
