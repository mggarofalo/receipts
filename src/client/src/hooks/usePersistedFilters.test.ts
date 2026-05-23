import { describe, it, expect, beforeEach, vi } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { usePersistedFilters } from "./usePersistedFilters";

const KEY = "receipts:filters:receipts";

describe("usePersistedFilters", () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  it("returns an empty object when nothing is stored", () => {
    const { result } = renderHook(() => usePersistedFilters("receipts"));
    expect(result.current[0]).toEqual({});
  });

  it("hydrates initial state from localStorage", () => {
    window.localStorage.setItem(KEY, JSON.stringify({ date: { from: "2026-01-01" } }));
    const { result } = renderHook(() => usePersistedFilters("receipts"));
    expect(result.current[0]).toEqual({ date: { from: "2026-01-01" } });
  });

  it("writes to localStorage on every update", () => {
    const { result } = renderHook(() => usePersistedFilters("receipts"));
    act(() => {
      result.current[1]({ date: { from: "2026-02-01" } });
    });
    expect(window.localStorage.getItem(KEY)).toBe(
      JSON.stringify({ date: { from: "2026-02-01" } }),
    );
  });

  it("removes the key when filters are cleared to empty", () => {
    window.localStorage.setItem(KEY, JSON.stringify({ date: { from: "x" } }));
    const { result } = renderHook(() => usePersistedFilters("receipts"));
    act(() => {
      result.current[1]({});
    });
    expect(window.localStorage.getItem(KEY)).toBeNull();
  });

  it("supports the function-updater form", () => {
    window.localStorage.setItem(KEY, JSON.stringify({ a: 1 }));
    const { result } = renderHook(() => usePersistedFilters("receipts"));
    act(() => {
      result.current[1]((prev) => ({ ...prev, b: 2 }));
    });
    expect(result.current[0]).toEqual({ a: 1, b: 2 });
    expect(JSON.parse(window.localStorage.getItem(KEY) ?? "{}")).toEqual({
      a: 1,
      b: 2,
    });
  });

  it("isolates state per entity type", () => {
    window.localStorage.setItem(
      "receipts:filters:cards",
      JSON.stringify({ brand: "Visa" }),
    );
    window.localStorage.setItem(
      "receipts:filters:receipts",
      JSON.stringify({ date: { from: "z" } }),
    );
    const cards = renderHook(() => usePersistedFilters("cards"));
    const receipts = renderHook(() => usePersistedFilters("receipts"));
    expect(cards.result.current[0]).toEqual({ brand: "Visa" });
    expect(receipts.result.current[0]).toEqual({ date: { from: "z" } });
  });

  it("re-hydrates when entityType changes", () => {
    window.localStorage.setItem(
      "receipts:filters:cards",
      JSON.stringify({ brand: "Visa" }),
    );
    window.localStorage.setItem(
      "receipts:filters:receipts",
      JSON.stringify({ date: { from: "x" } }),
    );
    const { result, rerender } = renderHook(
      ({ et }: { et: string }) => usePersistedFilters(et),
      { initialProps: { et: "cards" } },
    );
    expect(result.current[0]).toEqual({ brand: "Visa" });
    rerender({ et: "receipts" });
    expect(result.current[0]).toEqual({ date: { from: "x" } });
  });

  it("ignores corrupted JSON in storage", () => {
    window.localStorage.setItem(KEY, "{ not json");
    const { result } = renderHook(() => usePersistedFilters("receipts"));
    expect(result.current[0]).toEqual({});
  });

  it("ignores stored values that aren't plain objects", () => {
    window.localStorage.setItem(KEY, JSON.stringify(["not", "an", "object"]));
    const { result } = renderHook(() => usePersistedFilters("receipts"));
    expect(result.current[0]).toEqual({});
  });

  it("does not throw when localStorage.setItem rejects", () => {
    const spy = vi
      .spyOn(window.localStorage.__proto__, "setItem")
      .mockImplementation(() => {
        throw new Error("denied");
      });
    const { result } = renderHook(() => usePersistedFilters("receipts"));
    expect(() => {
      act(() => {
        result.current[1]({ x: 1 });
      });
    }).not.toThrow();
    spy.mockRestore();
  });
});
