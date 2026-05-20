/**
 * Design-system icon set (RECEIPTS-592 / Phase 18).
 *
 * Option A from the epic: the 30 icons are ported verbatim from the design
 * bundle's `primitives.jsx`, so they are stylistically matched to the shell
 * rather than approximated with a third-party library.
 *
 * Each icon renders a 24×24 `viewBox` SVG with `stroke="currentColor"`,
 * `stroke-width="1.6"`, and `fill="none"`. Default render size is 16px;
 * pass `width`/`height` (or a CSS rule) to resize, and any SVG prop to
 * override — e.g. `<Icon.Warn strokeWidth={1.8} />`.
 */
import type { ComponentType, ReactNode, SVGProps } from "react";

export type IconProps = SVGProps<SVGSVGElement>;
export type IconComponent = ComponentType<IconProps>;

function makeIcon(name: string, children: ReactNode): IconComponent {
  function IconImpl({ width = 16, height = 16, ...props }: IconProps) {
    return (
      <svg
        viewBox="0 0 24 24"
        width={width}
        height={height}
        stroke="currentColor"
        strokeWidth={1.6}
        fill="none"
        {...props}
      >
        {children}
      </svg>
    );
  }
  IconImpl.displayName = `Icon.${name}`;
  return IconImpl;
}

export const Icon = {
  Dashboard: makeIcon(
    "Dashboard",
    <>
      <rect x="3" y="3" width="7" height="9" rx="1.5" />
      <rect x="14" y="3" width="7" height="5" rx="1.5" />
      <rect x="14" y="12" width="7" height="9" rx="1.5" />
      <rect x="3" y="15" width="7" height="6" rx="1.5" />
    </>,
  ),
  Receipt: makeIcon(
    "Receipt",
    <>
      <path d="M5 3v18l2-1.5L9 21l2-1.5L13 21l2-1.5L17 21l2-1.5V3" />
      <path d="M8 8h8M8 12h8M8 16h5" />
    </>,
  ),
  Scan: makeIcon(
    "Scan",
    <>
      <path d="M3 8V5a2 2 0 0 1 2-2h3M21 8V5a2 2 0 0 0-2-2h-3M3 16v3a2 2 0 0 0 2 2h3M21 16v3a2 2 0 0 1-2 2h-3" />
      <path d="M7 12h10" />
    </>,
  ),
  Chart: makeIcon(
    "Chart",
    <>
      <path d="M3 3v18h18" />
      <path d="M7 15l4-5 3 3 5-7" />
    </>,
  ),
  Tag: makeIcon(
    "Tag",
    <>
      <path d="M3 12V4h8l10 10-8 8z" />
      <circle cx="7" cy="8" r="1.5" />
    </>,
  ),
  Wallet: makeIcon(
    "Wallet",
    <>
      <rect x="3" y="6" width="18" height="14" rx="2" />
      <path d="M16 13h3M3 10h18" />
    </>,
  ),
  Card: makeIcon(
    "Card",
    <>
      <rect x="3" y="5" width="18" height="14" rx="2" />
      <path d="M3 10h18M7 15h3" />
    </>,
  ),
  Link: makeIcon(
    "Link",
    <>
      <path d="M10 14a4 4 0 0 0 5.66 0l3-3a4 4 0 0 0-5.66-5.66l-1 1" />
      <path d="M14 10a4 4 0 0 0-5.66 0l-3 3a4 4 0 0 0 5.66 5.66l1-1" />
    </>,
  ),
  Settings: makeIcon(
    "Settings",
    <>
      <circle cx="12" cy="12" r="3" />
      <path d="M19 12a7 7 0 0 0-.2-1.6l2-1.5-2-3.5-2.3 1a7 7 0 0 0-2.8-1.6L13 2h-4L8.3 4.8a7 7 0 0 0-2.8 1.6l-2.3-1-2 3.5 2 1.5A7 7 0 0 0 3 12a7 7 0 0 0 .2 1.6l-2 1.5 2 3.5 2.3-1a7 7 0 0 0 2.8 1.6L9 22h4l.7-2.8a7 7 0 0 0 2.8-1.6l2.3 1 2-3.5-2-1.5c.1-.5.2-1 .2-1.6z" />
    </>,
  ),
  Users: makeIcon(
    "Users",
    <>
      <circle cx="9" cy="8" r="3.5" />
      <path d="M3 20c0-3 2.5-6 6-6s6 3 6 6" />
      <circle cx="17" cy="7" r="2.5" />
      <path d="M15 14c3 0 6 2 6 5" />
    </>,
  ),
  Plus: makeIcon("Plus", <path d="M12 5v14M5 12h14" />),
  Search: makeIcon(
    "Search",
    <>
      <circle cx="11" cy="11" r="7" />
      <path d="M20 20l-4-4" />
    </>,
  ),
  Arrow: makeIcon("Arrow", <path d="M5 12h14M13 6l6 6-6 6" />),
  Copy: makeIcon(
    "Copy",
    <>
      <rect x="9" y="9" width="11" height="11" rx="2" />
      <path d="M15 9V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h4" />
    </>,
  ),
  Edit: makeIcon("Edit", <path d="M4 20h4L19 9l-4-4L4 16z" />),
  Trash: makeIcon(
    "Trash",
    <path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2M6 6l1 14a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-14" />,
  ),
  Check: makeIcon("Check", <path d="M4 12l5 5L20 6" />),
  X: makeIcon("X", <path d="M6 6l12 12M18 6L6 18" />),
  Filter: makeIcon("Filter", <path d="M3 5h18l-7 9v6l-4-2v-4z" />),
  Calendar: makeIcon(
    "Calendar",
    <>
      <rect x="3" y="5" width="18" height="16" rx="2" />
      <path d="M8 3v4M16 3v4M3 10h18" />
    </>,
  ),
  AlertTriangle: makeIcon(
    "AlertTriangle",
    <>
      <path d="M12 3l10 18H2z" />
      <path d="M12 10v5M12 18v.01" />
    </>,
  ),
  Info: makeIcon(
    "Info",
    <>
      <circle cx="12" cy="12" r="9" />
      <path d="M12 8v.01M11 12h1v5h1" />
    </>,
  ),
  Command: makeIcon(
    "Command",
    <path d="M9 6a3 3 0 1 0-3 3h12a3 3 0 1 0-3-3v12a3 3 0 1 0 3-3H6a3 3 0 1 0 3 3z" />,
  ),
  Upload: makeIcon(
    "Upload",
    <>
      <path d="M12 16V4M6 10l6-6 6 6" />
      <path d="M4 18v2a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-2" />
    </>,
  ),
  Camera: makeIcon(
    "Camera",
    <>
      <path d="M3 7h4l2-3h6l2 3h4v13H3z" />
      <circle cx="12" cy="13" r="4" />
    </>,
  ),
  Inbox: makeIcon(
    "Inbox",
    <>
      <path d="M3 13l3-9h12l3 9v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />
      <path d="M3 13h5l2 3h4l2-3h5" />
    </>,
  ),
  Sparkle: makeIcon("Sparkle", <path d="M12 3l2 6 6 2-6 2-2 6-2-6-6-2 6-2z" />),
  Sliders: makeIcon(
    "Sliders",
    <>
      <path d="M4 6h16M4 12h16M4 18h16" />
      <circle cx="8" cy="6" r="2" fill="currentColor" />
      <circle cx="16" cy="12" r="2" fill="currentColor" />
      <circle cx="10" cy="18" r="2" fill="currentColor" />
    </>,
  ),
  Clock: makeIcon(
    "Clock",
    <>
      <circle cx="12" cy="12" r="9" />
      <path d="M12 7v5l3 2" />
    </>,
  ),
  ChevronR: makeIcon("ChevronR", <path d="M9 6l6 6-6 6" />),
  ChevronD: makeIcon("ChevronD", <path d="M6 9l6 6 6-6" />),
} as const;

export type IconName = keyof typeof Icon;
