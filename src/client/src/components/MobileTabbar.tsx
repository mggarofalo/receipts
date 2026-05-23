import { Link, useLocation } from "react-router";
import { Icon } from "@/components/primitives";
import { cn } from "@/lib/utils";

/**
 * Bottom-anchored navigation for the mobile shell (≤ 900px). Pairs with the
 * sidebar-hides rule in `index.css:1499` so JS and CSS stay in lockstep.
 * Visibility is CSS-driven (`.mobile-tabbar { display: none }` at ≥ 900px),
 * so the component always renders — that keeps the responsive swap free of
 * JS layout shift when the breakpoint is crossed.
 */
const TABS = [
  { to: "/", label: "Home", icon: Icon.Dashboard },
  // Label matches the sidebar entry so screen-reader / voice-control users
  // hear the same name for the same destination across the two shells
  // (WCAG 3.2.4 consistent identification).
  { to: "/receipts", label: "Receipts", icon: Icon.Receipt },
  { to: "/receipts/new", label: "New", icon: Icon.Plus },
  { to: "/reports", label: "Reports", icon: Icon.Chart },
] as const;

interface MobileTabbarProps {
  onOpenMore: () => void;
}

function isTabActive(pathname: string, to: string): boolean {
  if (to === "/") return pathname === "/";
  if (to === "/receipts") {
    // /receipts/new owns its own tab, so don't let the List tab claim it.
    // Detail routes (/receipts/:id) still light up List — keeps the mental
    // model consistent with the desktop sidebar.
    if (pathname === "/receipts/new") return false;
    return pathname === "/receipts" || pathname.startsWith("/receipts/");
  }
  if (to === "/receipts/new") return pathname === "/receipts/new";
  return pathname === to || pathname.startsWith(to + "/");
}

export function MobileTabbar({ onOpenMore }: MobileTabbarProps) {
  const location = useLocation();

  return (
    <nav className="mobile-tabbar" aria-label="Mobile navigation">
      {TABS.map((tab) => {
        const TabIcon = tab.icon;
        const active = isTabActive(location.pathname, tab.to);
        // Plain Link (not NavLink) — NavLink's own aria-current matching is
        // too coarse for /receipts vs /receipts/new; we drive the active
        // state from isTabActive instead.
        return (
          <Link
            key={tab.to}
            to={tab.to}
            className={cn(active && "on")}
            aria-current={active ? "page" : undefined}
          >
            <TabIcon />
            {tab.label}
          </Link>
        );
      })}
      <button
        type="button"
        onClick={onOpenMore}
        aria-label="More"
      >
        <Icon.Sliders />
        More
      </button>
    </nav>
  );
}
