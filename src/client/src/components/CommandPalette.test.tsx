import { describe, it, expect, vi, beforeAll, beforeEach } from "vitest";
import { screen, within, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { CommandPalette } from "./CommandPalette";
import { renderWithQueryClient } from "@/test/test-utils";
import { mockQueryResult } from "@/test/mock-hooks";

// cmdk uses ResizeObserver + scrollIntoView, absent in jsdom.
beforeAll(() => {
  globalThis.ResizeObserver = class ResizeObserver {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
  Element.prototype.scrollIntoView = vi.fn();
});

const navigateMock = vi.fn();

vi.mock("react-router", async () => {
  const actual = await vi.importActual<typeof import("react-router")>(
    "react-router",
  );
  return {
    ...actual,
    useNavigate: () => navigateMock,
  };
});

vi.mock("@/hooks/usePermission", () => ({
  usePermission: vi.fn(() => ({ isAdmin: () => false })),
}));

vi.mock("@/hooks/useAccounts", () => ({
  useAccounts: vi.fn(() => mockQueryResult()),
}));
vi.mock("@/hooks/useCards", () => ({
  useCards: vi.fn(() => mockQueryResult()),
}));
vi.mock("@/hooks/useCategories", () => ({
  useCategories: vi.fn(() => mockQueryResult()),
}));
vi.mock("@/hooks/useSubcategories", () => ({
  useSubcategories: vi.fn(() => mockQueryResult()),
}));
vi.mock("@/hooks/useItemTemplates", () => ({
  useItemTemplates: vi.fn(() => mockQueryResult()),
}));
vi.mock("@/hooks/useReceipts", () => ({
  useReceipts: vi.fn(() => mockQueryResult()),
}));
vi.mock("@/hooks/useReceiptItems", () => ({
  useReceiptItems: vi.fn(() => mockQueryResult()),
}));
vi.mock("@/hooks/useUsers", () => ({
  useUsers: vi.fn(() => mockQueryResult()),
}));

const bulkPushMutate = vi.fn();
const backupExportMutate = vi.fn();
const purgeTrashMutateAsync = vi.fn(async () => {});
const fetchAllReceiptIdsMock = vi.fn(async () => ({ ids: ["r1", "r2"], total: 2 }));

vi.mock("@/hooks/useYnab", () => ({
  fetchAllReceiptIds: () => fetchAllReceiptIdsMock(),
  useBulkPushYnabTransactions: () => ({ mutate: bulkPushMutate, isPending: false }),
}));

vi.mock("@/hooks/useBackup", () => ({
  useBackupExport: () => ({ mutate: backupExportMutate, isPending: false }),
}));

vi.mock("@/hooks/useTrash", () => ({
  usePurgeTrash: () => ({ mutateAsync: purgeTrashMutateAsync, isPending: false }),
}));

beforeEach(async () => {
  navigateMock.mockClear();
  bulkPushMutate.mockClear();
  backupExportMutate.mockClear();
  purgeTrashMutateAsync.mockClear();
  fetchAllReceiptIdsMock.mockClear();
  fetchAllReceiptIdsMock.mockResolvedValue({ ids: ["r1", "r2"], total: 2 });
  localStorage.clear();
  const { usePermission } = await import("@/hooks/usePermission");
  vi.mocked(usePermission).mockReturnValue({
    roles: [],
    hasRole: () => false,
    isAdmin: () => false,
  });
});

describe("CommandPalette", () => {
  it("does not render when closed", () => {
    renderWithQueryClient(
      <CommandPalette open={false} onOpenChange={vi.fn()} />,
    );
    expect(
      screen.queryByPlaceholderText(/type a command or search/i),
    ).not.toBeInTheDocument();
  });

  it("renders all four default groups when opened with no query", () => {
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
    );
    expect(screen.getByText("Create")).toBeInTheDocument();
    expect(screen.getByText("Go to")).toBeInTheDocument();
    expect(screen.getByText("Reports")).toBeInTheDocument();
    expect(screen.getByText("Preferences")).toBeInTheDocument();

    expect(screen.getByText("New Receipt")).toBeInTheDocument();
    expect(screen.getByText("Go to Dashboard")).toBeInTheDocument();
    expect(
      screen.getByText('Open "Out of Balance" Report'),
    ).toBeInTheDocument();
    expect(screen.getByText("Sign Out")).toBeInTheDocument();
  });

  it("hides admin-only commands when user is not admin", () => {
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
    );
    expect(screen.queryByText("Go to User Management")).not.toBeInTheDocument();
    expect(screen.queryByText("Go to Audit Log")).not.toBeInTheDocument();
    expect(screen.queryByText("Go to Trash")).not.toBeInTheDocument();
    expect(screen.queryByText("New User")).not.toBeInTheDocument();
  });

  it("shows admin commands when user is admin", async () => {
    const { usePermission } = await import("@/hooks/usePermission");
    vi.mocked(usePermission).mockReturnValue({
      roles: ["Admin"],
      hasRole: () => true,
      isAdmin: () => true,
    });
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
    );
    expect(screen.getByText("Go to User Management")).toBeInTheDocument();
    expect(screen.getByText("Go to Trash")).toBeInTheDocument();
    expect(screen.getByText("New User")).toBeInTheDocument();
  });

  it("selecting a navigate command calls navigate and closes", async () => {
    const user = userEvent.setup();
    const onOpenChange = vi.fn();
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={onOpenChange} />,
    );
    await user.click(screen.getByText("Go to Receipts"));
    expect(navigateMock).toHaveBeenCalledWith("/receipts");
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it("does not render entity groups when query is empty", async () => {
    const { useAccounts } = await import("@/hooks/useAccounts");
    vi.mocked(useAccounts).mockReturnValue(
      mockQueryResult({
        data: [{ id: "a1", name: "Apple Card" }],
      }),
    );
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
    );
    expect(screen.queryByText("Apple Card")).not.toBeInTheDocument();
  });

  it("renders entity results when query is typed", async () => {
    const { useAccounts } = await import("@/hooks/useAccounts");
    vi.mocked(useAccounts).mockReturnValue(
      mockQueryResult({
        data: [
          { id: "a1", name: "Apple Card" },
          { id: "a2", name: "Chase" },
        ],
      }),
    );
    const user = userEvent.setup();
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
    );
    const input = screen.getByPlaceholderText(/type a command or search/i);
    await user.type(input, "apple");
    expect(screen.getByText("Apple Card")).toBeInTheDocument();
  });

  it("navigates to a receipt when a receipt item entity is selected", async () => {
    const { useReceiptItems } = await import("@/hooks/useReceiptItems");
    vi.mocked(useReceiptItems).mockReturnValue(
      mockQueryResult({
        data: [
          {
            id: "ri1",
            receiptId: "r99",
            description: "Organic bananas",
            receiptItemCode: "BAN",
            category: "Produce",
          },
        ],
      }),
    );
    const user = userEvent.setup();
    const onOpenChange = vi.fn();
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={onOpenChange} />,
    );
    const input = screen.getByPlaceholderText(/type a command or search/i);
    await user.type(input, "bananas");
    await user.click(screen.getByText("Organic bananas"));
    expect(navigateMock).toHaveBeenCalledWith("/receipts/r99");
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it("shows ⇧ N shortcut hint on the create command matching the current route", () => {
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
      { route: "/accounts" },
    );
    const item = screen.getByText("New Account").closest("[cmdk-item]");
    expect(item).not.toBeNull();
    expect(within(item as HTMLElement).getByText("⇧ N")).toBeInTheDocument();
    // Screen readers see spelled-out text instead of the glyph
    expect(within(item as HTMLElement).getByText("Shift+N")).toBeInTheDocument();
  });

  it("does not show ⇧ N hint on create commands that don't match current route", () => {
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
      { route: "/" },
    );
    const newAccount = screen.getByText("New Account").closest("[cmdk-item]");
    expect(
      within(newAccount as HTMLElement).queryByText("⇧ N"),
    ).not.toBeInTheDocument();
  });

  it("query state resets when the palette unmounts and remounts", async () => {
    // Layout mounts <CommandPalette> conditionally on `searchOpen`, so each
    // open is a fresh instance. Simulate that here with unmount + remount.
    const user = userEvent.setup();
    const onOpenChange = vi.fn();
    const { unmount } = renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={onOpenChange} />,
    );
    await user.type(
      screen.getByPlaceholderText(/type a command or search/i),
      "xyz",
    );
    await user.keyboard("{Escape}");
    expect(onOpenChange).toHaveBeenCalledWith(false);
    unmount();
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={onOpenChange} />,
    );
    expect(
      (
        screen.getByPlaceholderText(
          /type a command or search/i,
        ) as HTMLInputElement
      ).value,
    ).toBe("");
  });

  it("renders Pinned section at the top when commands are pinned and query is empty", () => {
    localStorage.setItem(
      "receipts:palette-pinned",
      JSON.stringify(["nav:receipts"]),
    );
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
    );
    const pinnedHeading = screen.getByText("Pinned");
    expect(pinnedHeading).toBeInTheDocument();
    const pinnedGroup = pinnedHeading.closest("[cmdk-group]") as HTMLElement;
    expect(within(pinnedGroup).getByText("Go to Receipts")).toBeInTheDocument();
  });

  it("removes a pinned command from its regular group while query is empty", () => {
    localStorage.setItem(
      "receipts:palette-pinned",
      JSON.stringify(["nav:receipts"]),
    );
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
    );
    const goToGroup = screen
      .getByText("Go to")
      .closest("[cmdk-group]") as HTMLElement;
    expect(within(goToGroup).queryByText("Go to Receipts")).not.toBeInTheDocument();
    // Only one "Go to Receipts" row rendered (in Pinned, not in Go to).
    expect(screen.getAllByText("Go to Receipts")).toHaveLength(1);
  });

  it("restores a pinned command to its regular group while the user is typing", async () => {
    localStorage.setItem(
      "receipts:palette-pinned",
      JSON.stringify(["nav:receipts"]),
    );
    const user = userEvent.setup();
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
    );
    await user.type(
      screen.getByPlaceholderText(/type a command or search/i),
      "receipts",
    );
    // With Pinned hidden and the pinned command restored to Go to, the row is
    // still reachable via typing.
    expect(screen.getByText("Go to Receipts")).toBeInTheDocument();
  });

  it("does not match regular-group commands when the user types a group name", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
    );
    // Sanity: "Go to Receipts" is visible in the default empty-query render.
    expect(screen.getByText("Go to Receipts")).toBeInTheDocument();
    await user.type(
      screen.getByPlaceholderText(/type a command or search/i),
      "preferences",
    );
    // Typing the group name "preferences" should not match any preference
    // command — keywords on those commands don't include the group name,
    // and the cmdk value must not leak the group id either.
    expect(screen.queryByText("Light Theme")).not.toBeInTheDocument();
    expect(screen.queryByText("Dark Theme")).not.toBeInTheDocument();
    expect(screen.queryByText("Sign Out")).not.toBeInTheDocument();
  });

  it("renders Recent section below Pinned when recent history exists and query is empty", () => {
    localStorage.setItem(
      "receipts:palette-pinned",
      JSON.stringify(["nav:receipts"]),
    );
    localStorage.setItem(
      "receipts:palette-recent",
      JSON.stringify(["create:card"]),
    );
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
    );
    const pinnedHeading = screen.getByText("Pinned");
    const recentHeading = screen.getByText("Recent");
    expect(pinnedHeading).toBeInTheDocument();
    expect(recentHeading).toBeInTheDocument();
    expect(
      pinnedHeading.compareDocumentPosition(recentHeading) &
        Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
  });

  it("shows a command only in Pinned when it is both pinned and recent", () => {
    localStorage.setItem(
      "receipts:palette-pinned",
      JSON.stringify(["nav:receipts"]),
    );
    localStorage.setItem(
      "receipts:palette-recent",
      JSON.stringify(["nav:receipts", "create:card"]),
    );
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
    );
    const recentGroup = screen
      .getByText("Recent")
      .closest("[cmdk-group]") as HTMLElement;
    expect(within(recentGroup).queryByText("Go to Receipts")).not.toBeInTheDocument();
    expect(within(recentGroup).getByText("New Card")).toBeInTheDocument();
  });

  it("hides Pinned and Recent sections while the user is typing", async () => {
    localStorage.setItem(
      "receipts:palette-pinned",
      JSON.stringify(["nav:receipts"]),
    );
    localStorage.setItem(
      "receipts:palette-recent",
      JSON.stringify(["create:card"]),
    );
    const user = userEvent.setup();
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
    );
    expect(screen.getByText("Pinned")).toBeInTheDocument();
    await user.type(
      screen.getByPlaceholderText(/type a command or search/i),
      "dash",
    );
    expect(screen.queryByText("Pinned")).not.toBeInTheDocument();
    expect(screen.queryByText("Recent")).not.toBeInTheDocument();
  });

  it("selecting a command adds it to Recent in localStorage", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
    );
    await user.click(screen.getByText("Go to Receipts"));
    const recent = JSON.parse(
      localStorage.getItem("receipts:palette-recent") ?? "[]",
    );
    expect(recent[0]).toBe("nav:receipts");
  });

  it("clicking the pin button adds the command to Pinned without selecting it", async () => {
    const user = userEvent.setup();
    const onOpenChange = vi.fn();
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={onOpenChange} />,
    );
    const row = screen
      .getByText("Go to Receipts")
      .closest("[cmdk-item]") as HTMLElement;
    const pinButton = within(row).getByRole("button", {
      name: /pin Go to Receipts/i,
    });
    await user.click(pinButton);
    expect(screen.getByText("Pinned")).toBeInTheDocument();
    expect(navigateMock).not.toHaveBeenCalled();
    expect(onOpenChange).not.toHaveBeenCalled();
    const pinned = JSON.parse(
      localStorage.getItem("receipts:palette-pinned") ?? "[]",
    );
    expect(pinned).toContain("nav:receipts");
  });

  it("clicking the pin button on a pinned command unpins it", async () => {
    localStorage.setItem(
      "receipts:palette-pinned",
      JSON.stringify(["nav:receipts"]),
    );
    const user = userEvent.setup();
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
    );
    expect(screen.getByText("Pinned")).toBeInTheDocument();
    const pinnedGroup = screen
      .getByText("Pinned")
      .closest("[cmdk-group]") as HTMLElement;
    const unpinButton = within(pinnedGroup).getByRole("button", {
      name: /unpin Go to Receipts/i,
    });
    await user.click(unpinButton);
    expect(screen.queryByText("Pinned")).not.toBeInTheDocument();
    const pinned = JSON.parse(
      localStorage.getItem("receipts:palette-pinned") ?? "[]",
    );
    expect(pinned).toEqual([]);
  });

  it("does not render admin-only commands from localStorage when the user is not admin", () => {
    localStorage.setItem(
      "receipts:palette-pinned",
      JSON.stringify(["nav:audit", "nav:receipts"]),
    );
    localStorage.setItem(
      "receipts:palette-recent",
      JSON.stringify(["create:user"]),
    );
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
    );
    const pinnedGroup = screen
      .getByText("Pinned")
      .closest("[cmdk-group]") as HTMLElement;
    expect(within(pinnedGroup).queryByText("Go to Audit Log")).not.toBeInTheDocument();
    expect(within(pinnedGroup).getByText("Go to Receipts")).toBeInTheDocument();
    expect(screen.queryByText("Recent")).not.toBeInTheDocument();
  });

  it("silently drops unknown command ids from localStorage", () => {
    localStorage.setItem(
      "receipts:palette-pinned",
      JSON.stringify(["does-not-exist", "nav:receipts"]),
    );
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
    );
    const pinnedGroup = screen
      .getByText("Pinned")
      .closest("[cmdk-group]") as HTMLElement;
    expect(within(pinnedGroup).getByText("Go to Receipts")).toBeInTheDocument();
  });

  it("renders the Actions group with Sync YNAB Now for every user", () => {
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
    );
    expect(screen.getByText("Actions")).toBeInTheDocument();
    expect(screen.getByText("Sync YNAB Now")).toBeInTheDocument();
  });

  it("hides admin-only action commands when user is not admin", () => {
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
    );
    expect(screen.queryByText("Export Backup")).not.toBeInTheDocument();
    expect(screen.queryByText("Empty Trash")).not.toBeInTheDocument();
  });

  it("shows admin-only action commands for admins", async () => {
    const { usePermission } = await import("@/hooks/usePermission");
    vi.mocked(usePermission).mockReturnValue({
      roles: ["Admin"],
      hasRole: () => true,
      isAdmin: () => true,
    });
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
    );
    expect(screen.getByText("Export Backup")).toBeInTheDocument();
    expect(screen.getByText("Empty Trash")).toBeInTheDocument();
  });

  it("Sync YNAB Now fetches receipt IDs, fires bulk push, and closes the palette", async () => {
    const user = userEvent.setup();
    const onOpenChange = vi.fn();
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={onOpenChange} />,
    );
    await user.click(screen.getByText("Sync YNAB Now"));
    expect(onOpenChange).toHaveBeenCalledWith(false);
    await waitFor(() => {
      expect(fetchAllReceiptIdsMock).toHaveBeenCalled();
    });
    await waitFor(() => {
      expect(bulkPushMutate).toHaveBeenCalledWith(["r1", "r2"]);
    });
  });

  it("Sync YNAB Now skips the push when there are no receipts", async () => {
    fetchAllReceiptIdsMock.mockResolvedValueOnce({ ids: [], total: 0 });
    const user = userEvent.setup();
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
    );
    await user.click(screen.getByText("Sync YNAB Now"));
    await waitFor(() => {
      expect(fetchAllReceiptIdsMock).toHaveBeenCalled();
    });
    expect(bulkPushMutate).not.toHaveBeenCalled();
  });

  it("Export Backup fires the export mutation and closes the palette", async () => {
    const { usePermission } = await import("@/hooks/usePermission");
    vi.mocked(usePermission).mockReturnValue({
      roles: ["Admin"],
      hasRole: () => true,
      isAdmin: () => true,
    });
    const user = userEvent.setup();
    const onOpenChange = vi.fn();
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={onOpenChange} />,
    );
    await user.click(screen.getByText("Export Backup"));
    expect(backupExportMutate).toHaveBeenCalled();
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it("Empty Trash opens a confirmation dialog without closing the palette", async () => {
    const { usePermission } = await import("@/hooks/usePermission");
    vi.mocked(usePermission).mockReturnValue({
      roles: ["Admin"],
      hasRole: () => true,
      isAdmin: () => true,
    });
    const user = userEvent.setup();
    const onOpenChange = vi.fn();
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={onOpenChange} />,
    );
    await user.click(screen.getByText("Empty Trash"));
    expect(
      await screen.findByRole("alertdialog", { name: /empty trash/i }),
    ).toBeInTheDocument();
    expect(onOpenChange).not.toHaveBeenCalled();
    expect(purgeTrashMutateAsync).not.toHaveBeenCalled();
  });

  it("confirming Empty Trash fires purge and closes the palette", async () => {
    const { usePermission } = await import("@/hooks/usePermission");
    vi.mocked(usePermission).mockReturnValue({
      roles: ["Admin"],
      hasRole: () => true,
      isAdmin: () => true,
    });
    const user = userEvent.setup();
    const onOpenChange = vi.fn();
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={onOpenChange} />,
    );
    await user.click(screen.getByText("Empty Trash"));
    const dialog = await screen.findByRole("alertdialog");
    const confirm = within(dialog).getByRole("button", {
      name: /empty trash/i,
    });
    await user.click(confirm);
    await waitFor(() => {
      expect(purgeTrashMutateAsync).toHaveBeenCalled();
    });
    await waitFor(() => {
      expect(onOpenChange).toHaveBeenCalledWith(false);
    });
  });

  it("Empty Trash keeps the palette open when the purge mutation fails", async () => {
    purgeTrashMutateAsync.mockRejectedValueOnce(new Error("network"));
    const { usePermission } = await import("@/hooks/usePermission");
    vi.mocked(usePermission).mockReturnValue({
      roles: ["Admin"],
      hasRole: () => true,
      isAdmin: () => true,
    });
    const user = userEvent.setup();
    const onOpenChange = vi.fn();
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={onOpenChange} />,
    );
    await user.click(screen.getByText("Empty Trash"));
    const dialog = await screen.findByRole("alertdialog");
    const confirm = within(dialog).getByRole("button", {
      name: /empty trash/i,
    });
    await user.click(confirm);
    await waitFor(() => {
      expect(purgeTrashMutateAsync).toHaveBeenCalled();
    });
    // Palette must stay open so the user can retry.
    expect(onOpenChange).not.toHaveBeenCalled();
  });

  it("cancelling Empty Trash closes the dialog without calling purge", async () => {
    const { usePermission } = await import("@/hooks/usePermission");
    vi.mocked(usePermission).mockReturnValue({
      roles: ["Admin"],
      hasRole: () => true,
      isAdmin: () => true,
    });
    const user = userEvent.setup();
    const onOpenChange = vi.fn();
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={onOpenChange} />,
    );
    await user.click(screen.getByText("Empty Trash"));
    const dialog = await screen.findByRole("alertdialog");
    const cancel = within(dialog).getByRole("button", { name: /cancel/i });
    await user.click(cancel);
    expect(purgeTrashMutateAsync).not.toHaveBeenCalled();
    expect(onOpenChange).not.toHaveBeenCalled();
  });

  it("collapses large entity groups to a Show N more trailer", async () => {
    const { useCategories } = await import("@/hooks/useCategories");
    const many = Array.from({ length: 12 }, (_, i) => ({
      id: `c${i}`,
      name: `Cat ${i}`,
      description: null,
    }));
    vi.mocked(useCategories).mockReturnValue(mockQueryResult({ data: many }));
    const user = userEvent.setup();
    renderWithQueryClient(
      <CommandPalette open={true} onOpenChange={vi.fn()} />,
    );
    await user.type(
      screen.getByPlaceholderText(/type a command or search/i),
      "cat",
    );
    expect(screen.getByText("Cat 0")).toBeInTheDocument();
    expect(screen.queryByText("Cat 9")).not.toBeInTheDocument();
    const moreRow = screen.getByText(/Show 4 more categories/i);
    await user.click(moreRow);
    expect(screen.getByText("Cat 11")).toBeInTheDocument();
  });
});
