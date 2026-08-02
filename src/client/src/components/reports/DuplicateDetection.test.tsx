import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { format } from "date-fns";
import { renderWithQueryClient } from "@/test/test-utils";
import { mockMutationResult, mockQueryResult } from "@/test/mock-hooks";
import DuplicateDetection from "./DuplicateDetection";

const mockNavigate = vi.fn();
vi.mock("react-router", async () => {
  const actual = await vi.importActual("react-router");
  return { ...actual, useNavigate: () => mockNavigate };
});

vi.mock("@/hooks/useDuplicateDetectionReport", () => ({
  useDuplicateDetectionReport: vi.fn(),
}));

vi.mock("@/hooks/useDuplicateAcceptance", () => ({
  useAcceptedDuplicates: vi.fn(),
  useAcceptDuplicateGroup: vi.fn(),
  useUnacceptDuplicateGroup: vi.fn(),
}));

vi.mock("@/hooks/useReceipts", () => ({
  useDeleteReceipts: vi.fn(() => mockMutationResult()),
}));

vi.mock("@/lib/export-csv", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/export-csv")>();
  return { ...actual, downloadCsv: vi.fn() };
});

import { useDuplicateDetectionReport } from "@/hooks/useDuplicateDetectionReport";
import { downloadCsv } from "@/lib/export-csv";
import {
  useAcceptedDuplicates,
  useAcceptDuplicateGroup,
  useUnacceptDuplicateGroup,
} from "@/hooks/useDuplicateAcceptance";

const mockHook = vi.mocked(useDuplicateDetectionReport);
const mockDownloadCsv = vi.mocked(downloadCsv);
const mockAcceptedHook = vi.mocked(useAcceptedDuplicates);
const mockAcceptHook = vi.mocked(useAcceptDuplicateGroup);
const mockUnacceptHook = vi.mocked(useUnacceptDuplicateGroup);

const mockGroups = [
  {
    matchKey: "2025-03-01 @ Store A",
    isAccepted: false,
    receipts: [
      {
        receiptId: "id-1",
        location: "Store A",
        date: "2025-03-01",
        transactionTotal: 25.5,
      },
      {
        receiptId: "id-2",
        location: "Store A",
        date: "2025-03-01",
        transactionTotal: 30.0,
      },
    ],
  },
];

const mockAcceptedGroups = [
  {
    acceptedAt: "2025-04-05T10:00:00Z",
    receipts: [
      {
        receiptId: "id-3",
        location: "Store B",
        date: "2025-04-02",
        transactionTotal: 12.34,
      },
      {
        receiptId: "id-4",
        location: "Store B",
        date: "2025-04-02",
        transactionTotal: 12.34,
      },
    ],
  },
];

function setupMock(overrides: Record<string, unknown> = {}) {
  mockHook.mockReturnValue({
    data: {
      groupCount: 1,
      totalDuplicateReceipts: 2,
      groups: mockGroups,
    },
    isLoading: false,
    isError: false,
    ...overrides,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
  } as any);
}

/** Default acceptance-hook wiring: no accepted groups, idle mutations. */
function setupAcceptance(overrides: Record<string, unknown> = {}) {
  mockAcceptedHook.mockReturnValue(
    mockQueryResult({
      data: { groupCount: 0, groups: [] },
      isLoading: false,
      isError: false,
      isSuccess: true,
      isPending: false,
      status: "success",
      ...overrides,
    }),
  );
}

function setupMutations() {
  const acceptMutate = vi.fn();
  const unacceptMutate = vi.fn();
  mockAcceptHook.mockReturnValue(mockMutationResult({ mutate: acceptMutate }));
  mockUnacceptHook.mockReturnValue(
    mockMutationResult({ mutate: unacceptMutate }),
  );
  return { acceptMutate, unacceptMutate };
}

/** Scopes queries to the "Accepted Groups" section. */
function acceptedSection() {
  const section = screen.getByText("Accepted Groups").closest("section");
  if (!section) throw new Error("Accepted Groups section not found");
  return within(section);
}

describe("DuplicateDetection", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    setupAcceptance();
    setupMutations();
  });

  it("shows loading skeleton", () => {
    setupMock({ isLoading: true, data: undefined });
    renderWithQueryClient(<DuplicateDetection />);
    const skeletons = document.querySelectorAll("[data-slot='skeleton']");
    expect(skeletons.length).toBeGreaterThan(0);
  });

  it("shows error state", () => {
    setupMock({ isError: true, data: undefined });
    renderWithQueryClient(<DuplicateDetection />);
    expect(
      screen.getByText(/failed to load duplicate detection report/i),
    ).toBeInTheDocument();
  });

  it("shows empty state when no duplicates found", () => {
    setupMock({
      data: { groupCount: 0, totalDuplicateReceipts: 0, groups: [] },
    });
    renderWithQueryClient(<DuplicateDetection />);
    expect(screen.getByText("No Duplicates Found")).toBeInTheDocument();
    expect(
      screen.getByText(/no potential duplicate receipts/i),
    ).toBeInTheDocument();
  });

  it("shows empty state when data is null", () => {
    setupMock({ data: undefined });
    renderWithQueryClient(<DuplicateDetection />);
    expect(screen.getByText("No Duplicates Found")).toBeInTheDocument();
  });

  it("renders summary header with counts", () => {
    setupMock();
    renderWithQueryClient(<DuplicateDetection />);
    expect(screen.getByText("1")).toBeInTheDocument();
    expect(screen.getByText("2")).toBeInTheDocument();
  });

  it("renders duplicate group card with match key", () => {
    setupMock();
    renderWithQueryClient(<DuplicateDetection />);
    expect(screen.getByText("2025-03-01 @ Store A")).toBeInTheDocument();
    expect(
      screen.getByText("2 receipts in this group"),
    ).toBeInTheDocument();
  });

  it("renders receipt cards with location, date, and total", () => {
    setupMock();
    renderWithQueryClient(<DuplicateDetection />);
    const storeAs = screen.getAllByText("Store A");
    expect(storeAs.length).toBe(2);
    expect(screen.getByText("$25.50")).toBeInTheDocument();
    expect(screen.getByText("$30.00")).toBeInTheDocument();
  });

  it("highlights differing total fields", () => {
    setupMock();
    renderWithQueryClient(<DuplicateDetection />);
    const total25 = screen.getByText("$25.50");
    expect(total25.getAttribute("style")).toContain("var(--warn-ink)");
    const total30 = screen.getByText("$30.00");
    expect(total30.getAttribute("style")).toContain("var(--warn-ink)");
  });

  it("shows 'Total differs' badges when totals differ", () => {
    setupMock();
    renderWithQueryClient(<DuplicateDetection />);
    const badges = screen.getAllByText("Total differs");
    expect(badges.length).toBe(2);
  });

  it("navigates to receipt on View click", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<DuplicateDetection />);

    const viewButtons = screen.getAllByRole("button", { name: "View" });
    await user.click(viewButtons[0]);
    expect(mockNavigate).toHaveBeenCalledWith("/receipts/id-1");
  });

  it("opens delete confirmation dialog on Delete click", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<DuplicateDetection />);

    const deleteButtons = screen.getAllByRole("button", { name: "Delete" });
    await user.click(deleteButtons[0]);

    expect(screen.getByText("Delete Receipt")).toBeInTheDocument();
    expect(
      screen.getByText(/are you sure you want to delete/i),
    ).toBeInTheDocument();
  });

  it("calls deleteReceipts on confirm", async () => {
    const user = userEvent.setup();
    const mockMutate = vi.fn();
    const { useDeleteReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useDeleteReceipts).mockReturnValue(
      mockMutationResult({ mutate: mockMutate }),
    );
    setupMock();
    renderWithQueryClient(<DuplicateDetection />);

    const deleteButtons = screen.getAllByRole("button", { name: "Delete" });
    await user.click(deleteButtons[0]);

    const dialog = screen.getByRole("alertdialog");
    const confirmButton = within(dialog).getByRole("button", {
      name: "Delete",
    });
    await user.click(confirmButton);

    expect(mockMutate).toHaveBeenCalledWith(["id-1"], expect.any(Object));
  });

  it("exports flattened duplicate groups as csv", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<DuplicateDetection />);

    await user.click(screen.getByRole("button", { name: "Export CSV" }));
    await waitFor(() => expect(mockDownloadCsv).toHaveBeenCalledTimes(1));

    const [filename, csv] = mockDownloadCsv.mock.calls[0];
    const today = format(new Date(), "yyyy-MM-dd");
    expect(filename).toBe(`duplicate-detection_${today}.csv`);
    expect(csv).toBe(
      "Match Key,Location,Date,Transaction Total,Receipt ID\r\n" +
        "2025-03-01 @ Store A,Store A,2025-03-01,25.5,id-1\r\n" +
        "2025-03-01 @ Store A,Store A,2025-03-01,30,id-2\r\n",
    );
  });

  it("does not show the export button when no duplicates exist", () => {
    setupMock({
      data: { groupCount: 0, totalDuplicateReceipts: 0, groups: [] },
    });
    renderWithQueryClient(<DuplicateDetection />);
    expect(
      screen.queryByRole("button", { name: "Export CSV" }),
    ).not.toBeInTheDocument();
  });

  it("renders parameter controls", () => {
    setupMock();
    renderWithQueryClient(<DuplicateDetection />);
    expect(screen.getByText("Match On")).toBeInTheDocument();
    expect(screen.getByText("Location Matching")).toBeInTheDocument();
  });

  it("does not show location tolerance for DateAndTotal", () => {
    mockHook.mockImplementation(() => {
      return {
        data: { groupCount: 0, totalDuplicateReceipts: 0, groups: [] },
        isLoading: false,
        isError: false,
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any;
    });
    renderWithQueryClient(<DuplicateDetection />);
    // Default is DateAndLocation, so Location Matching should be visible
    expect(screen.getByText("Location Matching")).toBeInTheDocument();
  });

  it("does not show total tolerance in DateAndLocation match mode", () => {
    setupMock({
      data: { groupCount: 0, totalDuplicateReceipts: 0, groups: [] },
    });
    renderWithQueryClient(<DuplicateDetection />);

    // Initially DateAndLocation mode, no Total Tolerance control
    expect(screen.queryByText("Total Tolerance")).not.toBeInTheDocument();
  });

  describe("show-accepted toggle", () => {
    it("renders the 'Show accepted groups' switch unchecked", () => {
      setupMock();
      renderWithQueryClient(<DuplicateDetection />);

      expect(screen.getByText("Show accepted groups")).toBeInTheDocument();
      const switchEl = screen.getByRole("switch", {
        name: /show accepted groups/i,
      });
      expect(switchEl).toHaveAttribute("data-state", "unchecked");
    });

    it("omits includeAccepted until the switch is toggled on", () => {
      setupMock();
      renderWithQueryClient(<DuplicateDetection />);

      expect(mockHook).toHaveBeenLastCalledWith({
        matchOn: "dateAndLocation",
        locationTolerance: "exact",
        totalTolerance: 0,
        includeAccepted: undefined,
      });
    });

    it("requests includeAccepted when the switch is toggled on", async () => {
      const user = userEvent.setup();
      setupMock();
      renderWithQueryClient(<DuplicateDetection />);

      await user.click(
        screen.getByRole("switch", { name: /show accepted groups/i }),
      );

      expect(mockHook).toHaveBeenLastCalledWith(
        expect.objectContaining({ includeAccepted: true }),
      );
      expect(
        screen.getByRole("switch", { name: /show accepted groups/i }),
      ).toHaveAttribute("data-state", "checked");
    });

    it("drops includeAccepted again when the switch is toggled off", async () => {
      const user = userEvent.setup();
      setupMock();
      renderWithQueryClient(<DuplicateDetection />);

      const switchEl = screen.getByRole("switch", {
        name: /show accepted groups/i,
      });
      await user.click(switchEl);
      await user.click(switchEl);

      expect(mockHook).toHaveBeenLastCalledWith(
        expect.objectContaining({ includeAccepted: undefined }),
      );
    });
  });

  describe("per-group acceptance", () => {
    it("shows 'Not duplicates' for a group that is not accepted", () => {
      setupMock();
      renderWithQueryClient(<DuplicateDetection />);

      expect(
        screen.getByRole("button", { name: "Not duplicates" }),
      ).toBeInTheDocument();
      expect(
        screen.queryByRole("button", { name: "Report again" }),
      ).not.toBeInTheDocument();
      expect(screen.queryByText("Accepted")).not.toBeInTheDocument();
    });

    it("accepts the group's receipt ids on 'Not duplicates' click", async () => {
      const user = userEvent.setup();
      const { acceptMutate } = setupMutations();
      setupMock();
      renderWithQueryClient(<DuplicateDetection />);

      await user.click(screen.getByRole("button", { name: "Not duplicates" }));

      expect(acceptMutate).toHaveBeenCalledWith(["id-1", "id-2"]);
    });

    it("shows the Accepted badge and 'Report again' for an accepted group", () => {
      setupMock({
        data: {
          groupCount: 1,
          totalDuplicateReceipts: 2,
          groups: [{ ...mockGroups[0], isAccepted: true }],
        },
      });
      renderWithQueryClient(<DuplicateDetection />);

      expect(screen.getByText("Accepted")).toBeInTheDocument();
      expect(
        screen.getByRole("button", { name: "Report again" }),
      ).toBeInTheDocument();
      expect(
        screen.queryByRole("button", { name: "Not duplicates" }),
      ).not.toBeInTheDocument();
    });

    it("unaccepts the group's receipt ids on 'Report again' click", async () => {
      const user = userEvent.setup();
      const { unacceptMutate } = setupMutations();
      setupMock({
        data: {
          groupCount: 1,
          totalDuplicateReceipts: 2,
          groups: [{ ...mockGroups[0], isAccepted: true }],
        },
      });
      renderWithQueryClient(<DuplicateDetection />);

      await user.click(screen.getByRole("button", { name: "Report again" }));

      expect(unacceptMutate).toHaveBeenCalledWith(["id-1", "id-2"]);
    });

    it("disables the accept button of the group whose mutation is in flight", () => {
      setupMock();
      mockAcceptHook.mockReturnValue(
        mockMutationResult({ isPending: true, variables: ["id-1", "id-2"] }),
      );
      renderWithQueryClient(<DuplicateDetection />);

      expect(
        screen.getByRole("button", { name: "Not duplicates" }),
      ).toBeDisabled();
    });

    it("leaves other groups' accept buttons enabled while one is in flight", () => {
      setupMock({
        data: {
          groupCount: 2,
          totalDuplicateReceipts: 4,
          groups: [
            ...mockGroups,
            {
              matchKey: "2025-05-09 @ Store C",
              isAccepted: false,
              receipts: [
                {
                  receiptId: "id-9",
                  location: "Store C",
                  date: "2025-05-09",
                  transactionTotal: 8,
                },
                {
                  receiptId: "id-10",
                  location: "Store C",
                  date: "2025-05-09",
                  transactionTotal: 8,
                },
              ],
            },
          ],
        },
      });
      // Only the first group is being accepted.
      mockAcceptHook.mockReturnValue(
        mockMutationResult({ isPending: true, variables: ["id-1", "id-2"] }),
      );
      renderWithQueryClient(<DuplicateDetection />);

      const buttons = screen.getAllByRole("button", { name: "Not duplicates" });
      expect(buttons).toHaveLength(2);
      expect(buttons[0]).toBeDisabled();
      expect(buttons[1]).toBeEnabled();
    });
  });

  describe("accepted groups section", () => {
    it("renders the section heading and description", () => {
      setupMock();
      renderWithQueryClient(<DuplicateDetection />);

      expect(screen.getByText("Accepted Groups")).toBeInTheDocument();
      expect(
        acceptedSection().getByText(/hidden from the report above/i),
      ).toBeInTheDocument();
    });

    it("shows the empty state when nothing has been accepted", () => {
      setupMock();
      renderWithQueryClient(<DuplicateDetection />);

      expect(
        acceptedSection().getByText("No groups have been accepted yet."),
      ).toBeInTheDocument();
    });

    it("renders a row per accepted group", () => {
      setupMock();
      setupAcceptance({
        data: { groupCount: 1, groups: mockAcceptedGroups },
      });
      renderWithQueryClient(<DuplicateDetection />);

      const section = acceptedSection();
      expect(section.getByText("2 receipts")).toBeInTheDocument();
      expect(section.getAllByText(/Store B/)).toHaveLength(2);
      expect(section.getAllByText(/\$12\.34/)).toHaveLength(2);
      expect(
        section.queryByText("No groups have been accepted yet."),
      ).not.toBeInTheDocument();
    });

    it("unaccepts the group's receipt ids on Undo click", async () => {
      const user = userEvent.setup();
      const { unacceptMutate } = setupMutations();
      setupMock();
      setupAcceptance({
        data: { groupCount: 1, groups: mockAcceptedGroups },
      });
      renderWithQueryClient(<DuplicateDetection />);

      await user.click(acceptedSection().getByRole("button", { name: "Undo" }));

      expect(unacceptMutate).toHaveBeenCalledWith(["id-3", "id-4"]);
    });

    it("disables Undo for the group whose unaccept is in flight", () => {
      setupMock();
      setupAcceptance({
        data: { groupCount: 1, groups: mockAcceptedGroups },
      });
      mockUnacceptHook.mockReturnValue(
        mockMutationResult({ isPending: true, variables: ["id-3", "id-4"] }),
      );
      renderWithQueryClient(<DuplicateDetection />);

      expect(
        acceptedSection().getByRole("button", { name: "Undo" }),
      ).toBeDisabled();
    });

    it("leaves Undo enabled when a different group's unaccept is in flight", () => {
      setupMock();
      setupAcceptance({
        data: { groupCount: 1, groups: mockAcceptedGroups },
      });
      mockUnacceptHook.mockReturnValue(
        mockMutationResult({ isPending: true, variables: ["id-1", "id-2"] }),
      );
      renderWithQueryClient(<DuplicateDetection />);

      expect(
        acceptedSection().getByRole("button", { name: "Undo" }),
      ).toBeEnabled();
    });

    it("shows a skeleton while accepted groups load", () => {
      setupMock();
      setupAcceptance({
        data: undefined,
        isLoading: true,
        isPending: true,
        isSuccess: false,
        status: "pending",
      });
      renderWithQueryClient(<DuplicateDetection />);

      const section = screen.getByText("Accepted Groups").closest("section");
      expect(
        section?.querySelectorAll("[data-slot='skeleton']").length,
      ).toBeGreaterThan(0);
      expect(
        screen.queryByText("No groups have been accepted yet."),
      ).not.toBeInTheDocument();
    });

    it("shows an error message when accepted groups fail to load", () => {
      setupMock();
      setupAcceptance({
        data: undefined,
        isError: true,
        isSuccess: false,
        error: new Error("boom"),
        status: "error",
      });
      renderWithQueryClient(<DuplicateDetection />);

      expect(
        acceptedSection().getByText("Failed to load accepted groups."),
      ).toBeInTheDocument();
      expect(
        screen.queryByText("No groups have been accepted yet."),
      ).not.toBeInTheDocument();
    });

    it("is not rendered while the duplicate report itself is loading", () => {
      setupMock({ isLoading: true, data: undefined });
      renderWithQueryClient(<DuplicateDetection />);

      expect(screen.queryByText("Accepted Groups")).not.toBeInTheDocument();
    });

    it("still renders when the duplicate report is empty", () => {
      setupMock({
        data: { groupCount: 0, totalDuplicateReceipts: 0, groups: [] },
      });
      setupAcceptance({
        data: { groupCount: 1, groups: mockAcceptedGroups },
      });
      renderWithQueryClient(<DuplicateDetection />);

      expect(screen.getByText("No Duplicates Found")).toBeInTheDocument();
      expect(acceptedSection().getByText("2 receipts")).toBeInTheDocument();
    });
  });

  it("reads matchOn and tolerances from the URL on load", () => {
    setupMock({
      data: { groupCount: 0, totalDuplicateReceipts: 0, groups: [] },
    });
    renderWithQueryClient(<DuplicateDetection />, {
      route:
        "/?matchOn=dateAndLocationAndTotal&locationTolerance=normalized&totalTolerance=0.5",
    });

    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        matchOn: "dateAndLocationAndTotal",
        locationTolerance: "normalized",
        totalTolerance: 0.5,
      }),
    );
    // Both tolerance controls should be visible for the combined match mode.
    expect(screen.getByText("Location Matching")).toBeInTheDocument();
    expect(screen.getByText("Total Tolerance")).toBeInTheDocument();
  });

  it("falls back to defaults for malformed URL params instead of crashing", () => {
    setupMock({
      data: { groupCount: 0, totalDuplicateReceipts: 0, groups: [] },
    });
    renderWithQueryClient(<DuplicateDetection />, {
      route:
        "/?matchOn=bogus&locationTolerance=bogus&totalTolerance=bogus",
    });

    expect(screen.getByText("Match On")).toBeInTheDocument();
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        matchOn: "dateAndLocation",
        locationTolerance: "exact",
        totalTolerance: 0,
      }),
    );
  });
});
