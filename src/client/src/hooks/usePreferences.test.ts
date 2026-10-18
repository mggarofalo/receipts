import { describe, it, expect, beforeEach, vi } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { findStorageMethodOwner } from "@/test/storage-spy";
import { usePreferences } from "./usePreferences";
const key = "receipts:preferences";
function storageChanged(changedKey: string | null = key) {
  window.dispatchEvent(new StorageEvent("storage", { key: changedKey }));
}
describe("usePreferences", () => {
  beforeEach(() => {
    localStorage.clear();
    storageChanged(null);
  });
  it("defaults to visible hints without an unused week-start preference", () => {
    const { result } = renderHook(() => usePreferences());
    expect(result.current.preferences).toEqual({ showKeyboardHints: true });
  });
  it("reads legacy storage without losing the hint choice and drops weekStart on next write", () => {
    localStorage.setItem(
      key,
      JSON.stringify({ weekStart: "monday", showKeyboardHints: false }),
    );
    const { result } = renderHook(() => usePreferences());
    expect(result.current.preferences).toEqual({ showKeyboardHints: false });
    act(() => result.current.setShowKeyboardHints(true));
    expect(JSON.parse(localStorage.getItem(key)!)).toEqual({
      showKeyboardHints: true,
    });
  });
  it.each([
    "{ broken",
    "null",
    "[]",
    '"false"',
    '{"showKeyboardHints":"false"}',
    "{}",
  ])("falls back safely for invalid storage %s", (raw) => {
    localStorage.setItem(key, raw);
    const { result } = renderHook(() => usePreferences());
    expect(result.current.preferences.showKeyboardHints).toBe(true);
  });
  it("updates every mounted consumer immediately and preserves the choice after remount", () => {
    const first = renderHook(() => usePreferences());
    const second = renderHook(() => usePreferences());
    act(() => first.result.current.setShowKeyboardHints(false));
    expect(first.result.current.preferences.showKeyboardHints).toBe(false);
    expect(second.result.current.preferences.showKeyboardHints).toBe(false);
    expect(JSON.parse(localStorage.getItem(key)!)).toEqual({
      showKeyboardHints: false,
    });
    first.unmount();
    second.unmount();
    const reloaded = renderHook(() => usePreferences());
    expect(reloaded.result.current.preferences.showKeyboardHints).toBe(false);
  });
  it("synchronizes other-tab changes, removal and clear", () => {
    const { result } = renderHook(() => usePreferences());
    act(() => {
      localStorage.setItem(key, '{"showKeyboardHints":false}');
      storageChanged();
    });
    expect(result.current.preferences.showKeyboardHints).toBe(false);
    act(() => storageChanged("another-setting"));
    expect(result.current.preferences.showKeyboardHints).toBe(false);
    act(() => {
      localStorage.removeItem(key);
      storageChanged();
    });
    expect(result.current.preferences.showKeyboardHints).toBe(true);
    act(() => result.current.setShowKeyboardHints(false));
    act(() => {
      localStorage.clear();
      storageChanged(null);
    });
    expect(result.current.preferences.showKeyboardHints).toBe(true);
  });
  it("falls back when storage reads are denied", () => {
    const spy = vi
      .spyOn(findStorageMethodOwner(localStorage, "getItem"), "getItem")
      .mockImplementation(() => {
        throw new Error("denied");
      });
    try {
      const { result } = renderHook(() => usePreferences());
      expect(result.current.preferences.showKeyboardHints).toBe(true);
    } finally {
      spy.mockRestore();
    }
  });
  it("keeps current consumers synchronized when persistence is denied", () => {
    const first = renderHook(() => usePreferences());
    const second = renderHook(() => usePreferences());
    const spy = vi
      .spyOn(findStorageMethodOwner(localStorage, "setItem"), "setItem")
      .mockImplementation(() => {
        throw new Error("denied");
      });
    try {
      act(() => first.result.current.setShowKeyboardHints(false));
      expect(first.result.current.preferences.showKeyboardHints).toBe(false);
      expect(second.result.current.preferences.showKeyboardHints).toBe(false);
    } finally {
      spy.mockRestore();
    }
  });
});
