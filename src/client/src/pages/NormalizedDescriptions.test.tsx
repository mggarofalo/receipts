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
  NORMALIZED_DESCRIPTION_PAGE_SIZE: 50,
  NORMALIZED_DESCRIPTION_MAX_PAGE_SIZE: 200,
}));

vi.mock("@/hooks/useNormalizedDescriptionActions", () => ({
  useMergeMutation: vi.fn(() => mockMutationResult()),
  useSplitMutation: vi.fn(() => mockMutationResult()),
  useUpdateStatusMutation: vi.fn(() => mockMutationResult()),
  useRenameMutation: vi.fn(() => mockMutationResult()),
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
  useLinkedReceiptItems: vi.fn(() => ({
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
  useRenameMutation,
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
import { useLinkedReceiptItems } from "@/hooks/useReceiptItems";
import { usePermission } from "@/hooks/usePermission";

// Fixtures carry the RECEIPTS-873 evidence fields because the real API always does. p-1 is the
// fully-evidenced case; p-2 is the row that was never compared against anything, which is what
// distinguishes "no comparison recorded" from a zero score.
const pendingItems = [
  {
    id: "p-1",
    canonicalName: "Strawberry Preserves",
    displayLabel: null,
    displayName: "Strawberry Preserves",
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
    displayLabel: null,
    displayName: "Organic Milk",
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
    displayLabel: null,
    displayName: "Apples",
    status: "active" as const,
    createdAt: "2025-02-01T00:00:00Z",
    linkedItemCount: 12,
    sampleRawDescriptions: ["GALA APPLES"],
    nearestNeighbourName: null,
    nearestNeighbourSimilarity: null,
  },
  {
    id: "a-2",
    canonicalName: "MILK 2% GAL",
    // The renamed case: what a user sees diverges from the text the embedding is anchored to.
    displayLabel: "Milk",
    displayName: "Milk",
    status: "active" as const,
    createdAt: "2025-02-02T00:00:00Z",
    linkedItemCount: 9,
    sampleRawDescriptions: ["MILK 2% GAL"],
    nearestNeighbourName: null,
    nearestNeighbourSimilarity: null,
  },
];

// The row action reads "Merge into…" (RECEIPTS-874) — the ellipsis says a picker follows and
// "into" says the direction. The dialog's own confirm button stays "Merge", which is what makes
// the two unambiguous to query separately.
const MERGE_ACTION = "Merge into…";

type ListItem = (typeof pendingItems)[number] | (typeof activeItems)[number];

/**
 * The shape the hook returns since RECEIPTS-879: the query plus a materialised page. `total` is
 * the count of matching rows, deliberately independent of `items.length` — a fixture where they
 * always agree cannot catch a pager that reads the page length.
 */
function listResult(items: ListItem[], total: number = items.length) {
  return {
    ...mockQueryResult({
      data: { items, totalCount: total },
      isLoading: false,
      isSuccess: true,
      isPending: false,
      status: "success",
    }),
    items,
    total,
  };
}

type ListOptions = {
  status?: string | string[];
  q?: string;
  offset?: number;
  limit?: number;
};

/** The status filter is one value or several since RECEIPTS-878. */
function statusesOf(options: ListOptions | undefined): string[] {
  if (options?.status === undefined) return [];
  return Array.isArray(options.status) ? options.status : [options.status];
}

function mockList(options: ListOptions | undefined) {
  const statuses = statusesOf(options);
  const matched = [
    ...(statuses.includes("Active") ? activeItems : []),
    ...(statuses.includes("PendingReview") ? pendingItems : []),
  ];
  return listResult(matched);
}

const requeuePreview = {
  pendingDescriptionCount: 4,
  pendingFingerprint: "digest-abc",
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
  vi.mocked(useNormalizedDescriptions).mockImplementation(
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    ((options?: ListOptions) => mockList(options)) as any,
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
  vi.mocked(useLinkedReceiptItems).mockReturnValue({
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

/** The items the server reports as linked to whichever entry the split dialog asked about. */
function mockLinkedItems(items: { id: string; description: string }[]) {
  vi.mocked(useLinkedReceiptItems).mockReturnValue({
    data: items,
    total: items.length,
    isLoading: false,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
  } as any);
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
    vi.mocked(useNormalizedDescriptions).mockImplementation(((
      options?: ListOptions,
    ) =>
      statusesOf(options).includes("PendingReview")
        ? listResult([])
        : // eslint-disable-next-line @typescript-eslint/no-explicit-any
          mockList(options)) as any);
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
    await user.click(within(row).getByRole("button", { name: MERGE_ACTION }));
    expect(screen.getByText("Merge Into Another Entry")).toBeInTheDocument();
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
    await user.click(within(row).getByRole("button", { name: MERGE_ACTION }));

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

  // RECEIPTS-877: the dialog now asks the server for this entry's items, takes a multi-select,
  // and prompts for the name rather than deriving one.
  it("splits several selected items into one named entry", async () => {
    const mutate = vi.fn((_vars, opts?: { onSuccess?: () => void }) => {
      opts?.onSuccess?.();
    });
    vi.mocked(useSplitMutation).mockReturnValue(mockMutationResult({ mutate }));
    mockLinkedItems([
      { id: "ri-1", description: "MILK 2% GAL" },
      { id: "ri-2", description: "milk gallon" },
      { id: "ri-3", description: "CHEDDAR BLOCK" },
    ]);

    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Strawberry Preserves")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: "Split" }));

    const dialog = screen.getByRole("dialog");
    await user.click(within(dialog).getByLabelText("MILK 2% GAL"));
    await user.click(within(dialog).getByLabelText("milk gallon"));

    // Pre-filled from the first selection, then overridden — the reviewer names the group.
    const nameInput = within(dialog).getByLabelText(/name for the new entry/i);
    expect(nameInput).toHaveValue("MILK 2% GAL");
    await user.clear(nameInput);
    await user.type(nameInput, "Milk");

    await user.click(within(dialog).getByRole("button", { name: "Split" }));

    expect(mutate).toHaveBeenCalledWith(
      { id: "p-1", receiptItemIds: ["ri-1", "ri-2"], canonicalName: "Milk" },
      expect.any(Object),
    );
  });

  it("asks the server for this entry's items instead of filtering a page client-side", async () => {
    mockLinkedItems([{ id: "ri-1", description: "MILK 2% GAL" }]);

    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Strawberry Preserves")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: "Split" }));

    // The old dialog pulled a fixed page of every receipt item app-wide and filtered it in the
    // browser, which could never match because the list projection dropped the FK.
    expect(vi.mocked(useLinkedReceiptItems)).toHaveBeenCalledWith("p-1", 0, 50);
  });

  it("will not split without both a selection and a name", async () => {
    mockLinkedItems([{ id: "ri-1", description: "MILK 2% GAL" }]);

    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Strawberry Preserves")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: "Split" }));

    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByRole("button", { name: "Split" })).toBeDisabled();

    await user.click(within(dialog).getByLabelText("MILK 2% GAL"));
    await user.clear(within(dialog).getByLabelText(/name for the new entry/i));
    expect(within(dialog).getByRole("button", { name: "Split" })).toBeDisabled();
  });

  it("says the entry has no linked items rather than blaming the lookup", async () => {
    mockLinkedItems([]);

    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Organic Milk")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: "Split" }));

    // The old copy — "not found in the most recent 200 items" — described the query rather than
    // the data, and was shown even when the entry did have linked items.
    expect(await screen.findByTestId("split-empty")).toHaveTextContent(
      /no receipt items are linked to this entry/i,
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

  // ── RECEIPTS-879: paging and server-side search ──────────────

  it("asks the server for a bounded page rather than the whole registry", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Registry" }));
    await screen.findByText("Apples");

    // The tab used to fetch every Active row and filter the array in the browser. A window is
    // now mandatory: without one the client is back to holding the entire registry.
    expect(vi.mocked(useNormalizedDescriptions)).toHaveBeenCalledWith(
      expect.objectContaining({
        status: "Active",
        offset: 0,
        limit: expect.any(Number),
      }),
    );
  });

  it("sends the search term to the server instead of filtering the page", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Registry" }));
    const input = await screen.findByLabelText("Search");
    await user.type(input, "apple");

    // Client-side filtering can only ever search the page in hand, so a match on page 7 was
    // invisible. Debounced, hence waitFor.
    await waitFor(() =>
      expect(vi.mocked(useNormalizedDescriptions)).toHaveBeenCalledWith(
        expect.objectContaining({ status: "Active", q: "apple" }),
      ),
    );
  });

  it("reports the matching total, not the number of rows on this page", async () => {
    vi.mocked(useNormalizedDescriptions).mockImplementation(((
      options?: ListOptions,
    ) =>
      statusesOf(options).includes("Active")
        ? listResult(activeItems, 843)
        : // eslint-disable-next-line @typescript-eslint/no-explicit-any
          mockList(options)) as any);

    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Registry" }));

    expect(await screen.findByText("843")).toBeInTheDocument();
  });

  it("pages forward through the registry", async () => {
    vi.mocked(useNormalizedDescriptions).mockImplementation(((
      options?: ListOptions,
    ) =>
      statusesOf(options).includes("Active")
        ? listResult(activeItems, 843)
        : // eslint-disable-next-line @typescript-eslint/no-explicit-any
          mockList(options)) as any);

    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Registry" }));
    await screen.findByText("Apples");

    await user.click(screen.getByRole("button", { name: "Next page" }));

    await waitFor(() =>
      expect(vi.mocked(useNormalizedDescriptions)).toHaveBeenCalledWith(
        expect.objectContaining({ status: "Active", offset: expect.any(Number) }),
      ),
    );
    const offsets = vi
      .mocked(useNormalizedDescriptions)
      .mock.calls.map(([o]) => (o as ListOptions | undefined))
      .filter((o) => o?.status === "Active")
      .map((o) => o!.offset);
    expect(offsets.some((offset) => (offset ?? 0) > 0)).toBe(true);
  });

  it("shows how many receipt items each registry row holds", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Registry" }));

    // Without this an over-matching entry that has swallowed thousands of items looks exactly
    // like one holding three.
    const row = (await screen.findByText("Apples")).closest("tr")!;
    expect(within(row).getByText("12")).toBeInTheDocument();
  });

  // ── RECEIPTS-879: row actions ────────────────────────────────

  it("offers merge, split and send-back-to-review on an active entry", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Registry" }));

    // The registry is the only place an approved mistake can be corrected. Read-only meant
    // every approval was permanent.
    const row = (await screen.findByText("Apples")).closest("tr")!;
    expect(within(row).getByRole("button", { name: MERGE_ACTION })).toBeInTheDocument();
    expect(within(row).getByRole("button", { name: "Split" })).toBeInTheDocument();
    expect(
      within(row).getByRole("button", { name: /rename apples/i }),
    ).toBeInTheDocument();
    expect(
      within(row).getByRole("button", { name: "Send back to review" }),
    ).toBeInTheDocument();
  });

  it("merges one active entry into another and says how many items move", async () => {
    const mutate = vi.fn((_vars, opts?: { onSuccess?: () => void }) => {
      opts?.onSuccess?.();
    });
    vi.mocked(useMergeMutation).mockReturnValue(mockMutationResult({ mutate }));

    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Registry" }));
    const row = (await screen.findByText("Apples")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: MERGE_ACTION }));

    const dialog = await screen.findByRole("dialog");
    // a-1 holds 12 items. The count is the whole confirmation: merging the wrong direction
    // moves the larger set into the smaller name, and it cannot be undone.
    expect(dialog).toHaveTextContent(/12 receipt items/i);
    expect(dialog).toHaveTextContent(/cannot be undone/i);

    const target = within(dialog).getByText("Milk").closest("label")!;
    await user.click(within(target).getByRole("radio"));
    await user.click(within(dialog).getByRole("button", { name: "Merge" }));

    expect(mutate).toHaveBeenCalledWith(
      { id: "a-2", discardId: "a-1" },
      expect.any(Object),
    );
  });

  it("splits items out of an active entry", async () => {
    const mutate = vi.fn((_vars, opts?: { onSuccess?: () => void }) => {
      opts?.onSuccess?.();
    });
    vi.mocked(useSplitMutation).mockReturnValue(mockMutationResult({ mutate }));
    mockLinkedItems([{ id: "ri-9", description: "GALA APPLES" }]);

    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Registry" }));
    const row = (await screen.findByText("Apples")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: "Split" }));

    const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getByLabelText("GALA APPLES"));
    await user.click(within(dialog).getByRole("button", { name: "Split" }));

    expect(mutate).toHaveBeenCalledWith(
      { id: "a-1", receiptItemIds: ["ri-9"], canonicalName: "GALA APPLES" },
      expect.any(Object),
    );
  });

  it("sends an active entry back to review without unlinking anything", async () => {
    const mutate = vi.fn();
    vi.mocked(useUpdateStatusMutation).mockReturnValue(
      mockMutationResult({ mutate }),
    );

    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Registry" }));
    const row = (await screen.findByText("Apples")).closest("tr")!;
    await user.click(
      within(row).getByRole("button", { name: "Send back to review" }),
    );

    const dialog = await screen.findByRole("dialog");
    // The reversible action. Saying so is what keeps it distinct from Reject, which unlinks
    // every item and tombstones the text.
    expect(dialog).toHaveTextContent(/nothing is unlinked/i);

    await user.click(
      within(dialog).getByRole("button", { name: "Send back to review" }),
    );
    expect(mutate).toHaveBeenCalledWith(
      { id: "a-1", status: "pendingReview" },
      expect.any(Object),
    );
  });
});

// RECEIPTS-874. Three bare outline buttons used to sit at the end of every row with no
// explanation anywhere except inside the dialog you got *after* clicking — and Approve has no
// dialog at all, so its consequences were never stated anywhere.
describe("NormalizedDescriptions action explanations", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    wireDefaults();
  });

  it("explains what the queue is and what each action does", async () => {
    renderWithQueryClient(<NormalizedDescriptions />);

    const explainer = await screen.findByTestId("review-queue-explainer");
    // Why a row is here at all: the near-match band. Without this the queue reads as a list of
    // arbitrary rows a reviewer is expected to have an opinion about.
    expect(explainer).toHaveTextContent(/nearly matches an entry the registry already has/i);
    // And that the spend is already counted — approving is not what puts it in the reports.
    expect(explainer).toHaveTextContent(/already appears in reports/i);

    for (const action of ["Approve", "Merge into…", "Split", "Reject"]) {
      expect(within(explainer).getByText(action)).toBeInTheDocument();
    }
    // The sharpest edge, called out rather than implied.
    expect(explainer).toHaveTextContent(/this entry is deleted/i);
  });

  it("keeps the explainer up when the queue is empty", () => {
    vi.mocked(useNormalizedDescriptions).mockImplementation(((
      options?: ListOptions,
    ) =>
      statusesOf(options).includes("PendingReview")
        ? listResult([])
        : // eslint-disable-next-line @typescript-eslint/no-explicit-any
          mockList(options)) as any);

    renderWithQueryClient(<NormalizedDescriptions />);

    // An empty queue is exactly when someone is reading to find out what this page is for.
    expect(screen.getByTestId("review-queue-explainer")).toBeInTheDocument();
    expect(screen.getByText("Review Queue Empty")).toBeInTheDocument();
  });

  it("describes each row action, with the count of items it would move", async () => {
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Strawberry Preserves")).closest("tr")!;

    // p-1 holds 4 items. The hint reaches keyboard and screen-reader users through
    // aria-describedby, not only whoever happens to hover.
    const expectations: [string, RegExp][] = [
      ["Approve", /nothing is re-linked or moved.*4 receipt items stay where they are/is],
      [MERGE_ACTION, /4 receipt items are re-pointed.*this one is deleted/is],
      ["Split", /everything you leave unselected stays here/i],
      ["Reject", /4 receipt items become unnormalized/i],
    ];

    for (const [name, pattern] of expectations) {
      const button = within(row).getByRole("button", { name });
      const describedBy = button.getAttribute("aria-describedby");
      expect(describedBy).toBeTruthy();
      expect(document.getElementById(describedBy!)).toHaveTextContent(pattern);
    }
  });

  it("says one receipt item rather than 1 receipt items", async () => {
    vi.mocked(useNormalizedDescriptions).mockImplementation(((
      options?: ListOptions,
    ) =>
      statusesOf(options).includes("PendingReview")
        ? listResult([{ ...pendingItems[0], linkedItemCount: 1 }])
        : // eslint-disable-next-line @typescript-eslint/no-explicit-any
          mockList(options)) as any);

    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Strawberry Preserves")).closest("tr")!;
    const button = within(row).getByRole("button", { name: "Approve" });
    const hint = document.getElementById(button.getAttribute("aria-describedby")!);

    expect(hint).toHaveTextContent(/1 receipt item stay/i);
    expect(hint).not.toHaveTextContent(/1 receipt items/i);
  });

  it("names the direction of a merge in the button itself", async () => {
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Strawberry Preserves")).closest("tr")!;

    // "Merge" alone said nothing about which of the two rows survives, and the answer is that
    // this one does not.
    expect(within(row).getByRole("button", { name: MERGE_ACTION })).toBeInTheDocument();
    expect(within(row).queryByRole("button", { name: "Merge" })).not.toBeInTheDocument();
  });

  it("explains the registry's actions too", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Registry" }));
    const row = (await screen.findByText("Apples")).closest("tr")!;

    const button = within(row).getByRole("button", { name: "Send back to review" });
    const hint = document.getElementById(button.getAttribute("aria-describedby")!);
    // The reversible one. Saying "nothing is unlinked" is what separates it from Reject.
    expect(hint).toHaveTextContent(/nothing is unlinked/i);
    expect(hint).toHaveTextContent(/12 receipt items stay attached/i);
  });
});

// RECEIPTS-879 moved the candidate search server-side. The dialog is the second consumer of the
// list hook, and the one that breaks least visibly when it is only searching a page.
describe("NormalizedDescriptions merge candidates", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    wireDefaults();
  });

  it("searches candidates on the server rather than filtering the loaded page", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Strawberry Preserves")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: MERGE_ACTION }));

    const dialog = await screen.findByRole("dialog");
    await user.type(
      within(dialog).getByLabelText(/search active entries/i),
      "milk",
    );

    await waitFor(() =>
      expect(vi.mocked(useNormalizedDescriptions)).toHaveBeenCalledWith(
        expect.objectContaining({
          status: ["Active", "PendingReview"],
          q: "milk",
        }),
      ),
    );
  });

  it("says when there are more matches than it is showing", async () => {
    vi.mocked(useNormalizedDescriptions).mockImplementation(((
      options?: ListOptions,
    ) =>
      statusesOf(options).includes("Active")
        ? listResult(activeItems, 412)
        : // eslint-disable-next-line @typescript-eslint/no-explicit-any
          mockList(options)) as any);

    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Strawberry Preserves")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: MERGE_ACTION }));

    // Silently showing the first 50 of 412 reads as "the entry you want does not exist"
    // (RECEIPTS-878).
    expect(await screen.findByTestId("merge-truncation-notice")).toHaveTextContent(
      /showing 2 of 412/i,
    );
  });

  it("stays quiet when every match is on screen", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Strawberry Preserves")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: MERGE_ACTION }));

    await screen.findByRole("dialog");
    expect(
      screen.queryByTestId("merge-truncation-notice"),
    ).not.toBeInTheDocument();
  });

  it("never offers the entry being merged away as its own target", async () => {
    vi.mocked(useNormalizedDescriptions).mockImplementation(((
      options?: ListOptions,
    ) =>
      statusesOf(options).includes("Active")
        ? listResult(activeItems)
        : // eslint-disable-next-line @typescript-eslint/no-explicit-any
          mockList(options)) as any);

    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Registry" }));
    const row = (await screen.findByText("Apples")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: MERGE_ACTION }));

    // Merging a row into itself is a 400 from the server; it should not be reachable. The
    // registry makes this possible for the first time, since source and candidates are now the
    // same set.
    const dialog = await screen.findByRole("dialog");
    const radios = within(dialog).getAllByRole("radio");
    expect(radios).toHaveLength(1);
    expect(within(dialog).getByText("Milk")).toBeInTheDocument();
  });

  it("asks for active and pending targets, and never rejected ones", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Strawberry Preserves")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: MERGE_ACTION }));

    // Rejected rows are tombstones. Merging items into one would resurrect text a reviewer
    // retired on purpose, so it must never be in the candidate query at all — filtering it out
    // in the browser would still let it consume the page.
    const calls = vi
      .mocked(useNormalizedDescriptions)
      .mock.calls.map(([o]) => o as ListOptions | undefined)
      .filter((o) => Array.isArray(o?.status));
    expect(calls.length).toBeGreaterThan(0);
    for (const call of calls) {
      expect(call!.status).toEqual(["Active", "PendingReview"]);
    }
  });

  it("offers a pending entry as a target and marks it as pending", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Strawberry Preserves")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: MERGE_ACTION }));

    // Two near-duplicates out of the same resolver batch are exactly the pair you want to merge.
    // Requiring one to be approved first forced a judgement the reviewer had not made yet.
    const dialog = await screen.findByRole("dialog");
    const candidate = within(dialog).getByText("Organic Milk").closest("label")!;
    // Distinguished, because the survivor stays pending and still needs review — that is a
    // different outcome from merging into something already approved.
    expect(within(candidate).getByText(/pending review/i)).toBeInTheDocument();

    const active = within(dialog).getByText("Apples").closest("label")!;
    expect(within(active).queryByText(/pending review/i)).not.toBeInTheDocument();
  });

  it("merges one pending entry into another", async () => {
    const mutate = vi.fn((_vars, opts?: { onSuccess?: () => void }) => {
      opts?.onSuccess?.();
    });
    vi.mocked(useMergeMutation).mockReturnValue(mockMutationResult({ mutate }));

    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Strawberry Preserves")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: MERGE_ACTION }));

    const dialog = await screen.findByRole("dialog");
    const target = within(dialog).getByText("Organic Milk").closest("label")!;
    await user.click(within(target).getByRole("radio"));
    await user.click(within(dialog).getByRole("button", { name: "Merge" }));

    // p-2 survives and stays pending — the server does not touch the keeper's status, which is
    // correct: merging two near-duplicates is a separate judgement from approving the survivor.
    expect(mutate).toHaveBeenCalledWith(
      { id: "p-2", discardId: "p-1" },
      expect.any(Object),
    );
  });

  it("shows each candidate's linked-item count", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Strawberry Preserves")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: MERGE_ACTION }));

    // Merging is direction-sensitive and irreversible. Without the counts there is nothing on
    // screen to say which way round moves the fewest items.
    const dialog = await screen.findByRole("dialog");
    const apples = within(dialog).getByText("Apples").closest("label")!;
    expect(within(apples).getByText("12 items")).toBeInTheDocument();

    const milk = within(dialog).getByText("Milk").closest("label")!;
    expect(within(milk).getByText("9 items")).toBeInTheDocument();
  });
});

// RECEIPTS-876: the two dispositions the queue previously could not express — "call this
// something else" and "this text is garbage, stop asking me".
describe("NormalizedDescriptions rename and reject", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    wireDefaults();
  });

  it("shows the display label and the matched text it diverges from", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Registry" }));

    const row = (await screen.findByText("Milk")).closest("tr")!;
    // Both, because a reviewer renaming "MILK 2% GAL" to "Milk" still needs to know which
    // receipt text the entry actually covers.
    expect(within(row).getByText(/matches/i)).toHaveTextContent("MILK 2% GAL");
  });

  it("renames a row to a new label", async () => {
    const user = userEvent.setup();
    const mutate = vi.fn();
    vi.mocked(useRenameMutation).mockReturnValue(mockMutationResult({ mutate }));

    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(
      await screen.findByRole("button", { name: /rename strawberry preserves/i }),
    );

    const input = screen.getByLabelText(/display name for strawberry preserves/i);
    await user.clear(input);
    await user.type(input, "Jam");
    await user.click(screen.getByRole("button", { name: "Save" }));

    expect(mutate).toHaveBeenCalledWith(
      { id: "p-1", displayLabel: "Jam" },
      expect.any(Object),
    );
  });

  it("treats renaming a row back to its matched text as clearing the label", async () => {
    const user = userEvent.setup();
    const mutate = vi.fn();
    vi.mocked(useRenameMutation).mockReturnValue(mockMutationResult({ mutate }));

    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(screen.getByRole("tab", { name: "Registry" }));
    await user.click(await screen.findByRole("button", { name: /rename milk/i }));

    const input = screen.getByLabelText(/display name for milk/i);
    await user.clear(input);
    await user.type(input, "MILK 2% GAL");
    await user.click(screen.getByRole("button", { name: "Save" }));

    // null, not the string — storing a label identical to the matched text would leave the row
    // permanently "renamed" to what it already displayed.
    expect(mutate).toHaveBeenCalledWith(
      { id: "a-2", displayLabel: null },
      expect.any(Object),
    );
  });

  it("will not submit an empty name", async () => {
    const user = userEvent.setup();
    const mutate = vi.fn();
    vi.mocked(useRenameMutation).mockReturnValue(mockMutationResult({ mutate }));

    renderWithQueryClient(<NormalizedDescriptions />);
    await user.click(
      await screen.findByRole("button", { name: /rename strawberry preserves/i }),
    );
    await user.clear(screen.getByLabelText(/display name for strawberry preserves/i));

    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
    expect(mutate).not.toHaveBeenCalled();
  });

  it("confirms a rejection and names its two consequences", async () => {
    const user = userEvent.setup();
    const mutate = vi.fn();
    vi.mocked(useUpdateStatusMutation).mockReturnValue(
      mockMutationResult({ mutate }),
    );

    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Strawberry Preserves")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: "Reject" }));

    const dialog = await screen.findByRole("dialog");
    // p-1 has 4 linked items. Both consequences are non-obvious, so both are spelled out.
    expect(dialog).toHaveTextContent(/4 receipt items will become unnormalized/i);
    expect(dialog).toHaveTextContent(/will not create it again/i);

    await user.click(within(dialog).getByRole("button", { name: "Reject" }));
    expect(mutate).toHaveBeenCalledWith(
      { id: "p-1", status: "rejected" },
      expect.any(Object),
    );
  });

  it("does not claim items will move when the entry has none", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<NormalizedDescriptions />);

    // p-2 has linkedItemCount 0.
    const row = (await screen.findByText("Organic Milk")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: "Reject" }));

    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent(/no receipt items are linked/i);
  });

  it("keeps Reject distinct from Merge in the row actions", async () => {
    renderWithQueryClient(<NormalizedDescriptions />);
    const row = (await screen.findByText("Strawberry Preserves")).closest("tr")!;

    // Merge means "this is the same as X" and re-points items; Reject means "this is not worth
    // an entry" and unlinks them. Collapsing them was the original complaint.
    expect(within(row).getByRole("button", { name: MERGE_ACTION })).toBeInTheDocument();
    expect(within(row).getByRole("button", { name: "Reject" })).toBeInTheDocument();
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

    // The previewed set digest rides along so the server can reject a stale confirmation
    // even when the pending total happens to be unchanged.
    expect(mutate).toHaveBeenCalledWith(
      { expectedFingerprint: "digest-abc" },
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
          pendingFingerprint: "digest-empty",
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
