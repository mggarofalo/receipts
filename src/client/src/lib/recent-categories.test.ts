import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  getRecentCategoryIds,
  rememberRecentCategory,
} from "./recent-categories";

const KEY = "category-picker:recent";

describe("recent categories", () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => vi.restoreAllMocks());

  it("starts empty", () => {
    expect(getRecentCategoryIds()).toEqual([]);
  });

  it("remembers the most recent first without duplicates", () => {
    rememberRecentCategory("a");
    rememberRecentCategory("b");
    rememberRecentCategory("a");
    expect(getRecentCategoryIds()).toEqual(["a", "b"]);
    expect(JSON.parse(localStorage.getItem(KEY)!)).toEqual(["a", "b"]);
  });

  it("keeps at most eight", () => {
    for (let i = 0; i < 10; i++) rememberRecentCategory(`c${i}`);
    expect(getRecentCategoryIds()).toEqual([
      "c9",
      "c8",
      "c7",
      "c6",
      "c5",
      "c4",
      "c3",
      "c2",
    ]);
  });

  it("ignores corrupt or unexpected stored values", () => {
    localStorage.setItem(KEY, "{not json");
    expect(getRecentCategoryIds()).toEqual([]);
    localStorage.setItem(KEY, JSON.stringify({ id: "a" }));
    expect(getRecentCategoryIds()).toEqual([]);
    localStorage.setItem(KEY, JSON.stringify(["a", 2, null, "b"]));
    expect(getRecentCategoryIds()).toEqual(["a", "b"]);
  });

  it("swallows storage failures", () => {
    vi.spyOn(localStorage, "getItem").mockImplementation(() => {
      throw new Error("denied");
    });
    vi.spyOn(localStorage, "setItem").mockImplementation(() => {
      throw new Error("full");
    });
    expect(getRecentCategoryIds()).toEqual([]);
    expect(() => rememberRecentCategory("a")).not.toThrow();
  });
});
