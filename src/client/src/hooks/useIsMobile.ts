import { useEffect, useState } from "react";

/**
 * Reactively reports whether the viewport matches the mobile breakpoint
 * (≤ 900px). Mirrors the CSS breakpoint at `.app { grid-template-columns: 1fr }`
 * in `index.css` (line 1495) so JS-side gating stays in lockstep with the
 * sidebar-hides / mobile-tabbar-shows transition.
 *
 * SSR-safe: returns `false` before the first client tick when `window` is
 * undefined.
 */
const MOBILE_QUERY = "(max-width: 900px)";

export function useIsMobile(): boolean {
  const [isMobile, setIsMobile] = useState<boolean>(() => {
    if (typeof window === "undefined" || typeof window.matchMedia !== "function") {
      return false;
    }
    return window.matchMedia(MOBILE_QUERY).matches;
  });

  useEffect(() => {
    if (typeof window === "undefined" || typeof window.matchMedia !== "function") {
      return;
    }
    const mql = window.matchMedia(MOBILE_QUERY);
    const update = (event: MediaQueryListEvent | MediaQueryList) => {
      setIsMobile(event.matches);
    };
    update(mql);
    // addEventListener is the modern API; addListener is the deprecated
    // fallback that Safari < 14 still needs.
    if (typeof mql.addEventListener === "function") {
      mql.addEventListener("change", update);
      return () => mql.removeEventListener("change", update);
    }
    mql.addListener(update);
    return () => mql.removeListener(update);
  }, []);

  return isMobile;
}
