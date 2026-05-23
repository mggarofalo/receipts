import { renderHook } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { useGlobalShortcuts } from "./useGlobalShortcuts";
import { ShortcutsContext } from "@/contexts/shortcuts-context";
import type { ReactNode } from "react";

// Mock useHotkeys to capture the shift+n callback
let shiftNCallback: (() => void) | null = null;
vi.mock("react-hotkeys-hook", () => ({
  useHotkeys: vi.fn(
    (keys: string, callback: () => void) => {
      if (keys === "shift+n") {
        shiftNCallback = callback;
      }
    },
  ),
}));

describe("useGlobalShortcuts", () => {
  beforeEach(() => {
    shiftNCallback = null;
  });

  it("toggles help via ? key", () => {
    const setHelpOpen = vi.fn();
    const wrapper = ({ children }: { children: ReactNode }) => (
      <MemoryRouter>
        <ShortcutsContext.Provider
          value={{ helpOpen: false, setHelpOpen }}
        >
          {children}
        </ShortcutsContext.Provider>
      </MemoryRouter>
    );

    renderHook(() => useGlobalShortcuts(), { wrapper });

    // The ? key handler uses useKeyboardShortcut which adds a keydown listener
    document.dispatchEvent(new KeyboardEvent("keydown", { key: "?" }));
    expect(setHelpOpen).toHaveBeenCalledWith(true);
  });

  it("dispatches shortcut:new-item event on Shift+N", () => {
    const wrapper = ({ children }: { children: ReactNode }) => (
      <MemoryRouter>
        <ShortcutsContext.Provider
          value={{ helpOpen: false, setHelpOpen: vi.fn() }}
        >
          {children}
        </ShortcutsContext.Provider>
      </MemoryRouter>
    );

    renderHook(() => useGlobalShortcuts(), { wrapper });

    const listener = vi.fn();
    window.addEventListener("shortcut:new-item", listener);

    if (shiftNCallback) shiftNCallback();
    expect(listener).toHaveBeenCalled();

    window.removeEventListener("shortcut:new-item", listener);
  });
});
