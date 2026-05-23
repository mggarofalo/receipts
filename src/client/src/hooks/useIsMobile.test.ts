import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { useIsMobile } from "./useIsMobile";

type Listener = (event: MediaQueryListEvent) => void;

interface MockMql {
  matches: boolean;
  media: string;
  addEventListener: ReturnType<typeof vi.fn>;
  removeEventListener: ReturnType<typeof vi.fn>;
  addListener: ReturnType<typeof vi.fn>;
  removeListener: ReturnType<typeof vi.fn>;
  // Test hook to fire a change event from the test side.
  fire(matches: boolean): void;
}

function installMatchMedia(initialMatches: boolean): MockMql {
  const listeners = new Set<Listener>();
  const mql: MockMql = {
    matches: initialMatches,
    media: "(max-width: 900px)",
    addEventListener: vi.fn((_event: string, l: Listener) => {
      listeners.add(l);
    }),
    removeEventListener: vi.fn((_event: string, l: Listener) => {
      listeners.delete(l);
    }),
    addListener: vi.fn((l: Listener) => listeners.add(l)),
    removeListener: vi.fn((l: Listener) => listeners.delete(l)),
    fire(matches: boolean) {
      mql.matches = matches;
      const ev = { matches, media: mql.media } as unknown as MediaQueryListEvent;
      for (const l of listeners) l(ev);
    },
  };
  Object.defineProperty(window, "matchMedia", {
    configurable: true,
    writable: true,
    value: vi.fn(() => mql),
  });
  return mql;
}

describe("useIsMobile", () => {
  let originalMatchMedia: typeof window.matchMedia | undefined;

  beforeEach(() => {
    originalMatchMedia = window.matchMedia;
  });

  afterEach(() => {
    if (originalMatchMedia) {
      Object.defineProperty(window, "matchMedia", {
        configurable: true,
        writable: true,
        value: originalMatchMedia,
      });
    } else {
      // @ts-expect-error - intentional cleanup for tests that mocked it.
      delete window.matchMedia;
    }
  });

  it("returns true when the viewport starts below the breakpoint", () => {
    installMatchMedia(true);
    const { result } = renderHook(() => useIsMobile());
    expect(result.current).toBe(true);
  });

  it("returns false when the viewport starts above the breakpoint", () => {
    installMatchMedia(false);
    const { result } = renderHook(() => useIsMobile());
    expect(result.current).toBe(false);
  });

  it("updates when the media query change event fires", () => {
    const mql = installMatchMedia(false);
    const { result } = renderHook(() => useIsMobile());
    expect(result.current).toBe(false);

    act(() => {
      mql.fire(true);
    });
    expect(result.current).toBe(true);

    act(() => {
      mql.fire(false);
    });
    expect(result.current).toBe(false);
  });

  it("registers and unregisters the change listener", () => {
    const mql = installMatchMedia(false);
    const { unmount } = renderHook(() => useIsMobile());
    expect(mql.addEventListener).toHaveBeenCalledWith("change", expect.any(Function));
    unmount();
    expect(mql.removeEventListener).toHaveBeenCalledWith("change", expect.any(Function));
  });

  it("falls back to false when matchMedia is unavailable", () => {
    // @ts-expect-error - simulating an SSR-like environment.
    delete window.matchMedia;
    const { result } = renderHook(() => useIsMobile());
    expect(result.current).toBe(false);
  });
});
