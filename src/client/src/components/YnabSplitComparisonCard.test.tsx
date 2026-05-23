import { screen } from "@testing-library/react";
import { renderWithProviders } from "@/test/test-utils";
import { mockQueryResult } from "@/test/mock-hooks";
import { YnabSplitComparisonCard } from "./YnabSplitComparisonCard";

const configuredStatus = () =>
  mockQueryResult({ isConfigured: true, isConnected: true, isLoading: false });

vi.mock("@/hooks/useYnab", () => ({
  useYnabConnectionStatus: vi.fn(() =>
    mockQueryResult({ isConfigured: true, isConnected: true, isLoading: false }),
  ),
  useYnabSplitComparison: vi.fn(() => mockQueryResult()),
}));

beforeEach(async () => {
  vi.clearAllMocks();
  const ynab = await import("@/hooks/useYnab");
  vi.mocked(ynab.useYnabConnectionStatus).mockReturnValue(configuredStatus());
  vi.mocked(ynab.useYnabSplitComparison).mockReturnValue(mockQueryResult());
});

describe("YnabSplitComparisonCard", () => {
  it("renders nothing when YNAB is not configured", async () => {
    const ynab = await import("@/hooks/useYnab");
    vi.mocked(ynab.useYnabConnectionStatus).mockReturnValue(
      mockQueryResult({
        isConfigured: false,
        isConnected: false,
        isLoading: false,
      }),
    );

    const { container } = renderWithProviders(
      <YnabSplitComparisonCard receiptId="r1" />,
    );

    expect(container).toBeEmptyDOMElement();
    expect(
      screen.queryByText("YNAB Split Comparison"),
    ).not.toBeInTheDocument();
  });

  it("renders nothing while the connection status is still loading", async () => {
    const ynab = await import("@/hooks/useYnab");
    vi.mocked(ynab.useYnabConnectionStatus).mockReturnValue(
      mockQueryResult({
        isConfigured: false,
        isConnected: false,
        isLoading: true,
      }),
    );

    const { container } = renderWithProviders(
      <YnabSplitComparisonCard receiptId="r1" />,
    );

    expect(container).toBeEmptyDOMElement();
  });

  it("renders a loading skeleton while the query is pending", async () => {
    const ynab = await import("@/hooks/useYnab");
    vi.mocked(ynab.useYnabSplitComparison).mockReturnValue(
      mockQueryResult({ isLoading: true, isPending: true }),
    );

    renderWithProviders(<YnabSplitComparisonCard receiptId="r1" />);

    expect(screen.getByText("YNAB Split Comparison")).toBeInTheDocument();
    // Skeleton contains no specific text but should be in the card body.
    expect(
      screen
        .getByText("YNAB Split Comparison")
        .closest(".rounded-lg, [data-slot='card']") ??
        document.querySelector("[class*='skeleton']"),
    ).toBeTruthy();
  });

  it("renders an error alert when the query fails", async () => {
    const ynab = await import("@/hooks/useYnab");
    vi.mocked(ynab.useYnabSplitComparison).mockReturnValue(
      mockQueryResult({
        isLoading: false,
        isPending: false,
        isError: true,
        error: new Error("Boom"),
      }),
    );

    renderWithProviders(<YnabSplitComparisonCard receiptId="r1" />);

    expect(screen.getByText(/Could not load split comparison/)).toBeInTheDocument();
    expect(screen.getByText(/Boom/)).toBeInTheDocument();
  });

  it("renders an unavailable state with unmapped categories and a settings link", async () => {
    const ynab = await import("@/hooks/useYnab");
    vi.mocked(ynab.useYnabSplitComparison).mockReturnValue(
      mockQueryResult({
        isLoading: false,
        isPending: false,
        isSuccess: true,
        data: {
          canComputeExpected: false,
          expectedUnavailableReason: "Unmapped categories found.",
          unmappedCategories: ["Gas", "Dining"],
          transactionComparisons: [],
        },
      }),
    );

    renderWithProviders(<YnabSplitComparisonCard receiptId="r1" />);

    expect(screen.getByText(/Unmapped categories found\./)).toBeInTheDocument();
    expect(screen.getByText(/Gas, Dining/)).toBeInTheDocument();
    const link = screen.getByRole("link", { name: /YNAB Settings/ });
    expect(link).toHaveAttribute("href", "/settings/ynab");
  });

  it("renders expected-only state when the receipt has not been pushed", async () => {
    const ynab = await import("@/hooks/useYnab");
    vi.mocked(ynab.useYnabSplitComparison).mockReturnValue(
      mockQueryResult({
        isLoading: false,
        isPending: false,
        isSuccess: true,
        data: {
          canComputeExpected: true,
          expectedUnavailableReason: null,
          unmappedCategories: [],
          transactionComparisons: [
            {
              localTransactionId: "tx-1",
              accountName: "Checking",
              totalMilliunits: -11000,
              expected: [
                {
                  ynabCategoryId: "cat-1",
                  categoryName: "Groceries",
                  milliunits: -11000,
                },
              ],
              actual: null,
              actualFetchError: null,
              matches: null,
            },
          ],
        },
      }),
    );

    renderWithProviders(<YnabSplitComparisonCard receiptId="r1" />);

    expect(
      screen.getByText(/Actual will appear here after you push to YNAB/),
    ).toBeInTheDocument();
    expect(screen.getByText("Checking")).toBeInTheDocument();
    expect(screen.getByText("Groceries")).toBeInTheDocument();
    expect(screen.queryByText("Actual")).not.toBeInTheDocument();
  });

  it("renders matching pushed state without a mismatch badge", async () => {
    const ynab = await import("@/hooks/useYnab");
    vi.mocked(ynab.useYnabSplitComparison).mockReturnValue(
      mockQueryResult({
        isLoading: false,
        isPending: false,
        isSuccess: true,
        data: {
          canComputeExpected: true,
          expectedUnavailableReason: null,
          unmappedCategories: [],
          transactionComparisons: [
            {
              localTransactionId: "tx-1",
              accountName: "Checking",
              totalMilliunits: -11000,
              expected: [
                { ynabCategoryId: "cat-1", categoryName: "Groceries", milliunits: -11000 },
              ],
              actual: [
                { ynabCategoryId: "cat-1", categoryName: "Groceries", milliunits: -11000 },
              ],
              actualFetchError: null,
              matches: true,
            },
          ],
        },
      }),
    );

    renderWithProviders(<YnabSplitComparisonCard receiptId="r1" />);

    expect(screen.getByText("Checking")).toBeInTheDocument();
    expect(screen.getByText("Expected")).toBeInTheDocument();
    expect(screen.getByText("Actual")).toBeInTheDocument();
    expect(screen.queryByText(/Mismatch/)).not.toBeInTheDocument();
  });

  it("renders mismatch highlight and badge when actual differs from expected", async () => {
    const ynab = await import("@/hooks/useYnab");
    vi.mocked(ynab.useYnabSplitComparison).mockReturnValue(
      mockQueryResult({
        isLoading: false,
        isPending: false,
        isSuccess: true,
        data: {
          canComputeExpected: true,
          expectedUnavailableReason: null,
          unmappedCategories: [],
          transactionComparisons: [
            {
              localTransactionId: "tx-1",
              accountName: "Checking",
              totalMilliunits: -11000,
              expected: [
                { ynabCategoryId: "cat-1", categoryName: "Groceries", milliunits: -11000 },
              ],
              actual: [
                { ynabCategoryId: "cat-2", categoryName: "Dining", milliunits: -11000 },
              ],
              actualFetchError: null,
              matches: false,
            },
          ],
        },
      }),
    );

    renderWithProviders(<YnabSplitComparisonCard receiptId="r1" />);

    // Top-level mismatch badge + per-transaction one
    const badges = screen.getAllByText(/Mismatch/);
    expect(badges.length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText("Dining")).toBeInTheDocument();
    expect(screen.getByText("Groceries")).toBeInTheDocument();
  });

  it("renders an actual fetch error warning even when every actual fetch failed", async () => {
    // Regression for a bug where a pushed receipt whose YNAB fetches all
    // failed (actual=null, actualFetchError set) fell through to the
    // not-yet-pushed state, hiding the error and misleading the user.
    const ynab = await import("@/hooks/useYnab");
    vi.mocked(ynab.useYnabSplitComparison).mockReturnValue(
      mockQueryResult({
        isLoading: false,
        isPending: false,
        isSuccess: true,
        data: {
          canComputeExpected: true,
          expectedUnavailableReason: null,
          unmappedCategories: [],
          transactionComparisons: [
            {
              localTransactionId: "tx-1",
              accountName: "Checking",
              totalMilliunits: -11000,
              expected: [
                { ynabCategoryId: "cat-1", categoryName: "Groceries", milliunits: -11000 },
              ],
              actual: null,
              actualFetchError: "YNAB 503",
              matches: null,
            },
          ],
        },
      }),
    );

    renderWithProviders(<YnabSplitComparisonCard receiptId="r1" />);

    expect(
      screen.getByText(/Could not fetch current YNAB state: YNAB 503/),
    ).toBeInTheDocument();
    // And we must NOT show the not-yet-pushed hint
    expect(
      screen.queryByText(/Actual will appear here after you push/),
    ).not.toBeInTheDocument();
  });

  it("preserves duplicate-category split lines with different amounts", async () => {
    // Regression for a bug where buildRows keyed the merge Map on categoryId
    // alone, collapsing duplicate-category lines with different amounts into
    // a single row and losing display information for a splitter like
    // `[(groceries, -30), (groceries, -50)]`.
    const ynab = await import("@/hooks/useYnab");
    vi.mocked(ynab.useYnabSplitComparison).mockReturnValue(
      mockQueryResult({
        isLoading: false,
        isPending: false,
        isSuccess: true,
        data: {
          canComputeExpected: true,
          expectedUnavailableReason: null,
          unmappedCategories: [],
          transactionComparisons: [
            {
              localTransactionId: "tx-1",
              accountName: "Checking",
              totalMilliunits: -80000,
              expected: [
                { ynabCategoryId: "cat-groceries", categoryName: "Groceries", milliunits: -30000 },
                { ynabCategoryId: "cat-groceries", categoryName: "Groceries", milliunits: -50000 },
              ],
              actual: [
                { ynabCategoryId: "cat-groceries", categoryName: "Groceries", milliunits: -30000 },
                { ynabCategoryId: "cat-groceries", categoryName: "Groceries", milliunits: -50000 },
              ],
              actualFetchError: null,
              matches: true,
            },
          ],
        },
      }),
    );

    renderWithProviders(<YnabSplitComparisonCard receiptId="r1" />);

    // Both distinct amounts should appear in the table — and each should
    // render twice (once in Expected, once in Actual) because the merge
    // preserved both expected lines and matched each with its actual sibling.
    expect(screen.getAllByText("-$30.00")).toHaveLength(2);
    expect(screen.getAllByText("-$50.00")).toHaveLength(2);
    // And no mismatch badge, since every (category, amount) tuple matched.
    expect(screen.queryByText(/Mismatch/i)).not.toBeInTheDocument();
  });
});
