import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import { mockMutationResult } from "@/test/mock-hooks";
import "@/test/setup-combobox-polyfills";
import NewReceiptPage from "./NewReceiptPage";
import { BalanceSummaryCard } from "@/components/BalanceSummaryCard";
import type { ReceiptLineItem } from "./LineItemsSection";
import type { ReceiptTransaction } from "./TransactionsSection";

const fixture = vi.hoisted(() => ({
  quantity: 0.5,
  unitPrice: 2.01,
  count: 2,
  payment: 2.02,
}));
const createReceipt = vi.hoisted(() => vi.fn());
const navigate = vi.hoisted(() => vi.fn());

vi.mock("@/hooks/usePageTitle", () => ({ usePageTitle: vi.fn() }));
vi.mock("@/hooks/useReceipts", () => ({
  useCreateCompleteReceipt: () =>
    mockMutationResult({ mutateAsync: createReceipt }),
}));
vi.mock("@/hooks/useLocationHistory", () => ({
  useLocationHistory: () => ({
    options: [{ value: "Market", label: "Market" }],
    add: vi.fn(),
  }),
}));
vi.mock("react-router", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router")>()),
  useNavigate: () => navigate,
  useBlocker: () => ({ state: "unblocked" }),
}));
vi.mock("sonner", () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

// Supply exact user-entered values at the child-input boundaries; the page,
// BalanceSidebar, sticky action bar, header validation and submission stay real.
vi.mock("./LineItemsSection", () => ({
  LineItemsSection: ({
    onChange,
  }: {
    onChange: (items: ReceiptLineItem[]) => void;
  }) => (
    <button
      onClick={() =>
        onChange(
          Array.from({ length: fixture.count }, (_, index) => ({
            id: `line-${index}`,
            receiptItemCode: "",
            description: `Fractional line ${index}`,
            quantity: fixture.quantity,
            unitPrice: fixture.unitPrice,
            category: "Food",
            subcategory: "",
          })),
        )
      }
    >
      Enter lines
    </button>
  ),
}));
vi.mock("./TransactionsSection", () => ({
  TransactionsSection: ({
    onChange,
  }: {
    onChange: (transactions: ReceiptTransaction[]) => void;
  }) => (
    <button
      onClick={() =>
        onChange([
          {
            id: "payment",
            accountId: "account",
            cardId: "card",
            amount: fixture.payment,
            date: "2024-01-15",
          },
        ])
      }
    >
      Enter payment
    </button>
  ),
}));
vi.mock("./AdjustmentsSection", () => ({ AdjustmentsSection: () => null }));

async function enterReceipt() {
  const user = userEvent.setup();
  renderWithProviders(<NewReceiptPage />);
  await user.click(screen.getByRole("combobox"));
  await user.click(await screen.findByText("Market"));
  const date = screen.getByPlaceholderText("MM/DD/YYYY");
  await user.click(date);
  await user.type(date, "01/15/2024");
  await user.click(screen.getByRole("button", { name: "Enter lines" }));
  await user.click(screen.getByRole("button", { name: "Enter payment" }));
  return user;
}

beforeEach(() => {
  vi.clearAllMocks();
  Object.assign(fixture, {
    quantity: 0.5,
    unitPrice: 2.01,
    count: 2,
    payment: 2.02,
  });
  createReceipt.mockResolvedValue({
    receipt: { id: "created" },
    items: [],
    transactions: [],
  });
});

describe("receipt calculation composition", () => {
  it.each([0, 1])(
    "rounds each fractional line before summing and permits submit button %i",
    async (buttonIndex) => {
      const user = await enterReceipt();
      expect(screen.getByText("Subtotal").nextElementSibling).toHaveTextContent(
        "$2.02",
      );
      expect(document.querySelector(".sticky.bottom-0")).toHaveTextContent(
        "Subtotal $2.02",
      );
      const submits = screen.getAllByRole("button", { name: "Submit Receipt" });
      expect(submits).toHaveLength(2);
      submits.forEach((button) => expect(button).toBeEnabled());
      await user.click(submits[buttonIndex]);
      await waitFor(() => expect(createReceipt).toHaveBeenCalledOnce());
      expect(createReceipt).toHaveBeenCalledWith(
        expect.objectContaining({
          items: [
            expect.objectContaining({ quantity: 0.5, unitPrice: 2.01 }),
            expect.objectContaining({ quantity: 0.5, unitPrice: 2.01 }),
          ],
          transactions: [expect.objectContaining({ amount: 2.02 })],
        }),
      );
      expect(navigate).toHaveBeenCalledWith("/receipts/created");
    },
  );

  it.each([
    [1.01, 0],
    [1.01, 1],
    [0.99, 0],
    [0.99, 1],
  ])(
    "accepts the server's exact one-cent boundary with payment %s through button %i",
    async (payment, buttonIndex) => {
      Object.assign(fixture, { quantity: 1, unitPrice: 1, count: 1, payment });
      const user = await enterReceipt();
      const submits = screen.getAllByRole("button", { name: "Submit Receipt" });
      submits.forEach((button) => expect(button).toBeEnabled());
      await user.click(submits[buttonIndex]);
      await waitFor(() => expect(createReceipt).toHaveBeenCalledOnce());
    },
  );

  it.each([0.98, 1.02])(
    "keeps both submits disabled beyond one cent with payment %s",
    async (payment) => {
      Object.assign(fixture, { quantity: 1, unitPrice: 1, count: 1, payment });
      const user = await enterReceipt();
      const submits = screen.getAllByRole("button", { name: "Submit Receipt" });
      for (const button of submits) {
        expect(button).toBeDisabled();
        await user.click(button);
      }
      expect(createReceipt).not.toHaveBeenCalled();
      expect(
        screen.getAllByText(payment < 1 ? "Over by $0.02" : "Remaining: $0.02"),
      ).toHaveLength(2);
    },
  );

  it("still shows a one-cent discrepancy in the persisted receipt summary", () => {
    renderWithProviders(
      <BalanceSummaryCard
        subtotal={1}
        taxAmount={0}
        adjustmentTotal={0}
        expectedTotal={1}
        transactionsTotal={1.01}
        showBalance
      />,
    );
    expect(screen.getByText("Unbalanced")).toBeInTheDocument();
    expect(screen.getByText("$1.01")).toBeInTheDocument();
  });
});
