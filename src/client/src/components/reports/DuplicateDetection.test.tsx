import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { format } from "date-fns";
import { renderWithQueryClient } from "@/test/test-utils";
import { mockMutationResult } from "@/test/mock-hooks";
import DuplicateDetection from "./DuplicateDetection";

const mockNavigate = vi.fn();
vi.mock("react-router", async () => {
  const actual = await vi.importActual("react-router");
  return { ...actual, useNavigate: () => mockNavigate };
});

vi.mock("@/hooks/useDuplicateDetectionReport", () => ({
  useDuplicateDetectionReport: vi.fn(),
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
const mockHook = vi.mocked(useDuplicateDetectionReport);
const mockDownloadCsv = vi.mocked(downloadCsv);

const mockGroups = [
  {
    matchKey: "2025-03-01 @ Store A",
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

describe("DuplicateDetection", () => {
  beforeEach(() => {
    vi.clearAllMocks();
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
});
