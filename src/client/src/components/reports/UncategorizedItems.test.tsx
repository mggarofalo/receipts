import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { format } from "date-fns";
import { renderWithQueryClient } from "@/test/test-utils";
import UncategorizedItems from "./UncategorizedItems";
import { taxonomyFixture } from "@/test/taxonomy-fixture";

const mockNavigate = vi.fn();
vi.mock("react-router", async () => {
  const actual = await vi.importActual("react-router");
  return { ...actual, useNavigate: () => mockNavigate };
});

vi.mock("@/hooks/useUncategorizedItemsReport", () => ({
  useUncategorizedItemsReport: vi.fn(),
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
import {
  chooseCategoryOption,
  findCategoryOption,
} from "@/test/category-picker";
const mockHook = vi.mocked(useUncategorizedItemsReport);
const mockClient = vi.mocked(client);
const mockDownloadCsv = vi.mocked(downloadCsv);

const mockItems = [
  {
    id: "item-1",
    receiptId: "receipt-1",
    receiptItemCode: "ABC",
    description: "Apples",
    location: "Corner Market",
    quantity: 2,
    unitPrice: 1.5,
    totalAmount: 3.0,
    totalAmountCurrency: "USD",
    classificationState: "missing_assignment",
    assignmentId: null,
    categoryId: null,
    taxonomyRevisionId: null,
  },
  {
    id: "item-2",
    receiptId: "receipt-2",
    receiptItemCode: null,
    description: "Bananas",
    location: "Fruit Stand",
    quantity: 1,
    unitPrice: 2.0,
    totalAmount: 2.0,
    totalAmountCurrency: "USD",
    classificationState: "unresolved_legacy",
    assignmentId: "assignment-2",
    categoryId: null,
    taxonomyRevisionId: "old-revision",
    suggestion: {
      attemptId: "attempt-2",
      state: "suggested",
      categoryId: taxonomyFixture.nodes[1].id,
      confidence: 0.9,
      abstentionReason: null,
      rootCandidates: [
        { categoryId: taxonomyFixture.nodes[0].id, probability: 0.97 },
      ],
      leafCandidates: [
        { categoryId: taxonomyFixture.nodes[1].id, probability: 0.9 },
      ],
    },
  },
];

function setupMock(overrides: Record<string, unknown> = {}) {
  mockHook.mockReturnValue({
    data: {
      totalCount: 2,
      items: mockItems,
      suggestionsAvailable: true,
    },
    isLoading: false,
    isError: false,
    ...overrides,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
  } as any);
}

/** Answers the export read with the report and the picker read with the taxonomy. */
function mockReportExport() {
  mockClient.GET.mockImplementation(
    async (path) =>
      ({
        data:
          path === "/api/taxonomy"
            ? taxonomyFixture
            : { totalCount: 2, items: mockItems, suggestionsAvailable: true },
        error: undefined,
        response: {} as Response,
      }) as never,
  );
}

describe("UncategorizedItems", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockClient.GET.mockImplementation(
      async (path) =>
        ({
          data: path === "/api/taxonomy" ? taxonomyFixture : undefined,
          error: undefined,
          response: {} as Response,
        }) as never,
    );
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
      data: { totalCount: 0, items: [], suggestionsAvailable: true },
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
    expect(within(table).getByText("Item type")).toBeInTheDocument();
  });

  it("displays item code or dash when missing", () => {
    setupMock();
    renderWithQueryClient(<UncategorizedItems />);
    expect(screen.getByText("ABC")).toBeInTheDocument();
    // Bananas has null item code, should show "-"
    const bananaRow = screen.getByText("Bananas").closest("tr")!;
    expect(within(bananaRow).getAllByText("-").length).toBeGreaterThanOrEqual(
      1,
    );
  });

  it("shows each item's store under its description", () => {
    setupMock();
    renderWithQueryClient(<UncategorizedItems />);
    const appleRow = screen.getByText("Apples").closest("tr")!;
    expect(within(appleRow).getByText("Corner Market")).toBeInTheDocument();
    const bananaRow = screen.getByText("Bananas").closest("tr")!;
    expect(within(bananaRow).getByText("Fruit Stand")).toBeInTheDocument();
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

    expect(screen.getByText("1 selected on this page")).toBeInTheDocument();
    expect(screen.getByText("Apply to Selected")).toBeInTheDocument();
  });

  it("keeps page size available when only one page", () => {
    setupMock();
    renderWithQueryClient(<UncategorizedItems />);
    expect(
      screen.getByRole("combobox", { name: "Rows per page" }),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Next page" })).toBeDisabled();
  });

  it("shows pagination when multiple pages", () => {
    const manyItems = Array.from({ length: 51 }, (_, i) => ({
      id: `id-${i}`,
      receiptId: `receipt-${i}`,
      receiptItemCode: null,
      description: `Item ${i}`,
      location: "Store",
      quantity: 1,
      unitPrice: 1.0,
      totalAmount: 1.0,
      totalAmountCurrency: "USD",
      classificationState: "missing_assignment",
      assignmentId: null,
      categoryId: null,
      taxonomyRevisionId: null,
    }));
    setupMock({
      data: { totalCount: 51, items: manyItems, suggestionsAvailable: true },
    });
    renderWithQueryClient(<UncategorizedItems />);
    expect(screen.getByText("1/2")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Previous page" }),
    ).toBeDisabled();
    expect(
      screen.getByRole("button", { name: "Next page" }),
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

    expect(screen.getByText("2 selected on this page")).toBeInTheDocument();
  });

  it("offers the nested taxonomy picker for selected items", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<UncategorizedItems />);

    // Select an item to show toolbar
    const checkboxes = screen.getAllByRole("checkbox");
    await user.click(checkboxes[1]);

    expect(
      await screen.findByRole("combobox", {
        name: "Category for selected items",
      }),
    ).toBeEnabled();
    await user.click(
      screen.getByRole("combobox", { name: "Category for selected items" }),
    );
    expect(
      screen.getByRole("option", { name: "Unclassified" }),
    ).toBeInTheDocument();
    expect(await findCategoryOption(user, "Food / Dairy")).toBeInTheDocument();
  });

  it("does not submit an explicit Unclassified choice as a categorization", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<UncategorizedItems />);
    await user.click(screen.getByRole("checkbox", { name: "Select Apples" }));
    await user.click(
      await screen.findByRole("combobox", {
        name: "Category for selected items",
      }),
    );
    await user.click(screen.getByRole("option", { name: "Unclassified" }));
    expect(
      screen.getByRole("button", { name: "Apply to Selected" }),
    ).toBeDisabled();
    expect(mockClient.PUT).not.toHaveBeenCalled();
  });

  it("exports the report as csv with the current sort", async () => {
    const user = userEvent.setup();
    setupMock();
    mockReportExport();

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
            suggestion: "any",
          },
        },
      },
    );

    const [filename, csv] = mockDownloadCsv.mock.calls[0];
    expect(filename).toBe(
      `uncategorized-items_${format(new Date(), "yyyy-MM-dd")}.csv`,
    );
    expect(csv).toBe(
      "Description,Item Code,Quantity,Unit Price,Total,Classification state,Currency,Receipt ID\r\n" +
        "Apples,ABC,2,1.5,3,missing_assignment,USD,receipt-1\r\n" +
        "Bananas,,1,2,2,unresolved_legacy,USD,receipt-2\r\n",
    );
  });

  it("reads sort and page from the URL on load", () => {
    setupMock();
    renderWithQueryClient(<UncategorizedItems />, {
      route: "/?sortBy=total&sortDirection=desc&page=2",
    });

    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        sortBy: "total",
        sortDirection: "desc",
        page: 2,
      }),
    );
  });

  it("falls back to defaults for malformed URL params instead of crashing", () => {
    setupMock();
    renderWithQueryClient(<UncategorizedItems />, {
      route: "/?sortBy=nonsense&sortDirection=up&page=0",
    });

    expect(screen.getByRole("table")).toBeInTheDocument();
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        sortBy: "description",
        sortDirection: "asc",
        page: 1,
      }),
    );
  });

  it("defaults the Jev suggestion filter to All", () => {
    setupMock();
    renderWithQueryClient(<UncategorizedItems />);
    const filter = screen.getByRole("group", {
      name: "Jev suggestion filter",
    });
    expect(within(filter).getByRole("button", { name: "All" })).toHaveAttribute(
      "aria-pressed",
      "true",
    );
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ suggestion: "any" }),
    );
  });

  it.each([
    ["Jev answered", "with"],
    ["Not asked", "without"],
  ])(
    "requests %s items from page 1 and clears the selection",
    async (label, value) => {
      const user = userEvent.setup();
      setupMock();
      renderWithQueryClient(<UncategorizedItems />, {
        route: "/?page=3",
      });
      await user.click(screen.getByRole("checkbox", { name: "Select Apples" }));
      expect(screen.getByText("1 selected on this page")).toBeInTheDocument();

      await user.click(screen.getByRole("button", { name: label }));

      expect(mockHook).toHaveBeenLastCalledWith(
        expect.objectContaining({ suggestion: value, page: 1 }),
      );
      expect(screen.getByRole("button", { name: label })).toHaveAttribute(
        "aria-pressed",
        "true",
      );
      expect(
        screen.queryByText("1 selected on this page"),
      ).not.toBeInTheDocument();
    },
  );

  it("reads the suggestion filter from the URL and exports with it", async () => {
    const user = userEvent.setup();
    setupMock();
    mockReportExport();
    renderWithQueryClient(<UncategorizedItems />, {
      route: "/?suggestion=with",
    });
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ suggestion: "with" }),
    );

    await user.click(screen.getByRole("button", { name: "Export CSV" }));
    await waitFor(() => expect(mockDownloadCsv).toHaveBeenCalledTimes(1));
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/reports/uncategorized-items",
      {
        params: {
          query: expect.objectContaining({ suggestion: "with" }),
        },
      },
    );
  });

  it("falls back to All for an unknown suggestion filter", () => {
    setupMock();
    renderWithQueryClient(<UncategorizedItems />, {
      route: "/?suggestion=maybe",
    });
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ suggestion: "any" }),
    );
  });

  it("hides the Jev filter when category assistance has no answers to offer", () => {
    setupMock({
      data: { totalCount: 2, items: mockItems, suggestionsAvailable: false },
    });
    renderWithQueryClient(<UncategorizedItems />);
    expect(screen.getByText("Apples")).toBeInTheDocument();
    expect(
      screen.queryByRole("group", { name: "Jev suggestion filter" }),
    ).not.toBeInTheDocument();
  });

  it("shows the usual empty state, not the filter one, without category assistance", () => {
    setupMock({
      data: { totalCount: 0, items: [], suggestionsAvailable: false },
    });
    renderWithQueryClient(<UncategorizedItems />, {
      route: "/?suggestion=without",
    });
    expect(screen.getByText("All Categorized")).toBeInTheDocument();
    expect(
      screen.queryByText("No uncategorized items match this filter."),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole("group", { name: "Jev suggestion filter" }),
    ).not.toBeInTheDocument();
  });

  it("says nothing matches when a filter leaves no items", async () => {
    const user = userEvent.setup();
    setupMock({
      data: { totalCount: 0, items: [], suggestionsAvailable: true },
    });
    renderWithQueryClient(<UncategorizedItems />, {
      route: "/?suggestion=without",
    });

    expect(
      screen.getByText("No uncategorized items match this filter."),
    ).toBeInTheDocument();
    expect(screen.queryByText("All Categorized")).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "All" }));
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ suggestion: "any", page: 1 }),
    );
    expect(screen.getByText("All Categorized")).toBeInTheDocument();
  });

  it("writes Jev's choice for one row with that item's assignment fence", async () => {
    const user = userEvent.setup();
    setupMock();
    mockClient.PUT.mockResolvedValue({
      data: undefined,
      error: undefined,
      response: new Response(null, { status: 204 }),
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);
    renderWithQueryClient(<UncategorizedItems />);

    const appleRow = screen.getByText("Apples").closest("tr")!;
    expect(
      within(appleRow).queryByRole("button", { name: /Use Jev's choice/ }),
    ).not.toBeInTheDocument();

    await user.click(
      await screen.findByRole("button", {
        name: "Use Jev's choice for Bananas",
      }),
    );

    await waitFor(() => expect(mockClient.PUT).toHaveBeenCalledTimes(1));
    expect(mockClient.PUT).toHaveBeenCalledWith(
      "/api/receipt-items/{id}/classification",
      expect.objectContaining({
        params: { path: { id: "item-2" } },
        body: expect.objectContaining({
          categoryId: taxonomyFixture.nodes[1].id,
          taxonomyRevisionId: taxonomyFixture.id,
          expectedAssignmentId: "assignment-2",
          reason: "Jev's suggestion accepted from unclassified items report",
        }),
      }),
    );
  });

  it("writes a row's picked category with the report's default reason", async () => {
    const user = userEvent.setup();
    setupMock();
    mockClient.PUT.mockResolvedValue({
      data: undefined,
      error: undefined,
      response: new Response(null, { status: 204 }),
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);
    renderWithQueryClient(<UncategorizedItems />);

    await user.click(
      await screen.findByRole("combobox", { name: "Category for Apples" }),
    );
    await chooseCategoryOption(user, "Food / Dairy");

    await waitFor(() => expect(mockClient.PUT).toHaveBeenCalledTimes(1));
    expect(mockClient.PUT).toHaveBeenCalledWith(
      "/api/receipt-items/{id}/classification",
      expect.objectContaining({
        params: { path: { id: "item-1" } },
        body: expect.objectContaining({
          categoryId: taxonomyFixture.nodes[1].id,
          taxonomyRevisionId: taxonomyFixture.id,
          expectedAssignmentId: null,
          reason: "Category selected from unclassified items report",
        }),
      }),
    );
  });
});
