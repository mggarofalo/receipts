import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import { mockMutationResult } from "@/test/mock-hooks";
import { YnabPushButton } from "./YnabPushButton";

const mockPushMutate = vi.fn();

vi.mock("@/hooks/useYnab", () => ({
  usePushYnabTransactions: vi.fn(() =>
    mockMutationResult({ mutate: mockPushMutate }),
  ),
}));

beforeEach(async () => {
  vi.clearAllMocks();
  const ynab = await import("@/hooks/useYnab");
  vi.mocked(ynab.usePushYnabTransactions).mockReturnValue(
    mockMutationResult({ mutate: mockPushMutate }),
  );
});

describe("YnabPushButton", () => {
  it("shows an enabled 'Push to YNAB' button and no badge when no persisted status and no mutation result", () => {
    renderWithProviders(
      <YnabPushButton receiptId="r1" hasTransactions={true} />,
    );

    const button = screen.getByRole("button", { name: /push to ynab/i });
    expect(button).toBeEnabled();
    expect(
      screen.queryByLabelText(/YNAB sync status/i),
    ).not.toBeInTheDocument();
  });

  it("disables the button when hasTransactions is false", () => {
    renderWithProviders(
      <YnabPushButton receiptId="r1" hasTransactions={false} />,
    );

    const button = screen.getByRole("button", { name: /push to ynab/i });
    expect(button).toBeDisabled();
  });

  it("calls the push mutation with the receipt id when clicked", async () => {
    const user = userEvent.setup();
    renderWithProviders(
      <YnabPushButton receiptId="r-clicked" hasTransactions={true} />,
    );

    await user.click(screen.getByRole("button", { name: /push to ynab/i }));

    expect(mockPushMutate).toHaveBeenCalledWith("r-clicked");
  });

  it("renders 'Already pushed' and disables the button when persisted status is Synced", () => {
    renderWithProviders(
      <YnabPushButton
        receiptId="r1"
        hasTransactions={true}
        persistedSyncStatus="synced"
      />,
    );

    expect(
      screen.getByRole("button", { name: /already pushed/i }),
    ).toBeDisabled();
    expect(screen.getByLabelText(/YNAB sync status: Synced/i)).toBeInTheDocument();
  });

  it("shows a 'Failed' badge but keeps the button enabled when persisted status is Failed (allows retry)", () => {
    renderWithProviders(
      <YnabPushButton
        receiptId="r1"
        hasTransactions={true}
        persistedSyncStatus="failed"
      />,
    );

    expect(screen.getByRole("button", { name: /push to ynab/i })).toBeEnabled();
    expect(screen.getByLabelText(/YNAB sync status: Failed/i)).toBeInTheDocument();
  });

  it("shows a 'Not Synced' badge and enables the button when persisted status is NotSynced", () => {
    renderWithProviders(
      <YnabPushButton
        receiptId="r1"
        hasTransactions={true}
        persistedSyncStatus="notSynced"
      />,
    );

    expect(screen.getByRole("button", { name: /push to ynab/i })).toBeEnabled();
    expect(
      screen.getByLabelText(/YNAB sync status: Not Synced/i),
    ).toBeInTheDocument();
  });

  it("a local mutation success overrides a NotSynced persisted status — button shows 'Pushed to YNAB' and is disabled", async () => {
    const ynab = await import("@/hooks/useYnab");
    vi.mocked(ynab.usePushYnabTransactions).mockReturnValue(
      mockMutationResult({
        mutate: mockPushMutate,
        data: {
          success: true,
          operationStatus: "synced",
          pushedTransactions: [
            {
              localTransactionId: "tx-1",
              ynabTransactionId: "ynab-tx-1",
              milliunits: -11000,
              subTransactionCount: 1,
            },
          ],
          error: null,
          unmappedCategories: [],
        },
      }),
    );

    renderWithProviders(
      <YnabPushButton
        receiptId="r1"
        hasTransactions={true}
        persistedSyncStatus="notSynced"
      />,
    );

    expect(
      screen.getByRole("button", { name: /pushed to ynab/i }),
    ).toBeDisabled();
    expect(screen.getByLabelText(/YNAB sync status: Synced/i)).toBeInTheDocument();
  });

  it("a local mutation failure overrides a Synced persisted status — shows Failed badge and re-enables the button for retry", async () => {
    // If the server state was Synced but this session's push mutation returned
    // a failure, we prefer the fresh (failure) result so the user knows the
    // in-flight operation failed and can retry.
    const ynab = await import("@/hooks/useYnab");
    vi.mocked(ynab.usePushYnabTransactions).mockReturnValue(
      mockMutationResult({
        mutate: mockPushMutate,
        data: {
          success: false,
          operationStatus: "failed",
          pushedTransactions: [],
          error: "YNAB 500",
          unmappedCategories: [],
        },
      }),
    );

    renderWithProviders(
      <YnabPushButton
        receiptId="r1"
        hasTransactions={true}
        persistedSyncStatus="synced"
      />,
    );

    expect(screen.getByRole("button", { name: /push to ynab/i })).toBeEnabled();
    expect(screen.getByLabelText(/YNAB sync status: Failed/i)).toBeInTheDocument();
    expect(screen.getByText(/YNAB 500/)).toBeInTheDocument();
  });

  it("shows a Pending spinner during an in-flight push mutation", async () => {
    const ynab = await import("@/hooks/useYnab");
    vi.mocked(ynab.usePushYnabTransactions).mockReturnValue(
      mockMutationResult({ mutate: mockPushMutate, isPending: true }),
    );

    renderWithProviders(
      <YnabPushButton receiptId="r1" hasTransactions={true} />,
    );

    expect(screen.getByText(/pushing to ynab/i)).toBeInTheDocument();
    expect(screen.getByRole("button")).toBeDisabled();
  });

  it("wraps push results in an aria-live polite region for screen-reader announcements", async () => {
    const ynab = await import("@/hooks/useYnab");
    vi.mocked(ynab.usePushYnabTransactions).mockReturnValue(
      mockMutationResult({
        mutate: mockPushMutate,
        data: {
          success: true,
          operationStatus: "synced",
          pushedTransactions: [
            { localTransactionId: "tx-1", ynabTransactionId: "y-1", milliunits: -5000, subTransactionCount: 1 },
          ],
          error: null,
          unmappedCategories: [],
        },
      }),
    );

    renderWithProviders(<YnabPushButton receiptId="r1" hasTransactions={true} />);

    const liveRegion = document.querySelector("[aria-live='polite']");
    expect(liveRegion).not.toBeNull();
    expect(liveRegion).toHaveAttribute("aria-atomic", "true");
    // Success message is inside the live region
    expect(liveRegion).toHaveTextContent(/transaction.*pushed/i);
  });

  it("renders error alerts with role=alert inside the live region on push failure", async () => {
    const ynab = await import("@/hooks/useYnab");
    vi.mocked(ynab.usePushYnabTransactions).mockReturnValue(
      mockMutationResult({
        mutate: mockPushMutate,
        data: {
          success: false,
          operationStatus: "failed",
          pushedTransactions: [],
          error: "Server error",
          unmappedCategories: [],
        },
      }),
    );

    renderWithProviders(<YnabPushButton receiptId="r1" hasTransactions={true} />);

    const alert = screen.getByRole("alert");
    expect(alert).toBeInTheDocument();
    expect(alert).toHaveTextContent("Server error");
    // The alert should be inside the aria-live region
    const liveRegion = document.querySelector("[aria-live='polite']");
    expect(liveRegion?.contains(alert)).toBe(true);
  });

  it.each([
    ["unknown", "Needs Review"],
    ["pending", "Pending"],
  ] as const)(
    "renders a retryable %s result as a warning instead of success or Failed",
    async (operationStatus, badgeLabel) => {
      const ynab = await import("@/hooks/useYnab");
      vi.mocked(ynab.usePushYnabTransactions).mockReturnValue(
        mockMutationResult({
          mutate: mockPushMutate,
          data: {
            success: false,
            operationStatus,
            pushedTransactions: [],
            error: "Refresh status before retrying",
            unmappedCategories: [],
          },
        }),
      );

      renderWithProviders(
        <YnabPushButton
          receiptId="r1"
          hasTransactions
          persistedSyncStatus="synced"
        />,
      );

      expect(screen.getByRole("button", { name: /push to ynab/i })).toBeEnabled();
      expect(
        screen.getByLabelText(`YNAB sync status: ${badgeLabel}`),
      ).toBeInTheDocument();
      expect(
        screen.queryByLabelText(/YNAB sync status: Failed/i),
      ).not.toBeInTheDocument();
      expect(screen.queryByText(/transaction.*pushed/i)).not.toBeInTheDocument();
      expect(screen.getByRole("alert")).toHaveTextContent(
        "Refresh status before retrying",
      );
    },
  );
});

it("blocks a receipt with transactions while its persisted sync state is unknown", async () => {
  renderWithProviders(<YnabPushButton receiptId="r-unknown" hasTransactions syncStatusUnavailable />);
  const button = screen.getByRole("button", { name: "Push to YNAB" });
  expect(button).toBeDisabled();
  expect(screen.getByText("Sync status unavailable")).toBeVisible();
  await userEvent.click(button);
  expect(mockPushMutate).not.toHaveBeenCalled();
});
