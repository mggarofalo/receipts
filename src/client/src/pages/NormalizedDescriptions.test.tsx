import { screen, within, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithQueryClient } from "@/test/test-utils";
import { mockMutationResult, mockQueryResult } from "@/test/mock-hooks";
import NormalizedDescriptions from "./NormalizedDescriptions";

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

vi.mock("@/hooks/useNormalizedDescriptions", () => ({
  useNormalizedDescriptions: vi.fn(),
  useNormalizedDescription: vi.fn(),
}));

vi.mock("@/hooks/useNormalizedDescriptionActions", () => ({
  useMergeMutation: vi.fn(() => mockMutationResult()),
  useSplitMutation: vi.fn(() => mockMutationResult()),
  useUpdateStatusMutation: vi.fn(() => mockMutationResult()),
}));

vi.mock("@/hooks/useNormalizedDescriptionSettings", () => ({
  useSettings: vi.fn(),
  useUpdateSettingsMutation: vi.fn(() => mockMutationResult()),
  useTestMatchMutation: vi.fn(() => mockMutationResult()),
  usePreviewImpactMutation: vi.fn(() => mockMutationResult()),
}));

vi.mock("@/hooks/useNormalizedDescriptionMaintenance", () => ({
  useRequeuePendingPreview: vi.fn(),
  useRequeuePendingMutation: vi.fn(() => mockMutationResult()),
}));

vi.mock("@/hooks/useReceiptItems", () => ({
  useReceiptItems: vi.fn(() => ({
    data: [],
    total: 0,
    isLoading: false,
  })),
}));

vi.mock("@/hooks/usePermission", () => ({
  usePermission: vi.fn(() => ({
    roles: ["Admin"],
    hasRole: (role: string) => role === "Admin",
    isAdmin: () => true,
  })),
}));

import { useNormalizedDescriptions } from "@/hooks/useNormalizedDescriptions";
import {
  useMergeMutation,
  useSplitMutation,
  useUpdateStatusMutation,
} from "@/hooks/useNormalizedDescriptionActions";
import {
  useSettings,
  useUpdateSettingsMutation,
  useTestMatchMutation,
  usePreviewImpactMutation,
} from "@/hooks/useNormalizedDescriptionSettings";
import {
  useRequeuePendingPreview,
  useRequeuePendingMutation,
} from "@/hooks/useNormalizedDescriptionMaintenance";
import { useReceiptItems } from "@/hooks/useReceiptItems";
import { usePermission } from "@/hooks/usePermission";

// Fixtures carry the RECEIPTS-873 evidence fields because the real API always does. p-1 is the
// fully-evidenced case; p-2 is the row that was never compared against anything, which is what
// distinguishes "no comparison recorded" from a zero score.
const pendingItems = [
  {
    id: "p-1",
    canonicalName: "Strawberry Preserves",
    status: "pendingReview" as const,
    createdAt: "2025-03-01T00:00:00Z",
    linkedItemCount: 4,
    sampleRawDescriptions: ["STRAWBERRY PRES", "STRWBRY PRESERVE"],
    nearestNeighbourName: "Strawberry Jam",
    nearestNeighbourSimilarity: 0.8642,
  },
  {
    id: "p-2",
    canonicalName: "Organic Milk",
    status: "pendingReview" as const,
    createdAt: "2025-03-02T00:00:00Z",
    linkedItemCount: 0,
    sampleRawDescriptions: [],
    nearestNeighbourName: null,
    nearestNeighbourSimilarity: null,
  },
];

const activeItems = [
  {
    id: "a-1",
    canonicalName: "Apples",
    status: "active" as const,
    createdAt: "2025-02-01T00:00:00Z",
    linkedItemCount: 12,
    sampleRawDescriptions: ["GALA APPLES"],
    nearestNeighbourName: null,
    nearestNeighbourSimilarity: null,
  },
  {
    id: "a-2",
    canonicalName: "Milk",
    status: "active" as const,
    createdAt: "2025-02-02T00:00:00Z",
    linkedItemCount: 9,
    sampleRawDescriptions: ["MILK 2% GAL"],
    nearestNeighbourName: null,
    nearestNeighbourSimilarity: null,
  },
];

function mockList(status: string | undefined) {
  if (status === "PendingReview") {
    return mockQueryResult({
      data: { items: pendingItems, totalCount: pendingItems.length },
      isLoading: false,
      isSuccess: true,
      isPending: false,
      status: "success",
    });
  }
  if (status === "Active") {
    return mockQueryResult({
      data: { items: activeItems, totalCount: activeItems.length },
      isLoading: false,
      isSuccess: true,
      isPending: false,
      status: "success",
    });
  }
  return mockQueryResult({
    data: { items: [], totalCount: 0 },
    isLoading: false,
    isSuccess: true,
    isPending: false,
    status: "success",
  });
}

const requeuePreview = {
  pendingDescriptionCount: 4,
  linkedItemCount: 120,
  staleMatchScoreCount: 118,
  estimatedResolverCycles: 3,
  estimatedCatchUpSeconds: 90,
};

const liveSettings = {
  id: "00000000-0000-0000-0000-000000000001",
  autoAcceptThreshold: 0.9,
  pendingReviewThreshold: 0.75,
  updatedAt: "2025-01-01T00:00:00Z",
};

function wireDefaults() {
  vi.mocked(useNormalizedDescriptions).mockImplementation((status) =>
    mockList(status),
  );
  vi.mocked(useSettings).mockReturnValue(
    mockQueryResult({
      data: liveSettings,
      isLoading: false,
      isSuccess: true,
      isPending: false,
      status: "success",
    }),
  );
  vi.mocked(useMergeMutation).mockReturnValue(mockMutationResult());
  vi.mocked(useSplitMutation).mockReturnValue(mockMutationResult());
  vi.mocked(useUpdateStatusMutation).mockReturnValue(mockMutationResult());
  vi.mocked(useUpdateSettingsMutation).mockReturnValue(mockMutationResult());
  vi.mocked(useTestMatchMutation).mockReturnValue(mockMutationResult());
  vi.mocked(usePreviewImpactMutation).mockReturnValue(mockMutationResult());
  vi.mocked(useRequeuePendingPreview).mockReturnValue(
    mockQueryResult({
      data: requeuePreview,
      isLoading: false,
      isSuccess: true,
      isPending: false,
      status: "success",
    }),
  );
  vi.mocked(useRequeuePendingMutation).mockReturnValue(mockMutationResult());
  vi.mocked(useReceiptItems).mockReturnValue({
    data: [],
    total: 0,
    isLoading: false,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
  } as any);
  vi.mocked(usePermission).mockReturnValue({
    roles: ["Admin"],
    hasRole: (role: string) => role === "Admin",
    isAdmin: () => true,
  });
}

describe("NormalizedDescriptions review queue", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    wireDefaults();
  });

  it("renders the page heading", () => {
    renderWithQueryClient(<NormalizedDescriptions />);
    expect(
      screen.getByRole("heading", { name: /normalized descriptions/i }),
    ).toBeInTheDocument();
  });

  it("renders pending-review rows by default", async () => {
    renderWithQueryClient(<NormalizedDescriptions />);
    expect(
      await screen.findByText("Strawberry Preserves"),
    ).toBeInTheDocument();
    expect(screen.getByText("Organic Milk")).toBeInTheDocument();
  });

  it("shows the near-miss that caused the row to be flagged", async () => {
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Strawberry Preserves")).closest("tr");
    expect(row).not.toBeNull();
    expect(within(row!).getByText(/nearly matched/i)).toBeInTheDocument();
    expect(within(row!).getByText("Strawberry Jam")).toBeInTheDocument();
    expect(within(row!).getByText("0.86")).toBeInTheDocument();
  });

  it("renders 'no comparison recorded' instead of a zero score when no neighbour was recorded", async () => {
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Organic Milk")).closest("tr");
    expect(row).not.toBeNull();
    expect(
      within(row!).getByText(/no comparison recorded/i),
    ).toBeInTheDocument();
    // The distinction is the whole point: an absent comparison must never read as a real
    // near-miss that scored zero.
    expect(within(row!).queryByText("0.00")).not.toBeInTheDocument();
    expect(within(row!).queryByText(/nearly matched/i)).not.toBeInTheDocument();
  });

  it("shows how many receipt items each pending row would affect", async () => {
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Strawberry Preserves")).closest("tr");
    expect(within(row!).getByText("4")).toBeInTheDocument();
  });

  it("shows sample raw descriptions so the reviewer sees what the row covers", async () => {
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Strawberry Preserves")).closest("tr");
    expect(
      within(row!).getByText(/STRAWBERRY PRES, STRWBRY PRESERVE/),
    ).toBeInTheDocument();
  });

  it("omits the samples line entirely when no items are linked", async () => {
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Organic Milk")).closest("tr");
    expect(within(row!).queryByText(/seen as:/i)).not.toBeInTheDocument();
  });

  it("shows empty state when queue is empty", () => {
    vi.mocked(useNormalizedDescriptions).mockImplementation((status) => {
      if (status === "PendingReview") {
        return mockQueryResult({
          data: { items: [], totalCount: 0 },
          isLoading: false,
          isSuccess: true,
          isPending: false,
          status: "success",
        });
      }
      return mockList(status);
    });
    renderWithQueryClient(<NormalizedDescriptions />);
    expect(screen.getByText("Review Queue Empty")).toBeInTheDocument();
  });

  it("approve button calls status update mutation", async () => {
    const mutate = vi.fn();
    vi.mocked(useUpdateStatusMutation).mockReturnValue(
      mockMutationResult({ mutate }),
    );

    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Strawberry Preserves")).closest(
      "tr",
    )!;
    const approveBtn = within(row).getByRole("button", { name: "Approve" });
    await user.click(approveBtn);
    expect(mutate).toHaveBeenCalledWith({ id: "p-1", status: "active" });
  });

  it("opens merge dialog when Merge is clicked", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Strawberry Preserves")).closest(
      "tr",
    )!;
    await user.click(within(row).getByRole("button", { name: "Merge" }));
    expect(screen.getByText("Merge Into Active Entry")).toBeInTheDocument();
    expect(screen.getByText("Apples")).toBeInTheDocument();
    expect(screen.getByText("Milk")).toBeInTheDocument();
  });

  it("confirm merge calls mutation with discard and target ids", async () => {
    const mutate = vi.fn((_vars, opts?: { onSuccess?: () => void }) => {
      opts?.onSuccess?.();
    });
    vi.mocked(useMergeMutation).mockReturnValue(
      mockMutationResult({ mutate }),
    );

    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Strawberry Preserves")).closest(
      "tr",
    )!;
    await user.click(within(row).getByRole("button", { name: "Merge" }));

    // Select target
    const dialog = screen.getByRole("dialog");
    const appleLabel = within(dialog).getByText("Apples").closest("label")!;
    const appleRadio = within(appleLabel).getByRole("radio");
    await user.click(appleRadio);
    await user.click(within(dialog).getByRole("button", { name: "Merge" }));

    expect(mutate).toHaveBeenCalledWith(
      { id: "a-1", discardId: "p-1" },
      expect.any(Object),
    );
  });

  it("opens split dialog and confirms with receipt item id", async () => {
    const mutate = vi.fn((_vars, opts?: { onSuccess?: () => void }) => {
      opts?.onSuccess?.();
    });
    vi.mocked(useSplitMutation).mockReturnValue(
      mockMutationResult({ mutate }),
    );
    vi.mocked(useReceiptItems).mockReturnValue({
      data: [
        {
          id: "ri-1",
          description: "STRAWBERRY PRESERVES",
          normalizedDescriptionId: "p-1",
          normalizedDescriptionName: "Strawberry Preserves",
        },
        {
          id: "ri-2",
          description: "OTHER",
          normalizedDescriptionId: "different",
        },
      ],
      total: 2,
      isLoading: false,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Strawberry Preserves")).closest(
      "tr",
    )!;
    await user.click(within(row).getByRole("button", { name: "Split" }));

    const dialog = screen.getByRole("dialog");
    expect(
      within(dialog).getByText("Split Out a Receipt Item"),
    ).toBeInTheDocument();
    const label = within(dialog)
      .getByText("STRAWBERRY PRESERVES")
      .closest("label")!;
    const radio = within(label).getByRole("radio");
    await user.click(radio);
    await user.click(within(dialog).getByRole("button", { name: "Split" }));

    expect(mutate).toHaveBeenCalledWith(
      { id: "p-1", receiptItemId: "ri-1" },
      expect.any(Object),
    );
  });
});

describe("NormalizedDescriptions registry tab", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    wireDefaults();
  });

  it("shows active entries when registry tab is selected", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Registry" }));
    expect(await screen.findByText("Apples")).toBeInTheDocument();
    expect(screen.getByText("Milk")).toBeInTheDocument();
  });

  it("filters by search box", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Registry" }));
    const input = await screen.findByLabelText("Search");
    await user.type(input, "apple");
    await waitFor(() => {
      expect(screen.getByText("Apples")).toBeInTheDocument();
      expect(screen.queryByText("Milk")).not.toBeInTheDocument();
    });
  });
});

describe("NormalizedDescriptions settings tab", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    wireDefaults();
  });

  it("renders settings tab for admins and hydrates from live settings", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Settings" }));
    const autoInput = (await screen.findByLabelText(
      "Auto-Accept Threshold",
    )) as HTMLInputElement;
    const pendingInput = screen.getByLabelText(
      "Pending-Review Threshold",
    ) as HTMLInputElement;
    await waitFor(() => expect(autoInput.value).toBe("0.9"));
    expect(pendingInput.value).toBe("0.75");
  });

  it("hides settings tab for non-admins", () => {
    vi.mocked(usePermission).mockReturnValue({
      roles: ["User"],
      hasRole: (role: string) => role === "User",
      isAdmin: () => false,
    });
    renderWithQueryClient(<NormalizedDescriptions />);
    expect(
      screen.queryByRole("tab", { name: "Settings" }),
    ).not.toBeInTheDocument();
  });

  it("save triggers update mutation with parsed thresholds", async () => {
    const mutate = vi.fn();
    vi.mocked(useUpdateSettingsMutation).mockReturnValue(
      mockMutationResult({ mutate }),
    );
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Settings" }));
    const autoInput = (await screen.findByLabelText(
      "Auto-Accept Threshold",
    )) as HTMLInputElement;
    await waitFor(() => expect(autoInput.value).toBe("0.9"));
    await user.click(screen.getByRole("button", { name: "Save" }));
    expect(mutate).toHaveBeenCalledWith({
      autoAcceptThreshold: 0.9,
      pendingReviewThreshold: 0.75,
    });
  });

  it("shows validation error when pending >= auto", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Settings" }));
    const autoInput = (await screen.findByLabelText(
      "Auto-Accept Threshold",
    )) as HTMLInputElement;
    const pendingInput = (await screen.findByLabelText(
      "Pending-Review Threshold",
    )) as HTMLInputElement;
    await waitFor(() => expect(autoInput.value).toBe("0.9"));

    await user.clear(autoInput);
    await user.type(autoInput, "0.5");
    await user.clear(pendingInput);
    await user.type(pendingInput, "0.8");

    expect(
      await screen.findByTestId("threshold-validation-error"),
    ).toHaveTextContent(/strictly less than the auto-accept threshold/i);
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
  });

  it("shows validation error when a value is out of range", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Settings" }));
    const autoInput = (await screen.findByLabelText(
      "Auto-Accept Threshold",
    )) as HTMLInputElement;
    await waitFor(() => expect(autoInput.value).toBe("0.9"));

    await user.clear(autoInput);
    await user.type(autoInput, "2");

    expect(
      await screen.findByTestId("threshold-validation-error"),
    ).toHaveTextContent(/between 0 and 1/i);
  });

  it("preview impact shows panel with deltas", async () => {
    vi.mocked(usePreviewImpactMutation).mockReturnValue(
      mockMutationResult({
        data: {
          current: { autoAccepted: 5, pendingReview: 2, unresolved: 1 },
          proposed: { autoAccepted: 6, pendingReview: 1, unresolved: 1 },
          deltas: {
            autoToPending: 0,
            pendingToAuto: 1,
            unresolvedToAuto: 0,
            unresolvedToPending: 0,
          },
        },
        isSuccess: true,
      }),
    );
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Settings" }));
    expect(
      await screen.findByTestId("preview-impact-panel"),
    ).toBeInTheDocument();
    expect(screen.getByText(/pending-to-auto: 1/i)).toBeInTheDocument();
  });

  it("preview impact button calls mutation with current edited thresholds", async () => {
    const mutate = vi.fn();
    vi.mocked(usePreviewImpactMutation).mockReturnValue(
      mockMutationResult({ mutate }),
    );
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Settings" }));
    const autoInput = (await screen.findByLabelText(
      "Auto-Accept Threshold",
    )) as HTMLInputElement;
    await waitFor(() => expect(autoInput.value).toBe("0.9"));
    await user.click(
      screen.getByRole("button", { name: /preview impact/i }),
    );
    expect(mutate).toHaveBeenCalledWith({
      autoAcceptThreshold: 0.9,
      pendingReviewThreshold: 0.75,
    });
  });

  it("test description box renders candidates and outcome", async () => {
    const mutate = vi.fn();
    vi.mocked(useTestMatchMutation).mockReturnValue(
      mockMutationResult({
        mutate,
        data: {
          candidates: [
            {
              normalizedDescriptionId: "a-1",
              canonicalName: "Apples",
              cosineSimilarity: 0.87,
              status: "Active",
            },
          ],
          simulatedOutcome: "AutoAccept",
        },
        isSuccess: true,
      }),
    );
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Settings" }));

    const testInput = await screen.findByLabelText("Description");
    await user.type(testInput, "apples");
    await user.click(screen.getByRole("button", { name: "Test" }));

    expect(mutate).toHaveBeenCalledWith(
      expect.objectContaining({ description: "apples", topN: 5 }),
    );

    const panel = await screen.findByTestId("test-match-panel");
    expect(within(panel).getByText("AutoAccept")).toBeInTheDocument();
    expect(within(panel).getByText("Apples")).toBeInTheDocument();
    expect(within(panel).getByText("0.8700")).toBeInTheDocument();
  });
});

describe("NormalizedDescriptions — Maintenance tab (RECEIPTS-883)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    wireDefaults();
  });

  it("shows the blast radius and the resolver catch-up estimate", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Maintenance" }));

    const panel = await screen.findByTestId("requeue-preview-panel");
    expect(within(panel).getByText("4")).toBeInTheDocument();
    expect(within(panel).getByText("120")).toBeInTheDocument();
    expect(within(panel).getByText("118")).toBeInTheDocument();
    // 90s renders as minutes-and-seconds so the operator isn't reading raw seconds.
    expect(await screen.findByText("1m 30s")).toBeInTheDocument();
  });

  it("requires confirmation and posts the previewed count", async () => {
    const mutate = vi.fn();
    vi.mocked(useRequeuePendingMutation).mockReturnValue(
      mockMutationResult({ mutate }),
    );
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Maintenance" }));

    await user.click(
      screen.getByRole("button", { name: "Requeue 4 pending descriptions" }),
    );

    // Nothing may be destroyed on the strength of one click on a destructive action.
    expect(mutate).not.toHaveBeenCalled();
    const dialog = await screen.findByRole("dialog");
    expect(
      within(dialog).getByText(/cannot be undone/i),
    ).toBeInTheDocument();

    await user.click(within(dialog).getByRole("button", { name: "Requeue" }));

    // The previewed count rides along so the server can reject a stale confirmation.
    expect(mutate).toHaveBeenCalledWith(
      { expectedPendingCount: 4 },
      expect.anything(),
    );
  });

  it("cancelling the dialog destroys nothing", async () => {
    const mutate = vi.fn();
    vi.mocked(useRequeuePendingMutation).mockReturnValue(
      mockMutationResult({ mutate }),
    );
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Maintenance" }));
    await user.click(
      screen.getByRole("button", { name: "Requeue 4 pending descriptions" }),
    );

    const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getByRole("button", { name: "Cancel" }));

    await waitFor(() =>
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument(),
    );
    expect(mutate).not.toHaveBeenCalled();
  });

  it("disables the action when there is nothing pending", async () => {
    vi.mocked(useRequeuePendingPreview).mockReturnValue(
      mockQueryResult({
        data: {
          pendingDescriptionCount: 0,
          linkedItemCount: 0,
          staleMatchScoreCount: 0,
          estimatedResolverCycles: 0,
          estimatedCatchUpSeconds: 0,
        },
        isLoading: false,
        isSuccess: true,
        isPending: false,
        status: "success",
      }),
    );
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Maintenance" }));

    expect(await screen.findByTestId("requeue-empty")).toBeInTheDocument();
    expect(screen.queryByTestId("requeue-preview-panel")).not.toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: /Requeue 0 pending/ }),
    ).toBeDisabled();
  });

  it("surfaces a failed preview instead of implying nothing needs doing", async () => {
    vi.mocked(useRequeuePendingPreview).mockReturnValue(
      mockQueryResult({
        data: undefined,
        isLoading: false,
        isError: true,
        isSuccess: false,
        isPending: false,
        status: "error",
      }),
    );
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Maintenance" }));

    // An unreadable preview must not render as "0 pending" — that reads as "all clear".
    expect(
      await screen.findByText("Failed to load maintenance status."),
    ).toBeInTheDocument();
    expect(screen.queryByTestId("requeue-empty")).not.toBeInTheDocument();
  });

  it("hides the tab from non-admins", () => {
    vi.mocked(usePermission).mockReturnValue({
      roles: ["User"],
      hasRole: (role: string) => role === "User",
      isAdmin: () => false,
    });
    renderWithQueryClient(<NormalizedDescriptions />);

    expect(
      screen.queryByRole("tab", { name: "Maintenance" }),
    ).not.toBeInTheDocument();
  });
});
