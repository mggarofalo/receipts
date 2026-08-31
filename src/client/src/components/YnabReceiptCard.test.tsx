import { screen } from "@testing-library/react";
import { renderWithProviders } from "@/test/test-utils";
import { YnabReceiptCard } from "./YnabReceiptCard";

const mockPushButton = vi.fn((_props: unknown) => (
  <div data-testid="ynab-push">Push</div>
));
const mockMemoCard = vi.fn((_props: unknown) => (
  <div data-testid="ynab-memo">Memo</div>
));
const mockSplitCard = vi.fn((_props: unknown) => (
  <div data-testid="ynab-split">Split</div>
));

vi.mock("@/components/YnabPushButton", () => ({
  YnabPushButton: (props: unknown) => mockPushButton(props),
}));

vi.mock("@/components/YnabMemoSyncCard", () => ({
  YnabMemoSyncContent: (props: unknown) => mockMemoCard(props),
}));

vi.mock("@/components/YnabSplitComparisonCard", () => ({
  YnabSplitComparisonContent: (props: unknown) => mockSplitCard(props),
}));

describe("YnabReceiptCard", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("combines all receipt-level YNAB workflows in one card", () => {
    renderWithProviders(
      <YnabReceiptCard
        receiptId="receipt-1"
        hasTransactions
        isAvailable
        persistedSyncStatus="Synced"
      />,
    );

    expect(screen.getByText("YNAB")).toBeInTheDocument();
    expect(
      screen.getByRole("heading", { name: "Transaction sync" }),
    ).toBeInTheDocument();
    expect(screen.getByTestId("ynab-push")).toBeInTheDocument();
    expect(screen.getByTestId("ynab-memo")).toBeInTheDocument();
    expect(screen.getByTestId("ynab-split")).toBeInTheDocument();
  });

  it("passes receipt state to each embedded workflow", () => {
    renderWithProviders(
      <YnabReceiptCard
        receiptId="receipt-42"
        hasTransactions={false}
        isAvailable
        persistedSyncStatus="Failed"
      />,
    );

    expect(mockPushButton).toHaveBeenCalledWith({
      receiptId: "receipt-42",
      hasTransactions: false,
      persistedSyncStatus: "Failed",
    });
    expect(mockMemoCard).toHaveBeenCalledWith({
      receiptId: "receipt-42",
      embedded: true,
    });
    expect(mockSplitCard).toHaveBeenCalledWith({
      receiptId: "receipt-42",
      embedded: true,
    });
  });

  it("renders nothing when the receipt-level integration gate is unavailable", () => {
    const { container } = renderWithProviders(
      <YnabReceiptCard
        receiptId="receipt-1"
        hasTransactions
        isAvailable={false}
      />,
    );

    expect(container.firstChild).toBeNull();
    expect(mockPushButton).not.toHaveBeenCalled();
    expect(mockMemoCard).not.toHaveBeenCalled();
    expect(mockSplitCard).not.toHaveBeenCalled();
  });
});
