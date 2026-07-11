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
  useItemTemplates,
  useItemTemplate,
  useCreateItemTemplate,
  useUpdateItemTemplate,
  useDeleteItemTemplates,
  useHideItemTemplate,
  useDeletedItemTemplates,
  useRestoreItemTemplate,
} from "./useItemTemplates";

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

describe("useItemTemplates", () => {
  it("list query returns data on success", async () => {
    const templates = [
      {
        id: "1",
        name: "Grocery Item",
        description: "Default grocery template",
        defaultCategory: "Food",
        defaultSubcategory: "Produce",
        defaultUnitPrice: 1.0,
        defaultItemCode: "GRO",
      },
    ];
    (client.GET as Mock).mockResolvedValue({ data: { data: templates, total: 1, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useItemTemplates(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(templates);
    expect(client.GET).toHaveBeenCalledWith("/api/item-templates", {
      params: { query: { offset: 0, limit: 50 } },
    });
  });

  it("single query is disabled when id is null", () => {
    const { result } = renderHook(() => useItemTemplate(null), {
      wrapper: createWrapper(),
    });

    expect(result.current.data).toBeUndefined();
    expect(result.current.fetchStatus).toBe("idle");
    expect(client.GET).not.toHaveBeenCalled();
  });

  it("single query fetches data when id is provided", async () => {
    const template = { id: "1", name: "Grocery Item", description: null };
    (client.GET as Mock).mockResolvedValue({ data: template, error: undefined });

    const { result } = renderHook(() => useItemTemplate("1"), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(template);
  });

  it("create mutation calls POST and shows toast on success", async () => {
    const newTemplate = {
      name: "Gas Template",
      description: "For fuel purchases",
      defaultCategory: "Transport",
      defaultSubcategory: null,
      defaultUnitPrice: null,
      defaultItemCode: "GAS",
    };
    const created = { id: "2", ...newTemplate };
    (client.POST as Mock).mockResolvedValue({ data: created, error: undefined });

    const { result } = renderHook(() => useCreateItemTemplate(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync(newTemplate);

    expect(client.POST).toHaveBeenCalledWith("/api/item-templates", {
      body: newTemplate,
    });
    expect(toast.success).toHaveBeenCalledWith("Item template created");
  });

  it("update mutation calls PUT and shows toast on success", async () => {
    const updated = {
      id: "1",
      name: "Grocery Item (v2)",
      description: "Updated",
      defaultCategory: "Food",
      defaultSubcategory: "Dairy",
      defaultUnitPrice: 2.0,
      defaultItemCode: "GRO",
    };
    (client.PUT as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useUpdateItemTemplate(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync(updated);

    expect(client.PUT).toHaveBeenCalledWith("/api/item-templates/{id}", {
      params: { path: { id: "1" } },
      body: updated,
    });
    expect(toast.success).toHaveBeenCalledWith("Item template updated");
  });

  it("delete mutation calls DELETE", async () => {
    (client.DELETE as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useDeleteItemTemplates(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync(["1", "2"]);

    expect(client.DELETE).toHaveBeenCalledWith("/api/item-templates", {
      body: ["1", "2"],
    });
    expect(toast.success).toHaveBeenCalledWith("Item template(s) deleted");
  });

  it("deleted item templates query returns data on success", async () => {
    const deleted = [{ id: "3", name: "Old Template", description: null }];
    (client.GET as Mock).mockResolvedValue({ data: { data: deleted, total: 1, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useDeletedItemTemplates(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(deleted);
    expect(client.GET).toHaveBeenCalledWith("/api/item-templates/deleted", {
      params: { query: { offset: 0, limit: 50 } },
    });
  });

  it("restore mutation calls POST and shows toast on success", async () => {
    (client.POST as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useRestoreItemTemplate(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync("1");

    expect(client.POST).toHaveBeenCalledWith("/api/item-templates/{id}/restore", {
      params: { path: { id: "1" } },
    });
    expect(toast.success).toHaveBeenCalledWith("Item template restored");
  });

  // --- Branch coverage: error callbacks ---

  it("create mutation does not toast on failure (surfaced by the global handler)", async () => {
    (client.POST as Mock).mockResolvedValue({ error: { message: "Server error" } });

    const { result } = renderHook(() => useCreateItemTemplate(), {
      wrapper: createWrapper(),
    });

    result.current.mutate({ name: "Template", description: null });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("update mutation does not toast on failure (surfaced by the global handler)", async () => {
    (client.PUT as Mock).mockResolvedValue({ error: { message: "Server error" } });

    const { result } = renderHook(() => useUpdateItemTemplate(), {
      wrapper: createWrapper(),
    });

    result.current.mutate({ id: "1", name: "Template", description: null });

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

    const templates = [
      { id: "1", name: "A" },
      { id: "2", name: "B" },
    ];
    const cacheKey = ["itemTemplates", "list", 0, 50, undefined, undefined];
    const cacheValue = { data: templates, total: 2, offset: 0, limit: 50 };
    queryClient.setQueryData(cacheKey, cacheValue);
    setQueryDataSpy.mockClear();

    (client.DELETE as Mock).mockResolvedValue({ error: { message: "Server error" } });

    const { result } = renderHook(() => useDeleteItemTemplates(), {
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

    queryClient.setQueryData(["itemTemplates", "list", 0, 50, undefined, undefined], undefined);

    (client.DELETE as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useDeleteItemTemplates(), {
      wrapper: Wrapper,
    });

    result.current.mutate(["1"]);

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(toast.success).toHaveBeenCalledWith("Item template(s) deleted");
  });

  it("restore mutation does not toast on failure (surfaced by the global handler)", async () => {
    (client.POST as Mock).mockResolvedValue({ error: { message: "Server error" } });

    const { result } = renderHook(() => useRestoreItemTemplate(), {
      wrapper: createWrapper(),
    });

    result.current.mutate("1");

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("list query throws on API error", async () => {
    (client.GET as Mock).mockResolvedValue({ data: undefined, error: { message: "Server error" } });

    const { result } = renderHook(() => useItemTemplates(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
  });

  it("list query passes sort parameters", async () => {
    (client.GET as Mock).mockResolvedValue({ data: { data: [], total: 0, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useItemTemplates(0, 50, "name", "asc"), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenCalledWith("/api/item-templates", {
      params: { query: { offset: 0, limit: 50, sortBy: "name", sortDirection: "asc" } },
    });
  });

  it("list query returns total of 0 when data is undefined", async () => {
    (client.GET as Mock).mockResolvedValue({ data: undefined, error: { message: "err" } });

    const { result } = renderHook(() => useItemTemplates(), {
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

    const { result } = renderHook(() => useDeleteItemTemplates(), {
      wrapper: Wrapper,
    });

    result.current.mutate(["1"]);

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ["itemTemplates"] });
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ["itemTemplates", "deleted"] });
  });

  // --- useHideItemTemplate ---

  it("hide mutation calls DELETE with single-element array and shows custom toast", async () => {
    (client.DELETE as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useHideItemTemplate(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync("template-1");

    expect(client.DELETE).toHaveBeenCalledWith("/api/item-templates", {
      body: ["template-1"],
    });
    expect(toast.success).toHaveBeenCalledWith(
      "Template hidden. You can restore it from the recycle bin.",
    );
  });

  it("hide mutation does not toast on failure (surfaced by the global handler)", async () => {
    (client.DELETE as Mock).mockResolvedValue({ error: { message: "Server error" } });

    const { result } = renderHook(() => useHideItemTemplate(), {
      wrapper: createWrapper(),
    });

    result.current.mutate("template-1");

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("hide mutation rolls back cache on failure", async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: 0 } },
    });
    const setQueryDataSpy = vi.spyOn(queryClient, "setQueryData");

    function Wrapper({ children }: { children: ReactNode }) {
      return createElement(QueryClientProvider, { client: queryClient }, children);
    }

    const templates = [
      { id: "template-1", name: "A" },
      { id: "template-2", name: "B" },
    ];
    const cacheKey = ["itemTemplates", "list", 0, 50, undefined, undefined];
    const cacheValue = { data: templates, total: 2, offset: 0, limit: 50 };
    queryClient.setQueryData(cacheKey, cacheValue);
    setQueryDataSpy.mockClear();

    (client.DELETE as Mock).mockResolvedValue({ error: { message: "Server error" } });

    const { result } = renderHook(() => useHideItemTemplate(), {
      wrapper: Wrapper,
    });

    result.current.mutate("template-1");

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(toast.error).not.toHaveBeenCalled();
    expect(setQueryDataSpy).toHaveBeenCalledWith(cacheKey, cacheValue);
  });

  it("hide mutation invalidates both list and deleted query keys on settled", async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: 0 } },
    });
    const invalidateSpy = vi.spyOn(queryClient, "invalidateQueries");

    function Wrapper({ children }: { children: ReactNode }) {
      return createElement(QueryClientProvider, { client: queryClient }, children);
    }

    (client.DELETE as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useHideItemTemplate(), {
      wrapper: Wrapper,
    });

    result.current.mutate("template-1");

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ["itemTemplates"] });
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ["itemTemplates", "deleted"] });
  });
});
