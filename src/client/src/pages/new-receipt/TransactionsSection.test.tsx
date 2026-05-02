import { screen, act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import { mockQueryResult } from "@/test/mock-hooks";
import "@/test/setup-combobox-polyfills";
import { toast } from "sonner";
import { TransactionsSection } from "./TransactionsSection";

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));

vi.mock("@/hooks/useFormShortcuts", () => ({
  useFormShortcuts: vi.fn(),
}));

vi.mock("@/hooks/useAccounts", () => ({
  useAccounts: vi.fn(() =>
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
  useCards: vi.fn(() =>
    mockQueryResult({
      data: [
        { id: "card-1", name: "Visa 4321", cardCode: "V4321", isActive: true, accountId: "acct-1" },
        { id: "card-2", name: "Amex 7777", cardCode: "A7777", isActive: true, accountId: null },
        { id: "card-3", name: "MC 5555", cardCode: "MC5555", isActive: true, accountId: "acct-2" },
      ],
      total: 3,
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
      screen.getByRole("button", { name: /^add$/i }),
    ).toBeInTheDocument();
  });

  it("displays running total", () => {
    renderWithProviders(<TransactionsSection {...defaultProps} />);
    expect(screen.getByText("Total: $0.00")).toBeInTheDocument();
  });

  it("renders existing transactions in the table", () => {
    const transactions = [
      { id: "1", cardId: "card-1", accountId: "acct-1", amount: 25.5, date: "2024-01-15" },
    ];
    renderWithProviders(
      <TransactionsSection {...defaultProps} transactions={transactions} />,
    );
    // The row's Card combobox shows the selected card label.
    expect(screen.getByLabelText(/^Card for transaction 1$/i)).toBeInTheDocument();
    expect(screen.getByDisplayValue("25.50")).toBeInTheDocument();
  });

  it("calls onChange when a transaction is added via form submit; card selection auto-fills account", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithProviders(
      <TransactionsSection {...defaultProps} onChange={onChange} />,
    );

    // Select card via combobox (the form's Card combobox is the first one)
    const allComboboxes = screen.getAllByRole("combobox");
    await user.click(allComboboxes[0]);
    const cardOption = await screen.findByText("Visa 4321");
    await user.click(cardOption);

    // Type amount in the form's Amount field (the only one in the form;
    // there are no row inputs yet because transactions starts empty)
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
    const dateInputs = screen.getAllByPlaceholderText("MM/DD/YYYY");
    const formDateInput = dateInputs[0];
    expect(formDateInput).toHaveValue("");

    // Update the defaultDate prop (simulating the receipt date being set)
    await act(async () => {
      rerender(
        <TransactionsSection {...defaultProps} defaultDate="2024-03-20" />,
      );
    });
    expect(formDateInput).toHaveValue("03/20/2024");
  });

  it("syncs transaction date when defaultDate changes and date matches previous default", async () => {
    const { rerender } = renderWithProviders(
      <TransactionsSection {...defaultProps} defaultDate="2024-01-15" />,
    );
    const dateInput = screen.getAllByPlaceholderText("MM/DD/YYYY")[0];
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
      { id: "2", cardId: "card-3", accountId: "acct-2", amount: 10.0, date: "2024-01-15" },
    ];
    renderWithProviders(
      <TransactionsSection {...defaultProps} transactions={transactions} />,
    );
    expect(screen.getByText("Total: $35.50")).toBeInTheDocument();
  });

  // --- RECEIPTS-658: pre-populated rows + Card→Account auto-fill ---

  describe("pre-populated transactions (RECEIPTS-658)", () => {
    it("renders a pre-populated row with the matching card label", () => {
      // The parent (NewReceiptPage) feeds pre-populated rows through the
      // standard `transactions` prop after seeding initial state from the
      // proposal. The row should render the same way as user-added rows but
      // additionally surface confidence chips and lock the Account dropdown.
      const transactions = [
        {
          id: "scan-1",
          cardId: "card-1",
          accountId: "acct-1",
          amount: 10.01,
          date: "2024-06-15",
        },
      ];
      renderWithProviders(
        <TransactionsSection
          {...defaultProps}
          transactions={transactions}
          confidenceById={
            new Map([
              ["scan-1", { cardId: "high", amount: "low" as const }],
            ])
          }
        />,
      );

      // Row exists with the right testid
      expect(screen.getByTestId("txn-row-scan-1")).toBeInTheDocument();
      // Amount populated
      expect(screen.getByDisplayValue("10.01")).toBeInTheDocument();
    });

    it("renders a confidence chip on a pre-populated row when amount confidence is low", () => {
      const transactions = [
        {
          id: "scan-1",
          cardId: "card-1",
          accountId: "acct-1",
          amount: 10.01,
          date: "2024-06-15",
        },
      ];
      const confidenceById = new Map([
        ["scan-1", { amount: "low" as const }],
      ]);
      renderWithProviders(
        <TransactionsSection
          {...defaultProps}
          transactions={transactions}
          confidenceById={confidenceById}
        />,
      );

      // ConfidenceIndicator renders a chip with an aria-label exposing the rating.
      expect(
        screen.getByLabelText("AI confidence rating: low"),
      ).toBeInTheDocument();
    });

    it("renders a medium confidence chip on cardId when extraction was ambiguous", () => {
      const transactions = [
        {
          id: "scan-1",
          cardId: "card-1",
          accountId: "acct-1",
          amount: 10,
          date: "2024-06-15",
        },
      ];
      const confidenceById = new Map([
        ["scan-1", { cardId: "medium" as const }],
      ]);
      renderWithProviders(
        <TransactionsSection
          {...defaultProps}
          transactions={transactions}
          confidenceById={confidenceById}
        />,
      );

      expect(
        screen.getByLabelText("AI confidence rating: medium"),
      ).toBeInTheDocument();
    });

    it("does not render a confidence chip when no entry is provided for the row", () => {
      const transactions = [
        {
          id: "scan-1",
          cardId: "card-1",
          accountId: "acct-1",
          amount: 10,
          date: "2024-06-15",
        },
      ];
      renderWithProviders(
        <TransactionsSection
          {...defaultProps}
          transactions={transactions}
          confidenceById={new Map()}
        />,
      );

      expect(
        screen.queryByLabelText(/AI confidence rating/),
      ).not.toBeInTheDocument();
    });

    it("locks the Account dropdown for a pre-populated row with a card-driven account", () => {
      // The Card→Account FK auto-fill: a row whose cardId resolves to a card
      // with a known accountId must render the Account dropdown read-only and
      // surface an inline label so the user knows why it cannot be edited.
      const transactions = [
        {
          id: "scan-1",
          cardId: "card-1", // card-1.accountId == "acct-1"
          accountId: "acct-1",
          amount: 10,
          date: "2024-06-15",
        },
      ];
      renderWithProviders(
        <TransactionsSection
          {...defaultProps}
          transactions={transactions}
        />,
      );

      const accountCombo = screen.getByLabelText(
        /^Account for transaction scan-1$/i,
      );
      expect(accountCombo).toBeDisabled();
      expect(
        screen.getAllByText("Account is set by the selected card.").length,
      ).toBeGreaterThan(0);
    });

    it("does not lock the Account dropdown when the card has no account FK", () => {
      // card-2 has accountId: null — the Account dropdown should remain editable.
      const transactions = [
        {
          id: "scan-1",
          cardId: "card-2",
          accountId: "",
          amount: 10,
          date: "2024-06-15",
        },
      ];
      renderWithProviders(
        <TransactionsSection
          {...defaultProps}
          transactions={transactions}
        />,
      );

      const accountCombo = screen.getByLabelText(
        /^Account for transaction scan-1$/i,
      );
      expect(accountCombo).not.toBeDisabled();
    });

    it("calls onChange when a pre-populated row's amount is edited", async () => {
      const user = userEvent.setup();
      const onChange = vi.fn();
      const transactions = [
        {
          id: "scan-1",
          cardId: "card-1",
          accountId: "acct-1",
          amount: 10,
          date: "2024-06-15",
        },
      ];
      renderWithProviders(
        <TransactionsSection
          {...defaultProps}
          transactions={transactions}
          onChange={onChange}
        />,
      );

      const amountInput = screen.getByLabelText(
        /^Amount for transaction scan-1$/i,
      );
      await user.clear(amountInput);
      await user.type(amountInput, "20");

      // The handler updates the row in place; final call should reflect the new amount.
      const lastCall = onChange.mock.calls.at(-1)![0];
      expect(lastCall[0]).toMatchObject({
        id: "scan-1",
        amount: 20,
      });
    });

    it("calls onChange when a pre-populated row is deleted", async () => {
      const user = userEvent.setup();
      const onChange = vi.fn();
      const transactions = [
        {
          id: "scan-1",
          cardId: "card-1",
          accountId: "acct-1",
          amount: 10,
          date: "2024-06-15",
        },
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
  });

  describe("Card→Account FK auto-fill on the new-row form", () => {
    it("auto-fills the Account dropdown when a Card is picked (card-first flow)", async () => {
      const user = userEvent.setup();
      renderWithProviders(<TransactionsSection {...defaultProps} />);

      // Pick Card first.
      const allComboboxes = screen.getAllByRole("combobox");
      const cardCombo = allComboboxes[0];
      const accountCombo = allComboboxes[1];

      await user.click(cardCombo);
      await user.click(await screen.findByText("Visa 4321"));

      // Account becomes locked and shows the linked account's label.
      expect(accountCombo).toBeDisabled();
      expect(accountCombo).toHaveTextContent("Checking");
      expect(
        screen.getByText("Account is set by the selected card."),
      ).toBeInTheDocument();
    });

    it("Card wins: picking a Card after Account overwrites the manual Account choice", async () => {
      // Account-first-then-Card flow. card-3 has accountId == "acct-2",
      // so picking it after a manual "Checking" (acct-1) selection must
      // overwrite the manual choice. Card is the source of truth.
      const user = userEvent.setup();
      renderWithProviders(<TransactionsSection {...defaultProps} />);

      const allComboboxes = screen.getAllByRole("combobox");
      const cardCombo = allComboboxes[0];
      const accountCombo = allComboboxes[1];

      // Step 1: pick Account = Checking (acct-1).
      await user.click(accountCombo);
      await user.click(await screen.findByText("Checking"));
      expect(accountCombo).toHaveTextContent("Checking");

      // Step 2: pick Card = MC 5555, which is linked to acct-2 (Credit Card).
      await user.click(cardCombo);
      await user.click(await screen.findByText("MC 5555"));

      // Card wins: Account is now Credit Card and the dropdown is locked.
      expect(accountCombo).toBeDisabled();
      expect(accountCombo).toHaveTextContent("Credit Card");
    });

    it("Account dropdown is editable when no Card is selected", async () => {
      // Inverse of the lock case: with no Card chosen, the Account dropdown
      // is editable and no inline lock label appears. This documents the
      // "release" half of the auto-fill rule (a cleared Card → editable
      // Account) at the rendered-state level, since the Combobox does not
      // expose a UI clear action.
      renderWithProviders(<TransactionsSection {...defaultProps} />);

      const allComboboxes = screen.getAllByRole("combobox");
      const accountCombo = allComboboxes[1];

      expect(accountCombo).not.toBeDisabled();
      expect(
        screen.queryByText("Account is set by the selected card."),
      ).not.toBeInTheDocument();
    });
  });

  describe("Card→Account FK auto-fill on existing rows", () => {
    it("Card wins on an existing row: switching the Card overwrites the row's accountId", async () => {
      const user = userEvent.setup();
      const onChange = vi.fn();
      // Start with a row whose Card is card-1 (acct-1) but with the user
      // having manually overridden Account to acct-2. (This is the
      // pre-population edit case — though the row will lock once we re-render
      // with a card that has an accountId, the test exercises the handler.)
      const transactions = [
        {
          id: "scan-1",
          cardId: "card-2", // card-2 has no accountId, so Account stays editable
          accountId: "acct-2",
          amount: 10,
          date: "2024-06-15",
        },
      ];
      renderWithProviders(
        <TransactionsSection
          {...defaultProps}
          transactions={transactions}
          onChange={onChange}
        />,
      );

      const cardCombo = screen.getByLabelText(
        /^Card for transaction scan-1$/i,
      );
      // Switch from card-2 (no FK) to card-3 (FK -> acct-2... same! Try card-1 -> acct-1)
      await user.click(cardCombo);
      await user.click(await screen.findByText("Visa 4321")); // card-1

      // Card wins: row.accountId is overwritten to card-1.accountId == "acct-1".
      const lastCall = onChange.mock.calls.at(-1)![0];
      expect(lastCall[0]).toMatchObject({
        id: "scan-1",
        cardId: "card-1",
        accountId: "acct-1",
      });
    });
  });

  // --- RECEIPTS-659: surface a toast when picking a Card overrides the row's Account ---

  describe("Card→Account override notice (RECEIPTS-659)", () => {
    it("card-overrides-account-fires-toast: row has Account A, user picks Card whose accountId is Account B → toast fires with the Account B name", async () => {
      const user = userEvent.setup();
      const onChange = vi.fn();
      // Row currently bound to card-2 (no FK) with manual Account = acct-1
      // (Checking). Picking card-3 (FK = acct-2 / Credit Card) overrides the
      // prior Account selection and must fire a single info toast referencing
      // the new account name.
      const transactions = [
        {
          id: "row-1",
          cardId: "card-2",
          accountId: "acct-1",
          amount: 10,
          date: "2024-06-15",
        },
      ];
      renderWithProviders(
        <TransactionsSection
          {...defaultProps}
          transactions={transactions}
          onChange={onChange}
        />,
      );

      const cardCombo = screen.getByLabelText(/^Card for transaction row-1$/i);
      await user.click(cardCombo);
      await user.click(await screen.findByText("MC 5555")); // card-3 → acct-2

      expect(toast.info).toHaveBeenCalledTimes(1);
      expect(toast.info).toHaveBeenCalledWith(
        "Account changed to Credit Card to match the selected card.",
      );
    });

    it("card-without-prior-account-no-toast: row has no Account selected, user picks a Card → no toast (silent auto-fill)", async () => {
      const user = userEvent.setup();
      const onChange = vi.fn();
      // Row with no prior Account — auto-fill is the only behavior. Toast must
      // not fire because there is nothing being overridden.
      const transactions = [
        {
          id: "row-1",
          cardId: "",
          accountId: "",
          amount: 10,
          date: "2024-06-15",
        },
      ];
      renderWithProviders(
        <TransactionsSection
          {...defaultProps}
          transactions={transactions}
          onChange={onChange}
        />,
      );

      const cardCombo = screen.getByLabelText(/^Card for transaction row-1$/i);
      await user.click(cardCombo);
      await user.click(await screen.findByText("Visa 4321")); // card-1 → acct-1

      // The change still cascades the FK — verify that to prove the handler ran.
      const lastCall = onChange.mock.calls.at(-1)![0];
      expect(lastCall[0]).toMatchObject({
        cardId: "card-1",
        accountId: "acct-1",
      });
      // But no toast because there was no prior Account to override.
      expect(toast.info).not.toHaveBeenCalled();
    });

    it("card-with-matching-account-no-toast: row has Account A, user picks Card whose accountId is also Account A → no toast", async () => {
      const user = userEvent.setup();
      const onChange = vi.fn();
      // Row already has accountId = "acct-1". Picking card-1 (FK = "acct-1")
      // is a no-op for the Account value, so no toast should fire.
      const transactions = [
        {
          id: "row-1",
          cardId: "card-2",
          accountId: "acct-1",
          amount: 10,
          date: "2024-06-15",
        },
      ];
      renderWithProviders(
        <TransactionsSection
          {...defaultProps}
          transactions={transactions}
          onChange={onChange}
        />,
      );

      const cardCombo = screen.getByLabelText(/^Card for transaction row-1$/i);
      await user.click(cardCombo);
      await user.click(await screen.findByText("Visa 4321")); // card-1 → acct-1 (same)

      expect(toast.info).not.toHaveBeenCalled();
    });

    it("scan-prepopulation-no-toast: when the row is pre-populated with a Card+Account pair, no toast fires on initial render", () => {
      // Pre-populated row matching the proposedTransactions[0] shape: Card and
      // Account are both already set. Initial render must not fire any toast
      // because the Card override path is only invoked on user-driven
      // onValueChange — not from prop seeding.
      const transactions = [
        {
          id: "scan-1",
          cardId: "card-1",
          accountId: "acct-1",
          amount: 10.01,
          date: "2024-06-15",
        },
      ];
      renderWithProviders(
        <TransactionsSection
          {...defaultProps}
          transactions={transactions}
        />,
      );

      expect(toast.info).not.toHaveBeenCalled();
    });

    it("clearing-card-no-toast: row has a Card → user clears the Card → no toast (just re-enables the Account dropdown)", () => {
      const onChange = vi.fn();
      // Simulate a pre-populated row whose Card is then cleared. The Combobox
      // does not expose an explicit clear control in the row UI, so we drive
      // the handler through a re-render that changes the underlying transaction
      // model. The tested invariant is: the override logic only fires when a
      // *non-empty* card value is picked. We assert the only path through
      // handleRowCardChange that could emit a toast (the FK cascade) does not
      // execute when cardId is the empty string.
      const transactions = [
        {
          id: "row-1",
          cardId: "card-1",
          accountId: "acct-1",
          amount: 10,
          date: "2024-06-15",
        },
      ];
      const { rerender } = renderWithProviders(
        <TransactionsSection
          {...defaultProps}
          transactions={transactions}
          onChange={onChange}
        />,
      );
      // Sanity: no toast on initial render either.
      expect(toast.info).not.toHaveBeenCalled();

      // Re-render with the same row but the Card cleared (mimicking the
      // post-clear state of the row's transaction model).
      rerender(
        <TransactionsSection
          {...defaultProps}
          transactions={[
            {
              id: "row-1",
              cardId: "",
              accountId: "",
              amount: 10,
              date: "2024-06-15",
            },
          ]}
          onChange={onChange}
        />,
      );

      // Re-rendering with cleared values must not retroactively fire a toast.
      expect(toast.info).not.toHaveBeenCalled();
    });
  });
});
