import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import { mockMutationResult } from "@/test/mock-hooks";
import "@/test/setup-combobox-polyfills";
import NewReceiptPage from "./NewReceiptPage";

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

const mockCreateCompleteReceiptAsync = vi.fn();

vi.mock("@/hooks/useReceipts", () => ({
  useCreateCompleteReceipt: vi.fn(() =>
    mockMutationResult({ mutateAsync: mockCreateCompleteReceiptAsync }),
  ),
}));

vi.mock("@/hooks/useLocationHistory", () => ({
  useLocationHistory: vi.fn(() => ({
    locations: [],
    options: [{ value: "Walmart", label: "Walmart" }],
    add: vi.fn(),
    clear: vi.fn(),
  })),
}));

const mockNavigate = vi.fn();
vi.mock("react-router", async (importOriginal) => {
  const actual = await importOriginal<typeof import("react-router")>();
  return {
    ...actual,
    useNavigate: vi.fn(() => mockNavigate),
  };
});

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));

// Mock child sections to isolate page logic
vi.mock("./TransactionsSection", () => ({
  TransactionsSection: ({
    transactions,
    onChange,
    confidenceById,
  }: {
    transactions: Array<{
      id: string;
      cardId: string;
      accountId: string;
      amount: number;
      date: string;
    }>;
    receiptDate: string;
    onChange: (data: unknown[]) => void;
    confidenceById?: Map<string, { cardId?: string; amount?: string }>;
  }) => (
    <div data-testid="transactions-section">
      <span data-testid="transactions-count">{transactions.length}</span>
      {confidenceById && (
        <span data-testid="transactions-confidence-size">
          {confidenceById.size}
        </span>
      )}
      {transactions.map((t) => (
        <span key={t.id} data-testid={`txn-${t.cardId || "empty"}`}>
          {t.cardId}:{t.accountId}:{t.amount}:{t.date}
        </span>
      ))}
      <button
        onClick={() =>
          onChange([
            { id: "t1", cardId: "card-1", accountId: "acct-1", amount: 55, date: "2024-01-15" },
          ])
        }
      >
        Add Transaction
      </button>
    </div>
  ),
}));

vi.mock("./LineItemsSection", () => ({
  LineItemsSection: ({
    onChange,
    itemConfidenceById,
  }: {
    items: unknown[];
    onChange: (data: unknown[]) => void;
    itemConfidenceById?: Map<string, { taxCode?: string }>;
  }) => (
    <div data-testid="line-items-section">
      {itemConfidenceById && (
        <span data-testid="line-items-confidence-size">
          {itemConfidenceById.size}
        </span>
      )}
      <button
        onClick={() =>
          onChange([
            {
              id: "i1",
              receiptItemCode: "",
              description: "Milk",
              pricingMode: "quantity",
              quantity: 1,
              unitPrice: 50,
              totalPrice: 50,
              category: "Food",
              subcategory: "",
              taxCode: "",
            },
          ])
        }
      >
        Add Item
      </button>
    </div>
  ),
}));

vi.mock("./BalanceSidebar", () => ({
  BalanceSidebar: ({
    onSubmit,
    onCancel,
    isSubmitting,
  }: {
    subtotal: number;
    taxAmount: number;
    transactionTotal: number;
    isSubmitting: boolean;
    onSubmit: () => void;
    onCancel: () => void;
  }) => (
    <div data-testid="balance-sidebar">
      <button onClick={onSubmit} disabled={isSubmitting}>
        {isSubmitting ? "Submitting..." : "Submit Receipt"}
      </button>
      <button onClick={onCancel}>Cancel</button>
    </div>
  ),
}));

describe("NewReceiptPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockCreateCompleteReceiptAsync.mockResolvedValue({
      receipt: { id: "receipt-123" },
      transactions: [],
      items: [],
    });
  });

  it("renders the page heading", () => {
    renderWithProviders(<NewReceiptPage />);
    expect(
      screen.getByRole("heading", { name: /new receipt/i }),
    ).toBeInTheDocument();
  });

  it("renders all three sections", () => {
    renderWithProviders(<NewReceiptPage />);
    expect(screen.getByText(/^Location/)).toBeInTheDocument();
    expect(screen.getByTestId("transactions-section")).toBeInTheDocument();
    expect(screen.getByTestId("line-items-section")).toBeInTheDocument();
  });

  it("renders balance sidebar", () => {
    renderWithProviders(<NewReceiptPage />);
    expect(screen.getAllByTestId("balance-sidebar").length).toBeGreaterThan(0);
  });

  it("navigates directly to /receipts when cancel clicked with no data", async () => {
    const user = userEvent.setup();
    renderWithProviders(<NewReceiptPage />);

    // Click the first cancel button (there are two — desktop and mobile)
    const cancelButtons = screen.getAllByText("Cancel");
    await user.click(cancelButtons[0]);
    expect(mockNavigate).toHaveBeenCalledWith("/receipts");
  });

  it("shows discard dialog when cancel clicked after entering data", async () => {
    const user = userEvent.setup();
    renderWithProviders(<NewReceiptPage />);

    // Add a transaction to make the form dirty
    await user.click(screen.getAllByText("Add Transaction")[0]);

    // Click cancel
    const cancelButtons = screen.getAllByText("Cancel");
    await user.click(cancelButtons[0]);

    expect(screen.getByText("Discard receipt?")).toBeInTheDocument();
  });

  it("discards and navigates when Discard is clicked", async () => {
    const user = userEvent.setup();
    renderWithProviders(<NewReceiptPage />);

    // Add data
    await user.click(screen.getAllByText("Add Transaction")[0]);

    // Open discard dialog
    const cancelButtons = screen.getAllByText("Cancel");
    await user.click(cancelButtons[0]);

    // Click Discard
    await user.click(screen.getByText("Discard"));
    expect(mockNavigate).toHaveBeenCalledWith("/receipts");
  });

  it("continues editing when Continue editing is clicked", async () => {
    const user = userEvent.setup();
    renderWithProviders(<NewReceiptPage />);

    // Add data
    await user.click(screen.getAllByText("Add Transaction")[0]);

    // Open discard dialog
    const cancelButtons = screen.getAllByText("Cancel");
    await user.click(cancelButtons[0]);

    // Click Continue editing
    await user.click(screen.getByText("Continue editing"));
    expect(screen.queryByText("Discard receipt?")).not.toBeInTheDocument();
  });

  it("shows error toast when submitting without transactions", async () => {
    const { toast } = await import("sonner");
    const user = userEvent.setup();
    renderWithProviders(<NewReceiptPage />);

    // Fill header (location is a combobox — select Walmart)
    const combobox = screen.getByRole("combobox");
    await user.click(combobox);
    const walmart = await screen.findByText("Walmart");
    await user.click(walmart);

    // Fill date
    const dateInput = screen.getByPlaceholderText("MM/DD/YYYY");
    await user.click(dateInput);
    await user.type(dateInput, "01/15/2024");

    // Add an item but no transaction
    await user.click(screen.getAllByText("Add Item")[0]);

    // Submit
    const submitButtons = screen.getAllByText("Submit Receipt");
    await user.click(submitButtons[0]);

    await vi.waitFor(() => {
      expect(toast.error).toHaveBeenCalledWith(
        "Add at least one transaction.",
      );
    });
  });

  it("shows error toast when submitting without line items", async () => {
    const { toast } = await import("sonner");
    const user = userEvent.setup();
    renderWithProviders(<NewReceiptPage />);

    // Fill header
    const combobox = screen.getByRole("combobox");
    await user.click(combobox);
    const walmart = await screen.findByText("Walmart");
    await user.click(walmart);

    const dateInput = screen.getByPlaceholderText("MM/DD/YYYY");
    await user.click(dateInput);
    await user.type(dateInput, "01/15/2024");

    // Add a transaction but no items
    await user.click(screen.getAllByText("Add Transaction")[0]);

    // Submit
    const submitButtons = screen.getAllByText("Submit Receipt");
    await user.click(submitButtons[0]);

    await vi.waitFor(() => {
      expect(toast.error).toHaveBeenCalledWith(
        "Add at least one line item.",
      );
    });
  });

  it("submits receipt successfully with all data", async () => {
    const { toast } = await import("sonner");
    const user = userEvent.setup();
    renderWithProviders(<NewReceiptPage />);

    // Fill header
    const combobox = screen.getByRole("combobox");
    await user.click(combobox);
    const walmart = await screen.findByText("Walmart");
    await user.click(walmart);

    const dateInput = screen.getByPlaceholderText("MM/DD/YYYY");
    await user.click(dateInput);
    await user.type(dateInput, "01/15/2024");

    // Add transaction and item
    await user.click(screen.getAllByText("Add Transaction")[0]);
    await user.click(screen.getAllByText("Add Item")[0]);

    // Submit
    const submitButtons = screen.getAllByText("Submit Receipt");
    await user.click(submitButtons[0]);

    await vi.waitFor(() => {
      expect(mockCreateCompleteReceiptAsync).toHaveBeenCalledWith(
        expect.objectContaining({
          receipt: expect.objectContaining({
            location: "Walmart",
          }),
          transactions: [
            expect.objectContaining({
              cardId: "card-1",
              accountId: "acct-1",
              amount: 55,
            }),
          ],
          items: [
            expect.objectContaining({
              description: "Milk",
              category: "Food",
            }),
          ],
        }),
      );
    });

    expect(toast.success).toHaveBeenCalledWith("Receipt created successfully!");
    expect(mockNavigate).toHaveBeenCalledWith(
      "/receipts/receipt-123",
    );
  });

  it("shows error toast when submission fails", async () => {
    const { toast } = await import("sonner");
    mockCreateCompleteReceiptAsync.mockRejectedValueOnce(
      new Error("Server error"),
    );
    const user = userEvent.setup();
    renderWithProviders(<NewReceiptPage />);

    // Fill header
    const combobox = screen.getByRole("combobox");
    await user.click(combobox);
    const walmart = await screen.findByText("Walmart");
    await user.click(walmart);

    const dateInput = screen.getByPlaceholderText("MM/DD/YYYY");
    await user.click(dateInput);
    await user.type(dateInput, "01/15/2024");

    // Add transaction and item
    await user.click(screen.getAllByText("Add Transaction")[0]);
    await user.click(screen.getAllByText("Add Item")[0]);

    // Submit
    const submitButtons = screen.getAllByText("Submit Receipt");
    await user.click(submitButtons[0]);

    await vi.waitFor(() => {
      expect(toast.error).toHaveBeenCalledWith("Failed to create receipt.");
    });
  });

  // --- Rich-fields tests (RECEIPTS-628) ---

  it("renders Store Address and Store Phone inputs", () => {
    renderWithProviders(<NewReceiptPage />);
    expect(screen.getByLabelText(/store address/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/store phone/i)).toBeInTheDocument();
  });

  it("pre-populates store address and phone from initial values", () => {
    renderWithProviders(
      <NewReceiptPage
        initialValues={{
          header: {
            location: "Walmart",
            date: "2024-06-15",
            taxAmount: 0,
            storeAddress: "123 Main St",
            storePhone: "(555) 123-4567",
          },
          metadata: { receiptId: "", storeNumber: "", terminalId: "" },
          proposedTransactions: [],
          items: [],
        }}
      />,
    );
    expect(
      (screen.getByLabelText(/store address/i) as HTMLInputElement).value,
    ).toBe("123 Main St");
    expect(
      (screen.getByLabelText(/store phone/i) as HTMLInputElement).value,
    ).toBe("(555) 123-4567");
  });

  it("does not render the receipt details panel when metadata is empty", () => {
    renderWithProviders(<NewReceiptPage />);
    expect(
      screen.queryByText(/receipt details/i),
    ).not.toBeInTheDocument();
  });

  it("renders the receipt details panel when metadata is populated", async () => {
    const user = userEvent.setup();
    renderWithProviders(
      <NewReceiptPage
        initialValues={{
          header: {
            location: "Walmart",
            date: "2024-06-15",
            taxAmount: 0,
            storeAddress: "",
            storePhone: "",
          },
          metadata: {
            receiptId: "TX-987654",
            storeNumber: "0042",
            terminalId: "T01",
          },
          proposedTransactions: [],
          items: [],
        }}
      />,
    );

    // The panel is collapsed by default, so the heading is rendered but values
    // are inside the collapsible content.
    expect(screen.getByText(/receipt details/i)).toBeInTheDocument();

    // Expand the panel to reveal values
    await user.click(
      screen.getByRole("button", { name: /expand receipt details/i }),
    );

    expect(screen.getByText("TX-987654")).toBeInTheDocument();
    expect(screen.getByText("0042")).toBeInTheDocument();
    expect(screen.getByText("T01")).toBeInTheDocument();
  });

  // --- Pre-populated transactions (RECEIPTS-658) ---

  it("does not render any transactions when initial proposedTransactions is empty", () => {
    renderWithProviders(<NewReceiptPage />);
    expect(screen.getByTestId("transactions-count")).toHaveTextContent("0");
  });

  it("pre-populates transactions from initial proposedTransactions", () => {
    renderWithProviders(
      <NewReceiptPage
        initialValues={{
          header: {
            location: "Walmart",
            date: "2024-06-15",
            taxAmount: 0,
            storeAddress: "",
            storePhone: "",
          },
          metadata: { receiptId: "", storeNumber: "", terminalId: "" },
          proposedTransactions: [
            {
              cardId: "card-99",
              accountId: "acct-99",
              amount: 10.01,
              date: "2024-06-15",
            },
          ],
          items: [],
        }}
      />,
    );

    expect(screen.getByTestId("transactions-count")).toHaveTextContent("1");
    expect(screen.getByTestId("txn-card-99")).toHaveTextContent(
      "card-99:acct-99:10.01:2024-06-15",
    );
  });

  it("builds a transaction-confidence Map keyed by transaction id when scan transactions are present", () => {
    renderWithProviders(
      <NewReceiptPage
        initialValues={{
          header: {
            location: "Walmart",
            date: "2024-06-15",
            taxAmount: 0,
            storeAddress: "",
            storePhone: "",
          },
          metadata: { receiptId: "", storeNumber: "", terminalId: "" },
          proposedTransactions: [
            {
              cardId: "card-99",
              accountId: "acct-99",
              amount: 10.01,
              date: "2024-06-15",
            },
          ],
          items: [],
        }}
        confidenceMap={{ transactions: [{ amount: "low" }] }}
      />,
    );

    expect(screen.getByTestId("transactions-confidence-size")).toHaveTextContent(
      "1",
    );
  });

  it("uses the pageTitle prop for the visible heading", () => {
    renderWithProviders(<NewReceiptPage pageTitle="Scan Receipt" />);
    expect(
      screen.getByRole("heading", { name: /scan receipt/i }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("heading", { name: /^new receipt$/i }),
    ).not.toBeInTheDocument();
  });

  it("falls back to 'New Receipt' for the heading when pageTitle is omitted", () => {
    renderWithProviders(<NewReceiptPage />);
    expect(
      screen.getByRole("heading", { name: /new receipt/i }),
    ).toBeInTheDocument();
  });

  it("builds an item-confidence Map keyed by item id when scan items are present", () => {
    renderWithProviders(
      <NewReceiptPage
        initialValues={{
          header: {
            location: "Walmart",
            date: "2024-06-15",
            taxAmount: 0,
            storeAddress: "",
            storePhone: "",
          },
          metadata: { receiptId: "", storeNumber: "", terminalId: "" },
          proposedTransactions: [],
          items: [
            {
              receiptItemCode: "MILK",
              description: "Milk",
              pricingMode: "quantity",
              quantity: 1,
              unitPrice: 3.5,
              totalPrice: 3.5,
              category: "",
              subcategory: "",
              taxCode: "F",
            },
          ],
        }}
        confidenceMap={{ items: [{ taxCode: "low" }] }}
      />,
    );

    // The Map should contain exactly one entry (one scan item with low taxCode confidence).
    expect(screen.getByTestId("line-items-confidence-size")).toHaveTextContent(
      "1",
    );
  });

  it("does not include confidence entries for items lacking a confidence record", () => {
    renderWithProviders(
      <NewReceiptPage
        initialValues={{
          header: {
            location: "Walmart",
            date: "2024-06-15",
            taxAmount: 0,
            storeAddress: "",
            storePhone: "",
          },
          metadata: { receiptId: "", storeNumber: "", terminalId: "" },
          proposedTransactions: [],
          items: [
            {
              receiptItemCode: "MILK",
              description: "Milk",
              pricingMode: "quantity",
              quantity: 1,
              unitPrice: 3.5,
              totalPrice: 3.5,
              category: "",
              subcategory: "",
              taxCode: "",
            },
          ],
        }}
        // No items entry in confidence map → empty Map.
        confidenceMap={{}}
      />,
    );

    expect(screen.getByTestId("line-items-confidence-size")).toHaveTextContent(
      "0",
    );
  });

  // --- Dropped-page warning (RECEIPTS-637) ---

  it("does not render dropped-pages warning when droppedPageCount is 0", () => {
    renderWithProviders(<NewReceiptPage droppedPageCount={0} />);
    expect(
      screen.queryByTestId("dropped-pages-warning"),
    ).not.toBeInTheDocument();
  });

  it("does not render dropped-pages warning when droppedPageCount is undefined", () => {
    renderWithProviders(<NewReceiptPage />);
    expect(
      screen.queryByTestId("dropped-pages-warning"),
    ).not.toBeInTheDocument();
  });

  it("renders dropped-pages warning with singular phrasing for one dropped page", () => {
    renderWithProviders(<NewReceiptPage droppedPageCount={1} />);
    expect(screen.getByTestId("dropped-pages-warning")).toBeInTheDocument();
    expect(
      screen.getByText(/this pdf had 2 pages.*only page 1 was extracted/i),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/missing details from page 2 manually/i),
    ).toBeInTheDocument();
  });

  it("renders dropped-pages warning with plural phrasing for multiple dropped pages", () => {
    renderWithProviders(<NewReceiptPage droppedPageCount={4} />);
    expect(screen.getByTestId("dropped-pages-warning")).toBeInTheDocument();
    expect(
      screen.getByText(/this pdf had 5 pages.*only page 1 was extracted/i),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/missing details from pages 2-5 manually/i),
    ).toBeInTheDocument();
  });

  it("validates the store phone format on submit", async () => {
    const user = userEvent.setup();
    renderWithProviders(<NewReceiptPage />);

    // Enter an invalid phone — alpha characters not allowed
    const phoneInput = screen.getByLabelText(/store phone/i);
    await user.type(phoneInput, "not-a-phone");

    // Fill rest of header so we get past required fields
    const combobox = screen.getByRole("combobox");
    await user.click(combobox);
    const walmart = await screen.findByText("Walmart");
    await user.click(walmart);

    const dateInput = screen.getByPlaceholderText("MM/DD/YYYY");
    await user.click(dateInput);
    await user.type(dateInput, "01/15/2024");

    // Add transaction + item so that we don't bail on those checks first
    await user.click(screen.getAllByText("Add Transaction")[0]);
    await user.click(screen.getAllByText("Add Item")[0]);

    // Submit triggers form validation
    const submitButtons = screen.getAllByText("Submit Receipt");
    await user.click(submitButtons[0]);

    expect(
      await screen.findByText(/store phone is not in a recognised format/i),
    ).toBeInTheDocument();
    expect(mockCreateCompleteReceiptAsync).not.toHaveBeenCalled();
  });
});
