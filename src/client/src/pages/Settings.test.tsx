import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import Settings from "./Settings";
import { usePermission } from "@/hooks/usePermission";

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

vi.mock("@/hooks/usePermission", () => ({
  usePermission: vi.fn(() => ({
    isAdmin: () => false,
    roles: ["User"],
    hasRole: () => false,
  })),
}));

const navigateMock = vi.fn();
vi.mock("react-router", async (importOriginal) => {
  const actual = await importOriginal<typeof import("react-router")>();
  return {
    ...actual,
    useNavigate: vi.fn(() => navigateMock),
  };
});

describe("Settings", () => {
  beforeEach(() => {
    vi.mocked(usePermission).mockReturnValue({
      isAdmin: () => false,
      roles: ["User"],
      hasRole: () => false,
    });
    navigateMock.mockClear();
    window.localStorage.clear();
  });

  it("renders the page heading", () => {
    renderWithProviders(<Settings />);
    expect(
      screen.getByRole("heading", { name: /^settings$/i }),
    ).toBeInTheDocument();
  });

  it.each([false, true])(
    "shows only working Settings tabs for admin=%s",
    (admin) => {
      vi.mocked(usePermission).mockReturnValue({
        isAdmin: () => admin,
        roles: admin ? ["Admin"] : ["User"],
        hasRole: () => admin,
      });
      renderWithProviders(<Settings />);
      expect(screen.getAllByRole("tab").map((tab) => tab.textContent)).toEqual([
        "Appearance",
        "Preferences",
        "Export",
      ]);
      expect(
        screen.queryByText(
          /Settings live here|Configure your workspace|Paper intensity and motion/i,
        ),
      ).not.toBeInTheDocument();
    },
  );

  it.each(["data", "ynab"])(
    "opens a visible Appearance panel for retired #%s links",
    (hash) => {
      renderWithProviders(<Settings />, { route: `/settings#${hash}` });
      expect(screen.getByRole("tab", { name: "Appearance" })).toHaveAttribute(
        "aria-selected",
        "true",
      );
      expect(
        screen.getByRole("tabpanel", { name: "Appearance" }),
      ).toBeVisible();
      expect(screen.getByRole("button", { name: "Graphite" })).toBeVisible();
    },
  );

  it("defaults to the Appearance tab when no hash is present", () => {
    renderWithProviders(<Settings />, { route: "/settings" });
    const tab = screen.getByRole("tab", { name: /appearance/i });
    expect(tab).toHaveAttribute("aria-selected", "true");
  });

  it("lands on the Preferences tab when the URL hash is #preferences", () => {
    renderWithProviders(<Settings />, { route: "/settings#preferences" });
    const tab = screen.getByRole("tab", { name: /preferences/i });
    expect(tab).toHaveAttribute("aria-selected", "true");
  });

  it("navigates to the hash-tab URL when switching tabs", async () => {
    const user = userEvent.setup();
    renderWithProviders(<Settings />);
    await user.click(screen.getByRole("tab", { name: /preferences/i }));
    expect(navigateMock).toHaveBeenCalledWith("/settings#preferences", {
      replace: true,
    });
  });

  it("falls back to Appearance when the URL hash is an unknown tab", () => {
    renderWithProviders(<Settings />, { route: "/settings#nope" });
    const tab = screen.getByRole("tab", { name: /appearance/i });
    expect(tab).toHaveAttribute("aria-selected", "true");
  });
});

describe("Settings export destinations", () => {
  beforeEach(() => {
    vi.mocked(usePermission).mockReturnValue({
      isAdmin: () => false,
      roles: ["User"],
      hasRole: () => false,
    });
    localStorage.clear();
  });

  it("opens #export with direct links to each working CSV report and API credentials", () => {
    renderWithProviders(<Settings />, { route: "/settings#export" });
    expect(screen.getByRole("tab", { name: "Export" })).toHaveAttribute(
      "aria-selected",
      "true",
    );
    const panel = within(screen.getByRole("tabpanel", { name: "Export" }));
    for (const [label, slug] of [
      ["Spending by Location", "spending-by-location"],
      [
        "Spending by Normalized Description",
        "spending-by-normalized-description",
      ],
      ["Out of Balance", "out-of-balance"],
      ["Duplicate Detection", "duplicate-detection"],
      ["Uncategorized Items", "uncategorized-items"],
    ]) {
      expect(panel.getByRole("link", { name: label })).toHaveAttribute(
        "href",
        `/reports?report=${slug}`,
      );
    }
    expect(panel.getByRole("link", { name: "API Keys" })).toHaveAttribute(
      "href",
      "/api-keys",
    );
    expect(panel.getByText(/choose.*filters.*Export CSV/i)).toBeInTheDocument();
    expect(
      panel.queryByText(/coming later|ZIP export/i),
    ).not.toBeInTheDocument();
    expect(
      panel.queryByRole("link", { name: /backup/i }),
    ).not.toBeInTheDocument();
    expect(panel.getAllByRole("link")).toHaveLength(6);
  });

  it("offers the portable backup destination only to administrators", () => {
    vi.mocked(usePermission).mockReturnValue({
      isAdmin: () => true,
      roles: ["Admin"],
      hasRole: () => true,
    });
    renderWithProviders(<Settings />, { route: "/settings#export" });
    const panel = within(screen.getByRole("tabpanel", { name: "Export" }));
    expect(panel.getByRole("link", { name: /backup/i })).toHaveAttribute(
      "href",
      "/admin/backup",
    );
    expect(
      panel.getByText(/SQLite backup.*application data/i),
    ).toBeInTheDocument();
    expect(panel.getByRole("link", { name: "API Keys" })).toHaveAttribute(
      "href",
      "/api-keys",
    );
    expect(panel.getAllByRole("link")).toHaveLength(7);
  });
});
