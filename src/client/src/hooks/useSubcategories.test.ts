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
  useSubcategories,
  useSubcategory,
  useSubcategoriesByCategoryId,
  useAllSubcategoriesByCategoryId,
  useCreateSubcategory,
  useUpdateSubcategory,
} from "./useSubcategories";

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

describe("useSubcategories", () => {
  it("list query returns data on success", async () => {
    const subcategories = [
      { id: "1", name: "Produce", categoryId: "cat-1", description: null },
    ];
    (client.GET as Mock).mockResolvedValue({
      data: { data: subcategories, total: 1, offset: 0, limit: 50 },
      error: undefined,
    });

    const { result } = renderHook(() => useSubcategories(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(subcategories);
    expect(client.GET).toHaveBeenCalledWith("/api/subcategories", {
      params: { query: { offset: 0, limit: 50 } },
    });
  });

  it("list query passes isActive filter when provided", async () => {
    const subcategories = [
      { id: "1", name: "Produce", categoryId: "cat-1", description: null, isActive: true },
    ];
    (client.GET as Mock).mockResolvedValue({
      data: { data: subcategories, total: 1, offset: 0, limit: 50 },
      error: undefined,
    });

    const { result } = renderHook(() => useSubcategories(0, 50, null, null, false), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenCalledWith("/api/subcategories", {
      params: { query: { offset: 0, limit: 50, isActive: false } },
    });
  });

  it("list query trims q, sends it to the API, and refetches for a new q", async () => {
    (client.GET as Mock).mockResolvedValue({ data: { data: [], total: 0, offset: 0, limit: 50 } });
    const { result, rerender } = renderHook(
      ({ q }) => useSubcategories(0, 50, null, null, null, { q }),
      { initialProps: { q: "  produce  " }, wrapper: createWrapper() },
    );
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenLastCalledWith("/api/subcategories", {
      params: { query: { offset: 0, limit: 50, q: "produce" } },
    });
    rerender({ q: "dairy" });
    await waitFor(() => expect(client.GET).toHaveBeenCalledTimes(2));
    expect(client.GET).toHaveBeenLastCalledWith("/api/subcategories", {
      params: { query: { offset: 0, limit: 50, q: "dairy" } },
    });
  });

  it("single query is disabled when id is null", () => {
    const { result } = renderHook(() => useSubcategory(null), {
      wrapper: createWrapper(),
    });

    expect(result.current.data).toBeUndefined();
    expect(result.current.fetchStatus).toBe("idle");
    expect(client.GET).not.toHaveBeenCalled();
  });

  it("single query fetches data when id is provided", async () => {
    const subcategory = { id: "1", name: "Produce", categoryId: "cat-1" };
    (client.GET as Mock).mockResolvedValue({
      data: subcategory,
      error: undefined,
    });

    const { result } = renderHook(() => useSubcategory("1"), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(subcategory);
  });

  it("by-category query returns data when categoryId is provided", async () => {
    const items = [{ id: "1", name: "Produce", categoryId: "cat-1" }];
    (client.GET as Mock).mockResolvedValue({ data: { data: items, total: 1, offset: 0, limit: 200 }, error: undefined });

    const { result } = renderHook(() => useSubcategoriesByCategoryId("cat-1"), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(items);
    expect(client.GET).toHaveBeenCalledWith(
      "/api/subcategories",
      { params: { query: { categoryId: "cat-1", offset: 0, limit: 200 } } },
    );
  });

  it("by-category query is disabled when categoryId is null", () => {
    const { result } = renderHook(() => useSubcategoriesByCategoryId(null), {
      wrapper: createWrapper(),
    });

    expect(result.current.data).toBeUndefined();
    expect(result.current.fetchStatus).toBe("idle");
  });

  it("create mutation calls POST and shows toast on success", async () => {
    const newSub = { name: "Dairy", categoryId: "cat-1", description: "Milk products", isActive: true };
    const created = { id: "2", ...newSub };
    (client.POST as Mock).mockResolvedValue({ data: created, error: undefined });

    const { result } = renderHook(() => useCreateSubcategory(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync(newSub);

    expect(client.POST).toHaveBeenCalledWith("/api/subcategories", { body: newSub });
    expect(toast.success).toHaveBeenCalledWith("Subcategory created");
  });

  it("update mutation calls PUT and shows toast on success", async () => {
    const updated = { id: "1", name: "Organic Produce", categoryId: "cat-1", isActive: true };
    (client.PUT as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useUpdateSubcategory(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync(updated);

    expect(client.PUT).toHaveBeenCalledWith("/api/subcategories/{id}", {
      params: { path: { id: "1" } },
      body: updated,
    });
    expect(toast.success).toHaveBeenCalledWith("Subcategory updated");
  });

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

    const { result } = renderHook(() => useAllSubcategoriesByCategoryId("cat-1", true), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(subcategories);
    expect(client.GET).toHaveBeenCalledTimes(1);
    expect(client.GET).toHaveBeenCalledWith("/api/subcategories", {
      params: {
        query: { categoryId: "cat-1", offset: 0, limit: 500, sortBy: "name", sortDirection: "asc", isActive: true },
      },
      signal: expect.any(AbortSignal),
    });
  });

  it("auto-paginates across multiple pages", async () => {
    const pageOne = Array.from({ length: 500 }, (_, i) => ({ id: `${i}`, name: `Sub ${i}`, categoryId: "cat-1" }));
    const pageTwo = Array.from({ length: 50 }, (_, i) => ({ id: `${500 + i}`, name: `Sub ${500 + i}`, categoryId: "cat-1" }));
    (client.GET as Mock).mockImplementation((_path, opts) => {
      const offset = opts.params.query.offset;
      return Promise.resolve({
        data: { data: offset === 0 ? pageOne : pageTwo, total: 550, offset, limit: 500 },
        error: undefined,
      });
    });

    const { result } = renderHook(() => useAllSubcategoriesByCategoryId("cat-1"), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toHaveLength(550);
    expect(client.GET).toHaveBeenCalledTimes(2);
  });
});
