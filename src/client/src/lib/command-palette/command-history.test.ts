import { describe, it, expect, beforeEach } from "vitest";
import {
  addRecent,
  getPinned,
  getRecent,
  togglePinned,
} from "./command-history";

const RECENT_KEY = "receipts:palette-recent";
const PINNED_KEY = "receipts:palette-pinned";

beforeEach(() => {
  localStorage.clear();
});

describe("getRecent", () => {
  it("returns empty when storage is empty", () => {
    expect(getRecent()).toEqual([]);
  });

  it("returns stored entries in order", () => {
    localStorage.setItem(
      RECENT_KEY,
      JSON.stringify(["nav:receipts", "create:card"]),
    );
    expect(getRecent()).toEqual(["nav:receipts", "create:card"]);
  });

  it("returns empty on malformed JSON", () => {
    localStorage.setItem(RECENT_KEY, "{not-json");
    expect(getRecent()).toEqual([]);
  });

  it("returns empty when stored value is not an array", () => {
    localStorage.setItem(RECENT_KEY, JSON.stringify({ foo: "bar" }));
    expect(getRecent()).toEqual([]);
  });

  it("filters non-string entries", () => {
    localStorage.setItem(
      RECENT_KEY,
      JSON.stringify(["nav:receipts", 42, null, "create:card", { bad: true }]),
    );
    expect(getRecent()).toEqual(["nav:receipts", "create:card"]);
  });
});

describe("addRecent", () => {
  it("prepends new ids", () => {
    addRecent("nav:receipts");
    addRecent("create:card");
    expect(getRecent()).toEqual(["create:card", "nav:receipts"]);
  });

  it("returns the new list so callers can sync state in one read", () => {
    addRecent("nav:receipts");
    const next = addRecent("create:card");
    expect(next).toEqual(["create:card", "nav:receipts"]);
  });

  it("dedupes on exact match and moves existing id to the front", () => {
    addRecent("nav:receipts");
    addRecent("create:card");
    addRecent("nav:receipts");
    expect(getRecent()).toEqual(["nav:receipts", "create:card"]);
  });

  it("does not case-fold (command ids are opaque)", () => {
    addRecent("nav:receipts");
    addRecent("NAV:RECEIPTS");
    expect(getRecent()).toEqual(["NAV:RECEIPTS", "nav:receipts"]);
  });

  it("caps the list at 5", () => {
    for (let i = 0; i < 5 + 3; i++) {
      addRecent(`cmd:${i}`);
    }
    const recent = getRecent();
    expect(recent).toHaveLength(5);
    expect(recent[0]).toBe(`cmd:${5 + 2}`);
  });

  it("is a no-op for empty ids", () => {
    addRecent("");
    expect(getRecent()).toEqual([]);
  });

  it("returns the current list when called with an empty id", () => {
    addRecent("nav:receipts");
    expect(addRecent("")).toEqual(["nav:receipts"]);
  });

  it("does not throw when localStorage.setItem throws (quota exceeded)", () => {
    const original = localStorage.setItem.bind(localStorage);
    localStorage.setItem = () => {
      throw new DOMException("QuotaExceededError", "QuotaExceededError");
    };
    expect(() => addRecent("nav:receipts")).not.toThrow();
    localStorage.setItem = original;
  });
});

describe("getPinned", () => {
  it("returns empty when storage is empty", () => {
    expect(getPinned()).toEqual([]);
  });

  it("returns stored entries in insertion order", () => {
    localStorage.setItem(
      PINNED_KEY,
      JSON.stringify(["create:receipt", "nav:reports"]),
    );
    expect(getPinned()).toEqual(["create:receipt", "nav:reports"]);
  });

  it("returns empty on malformed JSON", () => {
    localStorage.setItem(PINNED_KEY, "not-json");
    expect(getPinned()).toEqual([]);
  });

  it("filters non-string entries", () => {
    localStorage.setItem(PINNED_KEY, JSON.stringify([null, "create:card", 99]));
    expect(getPinned()).toEqual(["create:card"]);
  });
});

describe("togglePinned", () => {
  it("adds an id when absent and returns the new list", () => {
    const next = togglePinned("create:card");
    expect(next).toEqual(["create:card"]);
    expect(getPinned()).toEqual(["create:card"]);
  });

  it("removes an id when already present", () => {
    togglePinned("create:card");
    const next = togglePinned("create:card");
    expect(next).toEqual([]);
    expect(getPinned()).toEqual([]);
  });

  it("preserves order of other pins when removing one", () => {
    togglePinned("a");
    togglePinned("b");
    togglePinned("c");
    const next = togglePinned("b");
    expect(next).toEqual(["a", "c"]);
  });

  it("is a no-op for empty ids", () => {
    expect(togglePinned("")).toEqual([]);
    expect(getPinned()).toEqual([]);
  });

  it("does not throw when localStorage.setItem throws", () => {
    const original = localStorage.setItem.bind(localStorage);
    localStorage.setItem = () => {
      throw new DOMException("QuotaExceededError", "QuotaExceededError");
    };
    expect(() => togglePinned("create:card")).not.toThrow();
    localStorage.setItem = original;
  });
});
