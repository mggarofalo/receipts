import { createElement, type ReactNode } from "react";
import { act, renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import client from "@/lib/api-client";
import { useAccountCardSelection } from "./useAccountCardSelection";

vi.mock("@/lib/api-client", () => ({ default: { GET: vi.fn() } }));
const pair = { accountId: "account-1", cardId: "card-1" };
const card = {
  id: "card-1",
  accountId: "account-1",
  name: "Historic card",
  cardCode: "1234",
  isActive: false,
};
const key = ["cards", "byAccount", "account-1"];
function setup(cards?: (typeof card)[]) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime: Infinity, gcTime: 0 },
    },
  });
  queryClient.setQueryData(
    ["accounts", "all", undefined],
    [{ id: "account-1", name: "Historic account", isActive: false }],
  );
  if (cards) queryClient.setQueryData(key, cards);
  function wrapper({ children }: { children: ReactNode }) {
    return createElement(
      QueryClientProvider,
      { client: queryClient },
      children,
    );
  }
  return {
    queryClient,
    ...renderHook(() => useAccountCardSelection({ ...pair }), { wrapper }),
  };
}
beforeEach(() => vi.clearAllMocks());

describe("useAccountCardSelection", () => {
  it("preserves historical choices and stable result/callback identity across parent renders", () => {
    const { result, rerender } = setup([card]);
    const original = result.current;
    expect(original.accountOptions).toEqual([
      { value: "account-1", label: "Historic account" },
    ]);
    expect(original.cardOptions[0]).toMatchObject({
      value: "card-1",
      label: "Historic card (inactive)",
    });
    expect(original.validate(pair)).toBeUndefined();
    rerender();
    expect(result.current).toBe(original);
    expect(result.current.validate).toBe(original.validate);
  });

  it("validates against the latest successful data even before the caller rerenders", () => {
    const { queryClient, result } = setup([card]);
    const validate = result.current.validate;
    act(() => {
      queryClient.setQueryData(key, []);
    });
    expect(validate(pair)).toMatch(/no longer belongs to this account/);
    expect(result.current.value).toEqual(pair);
  });

  it("keeps an unverified historical pair through pending and failed lookup", async () => {
    let reject!: (reason: Error) => void;
    vi.mocked(client.GET).mockImplementation(
      () =>
        new Promise((_resolve, fail) => {
          reject = fail;
        }),
    );
    const { result } = setup();
    await waitFor(() => expect(result.current.cardsLoading).toBe(true));
    expect(result.current.validate(pair)).toBeUndefined();
    act(() => {
      reject(new Error("offline"));
    });
    await waitFor(() => expect(result.current.cardsError).toBe(true));
    expect(result.current.value).toEqual(pair);
    expect(result.current.validate(pair)).toBeUndefined();
  });

  it("retains known invalid membership if a later refresh fails", async () => {
    vi.mocked(client.GET).mockRejectedValue(new Error("offline"));
    const { result } = setup([]);
    expect(result.current.validate(pair)).toMatch(/no longer belongs/);
    await act(async () => {
      await result.current.retryCards();
    });
    await waitFor(() => expect(result.current.cardsError).toBe(true));
    expect(result.current.value).toEqual(pair);
    expect(result.current.validate(pair)).toMatch(/no longer belongs/);
  });
  it("preserves a previously verified historical card through a failed refetch", async () => {
    vi.mocked(client.GET).mockRejectedValue(new Error("offline"));
    const { result } = setup([card]);
    await act(async () => { await result.current.retryCards(); });
    await waitFor(() => expect(result.current.cardsError).toBe(true));
    expect(result.current.cardOptions[0]).toMatchObject({ value: "card-1", label: "Historic card (inactive)" });
    expect(result.current.validate(pair)).toBeUndefined();
  });

});
