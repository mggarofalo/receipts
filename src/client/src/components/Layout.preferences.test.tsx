import { beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { AppearanceProvider } from "@/contexts/AppearanceContext";
import { ShortcutsProvider } from "@/contexts/ShortcutsContext";
import { AuthContext, type AuthContextValue } from "@/contexts/auth-context";
import Settings from "@/pages/Settings";
import { Layout } from "./Layout";
vi.mock("@/hooks/useSignalR", () => ({
  useSignalR: () => ({ connectionState: "connected" }),
}));
vi.mock("@/hooks/useBreadcrumbs", () => ({ useBreadcrumbs: () => [] }));
// Isolate search data loading, while retaining Layout's real keyboard listener.
vi.mock("@/components/CommandPalette", () => ({
  CommandPalette: ({ open }: { open: boolean }) =>
    open ? <div role="dialog" aria-label="Search palette" /> : null,
}));
const auth: AuthContextValue = {
  user: {
    userId: "reader",
    email: "reader@example.test",
    roles: ["User"],
    mustResetPassword: false,
  },
  isLoading: false,
  mustResetPassword: false,
  login: async () => {},
  logout: async () => {},
  changePassword: async () => {},
};
function mountSettings() {
  const router = createMemoryRouter(
    [
      {
        path: "/",
        element: <Layout />,
        children: [
          { path: "settings", element: <Settings /> },
          { path: "reports", element: <div>Reports destination</div> },
        ],
      },
    ],
    { initialEntries: ["/settings#preferences"] },
  );
  const view = render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <AppearanceProvider>
        <AuthContext.Provider value={auth}>
          <ShortcutsProvider>
            <RouterProvider router={router} />
          </ShortcutsProvider>
        </AuthContext.Provider>
      </AppearanceProvider>
    </QueryClientProvider>,
  );
  return { ...view, router };
}
function expectHints(visible: boolean) {
  for (const hint of ["G D", "G R", "G P"]) {
    if (visible) expect(screen.getByText(hint)).toBeInTheDocument();
    else expect(screen.queryByText(hint)).not.toBeInTheDocument();
  }
  const search = within(
    screen.getByRole("button", { name: "Search or jump to…" }),
  );
  if (visible) expect(search.getByText("⌘")).toBeInTheDocument();
  else expect(search.queryByText("⌘")).not.toBeInTheDocument();
}
beforeEach(() => {
  localStorage.clear();
  window.dispatchEvent(new StorageEvent("storage", { key: null }));
});
describe("Settings keyboard hint consumers", () => {
  it("changes mounted Layout immediately, persists on remount, and removes the inert week-start control", async () => {
    const user = userEvent.setup();
    const first = mountSettings();
    expect(screen.queryByText("Week starts on")).not.toBeInTheDocument();
    const toggle = screen.getByRole("switch", { name: "Keyboard hints" });
    expect(toggle).toBeChecked();
    expectHints(true);
    await user.click(toggle);
    expect(toggle).not.toBeChecked();
    expectHints(false);
    first.unmount();
    mountSettings();
    expect(
      screen.getByRole("switch", { name: "Keyboard hints" }),
    ).not.toBeChecked();
    expectHints(false);
    await user.click(screen.getByRole("switch", { name: "Keyboard hints" }));
    expectHints(true);
  });
  it("updates Settings and Layout together when another tab changes or clears preferences", () => {
    mountSettings();
    act(() => {
      localStorage.setItem(
        "receipts:preferences",
        '{"showKeyboardHints":false}',
      );
      window.dispatchEvent(
        new StorageEvent("storage", { key: "receipts:preferences" }),
      );
    });
    expect(
      screen.getByRole("switch", { name: "Keyboard hints" }),
    ).not.toBeChecked();
    expectHints(false);
    act(() => {
      localStorage.clear();
      window.dispatchEvent(new StorageEvent("storage", { key: null }));
    });
    expect(
      screen.getByRole("switch", { name: "Keyboard hints" }),
    ).toBeChecked();
    expectHints(true);
  });
  it("keeps search shortcuts and explicit shortcut help functional when hints are hidden", async () => {
    localStorage.setItem("receipts:preferences", '{"showKeyboardHints":false}');
    const user = userEvent.setup();
    const first = mountSettings();
    expectHints(false);
    fireEvent.keyDown(document, { key: "k", ctrlKey: true });
    expect(
      screen.getByRole("dialog", { name: "Search palette" }),
    ).toBeInTheDocument();
    first.unmount();
    mountSettings();
    await user.keyboard("?");
    const help = screen.getByRole("dialog", { name: "Keyboard Shortcuts" });
    expect(within(help).getByText("Ctrl+K")).toBeInTheDocument();
  });
});
