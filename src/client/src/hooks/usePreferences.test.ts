import { describe, it, expect, beforeEach, vi } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { usePreferences } from "./usePreferences";

describe("usePreferences", () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  it("returns defaults when nothing is stored", () => {
    const { result } = renderHook(() => usePreferences());
    expect(result.current.preferences).toEqual({
      weekStart: "sunday",
      showKeyboardHints: true,
    });
  });

  it("hydrates from localStorage", () => {
    window.localStorage.setItem(
      "receipts:preferences",
      JSON.stringify({ weekStart: "monday", showKeyboardHints: false }),
    );
    const { result } = renderHook(() => usePreferences());
    expect(result.current.preferences).toEqual({
      weekStart: "monday",
      showKeyboardHints: false,
    });
  });

  it("ignores corrupted JSON in storage", () => {
    window.localStorage.setItem("receipts:preferences", "{ not json");
    const { result } = renderHook(() => usePreferences());
    expect(result.current.preferences).toEqual({
      weekStart: "sunday",
      showKeyboardHints: true,
    });
  });

  it("clamps invalid weekStart values back to default", () => {
    window.localStorage.setItem(
      "receipts:preferences",
      JSON.stringify({ weekStart: "tuesday" }),
    );
    const { result } = renderHook(() => usePreferences());
    expect(result.current.preferences.weekStart).toBe("sunday");
  });

  it("updates and persists weekStart", () => {
    const { result } = renderHook(() => usePreferences());
    act(() => {
      result.current.setWeekStart("monday");
    });
    expect(result.current.preferences.weekStart).toBe("monday");
    const stored = JSON.parse(
      window.localStorage.getItem("receipts:preferences") ?? "{}",
    );
    expect(stored.weekStart).toBe("monday");
  });

  it("updates and persists showKeyboardHints", () => {
    const { result } = renderHook(() => usePreferences());
    act(() => {
      result.current.setShowKeyboardHints(false);
    });
    expect(result.current.preferences.showKeyboardHints).toBe(false);
    const stored = JSON.parse(
      window.localStorage.getItem("receipts:preferences") ?? "{}",
    );
    expect(stored.showKeyboardHints).toBe(false);
  });

  it("syncs across tabs via the storage event", () => {
    const { result } = renderHook(() => usePreferences());
    expect(result.current.preferences.weekStart).toBe("sunday");

    window.localStorage.setItem(
      "receipts:preferences",
      JSON.stringify({ weekStart: "monday", showKeyboardHints: true }),
    );
    act(() => {
      window.dispatchEvent(
        new StorageEvent("storage", {
          key: "receipts:preferences",
          newValue: JSON.stringify({
            weekStart: "monday",
            showKeyboardHints: true,
          }),
        }),
      );
    });
    expect(result.current.preferences.weekStart).toBe("monday");
  });

  it("tolerates setItem failures", () => {
    const spy = vi
      .spyOn(window.localStorage.__proto__, "setItem")
      .mockImplementation(() => {
        throw new Error("denied");
      });
    const { result } = renderHook(() => usePreferences());
    expect(() => {
      act(() => {
        result.current.setWeekStart("monday");
      });
    }).not.toThrow();
    spy.mockRestore();
  });
});
