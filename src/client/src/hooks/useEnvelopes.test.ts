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
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() },
}));

import client from "@/lib/api-client";
import { toast } from "sonner";
import {
  useEnvelopes,
  useCreateEnvelope,
  useUpdateEnvelope,
  useDeleteEnvelope,
  useOccasions,
  useCreateOccasion,
  useUpdateOccasion,
  useDeleteOccasion,
  useItemTypeEnvelopeDefaults,
  useSetItemTypeEnvelopeDefault,
  useDeleteItemTypeEnvelopeDefault,
} from "./useEnvelopes";

const groceries = { id: "env-groceries", name: "Groceries", isActive: true };
const gifts = { id: "env-gifts", name: "Gifts", isActive: false };
const birthday = { id: "occ-birthday", name: "Birthday", isActive: true };
const defaultRow = {
  categoryId: "cat-food",
  envelopeId: "env-groceries",
  updatedAt: "2026-01-01T00:00:00Z",
};

function createQueryClient() {
  return new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: Infinity } },
  });
}

function createWrapper(queryClient = createQueryClient()) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return createElement(
      QueryClientProvider,
      { client: queryClient },
      children,
    );
  };
}

// Seed every envelope-family projection so each mutation's repair set is visible.
const projectionKeys = {
  envelopes: ["envelopes", "all"],
  occasions: ["occasions", "all"],
  defaults: ["item-type-envelope-defaults"],
  envelopeMappings: ["ynab", "envelope-mappings", "budget-1"],
  unmappedEnvelopes: ["ynab", "envelope-mappings", "unmapped", "budget-1"],
  unrelated: ["receipts"],
} as const;

function seededClient() {
  const queryClient = createQueryClient();
  for (const key of Object.values(projectionKeys))
    queryClient.setQueryData(key, { cached: true });
  return queryClient;
}

function invalidated(queryClient: QueryClient) {
  return Object.entries(projectionKeys)
    .filter(([, key]) => queryClient.getQueryState(key)?.isInvalidated)
    .map(([name]) => name)
    .sort();
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe("envelope reads", () => {
  it.each([
    ["useEnvelopes", useEnvelopes, "/api/envelopes", "envelopes", [groceries, gifts]],
    ["useOccasions", useOccasions, "/api/occasions", "occasions", [birthday]],
    [
      "useItemTypeEnvelopeDefaults",
      useItemTypeEnvelopeDefaults,
      "/api/item-type-envelope-defaults",
      "defaults",
      [defaultRow],
    ],
  ] as const)(
    "%s unwraps the list response and owns its errors locally",
    async (_name, useHook, path, field, rows) => {
      (client.GET as Mock).mockResolvedValue({
        data: { data: rows },
        error: undefined,
      });

      const { result } = renderHook(() => useHook(), {
        wrapper: createWrapper(),
      });

      await waitFor(() => expect(result.current.isSuccess).toBe(true));
      expect((result.current as Record<string, unknown>)[field]).toEqual(rows);
      expect(client.GET).toHaveBeenCalledWith(
        path,
        expect.objectContaining({
          middleware: expect.any(Array),
          signal: expect.any(AbortSignal),
        }),
      );
    },
  );

  it.each([
    ["useEnvelopes", useEnvelopes, "envelopes"],
    ["useOccasions", useOccasions, "occasions"],
    ["useItemTypeEnvelopeDefaults", useItemTypeEnvelopeDefaults, "defaults"],
  ] as const)(
    "%s reports an error with an empty list when the read fails",
    async (_name, useHook, field) => {
      (client.GET as Mock).mockResolvedValue({
        data: undefined,
        error: { status: 503, detail: "unavailable" },
      });

      const { result } = renderHook(() => useHook(), {
        wrapper: createWrapper(),
      });

      await waitFor(() => expect(result.current.isError).toBe(true));
      expect((result.current as Record<string, unknown>)[field]).toEqual([]);
      expect(toast.error).not.toHaveBeenCalled();
    },
  );

  it("does not request envelopes while disabled", () => {
    const { result } = renderHook(() => useEnvelopes(false), {
      wrapper: createWrapper(),
    });

    expect(result.current.envelopes).toEqual([]);
    expect(client.GET).not.toHaveBeenCalled();
  });
});

describe("envelope mutations", () => {
  it("useCreateEnvelope POSTs an active envelope and repairs envelope projections", async () => {
    (client.POST as Mock).mockResolvedValue({
      data: { id: "env-new", name: "Dining", isActive: true },
      error: undefined,
    });
    const queryClient = seededClient();
    const { result } = renderHook(() => useCreateEnvelope(), {
      wrapper: createWrapper(queryClient),
    });

    await result.current.mutateAsync("Dining");

    expect(client.POST).toHaveBeenCalledWith(
      "/api/envelopes",
      expect.objectContaining({ body: { name: "Dining", isActive: true } }),
    );
    expect(toast.success).toHaveBeenCalledWith("Envelope created");
    expect(invalidated(queryClient)).toEqual([
      "defaults",
      "envelopeMappings",
      "envelopes",
      "unmappedEnvelopes",
    ]);
  });

  it("useUpdateEnvelope PUTs name and archive state by id", async () => {
    (client.PUT as Mock).mockResolvedValue({ error: undefined });
    const queryClient = seededClient();
    const { result } = renderHook(() => useUpdateEnvelope(), {
      wrapper: createWrapper(queryClient),
    });

    await result.current.mutateAsync({ ...groceries, isActive: false });

    expect(client.PUT).toHaveBeenCalledWith(
      "/api/envelopes/{id}",
      expect.objectContaining({
        params: { path: { id: "env-groceries" } },
        body: { name: "Groceries", isActive: false },
      }),
    );
    expect(toast.success).toHaveBeenCalledWith("Envelope updated");
    expect(invalidated(queryClient)).toContain("envelopeMappings");
  });

  it("useDeleteEnvelope DELETEs by id", async () => {
    (client.DELETE as Mock).mockResolvedValue({ error: undefined });
    const queryClient = seededClient();
    const { result } = renderHook(() => useDeleteEnvelope(), {
      wrapper: createWrapper(queryClient),
    });

    await result.current.mutateAsync("env-groceries");

    expect(client.DELETE).toHaveBeenCalledWith(
      "/api/envelopes/{id}",
      expect.objectContaining({ params: { path: { id: "env-groceries" } } }),
    );
    expect(toast.success).toHaveBeenCalledWith("Envelope deleted");
    expect(invalidated(queryClient)).toContain("envelopes");
  });

  it("useCreateEnvelope rejects without a success toast or repair when the server refuses", async () => {
    (client.POST as Mock).mockResolvedValue({
      data: undefined,
      error: { status: 409, detail: "An envelope named Dining exists" },
    });
    const queryClient = seededClient();
    const { result } = renderHook(() => useCreateEnvelope(), {
      wrapper: createWrapper(queryClient),
    });

    await expect(result.current.mutateAsync("Dining")).rejects.toBeTruthy();

    expect(toast.success).not.toHaveBeenCalled();
    expect(invalidated(queryClient)).toEqual([]);
  });
});

describe("occasion mutations", () => {
  it("useCreateOccasion POSTs an active occasion and repairs only occasions", async () => {
    (client.POST as Mock).mockResolvedValue({
      data: birthday,
      error: undefined,
    });
    const queryClient = seededClient();
    const { result } = renderHook(() => useCreateOccasion(), {
      wrapper: createWrapper(queryClient),
    });

    await result.current.mutateAsync("Birthday");

    expect(client.POST).toHaveBeenCalledWith(
      "/api/occasions",
      expect.objectContaining({ body: { name: "Birthday", isActive: true } }),
    );
    expect(toast.success).toHaveBeenCalledWith("Occasion created");
    expect(invalidated(queryClient)).toEqual(["occasions"]);
  });

  it("useUpdateOccasion PUTs name and archive state by id", async () => {
    (client.PUT as Mock).mockResolvedValue({ error: undefined });
    const { result } = renderHook(() => useUpdateOccasion(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync({ ...birthday, name: "Birthdays" });

    expect(client.PUT).toHaveBeenCalledWith(
      "/api/occasions/{id}",
      expect.objectContaining({
        params: { path: { id: "occ-birthday" } },
        body: { name: "Birthdays", isActive: true },
      }),
    );
    expect(toast.success).toHaveBeenCalledWith("Occasion updated");
  });

  it("useDeleteOccasion DELETEs by id", async () => {
    (client.DELETE as Mock).mockResolvedValue({ error: undefined });
    const { result } = renderHook(() => useDeleteOccasion(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync("occ-birthday");

    expect(client.DELETE).toHaveBeenCalledWith(
      "/api/occasions/{id}",
      expect.objectContaining({ params: { path: { id: "occ-birthday" } } }),
    );
    expect(toast.success).toHaveBeenCalledWith("Occasion deleted");
  });
});

describe("item type default mutations", () => {
  it("useSetItemTypeEnvelopeDefault PUTs the envelope for the item type", async () => {
    (client.PUT as Mock).mockResolvedValue({
      data: defaultRow,
      error: undefined,
    });
    const queryClient = seededClient();
    const { result } = renderHook(() => useSetItemTypeEnvelopeDefault(), {
      wrapper: createWrapper(queryClient),
    });

    await result.current.mutateAsync({
      categoryId: "cat-food",
      envelopeId: "env-groceries",
    });

    expect(client.PUT).toHaveBeenCalledWith(
      "/api/item-type-envelope-defaults/{categoryId}",
      expect.objectContaining({
        params: { path: { categoryId: "cat-food" } },
        body: { envelopeId: "env-groceries" },
      }),
    );
    expect(toast.success).toHaveBeenCalledWith("Default envelope saved");
    expect(invalidated(queryClient)).toEqual(["defaults"]);
  });

  it("useDeleteItemTypeEnvelopeDefault DELETEs by item type", async () => {
    (client.DELETE as Mock).mockResolvedValue({ error: undefined });
    const queryClient = seededClient();
    const { result } = renderHook(() => useDeleteItemTypeEnvelopeDefault(), {
      wrapper: createWrapper(queryClient),
    });

    await result.current.mutateAsync("cat-food");

    expect(client.DELETE).toHaveBeenCalledWith(
      "/api/item-type-envelope-defaults/{categoryId}",
      expect.objectContaining({ params: { path: { categoryId: "cat-food" } } }),
    );
    expect(toast.success).toHaveBeenCalledWith("Default envelope removed");
    expect(invalidated(queryClient)).toEqual(["defaults"]);
  });
});
