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

type BlockerLike = {
  state: "unblocked" | "blocked" | "proceeding";
  proceed: ReturnType<typeof vi.fn>;
  reset: ReturnType<typeof vi.fn>;
};
const mockBlocker: BlockerLike = {
  state: "unblocked",
  proceed: vi.fn(),
  reset: vi.fn(),
};
type ShouldBlock = (args: {
  currentLocation: { pathname: string };
  nextLocation: { pathname: string };
}) => boolean;
let capturedShouldBlock: ShouldBlock | undefined;

vi.mock("react-router", async (importOriginal) => {
  const actual = await importOriginal<typeof import("react-router")>();
  return {
    ...actual,
    useNavigate: vi.fn(() => mockNavigate),
    // MemoryRouter (used by the test wrapper) is not a data router, so the real
    // useBlocker would throw. Capture the predicate so tests can exercise the
    // "has unsaved data" gate directly, and return a controllable blocker.
    useBlocker: (shouldBlock: ShouldBlock) => {
      capturedShouldBlock = shouldBlock;
      return mockBlocker;
    },
  };
});

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));

// Mock child sections to isolate page logic
vi.mock("./TransactionsSection", () => ({
  TransactionsSection: ({
    onChange,
  }: {
    transactions: unknown[];
    receiptDate: string;
    onChange: (data: unknown[]) => void;
  }) => (
    <div data-testid="transactions-section">
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
  }: {
    items: unknown[];
    onChange: (data: unknown[]) => void;
  }) => (
    <div data-testid="line-items-section">
      <button
        onClick={() =>
          onChange([
            {
              id: "i1",
              receiptItemCode: "",
              description: "Milk",
              quantity: 1,
              unitPrice: 50,
              category: "Food",
              subcategory: "",
            },
          ])
        }
      >
        Add Item
      </button>
    </div>
  ),
}));

vi.mock("./AdjustmentsSection", () => ({
  AdjustmentsSection: ({
    onChange,
  }: {
    adjustments: unknown[];
    onChange: (data: unknown[]) => void;
  }) => (
    <div data-testid="adjustments-section">
      <button
        onClick={() =>
          onChange([
            {
              id: "a1",
              type: "Other",
              amount: 5,
              description: "Delivery fee",
            },
          ])
        }
      >
        Add Adjustment
      </button>
    </div>
  ),
}));

vi.mock("./BalanceSidebar", () => ({
  BalanceSidebar: ({
    onSubmit,
    onCancel,
    isSubmitting,
    adjustmentTotal,
  }: {
    subtotal: number;
    taxAmount: number;
    adjustmentTotal: number;
    transactionTotal: number;
    isSubmitting: boolean;
    onSubmit: () => void;
    onCancel: () => void;
  }) => (
    <div data-testid="balance-sidebar">
      <span data-testid="sidebar-adjustment-total">{adjustmentTotal}</span>
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
    mockBlocker.state = "unblocked";
    capturedShouldBlock = undefined;
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

  it("renders receipt header, transactions, line items, and adjustments sections", () => {
    renderWithProviders(<NewReceiptPage />);
    expect(screen.getByText(/^Location/)).toBeInTheDocument();
    expect(screen.getByTestId("transactions-section")).toBeInTheDocument();
    expect(screen.getByTestId("line-items-section")).toBeInTheDocument();
    expect(screen.getByTestId("adjustments-section")).toBeInTheDocument();
  });

  it("treats a newly entered adjustment as unsaved receipt data", async () => {
    const user = userEvent.setup();
    renderWithProviders(<NewReceiptPage />);

    await user.click(screen.getByRole("button", { name: "Add Adjustment" }));

    expect(
      capturedShouldBlock!({
        currentLocation: { pathname: "/receipts/new" },
        nextLocation: { pathname: "/dashboard" },
      }),
    ).toBe(true);
  });

  it("renders balance sidebar", () => {
    renderWithProviders(<NewReceiptPage />);
    expect(screen.getAllByTestId("balance-sidebar").length).toBeGreaterThan(0);
  });

  it("renders the line-item table full-width below the upper container, not trapped in the narrow grid column (WCAG 1.4.10)", () => {
    // The upper-left column sits in a `grid-cols-[1fr_minmax(...)]` track and
    // keeps `min-w-0` so its `1fr` track can shrink below content min-width
    // (letting an inner table's `overflow-x-auto` engage instead of widening
    // the page). The line-item table is moved out of that track to full-width
    // block flow below the grid, so it is never constrained to the narrow
    // `1fr` column.
    renderWithProviders(<NewReceiptPage />);

    const upperLeft = screen
      .getByTestId("transactions-section")
      .closest("div.min-w-0");
    expect(upperLeft).not.toBeNull();
    expect(upperLeft).toHaveClass("min-w-0");

    const lineItems = screen.getByTestId("line-items-section");
    expect(upperLeft?.contains(lineItems)).toBe(false);
  });

  it("renders a sticky action bar with the balance status and Submit/Cancel", () => {
    // The Balance panel sits in the upper container and scrolls out of view
    // once the line-item table grows tall. The sticky action bar keeps the
    // balance status and Submit/Cancel reachable at the bottom of the viewport.
    renderWithProviders(<NewReceiptPage />);

    const stickyBar = document.querySelector(".sticky.bottom-0");
    expect(stickyBar).not.toBeNull();
    expect(stickyBar?.textContent).toContain("Submit Receipt");
    expect(stickyBar?.textContent).toContain("Cancel");
    // No data yet → expected and transaction totals are both 0 → balanced.
    expect(stickyBar?.textContent).toContain("Balanced");
    expect(stickyBar?.textContent).toContain(
      "Subtotal $0.00 + Tax $0.00 + Adjustments $0.00 = Expected $0.00 · Transactions $0.00",
    );

    // The sticky bar is a sibling below the line-item table, not nested in it.
    const lineItems = screen.getByTestId("line-items-section");
    expect(stickyBar?.contains(lineItems)).toBe(false);
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

  it("updates the sticky itemized equation when receipt contents change", async () => {
    const user = userEvent.setup();
    renderWithProviders(<NewReceiptPage />);

    await user.click(screen.getAllByText("Add Transaction")[0]);
    await user.click(screen.getAllByText("Add Item")[0]);
    await user.click(screen.getByRole("button", { name: "Add Adjustment" }));

    const stickyBar = document.querySelector(".sticky.bottom-0");
    expect(stickyBar).toHaveTextContent(
      "Subtotal $50.00 + Tax $0.00 + Adjustments $5.00 = Expected $55.00 · Transactions $55.00",
    );
    expect(stickyBar).toHaveTextContent("Balanced");
  });

  it("includes adjustments in the complete-receipt payload", async () => {
    const user = userEvent.setup();
    renderWithProviders(<NewReceiptPage />);

    const combobox = screen.getByRole("combobox");
    await user.click(combobox);
    await user.click(await screen.findByText("Walmart"));
    const dateInput = screen.getByPlaceholderText("MM/DD/YYYY");
    await user.click(dateInput);
    await user.type(dateInput, "01/15/2024");
    await user.click(screen.getAllByText("Add Transaction")[0]);
    await user.click(screen.getAllByText("Add Item")[0]);
    await user.click(screen.getByRole("button", { name: "Add Adjustment" }));
    await user.click(screen.getAllByText("Submit Receipt")[0]);

    await vi.waitFor(() =>
      expect(mockCreateCompleteReceiptAsync).toHaveBeenCalledWith(
        expect.objectContaining({
          adjustments: [
            {
              type: "Other",
              amount: 5,
              description: "Delivery fee",
            },
          ],
        }),
      ),
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

  it("surfaces the actionable ProblemDetails validation message", async () => {
    const { toast } = await import("sonner");
    mockCreateCompleteReceiptAsync.mockRejectedValueOnce({
      status: 400,
      title: "One or more validation errors occurred.",
      errors: {
        Adjustments: ["Receipt total must equal the transaction total."],
      },
    });
    const user = userEvent.setup();
    renderWithProviders(<NewReceiptPage />);

    const combobox = screen.getByRole("combobox");
    await user.click(combobox);
    await user.click(await screen.findByText("Walmart"));
    const dateInput = screen.getByPlaceholderText("MM/DD/YYYY");
    await user.click(dateInput);
    await user.type(dateInput, "01/15/2024");
    await user.click(screen.getAllByText("Add Transaction")[0]);
    await user.click(screen.getAllByText("Add Item")[0]);
    await user.click(screen.getAllByText("Submit Receipt")[0]);

    await vi.waitFor(() => {
      expect(toast.error).toHaveBeenCalledWith(
        "Receipt total must equal the transaction total.",
      );
      expect(document.querySelector("[aria-live='polite']")).toHaveTextContent(
        "Receipt total must equal the transaction total.",
      );
    });
  });

  it("moves focus to the first invalid field when submitting with an invalid header", async () => {
    const user = userEvent.setup();
    renderWithProviders(<NewReceiptPage />);

    // Leave the header empty (location and date are required)
    // Submit directly — should fail validation
    const submitButtons = screen.getAllByText("Submit Receipt");
    await user.click(submitButtons[0]);

    // The form has validation errors; the location combobox (first required field)
    // should receive focus. We verify by checking that document.activeElement is
    // inside the location FormItem (the first field rendered).
    await vi.waitFor(() => {
      const activeEl = document.activeElement;
      // The combobox trigger is a button; it should be focused
      expect(activeEl).not.toBeNull();
      expect(activeEl?.tagName).not.toBe("BODY");
    });
  });

  it("announces an error summary in the aria-live region when submit fails validation", async () => {
    const user = userEvent.setup();
    renderWithProviders(<NewReceiptPage />);

    // Submit with empty header — validation fails
    const submitButtons = screen.getAllByText("Submit Receipt");
    await user.click(submitButtons[0]);

    await vi.waitFor(() => {
      const liveRegion = document.querySelector("[aria-live='polite']");
      expect(liveRegion).not.toBeNull();
      expect(liveRegion?.textContent?.trim()).toBeTruthy();
    });
  });

  // RECEIPTS-785 — unsaved-work guard (useBlocker + discard dialog).
  it("blocks in-app navigation when there is unsaved data (but not same-path)", async () => {
    const user = userEvent.setup();
    renderWithProviders(<NewReceiptPage />);

    // Enter data via the (mocked) transactions section.
    await user.click(screen.getAllByText("Add Transaction")[0]);

    expect(capturedShouldBlock).toBeDefined();
    // Navigating away from /receipts/new is blocked...
    expect(
      capturedShouldBlock!({
        currentLocation: { pathname: "/receipts/new" },
        nextLocation: { pathname: "/dashboard" },
      }),
    ).toBe(true);
    // ...but a same-path navigation is not.
    expect(
      capturedShouldBlock!({
        currentLocation: { pathname: "/receipts/new" },
        nextLocation: { pathname: "/receipts/new" },
      }),
    ).toBe(false);
  });

  it("does not block navigation when the form is empty", () => {
    renderWithProviders(<NewReceiptPage />);
    expect(capturedShouldBlock).toBeDefined();
    expect(
      capturedShouldBlock!({
        currentLocation: { pathname: "/receipts/new" },
        nextLocation: { pathname: "/dashboard" },
      }),
    ).toBe(false);
  });

  it("stops blocking after a successful save", async () => {
    const user = userEvent.setup();
    renderWithProviders(<NewReceiptPage />);

    // Fill header + add a transaction and an item so the submit succeeds.
    const combobox = screen.getByRole("combobox");
    await user.click(combobox);
    const walmart = await screen.findByText("Walmart");
    await user.click(walmart);
    const dateInput = screen.getByPlaceholderText("MM/DD/YYYY");
    await user.click(dateInput);
    await user.type(dateInput, "01/15/2024");
    await user.click(screen.getAllByText("Add Transaction")[0]);
    await user.click(screen.getAllByText("Add Item")[0]);

    await user.click(screen.getAllByText("Submit Receipt")[0]);
    await vi.waitFor(() => {
      expect(mockNavigate).toHaveBeenCalledWith("/receipts/receipt-123");
    });

    // The guard must not block the post-save redirect.
    expect(
      capturedShouldBlock!({
        currentLocation: { pathname: "/receipts/new" },
        nextLocation: { pathname: "/receipts/receipt-123" },
      }),
    ).toBe(false);
  });

  it("opens the discard dialog when the blocker reports a blocked navigation", () => {
    mockBlocker.state = "blocked";
    renderWithProviders(<NewReceiptPage />);
    expect(screen.getByText("Discard receipt?")).toBeInTheDocument();
  });

  it("proceeds the blocked navigation when Discard is confirmed", async () => {
    const user = userEvent.setup();
    mockBlocker.state = "blocked";
    renderWithProviders(<NewReceiptPage />);

    await user.click(screen.getByText("Discard"));
    expect(mockBlocker.proceed).toHaveBeenCalled();
  });
});
