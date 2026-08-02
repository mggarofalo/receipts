import { renderHook } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { useBreadcrumbs } from "./useBreadcrumbs";
import type { ReactNode } from "react";

function createWrapper(route: string) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return <MemoryRouter initialEntries={[route]}>{children}</MemoryRouter>;
  };
}

describe("useBreadcrumbs", () => {
  it("returns empty array for root path", () => {
    const { result } = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper("/"),
    });
    expect(result.current).toEqual([]);
  });

  it("returns Home + page for known route", () => {
    const { result } = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper("/accounts"),
    });
    expect(result.current).toEqual([
      { label: "Home", path: "/" },
      { label: "Accounts", path: "/accounts" },
    ]);
  });

  it("handles nested known route like /admin/users", () => {
    const { result } = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper("/admin/users"),
    });
    expect(result.current).toEqual([
      { label: "Home", path: "/" },
      { label: "User Management", path: "/admin/users" },
    ]);
  });

  it("handles nested known route like /admin/normalized-descriptions without falling back to an intermediate /admin segment", () => {
    const { result } = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper("/admin/normalized-descriptions"),
    });
    expect(result.current).toEqual([
      { label: "Home", path: "/" },
      {
        label: "Normalized Descriptions",
        path: "/admin/normalized-descriptions",
      },
    ]);
  });

  it("builds segments for unknown paths with title casing", () => {
    const { result } = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper("/foo/bar"),
    });
    expect(result.current).toEqual([
      { label: "Home", path: "/" },
      { label: "Foo", path: "/foo" },
      { label: "Bar", path: "/foo/bar" },
    ]);
  });

  it("title-cases unknown hyphenated paths", () => {
    const { result } = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper("/some-route"),
    });
    expect(result.current[1]).toEqual({
      label: "Some Route",
      path: "/some-route",
    });
  });

  it("maps /receipts/:id via segment builder", () => {
    const { result } = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper("/receipts/some-uuid"),
    });
    expect(result.current).toEqual([
      { label: "Home", path: "/" },
      { label: "Receipts", path: "/receipts" },
      { label: "Some Uuid", path: "/receipts/some-uuid" },
    ]);
  });

  it("maps categories correctly", () => {
    const { result } = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper("/categories"),
    });
    expect(result.current[1]).toEqual({
      label: "Categories",
      path: "/categories",
    });
  });

  it("maps item-templates correctly", () => {
    const { result } = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper("/item-templates"),
    });
    expect(result.current[1]).toEqual({
      label: "Item Templates",
      path: "/item-templates",
    });
  });

  it("builds intermediate segments for nested paths like /receipts/new", () => {
    const { result } = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper("/receipts/new"),
    });
    expect(result.current).toEqual([
      { label: "Home", path: "/" },
      { label: "Receipts", path: "/receipts" },
      { label: "New", path: "/receipts/new" },
    ]);
  });

  it("maps login correctly", () => {
    const { result } = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper("/login"),
    });
    expect(result.current[1]).toEqual({
      label: "Login",
      path: "/login",
    });
  });

  it("maps change-password correctly", () => {
    const { result } = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper("/change-password"),
    });
    expect(result.current[1]).toEqual({
      label: "Change Password",
      path: "/change-password",
    });
  });

  it("maps subcategories correctly", () => {
    const { result } = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper("/subcategories"),
    });
    expect(result.current).toEqual([
      { label: "Home", path: "/" },
      { label: "Subcategories", path: "/subcategories" },
    ]);
  });

  it("produces the same breadcrumbs for trailing-slash paths", () => {
    // React Router's MemoryRouter normalizes "/accounts/" to "/accounts",
    // so the hook should produce identical breadcrumbs for both.
    const withSlash = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper("/accounts/"),
    });
    const withoutSlash = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper("/accounts"),
    });
    expect(withSlash.result.current).toEqual(withoutSlash.result.current);
  });

  // useLocation().pathname does not include query strings or hash fragments.
  // For example, navigating to "/accounts?sort=name" yields pathname "/accounts",
  // so breadcrumbs are never affected by query parameters (unless the route has
  // explicit query-param breadcrumb configuration).
  it("is unaffected by query strings (pathname excludes them)", () => {
    const { result } = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper("/accounts?sort=name&order=asc"),
    });
    expect(result.current).toEqual([
      { label: "Home", path: "/" },
      { label: "Accounts", path: "/accounts" },
    ]);
  });

  it("appends report name breadcrumb from query param on /reports", () => {
    const { result } = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper("/reports?report=out-of-balance"),
    });
    expect(result.current).toEqual([
      { label: "Home", path: "/" },
      { label: "Reports", path: "/reports" },
      {
        label: "Out Of Balance",
        path: "/reports?report=out-of-balance",
      },
    ]);
  });

  it("shows only Home > Reports when no report query param", () => {
    const { result } = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper("/reports"),
    });
    expect(result.current).toEqual([
      { label: "Home", path: "/" },
      { label: "Reports", path: "/reports" },
    ]);
  });

  it("title-cases multi-word report slugs in breadcrumb", () => {
    const { result } = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper("/reports?report=item-cost-over-time"),
    });
    expect(result.current[2]).toEqual({
      label: "Item Cost Over Time",
      path: "/reports?report=item-cost-over-time",
    });
  });

  it("maps /settings/ynab with correct YNAB casing", () => {
    const { result } = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper("/settings/ynab"),
    });
    expect(result.current).toEqual([
      { label: "Home", path: "/" },
      { label: "Settings", path: "/settings" },
      { label: "YNAB", path: "/settings/ynab" },
    ]);
  });

  it("renders a GUID segment as the last 12 hex chars, lowercased", () => {
    const guid = "3fa85f64-5717-4562-b3fc-2c963f66afa6";
    const { result } = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper(`/receipts/${guid}`),
    });
    expect(result.current).toEqual([
      { label: "Home", path: "/" },
      { label: "Receipts", path: "/receipts" },
      { label: "2c963f66afa6", path: `/receipts/${guid}` },
    ]);
  });

  it("lowercases uppercase GUID segments", () => {
    const guid = "3FA85F64-5717-4562-B3FC-2C963F66AFA6";
    const { result } = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper(`/receipts/${guid}`),
    });
    expect(result.current[2]).toEqual({
      label: "2c963f66afa6",
      path: `/receipts/${guid}`,
    });
  });

  it("still title-cases non-GUID hex-looking segments", () => {
    // "abc-123" superficially looks hex but is not a canonical GUID,
    // so it should fall through to the normal title-case branch.
    const { result } = renderHook(() => useBreadcrumbs(), {
      wrapper: createWrapper("/receipts/abc-123"),
    });
    expect(result.current[2]).toEqual({
      label: "Abc 123",
      path: "/receipts/abc-123",
    });
  });
});
