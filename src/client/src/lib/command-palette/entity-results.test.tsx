import { describe, expect, it, vi, beforeEach } from "vitest";
import { renderHook } from "@testing-library/react";
import { useEntityResults } from "./entity-results";
import { mockQueryResult } from "@/test/mock-hooks";

vi.mock("@/hooks/useAccounts", () => ({
  useAccounts: vi.fn(() => mockQueryResult()),
}));
vi.mock("@/hooks/useCards", () => ({
  useCards: vi.fn(() => mockQueryResult()),
}));
vi.mock("@/hooks/useCategories", () => ({
  useCategories: vi.fn(() => mockQueryResult()),
}));
vi.mock("@/hooks/useSubcategories", () => ({
  useSubcategories: vi.fn(() => mockQueryResult()),
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
    const { result } = renderHook(() => useEntityResults({ isAdmin: false }));
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
    const { result } = renderHook(() => useEntityResults({ isAdmin: false }));
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
    const { result } = renderHook(() => useEntityResults({ isAdmin: false }));
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
    const { result } = renderHook(() => useEntityResults({ isAdmin: false }));
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
    const { result } = renderHook(() => useEntityResults({ isAdmin: false }));
    expect(result.current.find((g) => g.id === "users")).toBeUndefined();
  });

  it("passes enabled=false to useUsers when not admin (no API storm)", async () => {
    const { useUsers } = await import("@/hooks/useUsers");
    renderHook(() => useEntityResults({ isAdmin: false }));
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
    renderHook(() => useEntityResults({ isAdmin: true }));
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
    renderHook(() => useEntityResults({ isAdmin: true, query: "bob" }));
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
          { userId: "u1", email: "bob@example.com", firstName: "Bob", lastName: "Roy" },
        ],
      }),
    );
    const { result } = renderHook(() => useEntityResults({ isAdmin: true }));
    const users = result.current.find((g) => g.id === "users");
    expect(users).toBeDefined();
    expect(users!.items[0].label).toBe("Bob Roy");
    expect(users!.items[0].meta).toBe("bob@example.com");
  });

  it("omits q from receipt hooks when query is empty", async () => {
    const { useReceipts } = await import("@/hooks/useReceipts");
    const { useReceiptItems } = await import("@/hooks/useReceiptItems");
    renderHook(() => useEntityResults({ isAdmin: false, query: "" }));
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
        ({ query }: { query: string }) => useEntityResults({ isAdmin: false, query }),
        { initialProps: { query: "" } },
      );

      vi.mocked(useReceipts).mockClear();
      vi.mocked(useReceiptItems).mockClear();
      rerender({ query: "  Walmart  " });
      vi.advanceTimersByTime(250);
      rerender({ query: "  Walmart  " });

      const receiptsCalls = vi.mocked(useReceipts).mock.calls;
      const receiptItemsCalls = vi.mocked(useReceiptItems).mock.calls;
      expect(receiptsCalls.at(-1)).toEqual([0, expect.any(Number), null, null, null, null, "Walmart", { enabled: true }]);
      expect(receiptItemsCalls.at(-1)).toEqual([0, expect.any(Number), null, null, "Walmart", { enabled: true }]);
    } finally {
      vi.useRealTimers();
    }
  });

  it("does not enable any entity hook when the palette input is empty", async () => {
    const { useAccounts } = await import("@/hooks/useAccounts");
    const { useCards } = await import("@/hooks/useCards");
    const { useCategories } = await import("@/hooks/useCategories");
    const { useSubcategories } = await import("@/hooks/useSubcategories");
    const { useItemTemplates } = await import("@/hooks/useItemTemplates");
    const { useReceipts } = await import("@/hooks/useReceipts");
    const { useReceiptItems } = await import("@/hooks/useReceiptItems");
    const { useUsers } = await import("@/hooks/useUsers");

    renderHook(() => useEntityResults({ isAdmin: true, query: "" }));

    for (const mock of [
      useAccounts,
      useCards,
      useCategories,
      useSubcategories,
      useItemTemplates,
      useReceipts,
      useReceiptItems,
      useUsers,
    ]) {
      const lastCall = vi.mocked(mock).mock.calls.at(-1);
      expect(lastCall?.at(-1)).toEqual({ enabled: false });
    }
  });

  it("enables the reference-list entity hooks once the palette input is non-empty", async () => {
    const { useAccounts } = await import("@/hooks/useAccounts");
    const { useCards } = await import("@/hooks/useCards");
    const { useCategories } = await import("@/hooks/useCategories");
    const { useSubcategories } = await import("@/hooks/useSubcategories");
    const { useItemTemplates } = await import("@/hooks/useItemTemplates");

    renderHook(() => useEntityResults({ isAdmin: false, query: "wal" }));

    for (const mock of [
      useAccounts,
      useCards,
      useCategories,
      useSubcategories,
      useItemTemplates,
    ]) {
      const lastCall = vi.mocked(mock).mock.calls.at(-1);
      expect(lastCall?.at(-1)).toEqual({ enabled: true });
    }
  });
});
