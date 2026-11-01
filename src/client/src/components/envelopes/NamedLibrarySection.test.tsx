import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import {
  NamedLibrarySection,
  type NamedLibraryItem,
} from "./NamedLibrarySection";

const items: NamedLibraryItem[] = [
  { id: "env-groceries", name: "Groceries", isActive: true },
  { id: "env-travel", name: "Travel", isActive: false },
];

function renderSection(
  overrides: Partial<Parameters<typeof NamedLibrarySection>[0]> = {},
) {
  const props = {
    title: "Envelopes",
    description: "Where money comes from",
    noun: "envelope",
    items,
    isLoading: false,
    isError: false,
    busy: false,
    onCreate: vi.fn().mockResolvedValue(undefined),
    onUpdate: vi.fn().mockResolvedValue(undefined),
    onDelete: vi.fn().mockResolvedValue(undefined),
    ...overrides,
  };
  render(<NamedLibrarySection {...props} />);
  return props;
}

function row(name: string) {
  return screen.getByText(name).closest("li") as HTMLElement;
}

describe("NamedLibrarySection", () => {
  it("lists items and marks archived ones", () => {
    renderSection();

    expect(screen.getByText("Envelopes")).toBeInTheDocument();
    expect(within(row("Groceries")).queryByText("Archived")).toBeNull();
    expect(within(row("Travel")).getByText("Archived")).toBeInTheDocument();
    expect(
      within(row("Groceries")).getByRole("button", { name: "Archive" }),
    ).toBeInTheDocument();
    expect(
      within(row("Travel")).getByRole("button", { name: "Restore" }),
    ).toBeInTheDocument();
  });

  it.each([
    [{ isLoading: true }, "Loading…"],
    [{ isError: true }, "The envelope list is unavailable."],
    [{ items: [] }, "No envelopes yet."],
  ])("shows the %o state", (overrides, text) => {
    renderSection(overrides);

    expect(screen.getByText(text)).toBeInTheDocument();
    expect(screen.queryByText("Groceries")).not.toBeInTheDocument();
  });

  it("creates a trimmed name and clears the field", async () => {
    const user = userEvent.setup();
    const { onCreate } = renderSection();
    const input = screen.getByLabelText("New envelope");

    await user.type(input, "  Dining  ");
    await user.click(screen.getByRole("button", { name: "Add" }));

    await waitFor(() => expect(onCreate).toHaveBeenCalledWith("Dining"));
    await waitFor(() => expect(input).toHaveValue(""));
  });

  it("keeps the typed name when creation is refused", async () => {
    const user = userEvent.setup();
    const { onCreate } = renderSection({
      onCreate: vi.fn().mockRejectedValue(new Error("Duplicate name")),
    });
    const input = screen.getByLabelText("New envelope");

    await user.type(input, "Groceries");
    await user.click(screen.getByRole("button", { name: "Add" }));

    await waitFor(() => expect(onCreate).toHaveBeenCalledWith("Groceries"));
    expect(input).toHaveValue("Groceries");
  });

  it("keeps the rename form open when the rename is refused", async () => {
    const user = userEvent.setup();
    const { onUpdate } = renderSection({
      onUpdate: vi.fn().mockRejectedValue(new Error("Duplicate name")),
    });

    await user.click(
      within(row("Groceries")).getByRole("button", { name: "Rename" }),
    );
    const input = screen.getByLabelText("Rename Groceries");
    await user.clear(input);
    await user.type(input, "Travel");
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(onUpdate).toHaveBeenCalled());
    expect(screen.getByLabelText("Rename Groceries")).toHaveValue("Travel");
  });

  it("rejects a blank name without calling create", async () => {
    const user = userEvent.setup();
    const { onCreate } = renderSection();

    await user.type(screen.getByLabelText("New envelope"), "   ");
    await user.click(screen.getByRole("button", { name: "Add" }));

    expect(await screen.findByText("Name is required")).toBeInTheDocument();
    expect(onCreate).not.toHaveBeenCalled();
  });

  it("renames an item, keeping its archive state", async () => {
    const user = userEvent.setup();
    const { onUpdate } = renderSection();

    await user.click(
      within(row("Travel")).getByRole("button", { name: "Rename" }),
    );
    const input = screen.getByLabelText("Rename Travel");
    expect(input).toHaveValue("Travel");
    await user.clear(input);
    await user.type(input, "Vacations");
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(onUpdate).toHaveBeenCalledWith({
        id: "env-travel",
        name: "Vacations",
        isActive: false,
      }),
    );
    await waitFor(() =>
      expect(screen.queryByLabelText("Rename Travel")).not.toBeInTheDocument(),
    );
  });

  it("cancels a rename without saving", async () => {
    const user = userEvent.setup();
    const { onUpdate } = renderSection();

    await user.click(
      within(row("Groceries")).getByRole("button", { name: "Rename" }),
    );
    await user.click(screen.getByRole("button", { name: "Cancel" }));

    expect(screen.queryByLabelText("Rename Groceries")).not.toBeInTheDocument();
    expect(onUpdate).not.toHaveBeenCalled();
  });

  it("archives an active item and restores an archived one", async () => {
    const user = userEvent.setup();
    const { onUpdate } = renderSection();

    await user.click(
      within(row("Groceries")).getByRole("button", { name: "Archive" }),
    );
    await user.click(
      within(row("Travel")).getByRole("button", { name: "Restore" }),
    );

    expect(onUpdate).toHaveBeenNthCalledWith(1, {
      id: "env-groceries",
      name: "Groceries",
      isActive: false,
    });
    expect(onUpdate).toHaveBeenNthCalledWith(2, {
      id: "env-travel",
      name: "Travel",
      isActive: true,
    });
  });

  it("deletes by id once the deletion is confirmed", async () => {
    const user = userEvent.setup();
    const { onDelete } = renderSection();

    await user.click(screen.getByRole("button", { name: "Delete Groceries" }));
    const dialog = await screen.findByRole("alertdialog", {
      name: "Delete envelope?",
    });
    expect(dialog).toHaveTextContent("Groceries");
    expect(onDelete).not.toHaveBeenCalled();
    await user.click(within(dialog).getByRole("button", { name: "Delete" }));

    expect(onDelete).toHaveBeenCalledExactlyOnceWith("env-groceries");
    await waitFor(() =>
      expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument(),
    );
  });

  it("deletes nothing when the confirmation is cancelled", async () => {
    const user = userEvent.setup();
    const { onDelete } = renderSection();

    await user.click(screen.getByRole("button", { name: "Delete Travel" }));
    const dialog = await screen.findByRole("alertdialog", {
      name: "Delete envelope?",
    });
    await user.click(within(dialog).getByRole("button", { name: "Cancel" }));

    await waitFor(() =>
      expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument(),
    );
    expect(onDelete).not.toHaveBeenCalled();
  });

  it("shows a detail line under each name when given one", () => {
    renderSection({ detail: (item) => `Detail for ${item.id}` });

    expect(
      within(row("Groceries")).getByText("Detail for env-groceries"),
    ).toBeInTheDocument();
    expect(
      within(row("Travel")).getByText("Detail for env-travel"),
    ).toBeInTheDocument();
  });

  it("disables every action while a write is pending", () => {
    renderSection({ busy: true });

    expect(screen.getByLabelText("New envelope")).toBeDisabled();
    expect(screen.getByRole("button", { name: "Add" })).toBeDisabled();
    for (const button of within(row("Groceries")).getAllByRole("button"))
      expect(button).toBeDisabled();
  });
});
