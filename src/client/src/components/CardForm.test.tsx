import "@/test/setup-combobox-polyfills";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { CardForm } from "./CardForm";

vi.mock("@/hooks/useFormShortcuts", () => ({
  useFormShortcuts: vi.fn(),
}));

const mockAccounts = [
  { id: "acct-1", name: "Checking", isActive: true },
  { id: "acct-2", name: "Savings", isActive: true },
];

vi.mock("@/hooks/useAccounts", () => ({
  useAllAccounts: vi.fn(() => ({
    data: mockAccounts,
    total: mockAccounts.length,
    isLoading: false,
  })),
}));

describe("CardForm", () => {
  const defaultProps = {
    mode: "create" as const,
    onSubmit: vi.fn(),
    onCancel: vi.fn(),
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("renders in create mode with empty fields and correct submit button text", () => {
    render(<CardForm {...defaultProps} />);

    expect(screen.getByLabelText(/^Card Code/)).toHaveValue("");
    expect(screen.getByLabelText(/^Name/)).toHaveValue("");
    expect(screen.getByRole("button", { name: /create card/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /cancel/i })).toBeInTheDocument();
  });

  it("renders in edit mode with pre-populated fields and correct submit button text", () => {
    render(
      <CardForm
        {...defaultProps}
        mode="edit"
        defaultValues={{ cardCode: "CARD-001", name: "Checking", isActive: false, accountId: "acct-1" }}
      />,
    );

    expect(screen.getByLabelText(/^Card Code/)).toHaveValue("CARD-001");
    expect(screen.getByLabelText(/^Name/)).toHaveValue("Checking");
    expect(screen.getByRole("checkbox")).not.toBeChecked();
    expect(screen.getByRole("button", { name: /update card/i })).toBeInTheDocument();
  });

  it("calls onSubmit with correct data when form is valid", async () => {
    const user = userEvent.setup();
    render(<CardForm {...defaultProps} />);

    await user.type(screen.getByLabelText(/^Card Code/), "CARD-002");
    await user.type(screen.getByLabelText(/^Name/), "Savings");
    await user.click(screen.getByRole("combobox", { name: /^account/i }));
    await user.click(await screen.findByText("Checking"));
    await user.click(screen.getByRole("button", { name: /create card/i }));

    await waitFor(() => {
      expect(defaultProps.onSubmit).toHaveBeenCalledWith(
        { cardCode: "CARD-002", name: "Savings", isActive: true, accountId: "acct-1" },
        expect.anything(),
      );
    });
  });

  it("renders parent Account dropdown with active accounts only (no '— None —')", async () => {
    const user = userEvent.setup();
    render(<CardForm {...defaultProps} />);

    const accountTrigger = screen.getByRole("combobox", { name: /^account/i });

    await user.click(accountTrigger);
    await waitFor(() => {
      expect(screen.getByText("Checking")).toBeInTheDocument();
      expect(screen.getByText("Savings")).toBeInTheDocument();
    });
    expect(screen.queryByText("— None —")).not.toBeInTheDocument();
  });

  it("pre-populates the parent Account in edit mode", () => {
    render(
      <CardForm
        {...defaultProps}
        mode="edit"
        defaultValues={{
          cardCode: "CARD-001",
          name: "Checking",
          isActive: true,
          accountId: "acct-1",
        }}
      />,
    );

    expect(
      screen.getByRole("combobox", { name: /^account/i }),
    ).toHaveTextContent("Checking");
  });

  it("submits the selected accountId when an Account is chosen", async () => {
    const user = userEvent.setup();
    render(<CardForm {...defaultProps} />);

    await user.type(screen.getByLabelText(/^Card Code/), "CARD-003");
    await user.type(screen.getByLabelText(/^Name/), "Brokerage");
    await user.click(screen.getByRole("combobox", { name: /^account/i }));
    await user.click(await screen.findByText("Savings"));
    await user.click(screen.getByRole("button", { name: /create card/i }));

    await waitFor(() => {
      expect(defaultProps.onSubmit).toHaveBeenCalledWith(
        expect.objectContaining({ accountId: "acct-2" }),
        expect.anything(),
      );
    });
  });

  it("blocks submit without selecting an Account", async () => {
    const user = userEvent.setup();
    render(<CardForm {...defaultProps} />);

    await user.type(screen.getByLabelText(/^Card Code/), "CARD-004");
    await user.type(screen.getByLabelText(/^Name/), "Orphan");
    await user.click(screen.getByRole("button", { name: /create card/i }));

    await waitFor(() => {
      expect(screen.getByText("Account is required")).toBeInTheDocument();
    });
    expect(defaultProps.onSubmit).not.toHaveBeenCalled();
  });

  it("shows validation errors when required fields are empty", async () => {
    const user = userEvent.setup();
    render(<CardForm {...defaultProps} />);

    await user.click(screen.getByRole("button", { name: /create card/i }));

    await waitFor(() => {
      expect(screen.getByText("Card code is required")).toBeInTheDocument();
      expect(screen.getByText("Name is required")).toBeInTheDocument();
      expect(screen.getByText("Account is required")).toBeInTheDocument();
    });
    expect(defaultProps.onSubmit).not.toHaveBeenCalled();
  });

  it("calls onCancel when cancel button is clicked", async () => {
    const user = userEvent.setup();
    render(<CardForm {...defaultProps} />);

    await user.click(screen.getByRole("button", { name: /cancel/i }));

    expect(defaultProps.onCancel).toHaveBeenCalledTimes(1);
  });

  it("disables submit button and shows spinner when isSubmitting is true", () => {
    render(<CardForm {...defaultProps} isSubmitting={true} />);

    const submitButton = screen.getByRole("button", { name: /saving/i });
    expect(submitButton).toBeDisabled();
  });

  it("renders the Active checkbox checked by default in create mode", () => {
    render(<CardForm {...defaultProps} />);

    expect(screen.getByRole("checkbox")).toBeChecked();
  });

  it("does not show delete button in create mode", () => {
    render(
      <CardForm
        {...defaultProps}
        isAdmin={true}
        onDelete={vi.fn()}
      />,
    );

    expect(screen.queryByRole("button", { name: /delete/i })).not.toBeInTheDocument();
  });

  it("does not show delete button in edit mode for non-admin users", () => {
    render(
      <CardForm
        {...defaultProps}
        mode="edit"
        defaultValues={{ cardCode: "CARD-001", name: "Checking", isActive: true, accountId: "acct-1" }}
        isAdmin={false}
        onDelete={vi.fn()}
      />,
    );

    expect(screen.queryByRole("button", { name: /delete/i })).not.toBeInTheDocument();
  });

  it("shows delete button in edit mode for admin users", () => {
    render(
      <CardForm
        {...defaultProps}
        mode="edit"
        defaultValues={{ cardCode: "CARD-001", name: "Checking", isActive: true, accountId: "acct-1" }}
        isAdmin={true}
        onDelete={vi.fn()}
      />,
    );

    expect(screen.getByRole("button", { name: /delete/i })).toBeInTheDocument();
  });

  it("shows confirmation dialog when delete button is clicked", async () => {
    const user = userEvent.setup();
    render(
      <CardForm
        {...defaultProps}
        mode="edit"
        defaultValues={{ cardCode: "CARD-001", name: "Checking", isActive: true, accountId: "acct-1" }}
        isAdmin={true}
        onDelete={vi.fn()}
      />,
    );

    await user.click(screen.getByRole("button", { name: /delete/i }));

    await waitFor(() => {
      expect(screen.getByText("Delete Card?")).toBeInTheDocument();
      expect(screen.getByText(/permanently delete/i)).toBeInTheDocument();
    });
  });

  it("calls onDelete when delete is confirmed", async () => {
    const onDelete = vi.fn();
    const user = userEvent.setup();
    render(
      <CardForm
        {...defaultProps}
        mode="edit"
        defaultValues={{ cardCode: "CARD-001", name: "Checking", isActive: true, accountId: "acct-1" }}
        isAdmin={true}
        onDelete={onDelete}
      />,
    );

    await user.click(screen.getByRole("button", { name: /delete/i }));

    await waitFor(() => {
      expect(screen.getByText("Delete Card?")).toBeInTheDocument();
    });

    // Click the "Delete" action in the confirmation dialog
    const confirmButtons = screen.getAllByRole("button", { name: /delete/i });
    const confirmButton = confirmButtons[confirmButtons.length - 1];
    await user.click(confirmButton);

    expect(onDelete).toHaveBeenCalledTimes(1);
  });

  it("does not show delete button when onDelete is not provided", () => {
    render(
      <CardForm
        {...defaultProps}
        mode="edit"
        defaultValues={{ cardCode: "CARD-001", name: "Checking", isActive: true, accountId: "acct-1" }}
        isAdmin={true}
      />,
    );

    expect(screen.queryByRole("button", { name: /delete/i })).not.toBeInTheDocument();
  });
});
