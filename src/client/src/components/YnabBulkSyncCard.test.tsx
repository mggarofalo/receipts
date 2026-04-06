import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import { mockQueryResult, mockMutationResult } from "@/test/mock-hooks";
import { YnabBulkSyncCard } from "./YnabBulkSyncCard";

const mockBulkPushMutate = vi.fn();
const mockBulkMemoSyncMutate = vi.fn();

vi.mock("@/hooks/useYnab", () => ({
  useAllReceiptIds: vi.fn(() =>
    mockQueryResult({
      receiptIds: ["r1", "r2", "r3"],
      totalReceipts: 3,
      isLoading: false,
    }),
  ),
  useBulkPushYnabTransactions: vi.fn(() =>
    mockMutationResult({ mutate: mockBulkPushMutate }),
  ),
  useSyncYnabMemosBulk: vi.fn(() =>
    mockMutationResult({ mutate: mockBulkMemoSyncMutate }),
  ),
  useMemoSyncSummary: vi.fn(() => null),
}));

beforeEach(async () => {
  vi.clearAllMocks();
  // Reset mock return values to defaults to avoid leaks between tests
  const ynab = await import("@/hooks/useYnab");
  vi.mocked(ynab.useAllReceiptIds).mockReturnValue(
    mockQueryResult({
      receiptIds: ["r1", "r2", "r3"],
      totalReceipts: 3,
      isLoading: false,
    }),
  );
  vi.mocked(ynab.useBulkPushYnabTransactions).mockReturnValue(
    mockMutationResult({ mutate: mockBulkPushMutate }),
  );
  vi.mocked(ynab.useSyncYnabMemosBulk).mockReturnValue(
    mockMutationResult({ mutate: mockBulkMemoSyncMutate }),
  );
  vi.mocked(ynab.useMemoSyncSummary).mockReturnValue(null);
});

describe("YnabBulkSyncCard", () => {
  it("renders the card with title and description", () => {
    renderWithProviders(<YnabBulkSyncCard />);

    expect(screen.getByText("Bulk YNAB Sync")).toBeInTheDocument();
    expect(
      screen.getByText(/Push all receipts to YNAB or sync all transaction memos/),
    ).toBeInTheDocument();
  });

  it("shows receipt count in description", () => {
    renderWithProviders(<YnabBulkSyncCard />);

    expect(screen.getByText("(3 receipts)")).toBeInTheDocument();
  });

  it("renders both action buttons", () => {
    renderWithProviders(<YnabBulkSyncCard />);

    expect(
      screen.getByRole("button", { name: "Push All to YNAB" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Sync All Memos" }),
    ).toBeInTheDocument();
  });

  it("calls bulkPush with all receipt IDs when push button is clicked", async () => {
    const user = userEvent.setup();
    renderWithProviders(<YnabBulkSyncCard />);

    await user.click(screen.getByRole("button", { name: "Push All to YNAB" }));

    expect(mockBulkPushMutate).toHaveBeenCalledWith(["r1", "r2", "r3"]);
  });

  it("calls bulkMemoSync with all receipt IDs when memo sync button is clicked", async () => {
    const user = userEvent.setup();
    renderWithProviders(<YnabBulkSyncCard />);

    await user.click(screen.getByRole("button", { name: "Sync All Memos" }));

    expect(mockBulkMemoSyncMutate).toHaveBeenCalledWith(
      ["r1", "r2", "r3"],
      expect.any(Object),
    );
  });

  it("disables buttons when no receipts exist", async () => {
    const { useAllReceiptIds } = await import("@/hooks/useYnab");
    vi.mocked(useAllReceiptIds).mockReturnValue(
      mockQueryResult({
        receiptIds: [],
        totalReceipts: 0,
        isLoading: false,
      }),
    );

    renderWithProviders(<YnabBulkSyncCard />);

    expect(
      screen.getByRole("button", { name: "Push All to YNAB" }),
    ).toBeDisabled();
    expect(
      screen.getByRole("button", { name: "Sync All Memos" }),
    ).toBeDisabled();
    expect(
      screen.getByText("No receipts found. Create some receipts first."),
    ).toBeInTheDocument();
  });

  it("disables buttons while receipts are loading", async () => {
    const { useAllReceiptIds } = await import("@/hooks/useYnab");
    vi.mocked(useAllReceiptIds).mockReturnValue(
      mockQueryResult({
        receiptIds: [],
        totalReceipts: 0,
        isLoading: true,
      }),
    );

    renderWithProviders(<YnabBulkSyncCard />);

    expect(
      screen.getByRole("button", { name: "Push All to YNAB" }),
    ).toBeDisabled();
    expect(
      screen.getByRole("button", { name: "Sync All Memos" }),
    ).toBeDisabled();
  });

  it("disables buttons while push is pending", async () => {
    const { useBulkPushYnabTransactions } = await import("@/hooks/useYnab");
    vi.mocked(useBulkPushYnabTransactions).mockReturnValue(
      mockMutationResult({ isPending: true, mutate: mockBulkPushMutate }),
    );

    renderWithProviders(<YnabBulkSyncCard />);

    expect(
      screen.getByRole("button", { name: /Pushing to YNAB/ }),
    ).toBeDisabled();
    expect(
      screen.getByRole("button", { name: "Sync All Memos" }),
    ).toBeDisabled();
  });

  it("disables buttons while memo sync is pending", async () => {
    const { useBulkPushYnabTransactions, useSyncYnabMemosBulk } =
      await import("@/hooks/useYnab");
    vi.mocked(useBulkPushYnabTransactions).mockReturnValue(
      mockMutationResult({ mutate: mockBulkPushMutate }),
    );
    vi.mocked(useSyncYnabMemosBulk).mockReturnValue(
      mockMutationResult({ isPending: true, mutate: mockBulkMemoSyncMutate }),
    );

    renderWithProviders(<YnabBulkSyncCard />);

    expect(
      screen.getByRole("button", { name: "Push All to YNAB" }),
    ).toBeDisabled();
    expect(
      screen.getByRole("button", { name: /Syncing Memos/ }),
    ).toBeDisabled();
  });

  it("shows push result badges after successful push", async () => {
    const { useBulkPushYnabTransactions } = await import("@/hooks/useYnab");
    vi.mocked(useBulkPushYnabTransactions).mockReturnValue(
      mockMutationResult({
        mutate: mockBulkPushMutate,
        data: {
          results: [
            { receiptId: "r1", result: { success: true, pushedTransactions: [], error: null } },
            { receiptId: "r2", result: { success: true, pushedTransactions: [], error: null } },
            { receiptId: "r3", result: { success: false, pushedTransactions: [], error: "Unmapped categories" } },
          ],
        },
      }),
    );

    renderWithProviders(<YnabBulkSyncCard />);

    expect(screen.getByText("2 succeeded")).toBeInTheDocument();
    expect(screen.getByText("1 failed")).toBeInTheDocument();
  });

  it("shows memo sync summary badges", async () => {
    const { useBulkPushYnabTransactions, useMemoSyncSummary } =
      await import("@/hooks/useYnab");
    vi.mocked(useBulkPushYnabTransactions).mockReturnValue(
      mockMutationResult({ mutate: mockBulkPushMutate }),
    );
    vi.mocked(useMemoSyncSummary).mockReturnValue({
      synced: 5,
      alreadySynced: 2,
      noMatch: 1,
      ambiguous: 0,
      currencySkipped: 0,
      failed: 1,
      total: 9,
    });

    renderWithProviders(<YnabBulkSyncCard />);

    expect(screen.getByText("5 synced")).toBeInTheDocument();
    expect(screen.getByText("2 already synced")).toBeInTheDocument();
    expect(screen.getByText("1 no match")).toBeInTheDocument();
    expect(screen.getByText("1 failed")).toBeInTheDocument();
  });

  it("shows error alert when push fails", async () => {
    const { useBulkPushYnabTransactions } = await import("@/hooks/useYnab");
    vi.mocked(useBulkPushYnabTransactions).mockReturnValue(
      mockMutationResult({
        mutate: mockBulkPushMutate,
        isError: true,
      }),
    );

    renderWithProviders(<YnabBulkSyncCard />);

    expect(
      screen.getByText("Failed to push transactions to YNAB. Please try again."),
    ).toBeInTheDocument();
  });

  it("shows error alert when memo sync fails", async () => {
    const { useSyncYnabMemosBulk } = await import("@/hooks/useYnab");
    vi.mocked(useSyncYnabMemosBulk).mockReturnValue(
      mockMutationResult({
        mutate: mockBulkMemoSyncMutate,
        isError: true,
      }),
    );

    renderWithProviders(<YnabBulkSyncCard />);

    expect(
      screen.getByText("Failed to sync memos to YNAB. Please try again."),
    ).toBeInTheDocument();
  });
});
