import { describe, it, expect } from "vitest";
import {
  resolveActiveNavItem,
  type NavItem,
  type NavSection,
} from "./nav-active";

// The real NAV is exercised through the rendered component in Layout.test.tsx.
// These tests drive the resolver with synthetic navs instead, to pin the rules
// that the live NAV cannot falsify — most-specific-wins is indistinguishable
// from first-match-wins there purely because /settings/ynab happens to be
// declared before /settings, and no NAV entry exercises the tie-break, the alias
// penalty, or a filtered-out item.

const icon = (() => null) as unknown as NavItem["icon"];

function item(
  to: string,
  label: string,
  extra: Partial<NavItem> = {},
): NavItem {
  return { to, label, icon, ...extra };
}

function nav(...items: NavItem[]): NavSection[] {
  return [{ title: "Test", items }];
}

describe("resolveActiveNavItem", () => {
  it("prefers the most specific match over the first declared one", () => {
    // Declaration order is deliberately the reverse of the real NAV: the LESS
    // specific item comes first. A "first item that matches at all wins" rule
    // would return Settings here and still satisfy every real-NAV case.
    const settings = item("/settings", "Settings");
    const ynab = item("/settings/ynab", "YNAB");

    expect(resolveActiveNavItem("/settings/ynab", nav(settings, ynab))).toBe(
      ynab,
    );
    // ...and the same holds in the other declaration order.
    expect(resolveActiveNavItem("/settings/ynab", nav(ynab, settings))).toBe(
      ynab,
    );
  });

  it("still resolves the less specific item on its own route", () => {
    const settings = item("/settings", "Settings");
    const ynab = item("/settings/ynab", "YNAB");

    expect(resolveActiveNavItem("/settings", nav(settings, ynab))).toBe(
      settings,
    );
  });

  it("resolves a duplicated path to the first-declared item", () => {
    // Two items sharing a `to` would both satisfy an `item.to === activePath`
    // comparison at the render site, re-creating the double-aria-current defect.
    const first = item("/settings", "Settings");
    const second = item("/settings", "Preferences");

    const resolved = resolveActiveNavItem("/settings", nav(first, second));

    expect(resolved).toBe(first);
    expect(resolved).not.toBe(second);
  });

  it("matches an alias that is not a sub-path of the item's own `to`", () => {
    // The real NAV's aliases are all sub-paths of their own `to`, so the `to`
    // prefix match already covers them and the alias loop is never load-bearing
    // there. This is the case that actually exercises it.
    const reports = item("/reports", "Reports", { aliases: ["/analytics"] });
    const other = item("/receipts", "Receipts");

    expect(resolveActiveNavItem("/analytics", nav(other, reports))).toBe(
      reports,
    );
    expect(resolveActiveNavItem("/analytics/spend", nav(other, reports))).toBe(
      reports,
    );
  });

  it("lets an item's own `to` beat another item's identical alias", () => {
    // This is the only situation in which the -1 alias penalty decides anything.
    const aliasHolder = item("/a", "Alias holder", { aliases: ["/reports"] });
    const owner = item("/reports", "Reports");

    expect(resolveActiveNavItem("/reports", nav(aliasHolder, owner))).toBe(
      owner,
    );
  });

  it("ignores items filtered out of the sections it is given", () => {
    // Callers pass admin-filtered sections; scoring the unfiltered NAV instead
    // could elect an item that is never rendered, leaving nothing highlighted.
    const audit = item("/audit", "Audit", { admin: true });

    expect(resolveActiveNavItem("/audit", nav(audit))).toBe(audit);
    expect(resolveActiveNavItem("/audit", nav())).toBeNull();
  });

  it("matches the root only exactly, never as a prefix", () => {
    const dashboard = item("/", "Dashboard");
    const receipts = item("/receipts", "Receipts");

    expect(resolveActiveNavItem("/", nav(dashboard, receipts))).toBe(dashboard);
    expect(resolveActiveNavItem("/receipts", nav(dashboard, receipts))).toBe(
      receipts,
    );
  });

  it("honours aliases declared on the root item", () => {
    const dashboard = item("/", "Dashboard", { aliases: ["/home"] });

    expect(resolveActiveNavItem("/home", nav(dashboard))).toBe(dashboard);
  });

  it("requires a segment boundary rather than a bare prefix", () => {
    const receipts = item("/receipts", "Receipts");

    expect(resolveActiveNavItem("/receipts-archive", nav(receipts))).toBeNull();
    expect(resolveActiveNavItem("/receipts/new", nav(receipts))).toBe(receipts);
  });

  it("matches case-insensitively, as React Router routes do", () => {
    const receipts = item("/receipts", "Receipts");

    expect(resolveActiveNavItem("/RECEIPTS", nav(receipts))).toBe(receipts);
    expect(resolveActiveNavItem("/Receipts/New", nav(receipts))).toBe(receipts);
  });

  it("returns null when nothing claims the route", () => {
    expect(
      resolveActiveNavItem("/nowhere", nav(item("/receipts", "Receipts"))),
    ).toBeNull();
  });
});
