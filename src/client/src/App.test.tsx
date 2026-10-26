import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import {
  createMemoryRouter,
  RouterProvider,
  Outlet,
  Navigate,
} from "react-router";
import type { ReactNode } from "react";

const session = vi.hoisted(() => ({ authenticated: true }));
beforeEach(() => {
  session.authenticated = true;
});

vi.mock("@/pages/PurchaseSearch", () => ({
  default: () => <div data-testid="page-purchase-search">Purchase history</div>,
}));
// Mock all page components to simple stubs
vi.mock("@/pages/Dashboard", () => ({
  default: () => <div data-testid="page-dashboard">Dashboard Page</div>,
}));
vi.mock("@/pages/Login", () => ({
  default: () => <div data-testid="page-login">Login Page</div>,
}));
vi.mock("@/pages/ChangePassword", () => ({
  default: () => <div data-testid="page-change-password">Change Password</div>,
}));
vi.mock("@/pages/Cards", () => ({
  default: () => <div data-testid="page-cards">Cards Page</div>,
}));
vi.mock("@/pages/Taxonomy", () => ({
  default: () => <div data-testid="page-categories">Categories</div>,
}));
vi.mock("@/pages/Receipts", () => ({
  default: () => <div data-testid="page-receipts">Receipts</div>,
}));
vi.mock("@/pages/ReceiptDetail", () => ({
  default: () => <div data-testid="page-receipt-detail">ReceiptDetail</div>,
}));
vi.mock("@/pages/ApiKeys", () => ({
  default: () => <div data-testid="page-api-keys">ApiKeys</div>,
}));
vi.mock("@/pages/AdminUsers", () => ({
  default: () => <div data-testid="page-admin-users">AdminUsers</div>,
}));
vi.mock("@/pages/AuditLog", () => ({
  default: () => <div data-testid="page-audit-log">AuditLog</div>,
}));
vi.mock("@/pages/SecurityLog", () => ({
  default: () => <div data-testid="page-security-log">SecurityLog</div>,
}));
vi.mock("@/pages/RecycleBin", () => ({
  default: () => <div data-testid="page-recycle-bin">RecycleBin</div>,
}));
vi.mock("@/pages/Reports", () => ({
  default: () => <div data-testid="page-reports">Reports</div>,
}));
vi.mock("@/pages/new-receipt/NewReceiptPage", () => ({
  default: () => <div data-testid="page-new-receipt">NewReceipt</div>,
}));
vi.mock("@/pages/settings/YnabSettings", () => ({
  default: () => <div data-testid="page-ynab-settings">YnabSettings</div>,
}));
vi.mock("@/pages/BackupRestore", () => ({
  default: () => <div data-testid="page-backup-restore">BackupRestore</div>,
}));
vi.mock("@/pages/NotFound", () => ({
  default: () => <div data-testid="page-not-found">Not Found</div>,
}));

// Mock layout/route wrappers to pass through children via Outlet
vi.mock("@/components/RootLayout", () => ({
  RootLayout: () => (
    <div data-testid="root-layout">
      <Outlet />
    </div>
  ),
}));

vi.mock("@/components/PublicLayout", () => ({
  PublicLayout: () => (
    <div data-testid="public-layout">
      <Outlet />
    </div>
  ),
}));

vi.mock("@/components/Layout", () => ({
  Layout: () => (
    <div data-testid="layout">
      <Outlet />
    </div>
  ),
}));

vi.mock("@/components/ProtectedRoute", () => ({
  ProtectedRoute: ({ children }: { children: ReactNode }) =>
    session.authenticated ? <>{children}</> : <Navigate to="/login" replace />,
}));

vi.mock("@/components/AdminRoute", () => ({
  AdminRoute: ({ children }: { children: ReactNode }) => <>{children}</>,
}));

vi.mock("@/components/ui/sonner", () => ({
  Toaster: () => null,
}));

// Import routeConfig from App.tsx (vi.mock is hoisted, so mocks are applied)
import { routeConfig } from "./App";

function renderRoute(path: string) {
  const router = createMemoryRouter(routeConfig, { initialEntries: [path] });
  return render(<RouterProvider router={router} />);
}

describe("App router", () => {
  it('renders Dashboard page at "/" route', async () => {
    renderRoute("/");
    expect(await screen.findByTestId("page-dashboard")).toBeInTheDocument();
  });

  it('renders Cards page at "/cards" route', async () => {
    renderRoute("/cards");
    expect(await screen.findByTestId("page-cards")).toBeInTheDocument();
  });

  it('renders Login page at "/login" route', async () => {
    renderRoute("/login");
    expect(await screen.findByTestId("page-login")).toBeInTheDocument();
  });

  it("renders NotFound for unknown routes", async () => {
    renderRoute("/some/unknown/path");
    expect(await screen.findByTestId("page-not-found")).toBeInTheDocument();
  });

  it('renders Reports page at "/reports" route', async () => {
    renderRoute("/reports");
    expect(await screen.findByTestId("page-reports")).toBeInTheDocument();
  });

  it('renders NewReceipt page at "/receipts/new" route', async () => {
    renderRoute("/receipts/new");
    expect(await screen.findByTestId("page-new-receipt")).toBeInTheDocument();
  });

  it('renders YnabSettings page at "/settings/ynab" route', async () => {
    renderRoute("/settings/ynab");
    expect(await screen.findByTestId("page-ynab-settings")).toBeInTheDocument();
  });

  it('renders BackupRestore page at "/admin/backup" route', async () => {
    renderRoute("/admin/backup");
    expect(
      await screen.findByTestId("page-backup-restore"),
    ).toBeInTheDocument();
  });

  it("renders NotFound for deprecated redirect routes", async () => {
    for (const path of [
      "/receipt-items",
      "/transactions",
      "/trips",
      "/transaction-detail",
      "/receipt-detail",
    ]) {
      const { unmount } = renderRoute(path);
      expect(await screen.findByTestId("page-not-found")).toBeInTheDocument();
      unmount();
    }
  });
});

it("routes a shareable purchase search URL into the application layout", async () => {
  renderRoute("/search?q=hot+dogs&offset=25");
  expect(await screen.findByTestId("page-purchase-search")).toBeInTheDocument();
  expect(screen.getByTestId("layout")).toContainElement(
    screen.getByTestId("page-purchase-search"),
  );
});

it.each(["/item-templates", "/item-templates?new=true&templateId=old#edit"])(
  "replaces legacy template URL %s with the receipts list and drops template state",
  async (path) => {
    const router = createMemoryRouter(routeConfig, {
      initialEntries: ["/", path],
      initialIndex: 1,
    });
    render(<RouterProvider router={router} />);
    expect(await screen.findByTestId("page-receipts")).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/receipts");
    expect(router.state.location.search).toBe("");
    expect(router.state.location.hash).toBe("");
    expect(router.state.historyAction).toBe("REPLACE");
    await router.navigate(-1);
    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
    router.dispose();
  },
);

it.each([
  "/categories",
  "/taxonomy?revision=rev-42#details",
  "/subcategories?categoryId=old&new=true#details",
])("uses the authenticated Categories page for %s", async (path) => {
  const router = createMemoryRouter(routeConfig, {
    initialEntries: ["/", path],
    initialIndex: 1,
  });
  render(<RouterProvider router={router} />);
  expect(await screen.findByTestId("page-categories")).toBeInTheDocument();
  expect(router.state.location.pathname).toBe("/categories");
  expect(router.state.location.search).toBe(
    path.startsWith("/taxonomy") ? "?revision=rev-42" : "",
  );
  expect(screen.getByTestId("layout")).toContainElement(
    screen.getByTestId("page-categories"),
  );
  if (path !== "/categories") {
    expect(router.state.historyAction).toBe("REPLACE");
    expect(router.state.location.hash).toBe("#details");
    await router.navigate(-1);
    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
  }
  router.dispose();
});

it.each([
  "/categories",
  "/taxonomy?revision=rev-42",
  "/subcategories?categoryId=old",
])("keeps %s behind authentication", async (path) => {
  session.authenticated = false;
  renderRoute(path);
  expect(await screen.findByTestId("page-login")).toBeInTheDocument();
  expect(screen.queryByTestId("page-categories")).not.toBeInTheDocument();
});
