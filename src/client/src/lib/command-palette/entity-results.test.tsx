import { describe, expect, it, vi, beforeEach } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { useEntityResults } from "./entity-results";
import { mockQueryResult } from "@/test/mock-hooks";

vi.mock("@/hooks/useAccounts", () => ({
  useAccounts: vi.fn(() => mockQueryResult()),
}));
vi.mock("@/hooks/useCards", () => ({
  useCards: vi.fn(() => mockQueryResult()),
}));
vi.mock("@/hooks/useCategoryTaxonomy", () => ({
  useCategoryTaxonomy: vi.fn(() => mockQueryResult()),
}));
vi.mock("@/hooks/useItemTemplates", () => ({
  useItemTemplates: vi.fn(() => mockQueryResult()),
}));
vi.mock("@/hooks/useReceipts", () => ({
  useReceipts: vi.fn(() => mockQueryResult()),
}));
vi.mock("@/hooks/useReceiptItems", () => ({
  useReceiptItems: vi.fn(() => mockQueryResult()),
}));
vi.mock("@/hooks/useUsers", () => ({
  useUsers: vi.fn(() => mockQueryResult()),
}));

beforeEach(() => {
  vi.clearAllMocks();
});

describe("useEntityResults", () => {
  it("returns no groups when all hooks have no data", () => {
    const { result } = renderHook(() =>
      useEntityResults({ isAdmin: false, open: true }),
    );
    expect(result.current).toEqual([]);
  });

  it("builds account rows with search tokens", async () => {
    const { useAccounts } = await import("@/hooks/useAccounts");
    vi.mocked(useAccounts).mockReturnValue(
      mockQueryResult({
        data: [
          { id: "a1", name: "Apple Card" },
          { id: "a2", name: "Chase" },
        ],
      }),
    );
    const { result } = renderHook(() =>
      useEntityResults({ isAdmin: false, open: true, query: "search" }),
    );
    const accounts = result.current.find((g) => g.id === "accounts");
    expect(accounts).toBeDefined();
    expect(accounts!.items).toHaveLength(2);
    expect(accounts!.items[0].label).toBe("Apple Card");
    expect(accounts!.items[0].searchValue).toContain("apple card");
    expect(accounts!.items[0].href).toBe("/accounts");
  });

  it("cards expose cardCode as meta and include it in search tokens", async () => {
    const { useCards } = await import("@/hooks/useCards");
    vi.mocked(useCards).mockReturnValue(
      mockQueryResult({
        data: [{ id: "c1", name: "Checking", cardCode: "VISA-1234" }],
      }),
    );
    const { result } = renderHook(() =>
      useEntityResults({ isAdmin: false, open: true, query: "search" }),
    );
    const cards = result.current.find((g) => g.id === "cards");
    expect(cards!.items[0].meta).toBe("VISA-1234");
    expect(cards!.items[0].searchValue).toContain("visa-1234");
  });

  it("receipt items link back to their parent receipt", async () => {
    const { useReceiptItems } = await import("@/hooks/useReceiptItems");
    vi.mocked(useReceiptItems).mockReturnValue(
      mockQueryResult({
        data: [
          {
            id: "ri1",
            receiptId: "r42",
            description: "Organic bananas",
            receiptItemCode: "BAN-01",
            category: "Produce",
          },
        ],
      }),
    );
    const { result } = renderHook(() =>
      useEntityResults({ isAdmin: false, open: true, query: "search" }),
    );
    const receiptItems = result.current.find((g) => g.id === "receipt-items");
    expect(receiptItems!.items[0].href).toBe("/receipts/r42");
    expect(receiptItems!.items[0].label).toBe("Organic bananas");
  });

  it("hides user group when not admin", async () => {
    const { useUsers } = await import("@/hooks/useUsers");
    vi.mocked(useUsers).mockReturnValue(
      mockQueryResult({
        data: [{ userId: "u1", email: "bob@example.com" }],
      }),
    );
    const { result } = renderHook(() =>
      useEntityResults({ isAdmin: false, open: true, query: "search" }),
    );
    expect(result.current.find((g) => g.id === "users")).toBeUndefined();
  });

  it("passes enabled=false to useUsers when not admin (no API storm)", async () => {
    const { useUsers } = await import("@/hooks/useUsers");
    renderHook(() => useEntityResults({ isAdmin: false, open: true }));
    expect(vi.mocked(useUsers)).toHaveBeenCalledWith(
      0,
      expect.any(Number),
      undefined,
      undefined,
      { enabled: false },
    );
  });

  it("passes enabled=false to useUsers when admin but query is empty", async () => {
    const { useUsers } = await import("@/hooks/useUsers");
    renderHook(() => useEntityResults({ isAdmin: true, open: true }));
    expect(vi.mocked(useUsers)).toHaveBeenCalledWith(
      0,
      expect.any(Number),
      undefined,
      undefined,
      { enabled: false },
    );
  });

  it("passes enabled=true to useUsers when admin and query is non-empty", async () => {
    const { useUsers } = await import("@/hooks/useUsers");
    renderHook(() =>
      useEntityResults({ isAdmin: true, open: true, query: "bob" }),
    );
    expect(vi.mocked(useUsers)).toHaveBeenCalledWith(
      0,
      expect.any(Number),
      undefined,
      undefined,
      { enabled: true },
    );
  });

  it("shows user group when admin", async () => {
    const { useUsers } = await import("@/hooks/useUsers");
    vi.mocked(useUsers).mockReturnValue(
      mockQueryResult({
        data: [
          {
            userId: "u1",
            email: "bob@example.com",
            firstName: "Bob",
            lastName: "Roy",
          },
        ],
      }),
    );
    const { result } = renderHook(() =>
      useEntityResults({ isAdmin: true, open: true, query: "search" }),
    );
    const users = result.current.find((g) => g.id === "users");
    expect(users).toBeDefined();
    expect(users!.items[0].label).toBe("Bob Roy");
    expect(users!.items[0].meta).toBe("bob@example.com");
  });

  it("omits q from receipt hooks when query is empty", async () => {
    const { useReceipts } = await import("@/hooks/useReceipts");
    const { useReceiptItems } = await import("@/hooks/useReceiptItems");
    renderHook(() =>
      useEntityResults({ isAdmin: false, open: true, query: "" }),
    );
    expect(vi.mocked(useReceipts)).toHaveBeenCalledWith(
      0,
      expect.any(Number),
      null,
      null,
      null,
      null,
      undefined,
      { enabled: false },
    );
    expect(vi.mocked(useReceiptItems)).toHaveBeenCalledWith(
      0,
      expect.any(Number),
      null,
      null,
      undefined,
      { enabled: false },
    );
  });

  it("forwards debounced query as q to receipt hooks after the delay", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      const { useReceipts } = await import("@/hooks/useReceipts");
      const { useReceiptItems } = await import("@/hooks/useReceiptItems");
      const { rerender } = renderHook(
        ({ query }: { query: string }) =>
          useEntityResults({ isAdmin: false, open: true, query }),
        { initialProps: { query: "" } },
      );

      vi.mocked(useReceipts).mockClear();
      vi.mocked(useReceiptItems).mockClear();
      rerender({ query: "  Walmart  " });
      await act(() => vi.advanceTimersByTimeAsync(250));
      rerender({ query: "  Walmart  " });

      const receiptsCalls = vi.mocked(useReceipts).mock.calls;
      const receiptItemsCalls = vi.mocked(useReceiptItems).mock.calls;
      expect(receiptsCalls.at(-1)).toEqual([
        0,
        expect.any(Number),
        null,
        null,
        null,
        null,
        "Walmart",
        { enabled: true },
      ]);
      expect(receiptItemsCalls.at(-1)).toEqual([
        0,
        expect.any(Number),
        null,
        null,
        "Walmart",
        { enabled: true },
      ]);
    } finally {
      vi.useRealTimers();
    }
  });

  it("does not enable any entity hook when the palette input is empty", async () => {
    const { useAccounts } = await import("@/hooks/useAccounts");
    const { useCards } = await import("@/hooks/useCards");

    const { useReceipts } = await import("@/hooks/useReceipts");
    const { useReceiptItems } = await import("@/hooks/useReceiptItems");
    const { useUsers } = await import("@/hooks/useUsers");

    renderHook(() =>
      useEntityResults({ isAdmin: true, open: true, query: "" }),
    );

    for (const mock of [
      useAccounts,
      useCards,
      useReceipts,
      useReceiptItems,
      useUsers,
    ]) {
      const lastCall = vi.mocked(mock).mock.calls.at(-1);
      expect(lastCall?.at(-1)).toEqual({ enabled: false });
    }
  });

  it("keeps every hook disabled before debounce, then enables with the debounced q", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      const { useAccounts } = await import("@/hooks/useAccounts");
      const { useCards } = await import("@/hooks/useCards");

      const { useReceipts } = await import("@/hooks/useReceipts");
      const { useReceiptItems } = await import("@/hooks/useReceiptItems");
      const { useUsers } = await import("@/hooks/useUsers");
      const { rerender } = renderHook(
        ({ query }) => useEntityResults({ isAdmin: true, open: true, query }),
        { initialProps: { query: "" } },
      );

      rerender({ query: "  grocer  " });

      for (const hook of [
        useAccounts,
        useCards,
        useReceipts,
        useReceiptItems,
        useUsers,
      ]) {
        expect(vi.mocked(hook).mock.calls.at(-1)?.at(-1)).toMatchObject({
          enabled: false,
        });
      }
      for (const hook of [useAccounts, useCards]) {
        expect(vi.mocked(hook).mock.calls.at(-1)?.at(-1)).toEqual({
          enabled: false,
          q: undefined,
        });
      }

      await act(() => vi.advanceTimersByTimeAsync(200));
      rerender({ query: "  grocer  " });

      for (const hook of [useAccounts, useCards]) {
        expect(vi.mocked(hook).mock.calls.at(-1)?.at(-1)).toEqual({
          enabled: true,
          q: "grocer",
        });
      }
      for (const hook of [useReceipts, useReceiptItems, useUsers]) {
        expect(vi.mocked(hook).mock.calls.at(-1)?.at(-1)).toEqual({
          enabled: true,
        });
      }
      expect(vi.mocked(useReceipts).mock.calls.at(-1)?.[6]).toBe("grocer");
      expect(vi.mocked(useReceiptItems).mock.calls.at(-1)?.[4]).toBe("grocer");
    } finally {
      vi.useRealTimers();
    }
  });

  it("disables every entity query while closed even when a stale query remains", async () => {
    const hooks = await Promise.all([
      import("@/hooks/useAccounts").then((m) => m.useAccounts),
      import("@/hooks/useCards").then((m) => m.useCards),
      import("@/hooks/useReceipts").then((m) => m.useReceipts),
      import("@/hooks/useReceiptItems").then((m) => m.useReceiptItems),
      import("@/hooks/useUsers").then((m) => m.useUsers),
    ]);

    renderHook(() =>
      useEntityResults({ isAdmin: true, open: false, query: "stale search" }),
    );

    for (const hook of hooks) {
      expect(vi.mocked(hook).mock.calls.at(-1)?.at(-1)).toMatchObject({
        enabled: false,
      });
    }
  });

  it("does not revive a stale search after a rapid close, reopen, and clear", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      const hooks = await Promise.all([
        import("@/hooks/useAccounts").then((m) => m.useAccounts),
        import("@/hooks/useCards").then((m) => m.useCards),
        import("@/hooks/useReceipts").then((m) => m.useReceipts),
        import("@/hooks/useReceiptItems").then((m) => m.useReceiptItems),
        import("@/hooks/useUsers").then((m) => m.useUsers),
      ]);
      const searchableHooks = hooks.slice(0, 2);
      const { rerender } = renderHook(
        ({ open, query }) => useEntityResults({ isAdmin: true, open, query }),
        { initialProps: { open: true, query: "old search" } },
      );

      // Establish a previously valid debounced search.
      for (const hook of hooks) {
        expect(vi.mocked(hook).mock.calls.at(-1)?.at(-1)).toMatchObject({
          enabled: true,
        });
      }

      // Close and reopen with the input cleared well inside the debounce window.
      rerender({ open: false, query: "old search" });
      rerender({ open: true, query: "" });
      for (const hook of hooks) {
        expect(vi.mocked(hook).mock.calls.at(-1)?.at(-1)).toMatchObject({
          enabled: false,
        });
      }

      await act(() => vi.advanceTimersByTimeAsync(199));
      for (const hook of hooks) {
        expect(vi.mocked(hook).mock.calls.at(-1)?.at(-1)).toMatchObject({
          enabled: false,
        });
      }

      // A new term must not enable requests while the debounced value is still
      // empty (or stale from the previous palette lifecycle).
      rerender({ open: true, query: "new search" });
      for (const hook of hooks) {
        expect(vi.mocked(hook).mock.calls.at(-1)?.at(-1)).toMatchObject({
          enabled: false,
        });
      }
      await act(() => vi.advanceTimersByTimeAsync(200));
      rerender({ open: true, query: "new search" });

      for (const hook of hooks) {
        expect(vi.mocked(hook).mock.calls.at(-1)?.at(-1)).toMatchObject({
          enabled: true,
        });
      }
      for (const hook of searchableHooks) {
        expect(vi.mocked(hook).mock.calls.at(-1)?.at(-1)).toEqual({
          enabled: true,
          q: "new search",
        });
      }
    } finally {
      vi.useRealTimers();
    }
  });

  it("never requests an entity page above the API maximum of 500", async () => {
    const hooks = await Promise.all([
      import("@/hooks/useAccounts").then((m) => m.useAccounts),
      import("@/hooks/useCards").then((m) => m.useCards),
      import("@/hooks/useReceipts").then((m) => m.useReceipts),
      import("@/hooks/useReceiptItems").then((m) => m.useReceiptItems),
      import("@/hooks/useUsers").then((m) => m.useUsers),
    ]);

    renderHook(() =>
      useEntityResults({ isAdmin: true, open: true, query: "anything" }),
    );

    for (const hook of hooks) {
      expect(vi.mocked(hook).mock.calls.at(-1)?.[1]).toBeLessThanOrEqual(500);
    }
  });
});

it("hides prior entity data immediately while a changed query is debouncing", async () => {
  const { useAccounts } = await import("@/hooks/useAccounts");
  vi.mocked(useAccounts).mockReturnValue(
    mockQueryResult({
      data: [{ id: "a1", name: "Old account" }],
      isLoading: false,
    }),
  );
  const { result, rerender } = renderHook(
    ({ query }) => useEntityResults({ isAdmin: false, open: true, query }),
    { initialProps: { query: "old" } },
  );
  expect(
    result.current.find((group) => group.id === "accounts")?.items[0].label,
  ).toBe("Old account");
  rerender({ query: "new" });
  expect(result.current).toEqual([]);
});
it("discloses bounded users honestly and exposes retry without stale failed account rows", async () => {
  const { useUsers } = await import("@/hooks/useUsers");
  const { useAccounts } = await import("@/hooks/useAccounts");
  const retry = vi.fn();
  vi.mocked(useUsers).mockReturnValue(
    mockQueryResult({
      data: [{ userId: "u1", email: "bob@example.com" }],
      total: 501,
      isLoading: false,
    }),
  );
  vi.mocked(useAccounts).mockReturnValue(
    mockQueryResult({
      data: [{ id: "a1", name: "Stale" }],
      isError: true,
      isLoading: false,
      refetch: retry,
    }),
  );
  const { result } = renderHook(() =>
    useEntityResults({ isAdmin: true, open: true, query: "bob" }),
  );
  expect(
    result.current.find((group) => group.id === "users")?.scopeNotice,
  ).toBe("User search covers the first 1 of 501 users.");
  const accounts = result.current.find((group) => group.id === "accounts");
  expect(accounts?.items).toEqual([]);
  expect(accounts?.failure?.message).toBe("Accounts search unavailable.");
  accounts?.failure?.retry();
  expect(retry).toHaveBeenCalledOnce();
});

it("does not fetch or return templates for palette searches", async () => {
  const { useItemTemplates } = await import("@/hooks/useItemTemplates");

  vi.mocked(useItemTemplates).mockReturnValue(
    mockQueryResult({ data: [{ id: "t1", name: "Milk" }] }),
  );
  const { result } = renderHook(() =>
    useEntityResults({ isAdmin: true, open: true, query: "milk" }),
  );
  expect(useItemTemplates).not.toHaveBeenCalled();
  expect(result.current.some((group) => group.id === "item-templates")).toBe(
    false,
  );
});

it("searches current complete category paths in one group without legacy lookups", async () => {
  const { useCategoryTaxonomy } = await import("@/hooks/useCategoryTaxonomy");
  vi.mocked(useCategoryTaxonomy).mockReturnValue(
    mockQueryResult({
      data: {
        nodes: [
          { id: "food", name: "Food", archived: false },
          { id: "milk", name: "Milk", parentId: "food", archived: false },
          { id: "old", name: "Old", archived: true },
        ],
      },
    }) as ReturnType<typeof useCategoryTaxonomy>,
  );
  const { result } = renderHook(() =>
    useEntityResults({ isAdmin: false, open: true, query: "milk" }),
  );
  expect(useCategoryTaxonomy).toHaveBeenLastCalledWith(true);
  const group = result.current.find((g) => g.id === "categories")!;
  expect(group.items.map((item) => item.label)).toEqual([
    "Food",
    "Food / Milk",
  ]);
  expect(group.items[1].href).toBe("/categories");
  expect(result.current.some((g) => g.id === "subcategories")).toBe(false);
});
it("keeps category lookup disabled while the palette is closed or empty", async () => {
  const { useCategoryTaxonomy } = await import("@/hooks/useCategoryTaxonomy");
  renderHook(() =>
    useEntityResults({ isAdmin: false, open: false, query: "milk" }),
  );
  expect(useCategoryTaxonomy).toHaveBeenLastCalledWith(false);
  renderHook(() => useEntityResults({ isAdmin: false, open: true, query: "" }));
  expect(useCategoryTaxonomy).toHaveBeenLastCalledWith(false);
});
