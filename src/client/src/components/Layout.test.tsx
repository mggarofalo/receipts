import { describe, it, expect, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { render } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { AuthContext, type AuthContextValue } from "@/contexts/auth-context";
import { AppearanceProvider } from "@/contexts/AppearanceContext";
import {
  ShortcutsContext,
  type ShortcutsContextValue,
} from "@/contexts/shortcuts-context";
import { Layout } from "./Layout";

vi.mock("@/hooks/useSignalR", () => ({
  useSignalR: vi.fn(() => ({ connectionState: "connected" as const })),
}));

vi.mock("@/hooks/useGlobalShortcuts", () => ({
  useGlobalShortcuts: vi.fn(),
}));

vi.mock("@/hooks/useBreadcrumbs", () => ({
  useBreadcrumbs: vi.fn(() => []),
}));

vi.mock("@/components/ShortcutsHelp", () => ({
  ShortcutsHelp: () => <div data-testid="shortcuts-help" />,
}));

vi.mock("@/components/CommandPalette", () => ({
  CommandPalette: () => <div data-testid="command-palette" />,
}));

const defaultAuth: AuthContextValue = {
  user: {
    userId: "test-user-id",
    email: "test@example.com",
    roles: ["User"],
    mustResetPassword: false,
  },
  isLoading: false,
  mustResetPassword: false,
  login: async () => {},
  logout: async () => {},
  changePassword: async () => {},
};

const defaultShortcuts: ShortcutsContextValue = {
  helpOpen: false,
  setHelpOpen: vi.fn(),
};

function renderLayout(authOverrides?: Partial<AuthContextValue>) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  const authValue = { ...defaultAuth, ...authOverrides };

  const router = createMemoryRouter(
    [
      {
        path: "/",
        element: <Layout />,
        children: [{ index: true, element: <div>Home Page Content</div> }],
      },
    ],
    { initialEntries: ["/"] },
  );

  return render(
    <QueryClientProvider client={queryClient}>
      <AppearanceProvider>
        <AuthContext.Provider value={authValue}>
          <ShortcutsContext.Provider value={defaultShortcuts}>
            <RouterProvider router={router} />
          </ShortcutsContext.Provider>
        </AuthContext.Provider>
      </AppearanceProvider>
    </QueryClientProvider>,
  );
}

describe("Layout", () => {
  it("renders the app brand name", () => {
    renderLayout();
    expect(screen.getAllByText("Receipts").length).toBeGreaterThan(0);
  });

  it("renders outlet content", () => {
    renderLayout();
    expect(screen.getByText("Home Page Content")).toBeInTheDocument();
  });

  it("shows user email in the topbar", () => {
    renderLayout({
      user: {
        userId: "admin-id",
        email: "admin@test.com",
        roles: ["Admin"],
        mustResetPassword: false,
      },
    });
    expect(screen.getByText("admin@test.com")).toBeInTheDocument();
  });

  it("renders the topbar search button", () => {
    renderLayout();
    expect(
      screen.getByRole("button", { name: /search or jump to/i }),
    ).toBeInTheDocument();
  });

  it("renders connection status indicator", () => {
    renderLayout();
    expect(screen.getAllByText("Live").length).toBeGreaterThan(0);
  });

  it("renders skip-to-content link for accessibility", () => {
    renderLayout();
    const skipLink = screen.getByText("Skip to main content");
    expect(skipLink).toHaveAttribute("href", "#main-content");
  });

  it("renders the primary navigation sidebar with sections", () => {
    renderLayout();
    const sidebar = screen.getByRole("complementary", {
      name: /primary navigation/i,
    });
    expect(within(sidebar).getByText("Workspace")).toBeInTheDocument();
    expect(within(sidebar).getByText("Library")).toBeInTheDocument();
    expect(within(sidebar).getByText("Account")).toBeInTheDocument();
  });

  it("shows the Admin section only for admin users", () => {
    const { unmount } = renderLayout();
    expect(screen.queryByText("Admin")).not.toBeInTheDocument();
    unmount();
    renderLayout({
      user: {
        userId: "admin-id",
        email: "admin@test.com",
        roles: ["Admin"],
        mustResetPassword: false,
      },
    });
    expect(screen.getByText("Admin")).toBeInTheDocument();
  });

  it("marks the Dashboard nav item as active on the root route", () => {
    renderLayout();
    const sidebar = screen.getByRole("complementary", {
      name: /primary navigation/i,
    });
    const dashboard = within(sidebar)
      .getAllByRole("link")
      .find((link) => link.textContent?.includes("Dashboard"));
    expect(dashboard).toBeDefined();
    expect(dashboard).toHaveClass("active");
    expect(dashboard).toHaveAttribute("aria-current", "page");
  });

  it("opens the user dropdown and exposes API Keys + Logout", async () => {
    const user = userEvent.setup();
    renderLayout();
    await user.click(
      screen.getByRole("button", { name: /user menu for/i }),
    );
    await waitFor(() => {
      expect(
        screen.getByRole("menuitem", { name: "API Keys" }),
      ).toBeInTheDocument();
      expect(
        screen.getByRole("menuitem", { name: "Logout" }),
      ).toBeInTheDocument();
    });
  });

  it("calls logout when Logout is clicked in the user dropdown", async () => {
    const user = userEvent.setup();
    const logoutMock = vi.fn().mockResolvedValue(undefined);
    renderLayout({ logout: logoutMock });
    await user.click(
      screen.getByRole("button", { name: /user menu for/i }),
    );
    await waitFor(() =>
      expect(
        screen.getByRole("menuitem", { name: "Logout" }),
      ).toBeInTheDocument(),
    );
    await user.click(screen.getByRole("menuitem", { name: "Logout" }));
    await waitFor(() => expect(logoutMock).toHaveBeenCalled());
  });

  it("opens the More sheet from the mobile tabbar", async () => {
    const user = userEvent.setup();
    renderLayout();
    await user.click(
      screen.getByRole("button", { name: /more navigation/i }),
    );
    await waitFor(() => {
      const dialog = screen.getByRole("dialog");
      expect(within(dialog).getByText("Workspace")).toBeInTheDocument();
    });
  });
});
