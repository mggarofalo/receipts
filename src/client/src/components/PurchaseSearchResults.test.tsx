import type { components } from "@/generated/api";
import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createWrapper } from "@/test/test-utils";
import { mockQueryResult } from "@/test/mock-hooks";
import { Command, CommandList } from "@/components/ui/command";
import { usePurchaseSearch } from "@/hooks/usePurchaseSearch";
import { PurchaseSearchResults } from "./PurchaseSearchResults";
vi.mock("@/hooks/usePurchaseSearch", () => ({ usePurchaseSearch: vi.fn() }));
const navigate = vi.fn();
vi.mock("react-router", async () => ({
  ...(await vi.importActual("react-router")),
  useNavigate: () => navigate,
}));
const search = vi.mocked(usePurchaseSearch);
const item: components["schemas"]["PurchaseSearchItem"] = {
  id: "i1",
  receiptId: "r1",
  categoryState: "unclassified",
  categoryPath: [],
  description: "Historic milk",
  date: "2020-01-15",
  location: "Store",
  unitPrice: 1.2345,
  unitPriceCurrency: "USD",
  quantity: 2,
  totalAmount: 2.47,
  totalAmountCurrency: "USD",
};
const close = vi.fn();
function Subject({
  query = "milk",
  open = true,
}: {
  query?: string;
  open?: boolean;
}) {
  return (
    <Command>
      <CommandList>
        <PurchaseSearchResults query={query} open={open} close={close} />
      </CommandList>
    </Command>
  );
}
beforeAll(() => {
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
  Element.prototype.scrollIntoView = vi.fn();
});
beforeEach(() => {
  vi.clearAllMocks();
  search.mockReturnValue(
    mockQueryResult({
      isPending: false,
      data: {
        categoryView: "asClassified",
        assignmentSelection: "currentPointer",
        items: [item],
        total: 42,
        offset: 0,
        limit: 5,
      },
    }),
  );
});
afterEach(() => vi.useRealTimers());

it("offers a receipt and all matches with explicit price and provenance", async () => {
  render(<Subject />, { wrapper: createWrapper() });
  expect(search).toHaveBeenCalledWith("milk", 0, 5, true);
  expect(screen.getByText(/USD\s1\.2345/)).toHaveTextContent(/2020.*Store/);
  await userEvent.click(screen.getByRole("option", { name: /See all 42/ }));
  expect(close).toHaveBeenCalledOnce();
  expect(navigate).toHaveBeenCalledWith("/search?q=milk");
});
it("opens the originating receipt", async () => {
  render(<Subject />, { wrapper: createWrapper() });
  await userEvent.click(screen.getByRole("option", { name: /Historic milk/ }));
  expect(close).toHaveBeenCalledOnce();
  expect(navigate).toHaveBeenCalledWith("/receipts/r1");
});
it("hides obsolete results during debounce and does not search while closed", async () => {
  vi.useFakeTimers();
  const view = render(<Subject />, { wrapper: createWrapper() });
  view.rerender(<Subject query="bread" />);
  expect(screen.queryByText("Historic milk")).not.toBeInTheDocument();
  expect(screen.getByRole("status")).toHaveTextContent("Searching purchases");
  expect(search).toHaveBeenLastCalledWith("milk", 0, 5, false);
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
  await act(async () => {
    vi.advanceTimersByTime(200);
  });
  expect(search).toHaveBeenLastCalledWith("bread", 0, 5, true);
  view.rerender(<Subject query="bread" open={false} />);
  expect(screen.queryByText(/See all/)).not.toBeInTheDocument();
  expect(search).toHaveBeenLastCalledWith("bread", 0, 5, false);
  view.rerender(<Subject query="" />);
  expect(screen.queryByText("Historic milk")).not.toBeInTheDocument();
});
it("shows retry on failure while keeping full search navigation available", async () => {
  const refetch = vi.fn();
  search.mockReturnValue(
    mockQueryResult({ isPending: false, isError: true, refetch }),
  );
  render(<Subject />, { wrapper: createWrapper() });
  expect(screen.getByText("Purchase search unavailable.")).toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: /retry/i }));
  expect(refetch).toHaveBeenCalledOnce();
  expect(
    screen.getByRole("option", { name: "Search all purchase history" }),
  ).toBeInTheDocument();
});
