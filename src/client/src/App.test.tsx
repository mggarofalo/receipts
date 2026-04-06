import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { createMemoryRouter, RouterProvider, Outlet } from "react-router";
import type { ReactNode } from "react";

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
vi.mock("@/pages/Accounts", () => ({
  default: () => <div data-testid="page-accounts">Accounts Page</div>,
}));
vi.mock("@/pages/Categories", () => ({
  default: () => <div data-testid="page-categories">Categories</div>,
}));
vi.mock("@/pages/Subcategories", () => ({
  default: () => <div data-testid="page-subcategories">Subcategories</div>,
}));
vi.mock("@/pages/Receipts", () => ({
  default: () => <div data-testid="page-receipts">Receipts</div>,
}));
vi.mock("@/pages/ItemTemplates", () => ({
  default: () => <div data-testid="page-item-templates">ItemTemplates</div>,
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
  ProtectedRoute: ({ children }: { children: ReactNode }) => <>{children}</>,
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

  it('renders Accounts page at "/accounts" route', async () => {
    renderRoute("/accounts");
    expect(await screen.findByTestId("page-accounts")).toBeInTheDocument();
  });

  it('renders Login page at "/login" route', async () => {
    renderRoute("/login");
    expect(await screen.findByTestId("page-login")).toBeInTheDocument();
  });

  it("renders NotFound for unknown routes", async () => {
    renderRoute("/some/unknown/path");
    expect(await screen.findByTestId("page-not-found")).toBeInTheDocument();
  });

  it("renders NotFound for deprecated redirect routes", async () => {
    for (const path of ["/receipt-items", "/transactions", "/trips", "/transaction-detail", "/receipt-detail"]) {
      const { unmount } = renderRoute(path);
      expect(await screen.findByTestId("page-not-found")).toBeInTheDocument();
      unmount();
    }
  });
});
