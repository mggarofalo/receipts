import { afterEach, describe, expect, it, vi } from "vitest";
import { formatShortcutLabel, primaryModifierLabel } from "./keyboard-labels";

afterEach(() => vi.restoreAllMocks());

describe("platform shortcut labels", () => {
  it.each([
    ["Win32", "Ctrl"],
    ["Linux x86_64", "Ctrl"],
    ["", "Ctrl"],
    ["MacIntel", "Command"],
    ["iPad", "Command"],
    ["iPhone", "Command"],
  ])("uses the primary modifier for %s", (platform, label) => {
    vi.spyOn(navigator, "platform", "get").mockReturnValue(platform);
    expect(primaryModifierLabel()).toBe(label);
    for (const key of ["K", "A", "Enter"]) {
      expect(formatShortcutLabel(`Ctrl+${key}`)).toBe(`${label}+${key}`);
    }
    expect(formatShortcutLabel("Shift+N")).toBe("Shift+N");
    expect(formatShortcutLabel("?")).toBe("?");
  });
});
