import { describe, it, expect, beforeEach } from "vitest";
import { applySetting, readAppearance, DEFAULT_APPEARANCE } from "./appearance";

describe("appearance", () => {
  beforeEach(() => {
    localStorage.clear();
    for (const attr of [
      "data-palette",
      "data-density",
      "data-paper",
      "data-motion",
    ]) {
      document.documentElement.removeAttribute(attr);
    }
  });

  describe("readAppearance", () => {
    it("returns defaults when nothing is persisted", () => {
      expect(readAppearance()).toEqual(DEFAULT_APPEARANCE);
    });

    it("reads persisted values", () => {
      localStorage.setItem("appearance.palette", "paper");
      localStorage.setItem("appearance.density", "compact");
      expect(readAppearance()).toMatchObject({
        palette: "paper",
        density: "compact",
      });
    });

    it("falls back to the default for an invalid persisted value", () => {
      localStorage.setItem("appearance.motion", "bogus");
      expect(readAppearance().motion).toBe(DEFAULT_APPEARANCE.motion);
    });
  });

  describe("applySetting", () => {
    it("sets the data attribute on <html> and persists to localStorage", () => {
      applySetting("palette", "paper");
      expect(document.documentElement.getAttribute("data-palette")).toBe(
        "paper",
      );
      expect(localStorage.getItem("appearance.palette")).toBe("paper");
    });

    it("maps each setting to its own attribute and key", () => {
      applySetting("density", "spacious");
      applySetting("paper", "none");
      applySetting("motion", "none");
      expect(document.documentElement.getAttribute("data-density")).toBe(
        "spacious",
      );
      expect(document.documentElement.getAttribute("data-paper")).toBe("none");
      expect(document.documentElement.getAttribute("data-motion")).toBe("none");
    });
  });
});
