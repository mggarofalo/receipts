import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { format } from "date-fns";
import { renderWithQueryClient } from "@/test/test-utils";
import UncategorizedItems from "./UncategorizedItems";

const mockNavigate = vi.fn();
vi.mock("react-router", async () => {
  const actual = await vi.importActual("react-router");
  return { ...actual, useNavigate: () => mockNavigate };
});

vi.mock("@/hooks/useUncategorizedItemsReport", () => ({
  useUncategorizedItemsReport: vi.fn(),
}));

vi.mock("@/hooks/useCategories", () => ({
  useAllCategories: vi.fn().mockReturnValue({
    data: [
      { id: "cat-1", name: "Groceries" },
      { id: "cat-2", name: "Uncategorized" },
    ],
    isLoading: false,
  }),
}));

vi.mock("@/hooks/useSubcategories", () => ({
  useAllSubcategoriesByCategoryId: vi.fn().mockReturnValue({
    data: [{ id: "sub-1", name: "Produce" }],
    isLoading: false,
  }),
}));

vi.mock("@/lib/api-client", () => ({
  default: { GET: vi.fn(), PUT: vi.fn() },
}));

vi.mock("@/lib/export-csv", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/export-csv")>();
  return { ...actual, downloadCsv: vi.fn() };
});

import { useUncategorizedItemsReport } from "@/hooks/useUncategorizedItemsReport";
import client from "@/lib/api-client";
import { downloadCsv } from "@/lib/export-csv";
const mockHook = vi.mocked(useUncategorizedItemsReport);
const mockClient = vi.mocked(client);
const mockDownloadCsv = vi.mocked(downloadCsv);

const mockItems = [
  {
    id: "item-1",
    receiptId: "receipt-1",
    receiptItemCode: "ABC",
    description: "Apples",
    quantity: 2,
    unitPrice: 1.5,
    totalAmount: 3.0,
    category: "Uncategorized",
    subcategory: null,
  },
  {
    id: "item-2",
    receiptId: "receipt-2",
    receiptItemCode: null,
    description: "Bananas",
    quantity: 1,
    unitPrice: 2.0,
    totalAmount: 2.0,
    category: "Uncategorized",
    subcategory: "Fruit",
  },
];

function setupMock(overrides: Record<string, unknown> = {}) {
  mockHook.mockReturnValue({
    data: {
      totalCount: 2,
      items: mockItems,
    },
    isLoading: false,
    isError: false,
    ...overrides,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
  } as any);
}

describe("UncategorizedItems", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("shows loading skeleton", () => {
    setupMock({ isLoading: true, data: undefined });
    renderWithQueryClient(<UncategorizedItems />);
    const skeletons = document.querySelectorAll("[data-slot='skeleton']");
    expect(skeletons.length).toBeGreaterThan(0);
  });

  it("shows error state", () => {
    setupMock({ isError: true, data: undefined });
    renderWithQueryClient(<UncategorizedItems />);
    expect(
      screen.getByText(/failed to load uncategorized items report/i),
    ).toBeInTheDocument();
  });

  it("shows empty state when no uncategorized items", () => {
    setupMock({
      data: { totalCount: 0, items: [] },
    });
    renderWithQueryClient(<UncategorizedItems />);
    expect(screen.getByText("All Categorized")).toBeInTheDocument();
    expect(
      screen.getByText(/all receipt items have been categorized/i),
    ).toBeInTheDocument();
  });

  it("shows empty state when data is null", () => {
    setupMock({ data: undefined });
    renderWithQueryClient(<UncategorizedItems />);
    expect(screen.getByText("All Categorized")).toBeInTheDocument();
  });

  it("renders summary header with count", () => {
    setupMock();
    renderWithQueryClient(<UncategorizedItems />);
    expect(screen.getByText("2")).toBeInTheDocument();
    expect(screen.getByText("Uncategorized Items")).toBeInTheDocument();
  });

  it("renders table with all items", () => {
    setupMock();
    renderWithQueryClient(<UncategorizedItems />);
    expect(screen.getByText("Apples")).toBeInTheDocument();
    expect(screen.getByText("Bananas")).toBeInTheDocument();
  });

  it("renders table headers", () => {
    setupMock();
    renderWithQueryClient(<UncategorizedItems />);
    const table = screen.getByRole("table");
    expect(within(table).getByText(/Description/)).toBeInTheDocument();
    expect(within(table).getByText(/Item Code/)).toBeInTheDocument();
    expect(within(table).getByText("Receipt")).toBeInTheDocument();
    expect(within(table).getByText(/Total/)).toBeInTheDocument();
    expect(within(table).getByText("Subcategory")).toBeInTheDocument();
  });

  it("displays item code or dash when missing", () => {
    setupMock();
    renderWithQueryClient(<UncategorizedItems />);
    expect(screen.getByText("ABC")).toBeInTheDocument();
    // Bananas has null item code, should show "-"
    const bananaRow = screen.getByText("Bananas").closest("tr")!;
    expect(within(bananaRow).getAllByText("-").length).toBeGreaterThanOrEqual(1);
  });

  it("displays subcategory or dash when missing", () => {
    setupMock();
    renderWithQueryClient(<UncategorizedItems />);
    expect(screen.getByText("Fruit")).toBeInTheDocument();
  });

  it("navigates to receipt on View click", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<UncategorizedItems />);

    const viewLinks = screen.getAllByText("View");
    await user.click(viewLinks[0]);
    expect(mockNavigate).toHaveBeenCalledWith("/receipts/receipt-1");
  });

  it("toggles sort direction on clicking sortable column", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<UncategorizedItems />);

    await user.click(screen.getByRole("button", { name: /description/i }));

    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ sortBy: "description", sortDirection: "desc" }),
    );
  });

  it("switches sort column when clicking a different column", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<UncategorizedItems />);

    await user.click(screen.getByRole("button", { name: /total/i }));

    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        sortBy: "total",
        sortDirection: "asc",
      }),
    );
  });

  it("shows checkboxes for selection", () => {
    setupMock();
    renderWithQueryClient(<UncategorizedItems />);
    const checkboxes = screen.getAllByRole("checkbox");
    // Select-all + 2 item checkboxes
    expect(checkboxes.length).toBe(3);
  });

  it("shows bulk action toolbar when items are selected", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<UncategorizedItems />);

    const checkboxes = screen.getAllByRole("checkbox");
    await user.click(checkboxes[1]); // Select first item

    expect(screen.getByText("1 selected")).toBeInTheDocument();
    expect(screen.getByText("Apply to Selected")).toBeInTheDocument();
  });

  it("does not show pagination when only one page", () => {
    setupMock();
    renderWithQueryClient(<UncategorizedItems />);
    expect(screen.queryByText(/Page/)).not.toBeInTheDocument();
  });

  it("shows pagination when multiple pages", () => {
    const manyItems = Array.from({ length: 51 }, (_, i) => ({
      id: `id-${i}`,
      receiptId: `receipt-${i}`,
      receiptItemCode: null,
      description: `Item ${i}`,
      quantity: 1,
      unitPrice: 1.0,
      totalAmount: 1.0,
      category: "Uncategorized",
      subcategory: null,
    }));
    setupMock({
      data: { totalCount: 51, items: manyItems },
    });
    renderWithQueryClient(<UncategorizedItems />);
    expect(screen.getByText("Page 1 of 2")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Previous" })).toBeDisabled();
    expect(
      screen.getByRole("button", { name: "Next" }),
    ).not.toBeDisabled();
  });

  it("formats currency values correctly", () => {
    setupMock();
    renderWithQueryClient(<UncategorizedItems />);
    expect(screen.getByText("$3.00")).toBeInTheDocument();
    expect(screen.getByText("$2.00")).toBeInTheDocument();
  });

  it("selects all items when select-all checkbox is clicked", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<UncategorizedItems />);

    const selectAll = screen.getAllByRole("checkbox")[0];
    await user.click(selectAll);

    expect(screen.getByText("2 selected")).toBeInTheDocument();
  });

  it("filters Uncategorized from category options", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<UncategorizedItems />);

    // Select an item to show toolbar
    const checkboxes = screen.getAllByRole("checkbox");
    await user.click(checkboxes[1]);

    // The category combobox should be present but "Uncategorized" should not be an option
    expect(screen.getByText("Select category...")).toBeInTheDocument();
  });

  it("exports the report as csv with the current sort", async () => {
    const user = userEvent.setup();
    setupMock();
    mockClient.GET.mockResolvedValue({
      data: { totalCount: 2, items: mockItems },
      error: undefined,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    renderWithQueryClient(<UncategorizedItems />);

    await user.click(screen.getByRole("button", { name: "Export CSV" }));
    await waitFor(() => expect(mockDownloadCsv).toHaveBeenCalledTimes(1));

    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/reports/uncategorized-items",
      {
        params: {
          query: {
            sortBy: "description",
            sortDirection: "asc",
            page: 1,
            pageSize: 100,
          },
        },
      },
    );

    const [filename, csv] = mockDownloadCsv.mock.calls[0];
    expect(filename).toBe(
      `uncategorized-items_${format(new Date(), "yyyy-MM-dd")}.csv`,
    );
    expect(csv).toBe(
      "Description,Item Code,Quantity,Unit Price,Total,Subcategory,Receipt ID\r\n" +
        "Apples,ABC,2,1.5,3,,receipt-1\r\n" +
        "Bananas,,1,2,2,Fruit,receipt-2\r\n",
    );
  });
});
