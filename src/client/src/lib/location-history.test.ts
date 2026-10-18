import { locationHistory } from "./location-history";

describe("location-history", () => {
  beforeEach(() => {
    localStorage.clear();
  });

  describe("locationHistory.getHistory", () => {
    it("returns empty array when no history exists", () => {
      expect(locationHistory.getHistory()).toEqual([]);
    });

    it("returns stored locations", () => {
      localStorage.setItem(
        "receipts:location-history",
        JSON.stringify(["Walmart", "Target"]),
      );
      expect(locationHistory.getHistory()).toEqual(["Walmart", "Target"]);
    });

    it("returns empty array on malformed JSON", () => {
      localStorage.setItem("receipts:location-history", "not-json");
      expect(locationHistory.getHistory()).toEqual([]);
    });
  });

  describe("locationHistory.addEntry", () => {
    it("adds a location to the front", () => {
      locationHistory.addEntry("Walmart");
      locationHistory.addEntry("Target");
      expect(locationHistory.getHistory()).toEqual(["Target", "Walmart"]);
    });

    it("moves duplicate to the front (case-insensitive)", () => {
      locationHistory.addEntry("Walmart");
      locationHistory.addEntry("Target");
      locationHistory.addEntry("walmart");
      expect(locationHistory.getHistory()).toEqual(["walmart", "Target"]);
    });

    it("trims whitespace", () => {
      locationHistory.addEntry("  Costco  ");
      expect(locationHistory.getHistory()).toEqual(["Costco"]);
    });

    it("ignores empty strings", () => {
      locationHistory.addEntry("");
      locationHistory.addEntry("   ");
      expect(locationHistory.getHistory()).toEqual([]);
    });

    it("limits to 50 entries", () => {
      for (let i = 0; i < 60; i++) {
        locationHistory.addEntry(`Store ${i}`);
      }
      const history = locationHistory.getHistory();
      expect(history).toHaveLength(50);
      expect(history[0]).toBe("Store 59");
    });

    it("does not crash when localStorage.setItem throws (quota exceeded)", () => {
      const original = localStorage.setItem.bind(localStorage);
      localStorage.setItem = () => {
        throw new DOMException("QuotaExceededError", "QuotaExceededError");
      };

      expect(() => locationHistory.addEntry("Walmart")).not.toThrow();

      localStorage.setItem = original;
    });
  });

  describe("locationHistory.getHistory runtime validation", () => {
    it("filters out non-string entries from localStorage", () => {
      localStorage.setItem(
        "receipts:location-history",
        JSON.stringify(["Walmart", 42, null, "Target", { name: "bad" }]),
      );
      expect(locationHistory.getHistory()).toEqual(["Walmart", "Target"]);
    });

    it("returns empty array when localStorage contains a non-array JSON value", () => {
      localStorage.setItem(
        "receipts:location-history",
        JSON.stringify({ not: "an array" }),
      );
      expect(locationHistory.getHistory()).toEqual([]);
    });
  });

  describe("locationHistory.clearHistory", () => {
    it("removes all locations", () => {
      locationHistory.addEntry("Walmart");
      locationHistory.addEntry("Target");
      locationHistory.clearHistory();
      expect(locationHistory.getHistory()).toEqual([]);
    });
  });
});
