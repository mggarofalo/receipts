import type { components } from "@/generated/api";
import { fireEvent, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useLocation, useNavigate } from "react-router";
import { renderWithProviders } from "@/test/test-utils";
import { mockQueryResult } from "@/test/mock-hooks";
import { usePurchaseSearch } from "@/hooks/usePurchaseSearch";
import PurchaseSearch from "./PurchaseSearch";
vi.mock("@/hooks/usePurchaseSearch", () => ({ usePurchaseSearch: vi.fn() }));
const search = vi.mocked(usePurchaseSearch);
const item: components["schemas"]["PurchaseSearchItem"] = {
  id: "item-1",
  receiptId: "receipt-1",
  description: "Whole milk",
  receiptItemCode: "MILK-42",
  normalizedDescription: "Milk",
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
  search.mockReset();
  search.mockReturnValue(
    mockQueryResult({
      isPending: false,
      data: { items: [item], total: 26, offset: 0, limit: 25 },
    }),
  );
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
  expect(screen.getByText("Normalized: Milk")).toBeInTheDocument();
  expect(screen.getByRole("link", { name: "View receipt" })).toHaveAttribute(
    "href",
    "/receipts/receipt-1",
  );
});

it("paginates through URL state and resets the page when a new phrase is submitted", async () => {
  const user = userEvent.setup();
  renderPage();
  expect(screen.getByRole("button", { name: "Previous" })).toBeDisabled();
  await user.click(screen.getByRole("button", { name: "Next" }));
  expect(search).toHaveBeenLastCalledWith("milk", 25, 25);
  expect(screen.getByLabelText("Current URL")).toHaveTextContent("offset=25");
  await user.clear(screen.getByRole("textbox", { name: "Search purchases" }));
  await user.type(
    screen.getByRole("textbox", { name: "Search purchases" }),
    "  bread  ",
  );
  await user.click(screen.getByRole("button", { name: "Search" }));
  expect(search).toHaveBeenLastCalledWith("bread", 0, 25);
  expect(screen.getByLabelText("Current URL")).toHaveTextContent("?q=bread");
});

it.each(["-1", "nope", "2147483648", "9007199254740993"])(
  "rejects invalid URL offset %s",
  (offset) => {
    renderPage(`/search?q=milk&offset=${offset}`);
    expect(search).toHaveBeenCalledWith("milk", 0, 25);
  },
);

it("explains empty search without presenting results", () => {
  renderPage("/search");
  expect(screen.getByText(/Enter between 2 and 200/)).toBeInTheDocument();
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
      data: { items: [], total: 1, offset: 25, limit: 25 },
    }),
  );
  renderPage("/search?q=milk&offset=25");
  expect(screen.getByText(/No purchases on this page/)).toBeInTheDocument();
  search.mockReturnValue(
    mockQueryResult({
      isPending: false,
      data: { items: [], total: 0, offset: 0, limit: 25 },
    }),
  );
  await userEvent.click(screen.getByRole("button", { name: "First page" }));
  await waitFor(() => expect(search).toHaveBeenLastCalledWith("milk", 0, 25));
  expect(
    screen.getByText(/No purchases match this phrase/),
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
    expect(search).toHaveBeenLastCalledWith("milk", 25, 25);
  },
);
it("restores submitted query and page with browser Back, discarding the other search draft", async () => {
  renderPage("/search?q=milk&offset=25");
  fireEvent.change(screen.getByRole("textbox", { name: "Search purchases" }), {
    target: { value: "hot dogs" },
  });
  await userEvent.click(screen.getByRole("button", { name: "Search" }));
  await waitFor(() =>
    expect(search).toHaveBeenLastCalledWith("hot dogs", 0, 25),
  );
  fireEvent.change(screen.getByRole("textbox", { name: "Search purchases" }), {
    target: { value: "unsubmitted bread" },
  });
  await userEvent.click(screen.getByRole("button", { name: "Browser Back" }));
  expect(screen.getByRole("textbox", { name: "Search purchases" })).toHaveValue(
    "milk",
  );
  expect(search).toHaveBeenLastCalledWith("milk", 25, 25);
});
