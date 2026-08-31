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
  useAllCards,
  useCard,
  useCreateCard,
  useUpdateCard,
  useDeleteCard,
  useMergeCards,
  useMergeCardsPreview,
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

  it("list query trims q, sends it to the API, and refetches for a new q", async () => {
    (client.GET as Mock).mockResolvedValue({ data: { data: [], total: 0, offset: 0, limit: 50 } });
    const { result, rerender } = renderHook(
      ({ q }) => useCards(0, 50, null, null, null, { q }),
      { initialProps: { q: "  visa  " }, wrapper: createWrapper() },
    );
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.GET).toHaveBeenLastCalledWith("/api/cards", {
      params: { query: { offset: 0, limit: 50, q: "visa" } },
    });
    rerender({ q: "mastercard" });
    await waitFor(() => expect(client.GET).toHaveBeenCalledTimes(2));
    expect(client.GET).toHaveBeenLastCalledWith("/api/cards", {
      params: { query: { offset: 0, limit: 50, q: "mastercard" } },
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
    (client.DELETE as Mock).mockResolvedValue({ error: undefined, response: { status: 204, ok: true } });

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
    // RECEIPTS-886: the body is a problem document now. The prose moved from
    // `message` to `detail`; `transactionCount` stayed put, because RFC 9457
    // extension members serialise at the top level of the object.
    (client.DELETE as Mock).mockResolvedValue({
      error: {
        type: "https://tools.ietf.org/html/rfc9110#section-15.5.10",
        title: "Conflict",
        status: 409,
        detail: "Cannot delete — 3 transaction(s) reference this card",
        transactionCount: 3,
      },
      response: { status: 409, ok: false },
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
      response: { status: 500, ok: false },
    });

    const { result } = renderHook(() => useDeleteCard(), {
      wrapper: createWrapper(),
    });

    await expect(result.current.mutateAsync("1")).rejects.toThrow();

    await waitFor(() => {
      expect(toast.error).not.toHaveBeenCalled();
    });
  });

  it("merge mutation succeeds and reports what actually changed", async () => {
    (client.POST as Mock).mockResolvedValue({
      data: { accountsRemoved: 1, cardsMoved: 2, transactionsRepointed: 37 },
      error: undefined,
      response: { status: 200, ok: true },
    });

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
    expect(toast.success).toHaveBeenCalledWith("Cards merged", {
      description: "2 cards moved, 37 transactions repointed, 1 empty account removed.",
    });
    expect(toast.info).not.toHaveBeenCalled();
  });

  // RECEIPTS-893: the endpoint is idempotent, so this response is a success — but
  // reporting it as "Cards merged" is what made a merge that did nothing
  // indistinguishable from one that deleted accounts.
  it("merge mutation that changed nothing says so instead of claiming a merge", async () => {
    (client.POST as Mock).mockResolvedValue({
      data: { accountsRemoved: 0, cardsMoved: 0, transactionsRepointed: 0 },
      error: undefined,
      response: { status: 200, ok: true },
    });

    const { result } = renderHook(() => useMergeCards(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync({
      targetAccountId: "target",
      sourceCardIds: ["c1", "c2"],
    });

    expect(toast.info).toHaveBeenCalledWith("Nothing to merge", {
      description: "Every selected card already belonged to that account.",
    });
    expect(toast.success).not.toHaveBeenCalled();
  });

  it("merge success toast omits clauses with nothing behind them", async () => {
    // A merge between two accounts that had no transactions: claiming
    // "0 transactions repointed" reads like a partial failure.
    (client.POST as Mock).mockResolvedValue({
      data: { accountsRemoved: 1, cardsMoved: 1, transactionsRepointed: 0 },
      error: undefined,
      response: { status: 200, ok: true },
    });

    const { result } = renderHook(() => useMergeCards(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync({ targetAccountId: "t", sourceCardIds: ["c1", "c2"] });

    expect(toast.success).toHaveBeenCalledWith("Cards merged", {
      description: "1 card moved, 1 empty account removed.",
    });
  });

  it("merge mutation rejects with conflict object on 409 and does not toast error", async () => {
    (client.POST as Mock).mockResolvedValue({
      error: { message: "conflict", conflicts: [{ accountId: "a", accountName: "A", ynabBudgetId: "b", ynabAccountId: "y", ynabAccountName: "Y" }] },
      response: { status: 409, ok: false },
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
      response: { status: 500, ok: false },
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

  // A merge is destructive — it deletes the emptied source accounts — so the one
  // outcome the hook must never produce is "reported as merged when it wasn't".
  // 403 (RequireAdmin) and 404 (stale card/account) both arrive with an EMPTY
  // body, which openapi-fetch reports as `error: undefined`. Branching on
  // `error` instead of the status silently turned those into successes.
  it.each([403, 404])(
    "merge mutation rejects a bodiless %i instead of reporting success",
    async (status) => {
      (client.POST as Mock).mockResolvedValue({
        data: undefined,
        error: undefined,
        response: { status, ok: false },
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

      // The status must survive onto the thrown value: handleGlobalError keys
      // off it to decide whether to toast at all.
      expect(caught).toMatchObject({ status });
      expect(isMergeCardsConflict(caught)).toBe(false);
      await waitFor(() => {
        expect(toast.success).not.toHaveBeenCalled();
      });
    },
  );

  // `TypedResults.BadRequest("reason")` serialises to a bare JSON string, which
  // carries no status of its own. The reason is the only thing telling the user
  // why the merge was refused, so it has to reach the global handler intact.
  it("merge mutation does not crash on a 409 that carries no body", async () => {
    (client.POST as Mock).mockResolvedValue({
      data: undefined,
      error: undefined,
      response: { status: 409, ok: false },
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

    // Not a conflict object (there is nothing to resolve) and not a TypeError
    // from dereferencing an absent body — a plain, toastable API error.
    expect(isMergeCardsConflict(caught)).toBe(false);
    expect(caught).not.toBeInstanceOf(TypeError);
    expect(caught).toMatchObject({ status: 409 });
  });

  it("merge mutation preserves a bare-string 400 reason as ProblemDetails detail", async () => {
    const reason =
      "Source account would be partially merged: all of its cards must be included in the merge, or none.";
    (client.POST as Mock).mockResolvedValue({
      data: undefined,
      error: reason,
      response: { status: 400, ok: false },
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

    expect(caught).toMatchObject({ status: 400, detail: reason });
  });

});

describe("useAllCards", () => {
  it("auto-paginates across multiple pages", async () => {
    const pageOne = Array.from({ length: 500 }, (_, index) => ({
      id: `card-${index}`,
      cardCode: `CODE-${index}`,
      name: `Card ${index}`,
    }));
    const pageTwo = Array.from({ length: 100 }, (_, index) => ({
      id: `card-${500 + index}`,
      cardCode: `CODE-${500 + index}`,
      name: `Card ${500 + index}`,
    }));
    (client.GET as Mock).mockImplementation((_path, options) => {
      const offset = options.params.query.offset;
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

    const { result } = renderHook(() => useAllCards(true), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toHaveLength(600);
    expect(client.GET).toHaveBeenCalledTimes(2);
    expect(client.GET).toHaveBeenLastCalledWith("/api/cards", {
      params: {
        query: {
          offset: 500,
          limit: 500,
          sortBy: "name",
          sortDirection: "asc",
          isActive: true,
        },
      },
      signal: expect.any(AbortSignal),
    });
  });
});

// RECEIPTS-889. The preview exists so an irreversible operation is not confirmed blind.
describe("useMergeCardsPreview", () => {
  const PREVIEW = {
    accountsToRemove: [{ id: "a-source", name: "Source Account" }],
    cardsToMove: 2,
    transactionsToRepoint: 37,
    trashedTransactionsToRepoint: 4,
    survivingYnabMapping: null,
    conflicts: null,
  };

  it("posts the selection and returns the impact", async () => {
    (client.POST as Mock).mockResolvedValue({
      data: PREVIEW,
      error: undefined,
      response: { status: 200, ok: true },
    });

    const { result } = renderHook(
      () => useMergeCardsPreview({ targetAccountId: "t", sourceCardIds: ["c1", "c2"] }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.data).toEqual(PREVIEW));
    expect(client.POST).toHaveBeenCalledWith("/api/cards/merge/preview", {
      body: {
        targetAccountId: "t",
        sourceCardIds: ["c1", "c2"],
        ynabMappingWinnerAccountId: null,
      },
    });
  });

  // RECEIPTS-902. "New account" mode has to know a selection is valid before creating
  // the account, so the preview has to accept a target that does not exist yet.
  it("previews against a target that does not exist yet", async () => {
    (client.POST as Mock).mockResolvedValue({
      data: PREVIEW,
      error: undefined,
      response: { status: 200, ok: true },
    });

    const { result } = renderHook(
      () => useMergeCardsPreview({ targetAccountId: null, sourceCardIds: ["c1"] }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.data).toEqual(PREVIEW));
    expect(client.POST).toHaveBeenCalledWith("/api/cards/merge/preview", {
      body: {
        targetAccountId: null,
        sourceCardIds: ["c1"],
        ynabMappingWinnerAccountId: null,
      },
    });
  });

  it("does not fire with an empty selection, when there is nothing to describe", async () => {
    renderHook(
      () => useMergeCardsPreview({ targetAccountId: "t", sourceCardIds: [] }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(client.POST).not.toHaveBeenCalled());
  });

  it("does not fire when there is no selection to preview", async () => {
    renderHook(() => useMergeCardsPreview(null), { wrapper: createWrapper() });

    await waitFor(() => expect(client.POST).not.toHaveBeenCalled());
  });

  it("resolves to undefined on failure rather than toasting over an unsubmitted dialog", async () => {
    // A preview is speculative: the user has not asked for anything yet, so a failure
    // must stay quiet. The dialog holds submit, and the merge still validates.
    (client.POST as Mock).mockResolvedValue({
      data: undefined,
      error: { status: 403 },
      response: { status: 403, ok: false },
    });

    const { result } = renderHook(
      () => useMergeCardsPreview({ targetAccountId: "t", sourceCardIds: ["c1"] }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isFetching).toBe(false));
    // null, not undefined: React Query treats an undefined result as an error, which
    // would turn this quiet failure into the noisy one the hook is avoiding.
    expect(result.current.data).toBeNull();
    expect(result.current.isError).toBe(false);
    expect(toast.error).not.toHaveBeenCalled();
  });
});
