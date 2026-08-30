import { describe, it, expect, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import "@/test/setup-combobox-polyfills";
import { ReceiptTransactionForm } from "./ReceiptTransactionForm";

vi.mock("@/hooks/useFormShortcuts", () => ({
  useFormShortcuts: vi.fn(),
}));

vi.mock("@/hooks/useAccounts", () => ({
  useAllAccounts: vi.fn(() => ({
    data: [
      { id: "acct-1", name: "Checking", isActive: true },
      { id: "acct-2", name: "Savings", isActive: true },
    ],
    isLoading: false,
  })),
}));

vi.mock("@/hooks/useCards", () => ({
  useAllCards: vi.fn(() => ({
    data: [
      { id: "card-1", name: "Visa 4321", cardCode: "V4321", isActive: true, accountId: "acct-1" },
      { id: "card-2", name: "Amex 7777", cardCode: "A7777", isActive: true, accountId: null },
    ],
    isLoading: false,
  })),
}));

describe("ReceiptTransactionForm", () => {
  it("renders the Card, Account, Amount, and Date fields", () => {
    renderWithProviders(
      <ReceiptTransactionForm
        mode="create"
        onSubmit={vi.fn()}
        onCancel={vi.fn()}
      />,
    );
    expect(screen.getByText(/^Card$/)).toBeInTheDocument();
    expect(screen.getByText(/^Account$/)).toBeInTheDocument();
    expect(screen.getByLabelText(/amount/i)).toBeInTheDocument();
    expect(screen.getByText(/^Date$/)).toBeInTheDocument();
  });

  it("blocks submit when Card is empty", async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn();
    renderWithProviders(
      <ReceiptTransactionForm
        mode="create"
        onSubmit={onSubmit}
        onCancel={vi.fn()}
        defaultValues={{ accountId: "acct-1", amount: 42, date: "2024-01-15" }}
      />,
    );

    await user.click(screen.getByRole("button", { name: /add transaction/i }));
    expect(onSubmit).not.toHaveBeenCalled();
    expect(await screen.findByText("Card is required")).toBeInTheDocument();
  });

  it("auto-fills Account when a Card with a parent account is selected", async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn();
    renderWithProviders(
      <ReceiptTransactionForm
        mode="create"
        onSubmit={onSubmit}
        onCancel={vi.fn()}
        defaultValues={{ amount: 42, date: "2024-01-15" }}
      />,
    );

    const [cardCombobox] = screen.getAllByRole("combobox");
    await user.click(cardCombobox);
    await user.click(await screen.findByText("Visa 4321"));

    await user.click(screen.getByRole("button", { name: /add transaction/i }));

    expect(onSubmit.mock.calls[0][0]).toEqual(
      expect.objectContaining({
        cardId: "card-1",
        accountId: "acct-1",
        amount: 42,
        date: "2024-01-15",
      }),
    );
  });

  it("allows saving in edit mode when the Card field is empty (legacy rows)", async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn();
    renderWithProviders(
      <ReceiptTransactionForm
        mode="edit"
        onSubmit={onSubmit}
        onCancel={vi.fn()}
        defaultValues={{ cardId: "", accountId: "acct-1", amount: 42, date: "2024-01-15" }}
      />,
    );

    await user.click(screen.getByRole("button", { name: /update transaction/i }));

    expect(onSubmit.mock.calls[0][0]).toEqual(
      expect.objectContaining({
        cardId: "",
        accountId: "acct-1",
      }),
    );
  });

  it("leaves Account alone when the selected Card has no parent account", async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn();
    renderWithProviders(
      <ReceiptTransactionForm
        mode="create"
        onSubmit={onSubmit}
        onCancel={vi.fn()}
        defaultValues={{ accountId: "acct-2", amount: 42, date: "2024-01-15" }}
      />,
    );

    const [cardCombobox] = screen.getAllByRole("combobox");
    await user.click(cardCombobox);
    await user.click(await screen.findByText("Amex 7777"));

    await user.click(screen.getByRole("button", { name: /add transaction/i }));

    expect(onSubmit.mock.calls[0][0]).toEqual(
      expect.objectContaining({
        cardId: "card-2",
        accountId: "acct-2",
      }),
    );
  });

  it("routes server errors through FormMessage so they have role=alert and are field-associated", async () => {
    renderWithProviders(
      <ReceiptTransactionForm
        mode="create"
        onSubmit={vi.fn()}
        onCancel={vi.fn()}
        serverErrors={{ cardId: "Server-side card error" }}
      />,
    );

    // The error element must have role="alert" (set by FormMessage per RECEIPTS-686)
    // and carry data-slot="form-message", meaning it went through FormMessage
    // (not a bare <p className="text-destructive">).
    await waitFor(() => {
      const errorEl = screen.getByText("Server-side card error");
      expect(errorEl).toHaveAttribute("role", "alert");
      expect(errorEl).toHaveAttribute("data-slot", "form-message");
    });
  });
});
