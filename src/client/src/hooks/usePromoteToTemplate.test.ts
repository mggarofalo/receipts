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
import { usePromoteToTemplate } from "./usePromoteToTemplate";

function createWrapper(queryClient?: QueryClient) {
  const qc =
    queryClient ??
    new QueryClient({
      defaultOptions: {
        queries: { retry: false, gcTime: 0 },
        mutations: { retry: false },
      },
    });
  return function Wrapper({ children }: { children: ReactNode }) {
    return createElement(QueryClientProvider, { client: qc }, children);
  };
}

// Real API shape: /api/item-templates/similar returns a bare array of
// SimilarItemResponse objects (not a paginated envelope).
function similarItem(
  overrides: Partial<{
    name: string;
    similarity: number;
    combinedScore: number;
    source: "template" | "history";
    defaultCategory: string | null;
    defaultSubcategory: string | null;
    defaultUnitPrice: number | null;
    defaultItemCode: string | null;
  }> = {},
) {
  return {
    name: "Milk",
    similarity: 1,
    semanticSimilarity: null,
    combinedScore: 1,
    source: "history" as const,
    defaultCategory: null,
    defaultSubcategory: null,
    defaultUnitPrice: null,
    defaultItemCode: null,
    ...overrides,
  };
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe("usePromoteToTemplate", () => {
  it("creates a template when no duplicate template exists", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: [similarItem({ name: "Milk", source: "history" })],
      error: undefined,
    });
    (client.POST as Mock).mockResolvedValue({
      data: { id: "11111111-1111-1111-1111-111111111111", name: "Milk" },
      error: undefined,
    });

    const { result } = renderHook(() => usePromoteToTemplate(), {
      wrapper: createWrapper(),
    });

    result.current.mutate({
      name: "Milk",
      defaultCategory: "Food",
      defaultSubcategory: "Dairy",
      defaultUnitPrice: 3.5,
      defaultItemCode: "MILK-GAL",
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(client.GET).toHaveBeenCalledWith("/api/item-templates/similar", {
      params: { query: { q: "Milk", limit: 5, threshold: 0.3 } },
    });
    expect(client.POST).toHaveBeenCalledWith("/api/item-templates", {
      body: {
        name: "Milk",
        defaultCategory: "Food",
        defaultSubcategory: "Dairy",
        defaultUnitPrice: 3.5,
        defaultItemCode: "MILK-GAL",
      },
    });
    expect(result.current.data).toEqual({ created: true, name: "Milk" });
    expect(toast.success).toHaveBeenCalledWith('Saved "Milk" as a template');
    expect(toast.info).not.toHaveBeenCalled();
  });

  it("skips creation and shows an info toast when a template with the same name exists (case-insensitive)", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: [similarItem({ name: "MILK", source: "template" })],
      error: undefined,
    });

    const { result } = renderHook(() => usePromoteToTemplate(), {
      wrapper: createWrapper(),
    });

    result.current.mutate({ name: "milk" });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(client.POST).not.toHaveBeenCalled();
    expect(result.current.data).toEqual({ created: false, name: "milk" });
    expect(toast.info).toHaveBeenCalledWith(
      'A template named "milk" already exists',
    );
    expect(toast.success).not.toHaveBeenCalled();
  });

  it("does not treat a history result with the same name as a duplicate", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: [similarItem({ name: "milk", source: "history" })],
      error: undefined,
    });
    (client.POST as Mock).mockResolvedValue({
      data: { id: "11111111-1111-1111-1111-111111111111", name: "Milk" },
      error: undefined,
    });

    const { result } = renderHook(() => usePromoteToTemplate(), {
      wrapper: createWrapper(),
    });

    result.current.mutate({ name: "Milk" });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.POST).toHaveBeenCalledTimes(1);
    expect(result.current.data).toEqual({ created: true, name: "Milk" });
  });

  it("normalizes empty strings to null in the create body and keeps a zero unit price", async () => {
    (client.GET as Mock).mockResolvedValue({ data: [], error: undefined });
    (client.POST as Mock).mockResolvedValue({
      data: { id: "11111111-1111-1111-1111-111111111111", name: "Bread" },
      error: undefined,
    });

    const { result } = renderHook(() => usePromoteToTemplate(), {
      wrapper: createWrapper(),
    });

    result.current.mutate({
      name: "Bread",
      defaultCategory: "Food",
      defaultSubcategory: "",
      defaultUnitPrice: 0,
      defaultItemCode: "",
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.POST).toHaveBeenCalledWith("/api/item-templates", {
      body: {
        name: "Bread",
        defaultCategory: "Food",
        defaultSubcategory: null,
        defaultUnitPrice: 0,
        defaultItemCode: null,
      },
    });
  });

  it("invalidates the similarItems and itemTemplates caches after creating", async () => {
    (client.GET as Mock).mockResolvedValue({ data: [], error: undefined });
    (client.POST as Mock).mockResolvedValue({
      data: { id: "11111111-1111-1111-1111-111111111111", name: "Milk" },
      error: undefined,
    });
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false, gcTime: 0 },
        mutations: { retry: false },
      },
    });
    const invalidateSpy = vi.spyOn(queryClient, "invalidateQueries");

    const { result } = renderHook(() => usePromoteToTemplate(), {
      wrapper: createWrapper(queryClient),
    });

    result.current.mutate({ name: "Milk" });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ["itemTemplates"],
    });
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ["similarItems"],
    });
  });

  it("does not invalidate caches when the duplicate guard skips creation", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: [similarItem({ name: "Milk", source: "template" })],
      error: undefined,
    });
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false, gcTime: 0 },
        mutations: { retry: false },
      },
    });
    const invalidateSpy = vi.spyOn(queryClient, "invalidateQueries");

    const { result } = renderHook(() => usePromoteToTemplate(), {
      wrapper: createWrapper(queryClient),
    });

    result.current.mutate({ name: "Milk" });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(invalidateSpy).not.toHaveBeenCalled();
  });

  it("shows an error toast when the create request fails", async () => {
    (client.GET as Mock).mockResolvedValue({ data: [], error: undefined });
    (client.POST as Mock).mockResolvedValue({
      data: undefined,
      error: { message: "Internal Server Error" },
    });

    const { result } = renderHook(() => usePromoteToTemplate(), {
      wrapper: createWrapper(),
    });

    result.current.mutate({ name: "Milk" });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(toast.error).toHaveBeenCalledWith("Failed to save as template");
    expect(toast.success).not.toHaveBeenCalled();
  });

  it("errors without creating when the duplicate check fails", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: undefined,
      error: { message: "Internal Server Error" },
    });

    const { result } = renderHook(() => usePromoteToTemplate(), {
      wrapper: createWrapper(),
    });

    result.current.mutate({ name: "Milk" });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(client.POST).not.toHaveBeenCalled();
    expect(toast.error).toHaveBeenCalledWith("Failed to save as template");
  });

  it("trims the name before checking for duplicates and creating", async () => {
    (client.GET as Mock).mockResolvedValue({ data: [], error: undefined });
    (client.POST as Mock).mockResolvedValue({
      data: { id: "11111111-1111-1111-1111-111111111111", name: "Milk" },
      error: undefined,
    });

    const { result } = renderHook(() => usePromoteToTemplate(), {
      wrapper: createWrapper(),
    });

    result.current.mutate({ name: "  Milk  " });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenCalledWith("/api/item-templates/similar", {
      params: { query: { q: "Milk", limit: 5, threshold: 0.3 } },
    });
    expect(client.POST).toHaveBeenCalledWith(
      "/api/item-templates",
      expect.objectContaining({
        body: expect.objectContaining({ name: "Milk" }),
      }),
    );
  });
});
