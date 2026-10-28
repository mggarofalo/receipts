import type { components } from "@/generated/api";
import { fireEvent, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useLocation, useNavigate } from "react-router";
import { renderWithProviders } from "@/test/test-utils";
import { mockQueryResult } from "@/test/mock-hooks";
import { usePurchaseSearch } from "@/hooks/usePurchaseSearch";
import { useCategoryTaxonomy } from "@/hooks/useCategoryTaxonomy";
import { taxonomyFixture } from "@/test/taxonomy-fixture";
import PurchaseSearch from "./PurchaseSearch";
vi.mock("@/hooks/usePurchaseSearch", () => ({ usePurchaseSearch: vi.fn() }));
vi.mock("@/hooks/useCategoryTaxonomy", () => ({
  useCategoryTaxonomy: vi.fn(),
}));
const search = vi.mocked(usePurchaseSearch);
const taxonomy = vi.mocked(useCategoryTaxonomy);
const item: components["schemas"]["PurchaseSearchItem"] = {
  id: "item-1",
  receiptId: "receipt-1",
  categoryState: "unclassified",
  categoryPath: [],
  description: "Whole milk",
  receiptItemCode: "MILK-42",
  normalizedDescription: "Milk",
  normalizationReviewState: "verified",
  date: "2020-01-15",
  location: "Old store",
  quantity: 2,
  unitPrice: 1.2345,
  unitPriceCurrency: "USD",
  totalAmount: 2.47,
  totalAmountCurrency: "USD",
};
function Location() {
  const location = useLocation();
  const navigate = useNavigate();
  return (
    <>
      <output aria-label="Current URL">{location.search}</output>
      <button onClick={() => navigate(-1)}>Browser Back</button>
    </>
  );
}
function renderPage(route = "/search?q=milk") {
  return renderWithProviders(
    <>
      <PurchaseSearch />
      <Location />
    </>,
    { route },
  );
}
beforeEach(() => {
  localStorage.clear();
  search.mockReset();
  taxonomy.mockReturnValue(
    mockQueryResult({
      isPending: false,
      isLoading: false,
      data: taxonomyFixture,
    }),
  );
  search.mockReturnValue(
    mockQueryResult({
      isPending: false,
      data: {
        categoryView: "asClassified",
        assignmentSelection: "currentPointer",
        items: [item],
        total: 26,
        offset: 0,
        limit: 25,
      },
    }),
  );
});

function currentParams() {
  return new URLSearchParams(
    screen.getByLabelText("Current URL").textContent ?? "",
  );
}

it("sorts on the server and resets offset while preserving filters and unrelated URL state", async () => {
  renderPage(
    "/search?q=milk&offset=50&location=Market&minUnitPrice=1.2345&limit=25&unrelated=keep",
  );
  await userEvent.click(screen.getByRole("button", { name: "Unit price" }));
  expect(currentParams().get("offset")).toBeNull();
  expect(currentParams().get("location")).toBe("Market");
  expect(currentParams().get("unrelated")).toBe("keep");
  expect(search).toHaveBeenLastCalledWith(
    "milk",
    0,
    25,
    true,
    expect.objectContaining({
      sortBy: "unitPrice",
      sortDirection: "asc",
      location: "Market",
      minUnitPrice: 1.2345,
    }),
  );
  expect(
    screen.getByRole("columnheader", { name: "Unit price" }),
  ).toHaveAttribute("aria-sort", "ascending");
  await userEvent.click(screen.getByRole("button", { name: "Unit price" }));
  expect(search).toHaveBeenLastCalledWith(
    "milk",
    0,
    25,
    true,
    expect.objectContaining({ sortBy: "unitPrice", sortDirection: "desc" }),
  );
});

it("applies filters atomically and preserves them when submitting another phrase", async () => {
  renderPage("/search?q=milk&offset=50&sortBy=quantity&sortDirection=asc");
  const calls = search.mock.calls.length;
  fireEvent.change(screen.getByLabelText("Store contains"), {
    target: { value: "  New store  " },
  });
  fireEvent.change(screen.getByLabelText("Minimum unit price"), {
    target: { value: "0" },
  });
  expect(search).toHaveBeenCalledTimes(calls);
  await userEvent.click(screen.getByRole("button", { name: "Apply filters" }));
  await waitFor(() =>
    expect(search).toHaveBeenLastCalledWith(
      "milk",
      0,
      25,
      true,
      expect.objectContaining({
        location: "New store",
        minUnitPrice: 0,
        sortBy: "quantity",
        sortDirection: "asc",
      }),
    ),
  );
  await waitFor(() => {
    expect(currentParams().get("location")).toBe("New store");
    expect(currentParams().get("minUnitPrice")).toBe("0");
    expect(currentParams().get("offset")).toBeNull();
  });
  fireEvent.change(screen.getByRole("textbox", { name: "Search purchases" }), {
    target: { value: "hot dogs" },
  });
  await userEvent.click(screen.getByRole("button", { name: "Search" }));
  await waitFor(() => expect(currentParams().get("q")).toBe("hot dogs"));
  expect(currentParams().get("location")).toBe("New store");
  expect(currentParams().get("minUnitPrice")).toBe("0");
  expect(currentParams().get("sortBy")).toBe("quantity");
});

it("remembers rows per page only for purchase history and resets offset without losing filters", async () => {
  const user = userEvent.setup();
  const view = renderPage("/search?q=milk&offset=25&location=Market");
  screen.getByRole("combobox", { name: "Rows per page" }).focus();
  await user.keyboard("{Enter}{End}{Enter}");
  await waitFor(() =>
    expect(search).toHaveBeenLastCalledWith(
      "milk",
      0,
      100,
      true,
      expect.objectContaining({ location: "Market" }),
    ),
  );
  expect(currentParams().get("limit")).toBe("100");
  expect(localStorage.getItem("table-page-size:purchase-history")).toBe("100");
  expect(localStorage.getItem("table-page-size:receipts")).toBeNull();
  view.unmount();
  renderPage("/search?q=milk");
  expect(search).toHaveBeenLastCalledWith(
    "milk",
    0,
    100,
    true,
    expect.objectContaining({ sortBy: "date" }),
  );
});

it.each([
  "startDate=2025-02-30",
  "minUnitPrice=-1",
  "startDate=2025-02-02&endDate=2025-02-01",
])("rejects invalid linked filters without searching: %s", (filter) => {
  renderPage(`/search?q=milk&${filter}`);
  expect(screen.getByRole("alert")).toHaveTextContent(
    "Some filters are invalid",
  );
  expect(search.mock.calls.at(-1)?.[3]).toBe(false);
  expect(
    screen.queryByRole("table", { name: "Purchase results" }),
  ).not.toBeInTheDocument();
});

it("shows historical purchase evidence with precision and a receipt link", () => {
  renderPage();
  expect(
    screen.getByRole("heading", { name: "Purchase history" }),
  ).toBeInTheDocument();
  expect(
    screen.getByRole("table", { name: "Purchase results" }),
  ).toBeInTheDocument();
  expect(screen.getByText(/Old store/)).toBeInTheDocument();
  expect(screen.getByText(/USD\s1\.2345/)).toBeInTheDocument();
  expect(screen.getByText(/USD\s2\.47/)).toBeInTheDocument();
  expect(screen.getByText("Code: MILK-42")).toBeInTheDocument();
  expect(screen.getByText(/Verified product\s*:\s*Milk/)).toBeInTheDocument();
  expect(screen.getByRole("link", { name: "View receipt" })).toHaveAttribute(
    "href",
    "/receipts/receipt-1",
  );
});

it("paginates through URL state and resets the page when a new phrase is submitted", async () => {
  const user = userEvent.setup();
  renderPage();
  expect(screen.getByRole("button", { name: "Previous page" })).toBeDisabled();
  await user.click(screen.getByRole("button", { name: "Next page" }));
  await waitFor(() =>
    expect(screen.getByLabelText("Current URL")).toHaveTextContent(
      /^\?q=milk&offset=25$/,
    ),
  );
  expect(search).toHaveBeenLastCalledWith(
    "milk",
    25,
    25,
    true,
    expect.objectContaining({ sortBy: "date", sortDirection: "desc" }),
  );
  await user.clear(screen.getByRole("textbox", { name: "Search purchases" }));
  await user.type(
    screen.getByRole("textbox", { name: "Search purchases" }),
    "  bread  ",
  );
  await user.click(screen.getByRole("button", { name: "Search" }));
  // The async resolver and router transition may render before the URL commits.
  await waitFor(() =>
    expect(screen.getByLabelText("Current URL")).toHaveTextContent(
      /^\?q=bread$/,
    ),
  );
  expect(search).toHaveBeenLastCalledWith(
    "bread",
    0,
    25,
    true,
    expect.objectContaining({ sortBy: "date", sortDirection: "desc" }),
  );
});

it.each(["-1", "nope", "2147483648", "9007199254740993"])(
  "rejects invalid URL offset %s",
  (offset) => {
    renderPage(`/search?q=milk&offset=${offset}`);
    expect(search).toHaveBeenCalledWith(
      "milk",
      0,
      25,
      true,
      expect.objectContaining({ sortBy: "date", sortDirection: "desc" }),
    );
  },
);

it("explains empty search without presenting results", () => {
  renderPage("/search");
  expect(
    screen.getByPlaceholderText("Search item names, codes, or stores"),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole("table", { name: "Purchase results" }),
  ).not.toBeInTheDocument();
});
it("shows loading and retryable failures", async () => {
  search.mockReturnValue(mockQueryResult());
  const view = renderPage();
  expect(screen.getByText("Searching purchases…")).toHaveTextContent(
    "Searching purchases",
  );
  const retry = vi.fn();
  search.mockReturnValue(
    mockQueryResult({ isPending: false, isError: true, refetch: retry }),
  );
  view.rerender(
    <>
      <PurchaseSearch />
      <Location />
    </>,
  );
  expect(
    screen.getByText("Purchase history could not be loaded."),
  ).toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: /retry/i }));
  expect(retry).toHaveBeenCalledOnce();
});
it("distinguishes a stale empty page from no matching purchases", async () => {
  search.mockReturnValue(
    mockQueryResult({
      isPending: false,
      data: {
        categoryView: "asClassified",
        assignmentSelection: "currentPointer",
        items: [],
        total: 1,
        offset: 25,
        limit: 25,
      },
    }),
  );
  renderPage("/search?q=milk&offset=25");
  expect(screen.getByText(/No purchases on this page/)).toBeInTheDocument();
  search.mockReturnValue(
    mockQueryResult({
      isPending: false,
      data: {
        categoryView: "asClassified",
        assignmentSelection: "currentPointer",
        items: [],
        total: 0,
        offset: 0,
        limit: 25,
      },
    }),
  );
  await userEvent.click(screen.getByRole("button", { name: "First page" }));
  await waitFor(() =>
    expect(screen.getByLabelText("Current URL")).toHaveTextContent(
      /^\?q=milk$/,
    ),
  );
  expect(search).toHaveBeenLastCalledWith(
    "milk",
    0,
    25,
    true,
    expect.objectContaining({ sortBy: "date", sortDirection: "desc" }),
  );
  expect(
    screen.getByText(/No purchases match these filters/),
  ).toBeInTheDocument();
});

it.each([
  ["   ", "Enter at least 2 characters."],
  ["x".repeat(201), "Use at most 200 characters."],
])(
  "rejects invalid submitted phrase without replacing the current results",
  async (draft, message) => {
    renderPage("/search?q=milk&offset=25");
    fireEvent.change(
      screen.getByRole("textbox", { name: "Search purchases" }),
      { target: { value: draft } },
    );
    await userEvent.click(screen.getByRole("button", { name: "Search" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(message);
    expect(screen.getByLabelText("Current URL")).toHaveTextContent(
      "?q=milk&offset=25",
    );
    expect(search).toHaveBeenLastCalledWith(
      "milk",
      25,
      25,
      true,
      expect.objectContaining({ sortBy: "date", sortDirection: "desc" }),
    );
  },
);
it("restores submitted query and page with browser Back, discarding the other search draft", async () => {
  renderPage("/search?q=milk&offset=25");
  fireEvent.change(screen.getByRole("textbox", { name: "Search purchases" }), {
    target: { value: "hot dogs" },
  });
  await userEvent.click(screen.getByRole("button", { name: "Search" }));
  await waitFor(() =>
    expect(screen.getByLabelText("Current URL")).toHaveTextContent(
      /^\?q=hot\+dogs$/,
    ),
  );
  expect(search).toHaveBeenLastCalledWith(
    "hot dogs",
    0,
    25,
    true,
    expect.objectContaining({ sortBy: "date", sortDirection: "desc" }),
  );
  fireEvent.change(screen.getByRole("textbox", { name: "Search purchases" }), {
    target: { value: "unsubmitted bread" },
  });
  await userEvent.click(screen.getByRole("button", { name: "Browser Back" }));
  await waitFor(() =>
    expect(screen.getByLabelText("Current URL")).toHaveTextContent(
      /^\?q=milk&offset=25$/,
    ),
  );
  expect(screen.getByRole("textbox", { name: "Search purchases" })).toHaveValue(
    "milk",
  );
  expect(search).toHaveBeenLastCalledWith(
    "milk",
    25,
    25,
    true,
    expect.objectContaining({ sortBy: "date", sortDirection: "desc" }),
  );
});

it("loads GUID history without a placeholder phrase and clears identity for a new search", async () => {
  const id = "0d196918-51ca-4ddd-a4cd-039720bab253";
  renderPage(
    `/search?normalizedDescriptionId=${id}&startDate=2025-01-01&offset=25`,
  );
  expect(search).toHaveBeenLastCalledWith(
    "",
    25,
    25,
    true,
    expect.objectContaining({
      normalizedDescriptionId: id,
      includeUnreviewed: false,
    }),
  );
  expect(screen.getByText(/Filtered to one product identity/)).toBeVisible();
  await userEvent.type(
    screen.getByRole("textbox", { name: "Search purchases" }),
    "bread",
  );
  await userEvent.click(screen.getByRole("button", { name: "Search" }));
  await waitFor(() => expect(currentParams().get("q")).toBe("bread"));
  expect(currentParams().get("normalizedDescriptionId")).toBeNull();
  expect(currentParams().get("offset")).toBeNull();
  expect(currentParams().get("startDate")).toBe("2025-01-01");
});

it("includes unreviewed associations only after an explicit opt in", async () => {
  renderPage("/search?q=milk&offset=25");
  expect(search).toHaveBeenLastCalledWith(
    "milk",
    25,
    25,
    true,
    expect.objectContaining({ includeUnreviewed: false }),
  );
  await userEvent.click(
    screen.getByRole("checkbox", {
      name: "Include unreviewed product associations",
    }),
  );
  expect(currentParams().get("includeUnreviewed")).toBe("true");
  expect(currentParams().get("offset")).toBeNull();
  expect(search).toHaveBeenLastCalledWith(
    "milk",
    0,
    25,
    true,
    expect.objectContaining({ includeUnreviewed: true }),
  );
});

it("shows length validation only when a submitted query needs it", async () => {
  renderPage("/search");
  expect(screen.queryByText(/Enter between 2 and 200/)).not.toBeInTheDocument();
  expect(
    screen.getByPlaceholderText("Search item names, codes, or stores"),
  ).toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Search" }));
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Enter at least 2 characters.",
  );
  expect(
    screen.getByText(
      /package sizes and measurement units are not standardized/,
    ),
  ).toBeInTheDocument();
});

it("loads category-only history from a shared URL and changes category views without losing filters", async () => {
  const categoryId = taxonomyFixture.nodes[1].id;
  renderPage(
    `/search?categoryId=${categoryId}&categoryView=currentHierarchy&location=Market&offset=25`,
  );
  expect(search).toHaveBeenLastCalledWith(
    "",
    25,
    25,
    true,
    expect.objectContaining({
      categoryId,
      categoryView: "currentHierarchy",
      location: "Market",
    }),
  );
  const picker = screen.getByRole("combobox", { name: "Category" });
  expect(picker).toHaveAttribute("title", "Food / Dairy");
  expect(picker).toHaveTextContent("Dairy");
  expect(picker).toHaveTextContent("Food");
  await userEvent.selectOptions(
    screen.getByLabelText("Category view"),
    "asClassified",
  );
  expect(currentParams().get("offset")).toBeNull();
  expect(currentParams().get("location")).toBe("Market");
  expect(search).toHaveBeenLastCalledWith(
    "",
    0,
    25,
    true,
    expect.objectContaining({
      categoryId,
      categoryView: "asClassified",
      location: "Market",
    }),
  );
});

it("selects a nested path, resets paging, and shows category paths and unresolved states", async () => {
  search.mockReturnValue(
    mockQueryResult({
      isPending: false,
      data: {
        categoryView: "asClassified",
        assignmentSelection: "currentPointer",
        items: [
          {
            ...item,
            categoryState: "classified",
            categoryPath: [
              {
                categoryId: taxonomyFixture.nodes[0].id,
                name: "Food",
                archived: false,
              },
              {
                categoryId: taxonomyFixture.nodes[1].id,
                name: "Dairy",
                archived: false,
              },
            ],
          },
          {
            ...item,
            id: "item-2",
            categoryState: "unresolvedLegacy",
            categoryPath: [],
          },
        ],
        total: 2,
        offset: 0,
        limit: 25,
      },
    }),
  );
  renderPage("/search?q=milk&offset=25&sortBy=quantity");
  await userEvent.click(screen.getByRole("combobox", { name: "Category" }));
  await userEvent.click(screen.getByRole("option", { name: /Food \/ Dairy/ }));
  expect(currentParams().get("categoryId")).toBe(taxonomyFixture.nodes[1].id);
  expect(currentParams().get("offset")).toBeNull();
  expect(currentParams().get("sortBy")).toBe("quantity");
  expect(
    screen.getByText("Food / Dairy", { selector: "div" }),
  ).toBeInTheDocument();
  expect(screen.getByText("Legacy category unresolved")).toBeInTheDocument();
});

it("preserves a category filter when the current taxonomy cannot resolve it", () => {
  const oldId = "95000000-0000-4000-8000-000000000099";
  renderPage(`/search?categoryId=${oldId}`);
  expect(
    screen.getByText(/no longer in the current taxonomy/),
  ).toBeInTheDocument();
  expect(search).toHaveBeenLastCalledWith(
    "",
    0,
    25,
    true,
    expect.objectContaining({ categoryId: oldId }),
  );
});

it("shows the API reason when a captured hierarchy revision is invalid", () => {
  search.mockReturnValue(
    mockQueryResult({
      isPending: false,
      isError: true,
      error: {
        status: 400,
        detail: "The captured taxonomy revision is unavailable.",
      },
    }),
  );
  renderPage(
    `/search?categoryId=${taxonomyFixture.nodes[1].id}&categoryView=currentHierarchy&capturedCurrentRevisionId=95000000-0000-4000-8000-000000000099`,
  );
  expect(
    screen.getByText("The captured taxonomy revision is unavailable."),
  ).toBeInTheDocument();
});
