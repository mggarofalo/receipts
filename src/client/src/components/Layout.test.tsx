import { describe, it, expect, vi, beforeEach } from "vitest";
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

// Layout mounts useLastRoutePersistence, which redirects "/" to the last route
// stored in localStorage. Without a reset, one test's route leaks into the next.
beforeEach(() => {
  localStorage.clear();
});

const adminUser = {
  userId: "admin-id",
  email: "admin@test.com",
  roles: ["Admin"],
  mustResetPassword: false,
};

function renderLayout(authOverrides?: Partial<AuthContextValue>, route = "/") {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  const authValue = { ...defaultAuth, ...authOverrides };

  const router = createMemoryRouter(
    [
      {
        path: "/",
        element: <Layout />,
        children: [
          { index: true, element: <div>Home Page Content</div> },
          // Splat so any pathname under test renders without route-specific setup.
          { path: "*", element: <div>Page Content</div> },
        ],
      },
    ],
    { initialEntries: [route] },
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
    renderLayout({ user: adminUser });
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
    const sidebar = screen.getByRole("navigation", {
      name: /^primary$/i,
    });
    expect(within(sidebar).getByText("Workspace")).toBeInTheDocument();
    expect(within(sidebar).getByText("Library")).toBeInTheDocument();
    expect(within(sidebar).getByText("Account")).toBeInTheDocument();
  });

  it("shows the Admin section only for admin users", () => {
    const { unmount } = renderLayout();
    expect(screen.queryByText("Admin")).not.toBeInTheDocument();
    unmount();
    renderLayout({ user: adminUser });
    expect(screen.getByText("Admin")).toBeInTheDocument();
  });

  it("marks the Dashboard nav item as active on the root route", () => {
    renderLayout();
    const sidebar = screen.getByRole("navigation", {
      name: /^primary$/i,
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
    await user.click(screen.getByRole("button", { name: /user menu for/i }));
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
    await user.click(screen.getByRole("button", { name: /user menu for/i }));
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
    await user.click(screen.getByRole("button", { name: /^more$/i }));
    await waitFor(() => {
      const dialog = screen.getByRole("dialog");
      expect(within(dialog).getByText("Workspace")).toBeInTheDocument();
    });
  });
});

/** hrefs of every link inside `container` carrying aria-current="page". */
function currentHrefsWithin(container: HTMLElement): string[] {
  return within(container)
    .getAllByRole("link")
    .filter((link) => link.getAttribute("aria-current") === "page")
    .map((link) => link.getAttribute("href") ?? "");
}

/** hrefs of every link inside `container` carrying the `active` highlight class. */
function activeHrefsWithin(container: HTMLElement): string[] {
  return within(container)
    .getAllByRole("link")
    .filter((link) => link.classList.contains("active"))
    .map((link) => link.getAttribute("href") ?? "");
}

function sidebar(): HTMLElement {
  return screen.getByRole("navigation", { name: /^primary$/i });
}

async function openMobileDrawer(): Promise<HTMLElement> {
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: /^more$/i }));
  return await screen.findByRole("dialog");
}

describe("Layout active nav resolution", () => {
  // RECEIPTS-833: /settings/ynab matched both YNAB (exact) and Settings
  // (prefix), so two nav entries claimed aria-current="page" at once.
  const cases: ReadonlyArray<[route: string, expectedHref: string]> = [
    ["/", "/"],
    ["/receipts", "/receipts"],
    ["/receipts/new", "/receipts"],
    ["/receipts/8f0c1d2e-3a4b-5c6d-7e8f-9a0b1c2d3e4f", "/receipts"],
    ["/settings", "/settings"],
    ["/settings/ynab", "/settings/ynab"],
    ["/ynab", "/ynab"],
    ["/reports", "/reports"],
    ["/accounts", "/accounts"],
    ["/item-templates", "/item-templates"],
    ["/security", "/security"],
    ["/api-keys", "/api-keys"],
    // React Router compiles route paths case-insensitively unless a route opts
    // into `caseSensitive`, and none of ours do — so these URLs render a real
    // page and must still highlight. NavLink used to case-fold for us; when the
    // resolution moved in-house that was briefly lost, highlighting nothing.
    ["/RECEIPTS", "/receipts"],
    ["/Settings/YNAB", "/settings/ynab"],
  ];

  it.each(cases)(
    "highlights exactly one sidebar item on %s",
    (route, expectedHref) => {
      renderLayout(undefined, route);
      expect(currentHrefsWithin(sidebar())).toEqual([expectedHref]);
    },
  );

  it.each(cases)(
    "highlights exactly one mobile drawer item on %s",
    async (route, expectedHref) => {
      renderLayout(undefined, route);
      const drawer = await openMobileDrawer();
      expect(currentHrefsWithin(drawer)).toEqual([expectedHref]);
      // Assert the visible highlight too, not just the semantics: the drawer
      // could lose its `active` class entirely while aria-current still passed.
      expect(activeHrefsWithin(drawer)).toEqual([expectedHref]);
    },
  );

  it("keeps the sidebar and the mobile drawer in agreement", async () => {
    renderLayout(undefined, "/settings/ynab");
    const desktop = currentHrefsWithin(sidebar());
    const drawer = await openMobileDrawer();
    expect(currentHrefsWithin(drawer)).toEqual(desktop);
  });

  it("does not mark Settings current on the YNAB settings route", () => {
    renderLayout(undefined, "/settings/ynab");
    const settings = within(sidebar())
      .getAllByRole("link")
      .find((link) => link.getAttribute("href") === "/settings");
    expect(settings).toBeDefined();
    expect(settings).not.toHaveAttribute("aria-current");
    expect(settings).not.toHaveClass("active");
  });

  it("marks Dashboard current only on the exact root route", () => {
    renderLayout(undefined, "/reports");
    // Match on the label, not on href="/" — the brand link is also href="/",
    // sits inside the Primary nav ahead of the items, and never receives
    // aria-current under any implementation, so selecting by href alone makes
    // this assertion unconditionally true.
    const dashboard = within(sidebar())
      .getAllByRole("link")
      .find((link) => link.textContent?.includes("Dashboard"));
    expect(dashboard).toBeDefined();
    expect(dashboard).not.toHaveAttribute("aria-current");
    expect(dashboard).not.toHaveClass("active");
  });

  it("highlights exactly one item on an admin-only route", () => {
    renderLayout({ user: adminUser }, "/admin/users");
    expect(currentHrefsWithin(sidebar())).toEqual(["/admin/users"]);
  });

  it("highlights nothing on a route with no nav entry", () => {
    renderLayout(undefined, "/change-password");
    expect(currentHrefsWithin(sidebar())).toEqual([]);
  });

  it("does not let a nav path prefix-match an unrelated sibling route", () => {
    // "/receipts" must not claim "/receipts-archive" — the match needs a
    // segment boundary, not a bare string prefix.
    renderLayout(undefined, "/receipts-archive");
    expect(currentHrefsWithin(sidebar())).toEqual([]);
  });

  it("applies the active class to the resolved item", () => {
    renderLayout(undefined, "/settings/ynab");
    const ynab = within(sidebar())
      .getAllByRole("link")
      .find((link) => link.getAttribute("href") === "/settings/ynab");
    expect(ynab).toHaveClass("active");
  });
});
