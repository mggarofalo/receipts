import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Routes, Route } from "react-router";
import { mockQueryResult, mockMutationResult } from "@/test/mock-hooks";
import ReceiptDetail from "./ReceiptDetail";

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

vi.mock("@/hooks/useTrips", () => ({
  useTripByReceiptId: vi.fn(() => ({
    data: undefined,
    isLoading: false,
    isError: false,
  })),
}));

vi.mock("@/hooks/useReceipts", () => ({
  useUpdateReceipt: vi.fn(() => ({
    mutate: vi.fn(),
    isPending: false,
  })),
}));

vi.mock("@/hooks/useEnumMetadata", () => ({
  useEnumMetadata: vi.fn(() => ({
    adjustmentTypeLabels: { Coupon: "Coupon", Discount: "Discount" },
  })),
}));

vi.mock("@/components/ChangeHistory", () => ({
  ChangeHistory: function MockChangeHistory() {
    return null;
  },
}));

vi.mock("@/components/ValidationWarnings", () => ({
  ValidationWarnings: function MockValidationWarnings() {
    return null;
  },
}));

vi.mock("@/components/BalanceSummaryCard", () => ({
  BalanceSummaryCard: function MockBalanceSummaryCard() {
    return null;
  },
}));

vi.mock("@/components/ReceiptItemsCard", () => ({
  ReceiptItemsCard: function MockReceiptItemsCard() {
    return null;
  },
}));

vi.mock("@/components/ReceiptTransactionsCard", () => ({
  ReceiptTransactionsCard: function MockReceiptTransactionsCard(props: {
    transactions: unknown[];
  }) {
    return (
      <div data-testid="receipt-transactions-card">
        Transactions ({props.transactions.length})
      </div>
    );
  },
}));

vi.mock("@/components/YnabPushButton", () => ({
  YnabPushButton: function MockYnabPushButton() {
    return <div data-testid="ynab-push-button">Push to YNAB</div>;
  },
}));

vi.mock("@/components/ReceiptHeaderForm", () => ({
  ReceiptHeaderForm: function MockReceiptHeaderForm() {
    return <div data-testid="receipt-header-form">Receipt Header Form</div>;
  },
}));

vi.mock("@/components/YnabMemoSyncCard", () => ({
  YnabMemoSyncCard: function MockYnabMemoSyncCard() {
    return null;
  },
}));

function renderWithRoutes(initialRoute: string) {
  return render(
    <MemoryRouter initialEntries={[initialRoute]}>
      <Routes>
        <Route path="/receipts/:id" element={<ReceiptDetail />} />
        <Route path="/receipts" element={<div data-testid="receipts-page">Receipts</div>} />
      </Routes>
    </MemoryRouter>,
  );
}

const MOCK_TRIP = {
  receipt: {
    receipt: {
      id: "r1",
      location: "Walmart",
      date: "2024-01-15",
      taxAmount: 5.25,
    },
    items: [],
    adjustments: [],
    subtotal: 50.0,
    adjustmentTotal: 0,
    expectedTotal: 55.25,
    warnings: [],
  },
  transactions: [],
  warnings: [],
};

describe("ReceiptDetail", () => {
  it("renders the page heading when id is present", () => {
    renderWithRoutes("/receipts/some-uuid");
    expect(
      screen.getByRole("heading", { name: /receipt details/i }),
    ).toBeInTheDocument();
  });

  it("calls useTripByReceiptId with the path param", async () => {
    const { useTripByReceiptId } = await import("@/hooks/useTrips");
    const mockHook = vi.mocked(useTripByReceiptId);
    mockHook.mockReturnValue(
      mockQueryResult({
        data: undefined,
        isLoading: false,
        isError: false,
      }),
    );

    renderWithRoutes("/receipts/some-uuid");

    expect(mockHook).toHaveBeenCalledWith("some-uuid");
  });

  it("renders loading skeleton with accessible status when data is loading", async () => {
    const { useTripByReceiptId } = await import("@/hooks/useTrips");
    vi.mocked(useTripByReceiptId).mockReturnValue(
      mockQueryResult({
        data: undefined,
        isLoading: true,
        isError: false,
      }),
    );

    const { container } = renderWithRoutes("/receipts/some-uuid");
    expect(
      container.querySelector("[data-slot='skeleton']"),
    ).toBeInTheDocument();
    expect(screen.getByRole("status")).toBeInTheDocument();
    expect(screen.getByText(/loading receipt details/i)).toBeInTheDocument();
  });

  it("renders receipt data when loaded", async () => {
    const { useTripByReceiptId } = await import("@/hooks/useTrips");
    vi.mocked(useTripByReceiptId).mockReturnValue(
      mockQueryResult({
        data: MOCK_TRIP,
        isLoading: false,
        isError: false,
      }),
    );

    renderWithRoutes("/receipts/r1");
    expect(screen.getByText(/walmart/i)).toBeInTheDocument();
    expect(screen.getByText(/2024-01-15/)).toBeInTheDocument();
  });

  it("renders error state with alert role when receipt is not found", async () => {
    const { useTripByReceiptId } = await import("@/hooks/useTrips");
    vi.mocked(useTripByReceiptId).mockReturnValue(
      mockQueryResult({
        data: undefined,
        isLoading: false,
        isError: true,
      }),
    );

    renderWithRoutes("/receipts/bad-id");
    expect(screen.getByRole("alert")).toHaveTextContent(
      /no receipt found for this id/i,
    );
  });

  it("renders transactions card when trip has transactions", async () => {
    const { useTripByReceiptId } = await import("@/hooks/useTrips");
    vi.mocked(useTripByReceiptId).mockReturnValue(
      mockQueryResult({
        data: {
          ...MOCK_TRIP,
          transactions: [
            {
              transaction: { id: "t1", amount: 55.25, date: "2024-01-15" },
              account: {
                accountCode: "1234",
                name: "Checking",
                isActive: true,
              },
            },
          ],
        },
        isLoading: false,
        isError: false,
      }),
    );

    renderWithRoutes("/receipts/r1");
    expect(screen.getByTestId("receipt-transactions-card")).toBeInTheDocument();
    expect(screen.getByText("Transactions (1)")).toBeInTheDocument();
  });

  it("renders adjustments section when trip has adjustments", async () => {
    const { useTripByReceiptId } = await import("@/hooks/useTrips");
    vi.mocked(useTripByReceiptId).mockReturnValue(
      mockQueryResult({
        data: {
          ...MOCK_TRIP,
          receipt: {
            ...MOCK_TRIP.receipt,
            adjustments: [
              {
                id: "a1",
                type: "Coupon",
                description: "10% off",
                amount: -5.0,
              },
            ],
            adjustmentTotal: -5.0,
          },
        },
        isLoading: false,
        isError: false,
      }),
    );

    renderWithRoutes("/receipts/r1");
    expect(screen.getByText("Adjustments (1)")).toBeInTheDocument();
    expect(screen.getByText("Coupon")).toBeInTheDocument();
    expect(screen.getByText("10% off")).toBeInTheDocument();
  });

  it("renders transactions card when trip has no transactions", async () => {
    const { useTripByReceiptId } = await import("@/hooks/useTrips");
    vi.mocked(useTripByReceiptId).mockReturnValue(
      mockQueryResult({
        data: MOCK_TRIP,
        isLoading: false,
        isError: false,
      }),
    );

    renderWithRoutes("/receipts/r1");
    expect(screen.getByTestId("receipt-transactions-card")).toBeInTheDocument();
    expect(screen.getByText("Transactions (0)")).toBeInTheDocument();
  });

  it("renders empty adjustments message when no adjustments", async () => {
    const { useTripByReceiptId } = await import("@/hooks/useTrips");
    vi.mocked(useTripByReceiptId).mockReturnValue(
      mockQueryResult({
        data: MOCK_TRIP,
        isLoading: false,
        isError: false,
      }),
    );

    renderWithRoutes("/receipts/r1");
    expect(
      screen.getByText(/no adjustments for this receipt/i),
    ).toBeInTheDocument();
  });

  it("renders edit receipt button when data is loaded", async () => {
    const { useTripByReceiptId } = await import("@/hooks/useTrips");
    vi.mocked(useTripByReceiptId).mockReturnValue(
      mockQueryResult({
        data: MOCK_TRIP,
        isLoading: false,
        isError: false,
      }),
    );

    renderWithRoutes("/receipts/r1");
    expect(
      screen.getByRole("button", { name: /edit receipt/i }),
    ).toBeInTheDocument();
  });

  it("opens edit dialog when edit button is clicked", async () => {
    const user = userEvent.setup();
    const { useTripByReceiptId } = await import("@/hooks/useTrips");
    vi.mocked(useTripByReceiptId).mockReturnValue(
      mockQueryResult({
        data: MOCK_TRIP,
        isLoading: false,
        isError: false,
      }),
    );

    const { useUpdateReceipt } = await import("@/hooks/useReceipts");
    vi.mocked(useUpdateReceipt).mockReturnValue(
      mockMutationResult({ mutate: vi.fn(), isPending: false }),
    );

    renderWithRoutes("/receipts/r1");
    await user.click(screen.getByRole("button", { name: /edit receipt/i }));
    expect(screen.getByText("Edit Receipt")).toBeInTheDocument();
    expect(screen.getByTestId("receipt-header-form")).toBeInTheDocument();
  });

  it("calls useUpdateReceipt hook", async () => {
    const { useTripByReceiptId } = await import("@/hooks/useTrips");
    vi.mocked(useTripByReceiptId).mockReturnValue(
      mockQueryResult({
        data: MOCK_TRIP,
        isLoading: false,
        isError: false,
      }),
    );

    const { useUpdateReceipt } = await import("@/hooks/useReceipts");
    const mockMutation = mockMutationResult({
      mutate: vi.fn(),
      isPending: false,
    });
    vi.mocked(useUpdateReceipt).mockReturnValue(mockMutation);

    renderWithRoutes("/receipts/r1");
    expect(useUpdateReceipt).toHaveBeenCalled();
  });
});
