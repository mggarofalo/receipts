import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import Settings from "./Settings";

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
    navigateMock.mockClear();
    window.localStorage.clear();
  });

  it("renders the page heading", () => {
    renderWithProviders(<Settings />);
    expect(
      screen.getByRole("heading", { name: /^settings$/i }),
    ).toBeInTheDocument();
  });

  it("renders the tab list with at least Appearance, Preferences, Export, YNAB", () => {
    renderWithProviders(<Settings />);
    expect(
      screen.getByRole("tab", { name: /appearance/i }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("tab", { name: /preferences/i }),
    ).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: /export/i })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: /^ynab$/i })).toBeInTheDocument();
  });

  it("hides the admin Data & backup tab for non-admin users", () => {
    renderWithProviders(<Settings />);
    expect(
      screen.queryByRole("tab", { name: /data & backup/i }),
    ).not.toBeInTheDocument();
  });

  it("shows the Data & backup tab for admin users", async () => {
    const { usePermission } = await import("@/hooks/usePermission");
    vi.mocked(usePermission).mockReturnValue({
      isAdmin: () => true,
      roles: ["Admin"],
      hasRole: () => true,
    });
    renderWithProviders(<Settings />);
    expect(
      screen.getByRole("tab", { name: /data & backup/i }),
    ).toBeInTheDocument();
  });

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
