import type { IconComponent } from "@/components/primitives";

export interface NavItem {
  to: string;
  label: string;
  icon: IconComponent;
  kbd?: string;
  aliases?: readonly string[];
  admin?: boolean;
}

export interface NavSection {
  title: string;
  items: readonly NavItem[];
}

/**
 * How strongly `item` claims `pathname`. `0` means "no claim"; higher wins.
 *
 * Scoring is `matchedLength * 2` so a longer (more specific) path always beats a
 * shorter one; the `- 1` alias penalty then breaks the only tie that can actually
 * occur. Two bases that both match the same pathname and have equal length must
 * be the identical string, so the sole way to tie is two items declaring the same
 * base — one as its `to`, the other as an alias — and there the item's own `to`
 * should win. A length difference already moves the score by at least 2, so the
 * penalty can never flip a length ordering.
 *
 * (An exact-match bonus was tried here and removed: an exact match's base *is* the
 * whole pathname, so it is strictly longer than any prefix match's base and
 * already wins on length. The bonus could never discriminate.)
 *
 * Matching is case-insensitive. React Router compiles route paths with the `i`
 * flag unless a route sets `caseSensitive`, and none of ours do, so `/RECEIPTS`
 * genuinely renders the Receipts page. `NavLink` used to case-fold both sides for
 * us; now that the active item is resolved here, this has to do it too, or those
 * URLs render a real page with nothing highlighted at all.
 */
export function matchStrength(pathname: string, item: NavItem): number {
  const path = pathname.toLowerCase();

  let best = 0;
  const consider = (candidate: string, isAlias: boolean) => {
    // Normalise a trailing slash so "/receipts/" scores like "/receipts".
    const lowered = candidate.toLowerCase();
    const base = lowered.endsWith("/") ? lowered.slice(0, -1) : lowered;
    if (base === "") return;

    if (path !== base && !path.startsWith(base + "/")) return;

    let strength = base.length * 2;

    if (isAlias) strength -= 1;
    if (strength > best) best = strength;
  };

  // The root is exact-only for its own `to`, or it would prefix-claim every
  // route. Its aliases still apply — they carry their own concrete paths.
  if (item.to === "/") {
    if (path === "/") best = 1;
  } else {
    consider(item.to, false);
  }

  for (const alias of item.aliases ?? []) consider(alias, true);
  return best;
}

/**
 * Resolves the single nav item that owns `pathname`, or `null` when nothing
 * matches.
 *
 * Resolution must happen across the whole nav rather than per item: evaluated in
 * isolation, `/settings/ynab` satisfies both YNAB (exact) and Settings (prefix),
 * which lit up two sidebar entries and emitted two `aria-current="page"` elements
 * — an accessibility defect, not just a cosmetic one (RECEIPTS-833).
 *
 * Pass the *admin-filtered* sections, so a hidden item can never win and leave
 * the nav with nothing highlighted.
 *
 * Returns the item itself rather than its `to`, so callers compare by identity.
 * Comparing by `to` would light up *both* entries if two ever shared a path,
 * silently re-creating the defect this exists to prevent, and would make the
 * first-declared tie-break below a no-op at the render site.
 */
export function resolveActiveNavItem(
  pathname: string,
  sections: readonly NavSection[],
): NavItem | null {
  let bestItem: NavItem | null = null;
  let bestStrength = 0;
  for (const section of sections) {
    for (const item of section.items) {
      const strength = matchStrength(pathname, item);
      // Strictly greater, so the first-declared item wins a tie.
      if (strength > bestStrength) {
        bestStrength = strength;
        bestItem = item;
      }
    }
  }
  return bestItem;
}
