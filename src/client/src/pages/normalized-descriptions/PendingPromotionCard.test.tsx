import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithQueryClient } from "@/test/test-utils";
import { mockMutationResult, mockQueryResult } from "@/test/mock-hooks";
import { PendingPromotionCard } from "./PendingPromotionCard";

vi.mock("@/hooks/usePendingNamePromotion", () => ({
  usePendingNamePromotionPreview: vi.fn(),
  usePendingNamePromotions: vi.fn(),
  usePromotePendingNamesMutation: vi.fn(),
  useUndoPendingNamePromotionMutation: vi.fn(),
}));

import {
  usePendingNamePromotionPreview,
  usePendingNamePromotions,
  usePromotePendingNamesMutation,
  useUndoPendingNamePromotionMutation,
} from "@/hooks/usePendingNamePromotion";

const preview = {
  minimumPurchases: 2,
  nameCount: 2,
  purchaseCount: 9,
  fingerprint: "digest",
  names: [
    { id: "n-1", canonicalName: "Oat milk", purchaseCount: 7 },
    { id: "n-2", canonicalName: "Rye bread", purchaseCount: 2 },
  ],
};

function loaded<T>(data: T) {
  return mockQueryResult({
    data,
    isLoading: false,
    isSuccess: true,
    isPending: false,
    status: "success",
  });
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(usePendingNamePromotionPreview).mockReturnValue(loaded(preview));
  vi.mocked(usePendingNamePromotions).mockReturnValue(loaded([]));
  vi.mocked(usePromotePendingNamesMutation).mockReturnValue(
    mockMutationResult(),
  );
  vi.mocked(useUndoPendingNamePromotionMutation).mockReturnValue(
    mockMutationResult(),
  );
});

describe("PendingPromotionCard", () => {
  it("shows a skeleton while the preview loads", () => {
    vi.mocked(usePendingNamePromotionPreview).mockReturnValue(
      mockQueryResult({ isLoading: true, isPending: true, status: "pending" }),
    );

    const { container } = renderWithQueryClient(<PendingPromotionCard />);

    expect(container.querySelector('[data-slot="skeleton"]')).not.toBeNull();
    expect(screen.queryByRole("button", { name: /promote/i })).toBeNull();
  });

  it("reports a failed preview", () => {
    vi.mocked(usePendingNamePromotionPreview).mockReturnValue(
      mockQueryResult({ isError: true, isLoading: false, status: "error" }),
    );

    renderWithQueryClient(<PendingPromotionCard />);

    expect(
      screen.getByText("Failed to load names to promote."),
    ).toBeInTheDocument();
  });

  it("shows the eligible names, their purchases and the rule", () => {
    renderWithQueryClient(<PendingPromotionCard />);

    expect(screen.getByText(/at least 2 purchases/)).toBeInTheDocument();
    const names = screen.getByTestId("promotion-names");
    expect(within(names).getByText("Oat milk")).toBeInTheDocument();
    expect(within(names).getByText("7 purchases")).toBeInTheDocument();
    expect(within(names).getByText("2 purchases")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Promote 2 names" }),
    ).toBeEnabled();
  });

  it("disables promotion when nothing qualifies", () => {
    vi.mocked(usePendingNamePromotionPreview).mockReturnValue(
      loaded({ ...preview, nameCount: 0, purchaseCount: 0, names: [] }),
    );

    renderWithQueryClient(<PendingPromotionCard />);

    expect(screen.getByTestId("promotion-empty")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Promote 0 names" }),
    ).toBeDisabled();
  });

  it("confirms before promoting the previewed set", async () => {
    const mutate = vi.fn();
    vi.mocked(usePromotePendingNamesMutation).mockReturnValue(
      mockMutationResult({ mutate }),
    );
    const user = userEvent.setup();
    renderWithQueryClient(<PendingPromotionCard />);

    await user.click(screen.getByRole("button", { name: "Promote 2 names" }));
    const dialog = screen.getByRole("dialog");
    expect(
      within(dialog).getByText(/covering 9 purchases become active/),
    ).toBeInTheDocument();
    expect(mutate).not.toHaveBeenCalled();
    await user.click(within(dialog).getByRole("button", { name: "Promote" }));

    expect(mutate).toHaveBeenCalledWith(
      { expectedFingerprint: "digest" },
      expect.objectContaining({ onSettled: expect.any(Function) }),
    );
  });

  it("lists recent promotions and undoes one", async () => {
    const mutate = vi.fn();
    vi.mocked(useUndoPendingNamePromotionMutation).mockReturnValue(
      mockMutationResult({ mutate }),
    );
    vi.mocked(usePendingNamePromotions).mockReturnValue(
      loaded([
        {
          id: "p-2",
          createdBy: "admin",
          createdAt: "2026-10-31T13:00:00Z",
          promotedCount: 3,
          purchaseCount: 12,
          restoredCount: null,
          keptCount: null,
        },
        {
          id: "p-1",
          createdBy: "admin",
          createdAt: "2026-10-31T12:00:00Z",
          promotedCount: 4,
          purchaseCount: 20,
          undoneAt: "2026-10-31T12:30:00Z",
          undoneBy: "admin",
          restoredCount: 3,
          keptCount: 1,
        },
      ]),
    );
    const user = userEvent.setup();
    renderWithQueryClient(<PendingPromotionCard />);

    const history = screen.getByTestId("promotion-history");
    expect(
      within(history).getByText("Undone: 3 returned to review, 1 kept"),
    ).toBeInTheDocument();
    const undoButtons = within(history).getAllByRole("button", {
      name: "Undo",
    });
    expect(undoButtons).toHaveLength(1);
    await user.click(undoButtons[0]);

    expect(mutate).toHaveBeenCalledWith({ id: "p-2" });
  });
});
