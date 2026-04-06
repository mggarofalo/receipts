import { useLocation } from "react-router";
import { useMemo } from "react";

const routeLabels: Record<string, string> = {
  "/": "Home",
  "/accounts": "Accounts",
  "/categories": "Categories",
  "/subcategories": "Subcategories",
  "/item-templates": "Item Templates",
  "/receipts": "Receipts",
  "/reports": "Reports",
  "/api-keys": "API Keys",
  "/security": "Security",
  "/settings": "Settings",
  "/audit": "Audit Log",
  "/trash": "Recycle Bin",
  "/admin/users": "User Management",
  "/admin/backup": "Backup & Restore",
  "/login": "Login",
  "/change-password": "Change Password",
};

/** Overrides for individual path segments where `toTitleCase` produces the wrong result. */
const segmentOverrides: Record<string, string> = {
  ynab: "YNAB",
};

/** Maps query param keys to breadcrumb label resolvers for specific routes. */
const queryParamBreadcrumbs: Record<
  string,
  { param: string; resolve: (value: string) => string }
> = {
  "/reports": {
    param: "report",
    resolve: (slug) =>
      slug
        .split("-")
        .map((w) => w.charAt(0).toUpperCase() + w.slice(1))
        .join(" "),
  },
};

function toTitleCase(slug: string): string {
  return slug
    .split("-")
    .map((word) => word.charAt(0).toUpperCase() + word.slice(1))
    .join(" ");
}

export interface BreadcrumbSegment {
  label: string;
  path: string;
}

const EMPTY: BreadcrumbSegment[] = [];

export function useBreadcrumbs(): BreadcrumbSegment[] {
  const { pathname, search } = useLocation();

  return useMemo(() => {
    if (pathname === "/") return EMPTY;

    const crumbs: BreadcrumbSegment[] = [{ label: "Home", path: "/" }];

    // Handle nested paths like /admin/users
    const label = routeLabels[pathname];
    if (label) {
      crumbs.push({ label, path: pathname });
    } else {
      // Build segments for unknown paths
      const parts = pathname.split("/").filter(Boolean);
      let accumulated = "";
      for (const part of parts) {
        accumulated += `/${part}`;
        const segLabel =
          routeLabels[accumulated] ?? segmentOverrides[part] ?? toTitleCase(part);
        crumbs.push({ label: segLabel, path: accumulated });
      }
    }

    // Append query-param-based breadcrumb segment if configured for this route
    const qpConfig = queryParamBreadcrumbs[pathname];
    if (qpConfig) {
      const params = new URLSearchParams(search);
      const value = params.get(qpConfig.param);
      if (value) {
        crumbs.push({
          label: qpConfig.resolve(value),
          path: `${pathname}?${qpConfig.param}=${value}`,
        });
      }
    }

    return crumbs;
  }, [pathname, search]);
}
