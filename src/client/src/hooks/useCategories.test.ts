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
  useCategories,
  useAllCategories,
  useCategory,
  useCreateCategory,
  useUpdateCategory,
} from "./useCategories";

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

describe("useCategories", () => {
  it("list query returns data on success", async () => {
    const categories = [{ id: "1", name: "Food", description: "Groceries" }];
    (client.GET as Mock).mockResolvedValue({ data: { data: categories, total: 1, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useCategories(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(categories);
    expect(client.GET).toHaveBeenCalledWith("/api/categories", {
      params: { query: { offset: 0, limit: 50 } },
    });
  });

  it("list query passes isActive filter when provided", async () => {
    const categories = [{ id: "1", name: "Food", description: "Groceries", isActive: true }];
    (client.GET as Mock).mockResolvedValue({ data: { data: categories, total: 1, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useCategories(0, 50, null, null, true), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenCalledWith("/api/categories", {
      params: { query: { offset: 0, limit: 50, isActive: true } },
    });
  });

  it("list query omits isActive when null", async () => {
    const categories = [{ id: "1", name: "Food", description: "Groceries" }];
    (client.GET as Mock).mockResolvedValue({ data: { data: categories, total: 1, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useCategories(0, 50, null, null, null), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenCalledWith("/api/categories", {
      params: { query: { offset: 0, limit: 50 } },
    });
  });

  it("list query trims q, sends it to the API, and refetches for a new q", async () => {
    (client.GET as Mock).mockResolvedValue({ data: { data: [], total: 0, offset: 0, limit: 50 } });
    const { result, rerender } = renderHook(
      ({ q }) => useCategories(0, 50, null, null, null, { q }),
      { initialProps: { q: "  food  " }, wrapper: createWrapper() },
    );
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenLastCalledWith("/api/categories", {
      params: { query: { offset: 0, limit: 50, q: "food" } },
    });
    rerender({ q: "travel" });
    await waitFor(() => expect(client.GET).toHaveBeenCalledTimes(2));
    expect(client.GET).toHaveBeenLastCalledWith("/api/categories", {
      params: { query: { offset: 0, limit: 50, q: "travel" } },
    });
  });

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
      params: { query: { offset: 0, limit: 500, sortBy: "name", sortDirection: "asc" } },
      signal: expect.any(AbortSignal),
    });
  });

  it("useAllCategories auto-paginates across multiple pages", async () => {
    const pageOne = Array.from({ length: 500 }, (_, i) => ({ id: `${i}`, name: `Cat ${i}` }));
    const pageTwo = Array.from({ length: 100 }, (_, i) => ({ id: `${500 + i}`, name: `Cat ${500 + i}` }));
    (client.GET as Mock).mockImplementation((_path, opts) => {
      const offset = opts.params.query.offset;
      return Promise.resolve({
        data: { data: offset === 0 ? pageOne : pageTwo, total: 600, offset, limit: 500 },
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
      params: { query: { offset: 500, limit: 500, sortBy: "name", sortDirection: "asc", isActive: true } },
      signal: expect.any(AbortSignal),
    });
  });

  it("single query is disabled when id is null", () => {
    const { result } = renderHook(() => useCategory(null), {
      wrapper: createWrapper(),
    });

    expect(result.current.data).toBeUndefined();
    expect(result.current.fetchStatus).toBe("idle");
    expect(client.GET).not.toHaveBeenCalled();
  });

  it("single query fetches data when id is provided", async () => {
    const category = { id: "1", name: "Food", description: "Groceries" };
    (client.GET as Mock).mockResolvedValue({ data: category, error: undefined });

    const { result } = renderHook(() => useCategory("1"), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(category);
  });

  it("create mutation calls POST and shows toast on success", async () => {
    const newCategory = { name: "Travel", description: "Business travel", isActive: true };
    const created = { id: "2", ...newCategory };
    (client.POST as Mock).mockResolvedValue({ data: created, error: undefined });

    const { result } = renderHook(() => useCreateCategory(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync(newCategory);

    expect(client.POST).toHaveBeenCalledWith("/api/categories", { body: newCategory });
    expect(toast.success).toHaveBeenCalledWith("Category created");
  });

  it("update mutation calls PUT and shows toast on success", async () => {
    const updated = { id: "1", name: "Food & Drink", description: "Updated", isActive: true };
    (client.PUT as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useUpdateCategory(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync(updated);

    expect(client.PUT).toHaveBeenCalledWith("/api/categories/{id}", {
      params: { path: { id: "1" } },
      body: updated,
    });
    expect(toast.success).toHaveBeenCalledWith("Category updated");
  });

});
