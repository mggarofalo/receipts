import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";
import { renderHook, waitFor } from "@testing-library/react";
import {
  QueryClient,
  QueryClientProvider,
  QueryObserver,
} from "@tanstack/react-query";
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
import {
  useSetReceiptBudgetChoices,
  useSetReceiptItemBudgetChoices,
} from "./useBudgetChoices";

function createWrapper(queryClient: QueryClient) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return createElement(
      QueryClientProvider,
      { client: queryClient },
      children,
    );
  };
}

// Seed the projections a budget choice changes, plus one it must leave alone.
const projectionKeys = {
  receipts: ["receipts"],
  receiptItems: ["receipt-items"],
  trips: ["trips"],
  envelopes: ["envelopes", "all"],
} as const;

function seededClient() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: Infinity } },
  });
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

describe("useSetReceiptBudgetChoices", () => {
  it("PUTs both choices for the receipt and repairs ledger projections", async () => {
    (client.PUT as Mock).mockResolvedValue({ error: undefined });
    const queryClient = seededClient();
    const { result } = renderHook(() => useSetReceiptBudgetChoices(), {
      wrapper: createWrapper(queryClient),
    });

    await result.current.mutateAsync({
      receiptId: "receipt-1",
      paidFromEnvelopeId: "env-gifts",
      occasionId: null,
    });

    expect(client.PUT).toHaveBeenCalledWith(
      "/api/receipts/{id}/budget-choices",
      expect.objectContaining({
        params: { path: { id: "receipt-1" } },
        body: { paidFromEnvelopeId: "env-gifts", occasionId: null },
      }),
    );
    expect(invalidated(queryClient)).toEqual([
      "receiptItems",
      "receipts",
      "trips",
    ]);
  });

  it("stays pending until an active receipt read has refetched", async () => {
    (client.PUT as Mock).mockResolvedValue({ error: undefined });
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    // A mounted receipt view: its refetch after the save is held open.
    let release!: () => void;
    let reads = 0;
    const observer = new QueryObserver(queryClient, {
      queryKey: ["trips", "receipt-1"],
      queryFn: async () => {
        reads++;
        if (reads > 1)
          await new Promise<void>((resolve) => {
            release = resolve;
          });
        return { paidFromEnvelopeId: reads > 1 ? "env-gifts" : null };
      },
    });
    const unsubscribe = observer.subscribe(() => {});
    await waitFor(() => expect(reads).toBe(1));
    const { result } = renderHook(() => useSetReceiptBudgetChoices(), {
      wrapper: createWrapper(queryClient),
    });

    result.current.mutate({
      receiptId: "receipt-1",
      paidFromEnvelopeId: "env-gifts",
      occasionId: null,
    });
    await waitFor(() => expect(reads).toBe(2));

    // Saved, but the view still shows the old choice: a follow-up choice
    // must not be offered yet.
    expect(client.PUT).toHaveBeenCalledTimes(1);
    expect(result.current.isPending).toBe(true);

    release();
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(queryClient.getQueryData(["trips", "receipt-1"])).toEqual({
      paidFromEnvelopeId: "env-gifts",
    });
    unsubscribe();
    queryClient.clear();
  });

  it("rejects without repairing anything when the server refuses", async () => {
    (client.PUT as Mock).mockResolvedValue({
      error: { status: 400, detail: "Envelope Old budget is archived" },
    });
    const queryClient = seededClient();
    const { result } = renderHook(() => useSetReceiptBudgetChoices(), {
      wrapper: createWrapper(queryClient),
    });

    await expect(
      result.current.mutateAsync({
        receiptId: "receipt-1",
        paidFromEnvelopeId: "env-old",
        occasionId: null,
      }),
    ).rejects.toMatchObject({ status: 400 });
    expect(invalidated(queryClient)).toEqual([]);
  });
});

describe("useSetReceiptItemBudgetChoices", () => {
  it("PUTs the item override and occasion and repairs ledger projections", async () => {
    (client.PUT as Mock).mockResolvedValue({ error: undefined });
    const queryClient = seededClient();
    const { result } = renderHook(() => useSetReceiptItemBudgetChoices(), {
      wrapper: createWrapper(queryClient),
    });

    await result.current.mutateAsync({
      itemId: "item-1",
      envelopeId: null,
      occasionId: "occ-birthday",
    });

    expect(client.PUT).toHaveBeenCalledWith(
      "/api/receipt-items/{id}/budget-choices",
      expect.objectContaining({
        params: { path: { id: "item-1" } },
        body: { envelopeId: null, occasionId: "occ-birthday" },
      }),
    );
    expect(invalidated(queryClient)).toEqual([
      "receiptItems",
      "receipts",
      "trips",
    ]);
  });

  it("rejects without repairing anything when the item is gone", async () => {
    (client.PUT as Mock).mockResolvedValue({ error: { status: 404 } });
    const queryClient = seededClient();
    const { result } = renderHook(() => useSetReceiptItemBudgetChoices(), {
      wrapper: createWrapper(queryClient),
    });

    await expect(
      result.current.mutateAsync({
        itemId: "missing",
        envelopeId: "env-gifts",
        occasionId: null,
      }),
    ).rejects.toMatchObject({ status: 404 });
    expect(invalidated(queryClient)).toEqual([]);
  });
});
