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
  useCards,
  useCard,
  useCreateCard,
  useUpdateCard,
  useDeleteCard,
  useMergeCards,
  isMergeCardsConflict,
} from "./useCards";

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

describe("useCards", () => {
  it("list query returns data on success", async () => {
    const cards = [
      { id: "1", cardCode: "CARD1", name: "Checking", isActive: true },
    ];
    (client.GET as Mock).mockResolvedValue({ data: { data: cards, total: 1, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useCards(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(cards);
    expect(client.GET).toHaveBeenCalledWith("/api/cards", {
      params: { query: { offset: 0, limit: 50 } },
    });
  });

  it("list query passes isActive filter to API", async () => {
    const cards = [
      { id: "1", cardCode: "CARD1", name: "Checking", isActive: true },
    ];
    (client.GET as Mock).mockResolvedValue({ data: { data: cards, total: 1, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useCards(0, 50, null, null, true), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenCalledWith("/api/cards", {
      params: { query: { offset: 0, limit: 50, isActive: true } },
    });
  });

  it("list query omits isActive when null", async () => {
    const cards = [
      { id: "1", cardCode: "CARD1", name: "Checking", isActive: true },
    ];
    (client.GET as Mock).mockResolvedValue({ data: { data: cards, total: 1, offset: 0, limit: 50 }, error: undefined });

    const { result } = renderHook(() => useCards(0, 50, null, null, null), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenCalledWith("/api/cards", {
      params: { query: { offset: 0, limit: 50 } },
    });
  });

  it("single query is disabled when id is null", () => {
    const { result } = renderHook(() => useCard(null), {
      wrapper: createWrapper(),
    });

    expect(result.current.data).toBeUndefined();
    expect(result.current.fetchStatus).toBe("idle");
    expect(client.GET).not.toHaveBeenCalled();
  });

  it("single query fetches data when id is provided", async () => {
    const card = { id: "1", cardCode: "CARD1", name: "Checking", isActive: true };
    (client.GET as Mock).mockResolvedValue({ data: card, error: undefined });

    const { result } = renderHook(() => useCard("1"), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(card);
  });

  it("create mutation calls POST and shows toast on success", async () => {
    const newCard = { cardCode: "CARD2", name: "Savings", isActive: true, accountId: "acct-1" };
    const created = { id: "2", ...newCard };
    (client.POST as Mock).mockResolvedValue({ data: created, error: undefined });

    const { result } = renderHook(() => useCreateCard(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync(newCard);

    expect(client.POST).toHaveBeenCalledWith("/api/cards", { body: newCard });
    expect(toast.success).toHaveBeenCalledWith("Card created");
  });

  it("update mutation calls PUT and shows toast on success", async () => {
    const updated = { id: "1", cardCode: "CARD1", name: "Updated", isActive: false, accountId: "acct-1" };
    (client.PUT as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useUpdateCard(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync(updated);

    expect(client.PUT).toHaveBeenCalledWith("/api/cards/{id}", {
      params: { path: { id: "1" } },
      body: updated,
    });
    expect(toast.success).toHaveBeenCalledWith("Card updated");
  });

  it("delete mutation calls DELETE and shows toast on success", async () => {
    (client.DELETE as Mock).mockResolvedValue({ error: undefined, response: { status: 204 } });

    const { result } = renderHook(() => useDeleteCard(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync("1");

    expect(client.DELETE).toHaveBeenCalledWith("/api/cards/{id}", {
      params: { path: { id: "1" } },
    });
    expect(toast.success).toHaveBeenCalledWith("Card deleted");
  });

  it("delete mutation shows conflict toast on 409", async () => {
    (client.DELETE as Mock).mockResolvedValue({
      error: { message: "Cannot delete — 3 transaction(s) reference this card", transactionCount: 3 },
      response: { status: 409 },
    });

    const { result } = renderHook(() => useDeleteCard(), {
      wrapper: createWrapper(),
    });

    await expect(result.current.mutateAsync("1")).rejects.toThrow();

    await waitFor(() => {
      expect(toast.error).toHaveBeenCalledWith(
        "Cannot delete — 3 transaction(s) reference this card",
      );
    });
  });

  it("delete mutation does not toast on non-409 failure (surfaced by the global handler)", async () => {
    (client.DELETE as Mock).mockResolvedValue({
      error: { message: "Server error" },
      response: { status: 500 },
    });

    const { result } = renderHook(() => useDeleteCard(), {
      wrapper: createWrapper(),
    });

    await expect(result.current.mutateAsync("1")).rejects.toThrow();

    await waitFor(() => {
      expect(toast.error).not.toHaveBeenCalled();
    });
  });

  it("merge mutation succeeds and shows success toast", async () => {
    (client.POST as Mock).mockResolvedValue({ data: { success: true }, error: undefined, response: { status: 200 } });

    const { result } = renderHook(() => useMergeCards(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync({
      targetAccountId: "target",
      sourceCardIds: ["c1", "c2"],
    });

    expect(client.POST).toHaveBeenCalledWith("/api/cards/merge", {
      body: {
        targetAccountId: "target",
        sourceCardIds: ["c1", "c2"],
        ynabMappingWinnerAccountId: null,
      },
    });
    expect(toast.success).toHaveBeenCalledWith("Cards merged");
  });

  it("merge mutation rejects with conflict object on 409 and does not toast error", async () => {
    (client.POST as Mock).mockResolvedValue({
      error: { message: "conflict", conflicts: [{ accountId: "a", accountName: "A", ynabBudgetId: "b", ynabAccountId: "y", ynabAccountName: "Y" }] },
      response: { status: 409 },
    });

    const { result } = renderHook(() => useMergeCards(), {
      wrapper: createWrapper(),
    });

    let caught: unknown = null;
    try {
      await result.current.mutateAsync({ targetAccountId: "t", sourceCardIds: ["c1", "c2"] });
    } catch (err) {
      caught = err;
    }

    expect(isMergeCardsConflict(caught)).toBe(true);
    await waitFor(() => {
      expect(toast.error).not.toHaveBeenCalled();
    });
  });

  it("merge mutation does not toast on non-409 failure (surfaced by the global handler)", async () => {
    (client.POST as Mock).mockResolvedValue({
      error: { message: "boom" },
      response: { status: 500 },
    });

    const { result } = renderHook(() => useMergeCards(), {
      wrapper: createWrapper(),
    });

    await expect(
      result.current.mutateAsync({ targetAccountId: "t", sourceCardIds: ["c1", "c2"] }),
    ).rejects.toThrow();

    await waitFor(() => {
      expect(toast.error).not.toHaveBeenCalled();
    });
  });

});
