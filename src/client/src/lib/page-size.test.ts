import { describe, it, expect, beforeEach, vi } from "vitest";
import { findStorageMethodOwner } from "@/test/storage-spy";
import { getPersistedPageSize, persistPageSize } from "./page-size";

describe("getPersistedPageSize", () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it("returns null when nothing is stored", () => {
    expect(getPersistedPageSize("receipts")).toBeNull();
  });

  it("isolates table choices without overwriting the legacy fallback", () => {
    localStorage.setItem("table-page-size", "50");
    expect(getPersistedPageSize("receipts")).toBe(50);
    expect(getPersistedPageSize("purchases")).toBe(50);
    persistPageSize("receipts", 10);
    persistPageSize("purchases", 100);
    expect(getPersistedPageSize("receipts")).toBe(10);
    expect(getPersistedPageSize("purchases")).toBe(100);
    expect(getPersistedPageSize("accounts")).toBe(50);
    expect(localStorage.getItem("table-page-size")).toBe("50");
  });

  it.each(["NaN", "Infinity", "-1", "0", "15", "25.5", "{}"])(
    "rejects invalid table-specific size %s",
    (raw) => {
      localStorage.setItem("table-page-size:receipts", raw);
      expect(getPersistedPageSize("receipts")).toBeNull();
    },
  );

  it.each([10, 25, 50, 100])("returns %i when stored", (size) => {
    localStorage.setItem("table-page-size", String(size));
    expect(getPersistedPageSize("receipts")).toBe(size);
  });

  it.each(["abc", "15", "0", "-1", "999", ""])(
    'returns null for invalid stored value "%s"',
    (value) => {
      localStorage.setItem("table-page-size", value);
      expect(getPersistedPageSize("receipts")).toBeNull();
    },
  );

  it("returns null when localStorage.getItem throws", () => {
    localStorage.setItem("table-page-size:receipts", "50");
    vi.spyOn(
      findStorageMethodOwner(localStorage, "getItem"),
      "getItem",
    ).mockImplementation(() => {
      throw new Error("storage disabled");
    });
    expect(getPersistedPageSize("receipts")).toBeNull();
    vi.restoreAllMocks();
  });
});

describe("persistPageSize", () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it("writes the size to localStorage", () => {
    persistPageSize("receipts", 50);
    expect(localStorage.getItem("table-page-size:receipts")).toBe("50");
  });

  it("overwrites a previously stored size", () => {
    persistPageSize("receipts", 10);
    persistPageSize("receipts", 100);
    expect(localStorage.getItem("table-page-size:receipts")).toBe("100");
  });

  it("does not write an invalid size", () => {
    persistPageSize("receipts", 15);
    expect(localStorage.getItem("table-page-size:receipts")).toBeNull();
  });

  it("does not throw when localStorage.setItem throws", () => {
    const setItem = vi
      .spyOn(findStorageMethodOwner(localStorage, "setItem"), "setItem")
      .mockImplementation(() => {
        throw new Error("quota exceeded");
      });
    expect(() => persistPageSize("receipts", 25)).not.toThrow();
    expect(setItem).toHaveBeenCalledWith("table-page-size:receipts", "25");
    vi.restoreAllMocks();
  });
});
