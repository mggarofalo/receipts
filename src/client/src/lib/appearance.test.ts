import { describe, it, expect, beforeEach } from "vitest";
import { applySetting, readAppearance, DEFAULT_APPEARANCE } from "./appearance";

describe("appearance", () => {
  beforeEach(() => {
    localStorage.clear();
    for (const attr of ["data-palette", "data-density"]) {
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
      expect(readAppearance()).toEqual({
        palette: "paper",
        density: "compact",
      });
    });

    it("falls back to the default for an invalid persisted value", () => {
      localStorage.setItem("appearance.density", "bogus");
      expect(readAppearance().density).toBe(DEFAULT_APPEARANCE.density);
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
      expect(document.documentElement.getAttribute("data-density")).toBe(
        "spacious",
      );
      expect(localStorage.getItem("appearance.density")).toBe("spacious");
    });
  });
});
