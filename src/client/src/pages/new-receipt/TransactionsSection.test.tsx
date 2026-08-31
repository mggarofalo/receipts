import { screen, act, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import { mockQueryResult } from "@/test/mock-hooks";
import "@/test/setup-combobox-polyfills";
import { TransactionsSection } from "./TransactionsSection";

vi.mock("@/hooks/useFormShortcuts", () => ({
  useFormShortcuts: vi.fn(),
}));

vi.mock("@/hooks/useAccounts", () => ({
  useAllAccounts: vi.fn(() =>
    mockQueryResult({
      data: [
        { id: "acct-1", name: "Checking", isActive: true },
        { id: "acct-2", name: "Credit Card", isActive: true },
      ],
      total: 2,
      isLoading: false,
      isSuccess: true,
    }),
  ),
}));

vi.mock("@/hooks/useCards", () => ({
  useAllCards: vi.fn(() =>
    mockQueryResult({
      data: [
        { id: "card-1", name: "Visa 4321", cardCode: "V4321", isActive: true, accountId: "acct-1" },
        { id: "card-2", name: "Amex 7777", cardCode: "A7777", isActive: true, accountId: null },
      ],
      total: 2,
      isLoading: false,
      isSuccess: true,
    }),
  ),
}));

describe("TransactionsSection", () => {
  const defaultProps = {
    transactions: [] as { id: string; cardId: string; accountId: string; amount: number; date: string }[],
    defaultDate: "2024-01-15",
    onChange: vi.fn(),
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("renders the card title", () => {
    renderWithProviders(<TransactionsSection {...defaultProps} />);
    expect(screen.getByText("Transactions")).toBeInTheDocument();
  });

  it("renders the form fields", () => {
    renderWithProviders(<TransactionsSection {...defaultProps} />);
    expect(screen.getByLabelText(/amount/i)).toBeInTheDocument();
    expect(screen.getByText(/^date$/i)).toBeInTheDocument();
  });

  it("renders Add button", () => {
    renderWithProviders(<TransactionsSection {...defaultProps} />);
    expect(
      screen.getByRole("button", { name: /add/i }),
    ).toBeInTheDocument();
  });

  it("shows draft validation only after an Add attempt", async () => {
    const user = userEvent.setup();
    renderWithProviders(<TransactionsSection {...defaultProps} />);

    const amountInput = screen.getByLabelText(/amount/i);
    await user.click(amountInput);
    await user.tab();

    expect(screen.queryByText("Amount is required")).not.toBeInTheDocument();
    expect(screen.queryByText("Card is required")).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /add/i }));

    expect(await screen.findByText("Amount is required")).toBeInTheDocument();
    expect(screen.getByText("Card is required")).toBeInTheDocument();
    expect(defaultProps.onChange).not.toHaveBeenCalled();
  });

  it("keeps draft validation visible while focus moves within the Transactions card", async () => {
    const user = userEvent.setup();
    renderWithProviders(<TransactionsSection {...defaultProps} />);

    await user.click(screen.getByRole("button", { name: /add/i }));
    expect(await screen.findByText("Amount is required")).toBeInTheDocument();

    await user.click(screen.getByLabelText(/amount/i));

    expect(screen.getByText("Amount is required")).toBeInTheDocument();
    expect(screen.getByText("Card is required")).toBeInTheDocument();
  });

  it("clears draft validation when focus leaves the Transactions card without clearing values", async () => {
    const user = userEvent.setup();
    renderWithProviders(
      <>
        <TransactionsSection {...defaultProps} />
        <button type="button">Outside transactions</button>
      </>,
    );

    const amountInput = screen.getByLabelText(/amount/i);
    await user.clear(amountInput);
    await user.type(amountInput, "12.34");
    await user.click(screen.getByRole("button", { name: /add/i }));
    expect(await screen.findByText("Card is required")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Outside transactions" }));

    expect(screen.queryByText("Card is required")).not.toBeInTheDocument();
    expect(screen.queryByText("Account is required")).not.toBeInTheDocument();
    expect(amountInput).toHaveValue("12.34");
  });

  it("keeps validation dormant after leaving until Add is attempted again", async () => {
    const user = userEvent.setup();
    renderWithProviders(
      <>
        <TransactionsSection {...defaultProps} />
        <button type="button">Outside transactions</button>
      </>,
    );

    const amountInput = screen.getByLabelText(/amount/i);
    await user.clear(amountInput);
    await user.type(amountInput, "5.00");
    await user.click(screen.getByRole("button", { name: /add/i }));
    expect(await screen.findByText("Card is required")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Outside transactions" }));
    await waitFor(() => {
      expect(screen.queryByText("Card is required")).not.toBeInTheDocument();
    });

    await user.clear(amountInput);
    await user.type(amountInput, "6.00");
    await user.tab();

    expect(screen.queryByText("Card is required")).not.toBeInTheDocument();
    expect(screen.queryByText("Account is required")).not.toBeInTheDocument();
    expect(screen.queryByText("Amount is required")).not.toBeInTheDocument();
  });

  it.each([
    ["Card", "Search cards..."],
    ["Account", "Search accounts..."],
  ])(
    "treats the portaled %s combobox editor as part of the Transactions focus boundary",
    async (fieldName, searchPlaceholder) => {
      const user = userEvent.setup();
      renderWithProviders(
        <>
          <TransactionsSection {...defaultProps} />
          <button type="button">Outside transactions</button>
        </>,
      );

      await user.click(screen.getByRole("button", { name: /add/i }));
      expect(await screen.findByText("Amount is required")).toBeInTheDocument();

      await user.click(
        screen.getByRole("combobox", { name: new RegExp(`^${fieldName}`) }),
      );
      const searchInput = await screen.findByPlaceholderText(searchPlaceholder);
      expect(searchInput).toHaveFocus();
      expect(screen.getByText("Amount is required")).toBeInTheDocument();
      expect(screen.getByText(`${fieldName} is required`)).toBeInTheDocument();

      await user.click(screen.getByRole("button", { name: "Outside transactions" }));

      expect(screen.queryByText("Amount is required")).not.toBeInTheDocument();
      expect(screen.queryByText(`${fieldName} is required`)).not.toBeInTheDocument();
    },
  );

  it("displays running total", () => {
    renderWithProviders(<TransactionsSection {...defaultProps} />);
    expect(screen.getByText("Total: $0.00")).toBeInTheDocument();
  });

  it("renders existing transactions", () => {
    const transactions = [
      { id: "1", cardId: "card-1", accountId: "acct-1", amount: 25.5, date: "2024-01-15" },
    ];
    renderWithProviders(
      <TransactionsSection {...defaultProps} transactions={transactions} />,
    );
    expect(screen.getByText("$25.50")).toBeInTheDocument();
    expect(screen.getByText("Checking")).toBeInTheDocument();
    expect(screen.getByText("Visa 4321")).toBeInTheDocument();
  });

  it("calls onChange when a transaction is added via form submit; card selection auto-fills account", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithProviders(
      <TransactionsSection {...defaultProps} onChange={onChange} />,
    );

    // Select card via combobox (first combobox is Card)
    const [cardCombobox] = screen.getAllByRole("combobox");
    await user.click(cardCombobox);
    const cardOption = await screen.findByText("Visa 4321");
    await user.click(cardOption);

    // Type amount
    const amountInput = screen.getByLabelText(/amount/i);
    await user.click(amountInput);
    await user.type(amountInput, "42.50");

    // Press Enter to submit
    await user.keyboard("{Enter}");

    expect(onChange).toHaveBeenCalledWith(
      expect.arrayContaining([
        expect.objectContaining({
          cardId: "card-1",
          accountId: "acct-1",
          amount: 42.5,
          date: "2024-01-15",
        }),
      ]),
    );
  });

  it("calls onChange when a transaction is removed", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const transactions = [
      { id: "1", cardId: "card-1", accountId: "acct-1", amount: 25.5, date: "2024-01-15" },
    ];
    renderWithProviders(
      <TransactionsSection
        {...defaultProps}
        transactions={transactions}
        onChange={onChange}
      />,
    );

    await user.click(screen.getByRole("button", { name: /remove/i }));
    expect(onChange).toHaveBeenCalledWith([]);
  });

  it("syncs transaction date when defaultDate changes and date field is empty", async () => {
    const { rerender } = renderWithProviders(
      <TransactionsSection {...defaultProps} defaultDate="" />,
    );
    // The date input should be empty initially
    const dateInput = screen.getByPlaceholderText("MM/DD/YYYY");
    expect(dateInput).toHaveValue("");

    // Update the defaultDate prop (simulating the receipt date being set)
    await act(async () => {
      rerender(
        <TransactionsSection {...defaultProps} defaultDate="2024-03-20" />,
      );
    });
    expect(dateInput).toHaveValue("03/20/2024");
  });

  it("syncs transaction date when defaultDate changes and date matches previous default", async () => {
    const { rerender } = renderWithProviders(
      <TransactionsSection {...defaultProps} defaultDate="2024-01-15" />,
    );
    const dateInput = screen.getByPlaceholderText("MM/DD/YYYY");
    expect(dateInput).toHaveValue("01/15/2024");

    // Change the receipt date
    await act(async () => {
      rerender(
        <TransactionsSection {...defaultProps} defaultDate="2024-03-20" />,
      );
    });
    expect(dateInput).toHaveValue("03/20/2024");
  });

  it("displays running total with existing transactions", () => {
    const transactions = [
      { id: "1", cardId: "card-1", accountId: "acct-1", amount: 25.5, date: "2024-01-15" },
      { id: "2", cardId: "card-2", accountId: "acct-2", amount: 10.0, date: "2024-01-15" },
    ];
    renderWithProviders(
      <TransactionsSection {...defaultProps} transactions={transactions} />,
    );
    expect(screen.getByText("Total: $35.50")).toBeInTheDocument();
  });
});
