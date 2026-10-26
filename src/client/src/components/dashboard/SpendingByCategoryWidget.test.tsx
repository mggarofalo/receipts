import { screen } from "@testing-library/react";
import { renderWithQueryClient } from "@/test/test-utils";
import { SpendingByCategoryWidget } from "./SpendingByCategoryWidget";
import type { components } from "@/generated/api";
vi.mock("@/hooks/useDashboard", () => ({
  useDashboardSpendingByCategory: vi.fn(),
}));
vi.mock("@/components/charts", async () => {
  const actual = await vi.importActual<typeof import("@/components/charts")>(
    "@/components/charts",
  );
  return {
    ...actual,
    DonutChart: ({
      data,
      formatValue,
    }: {
      data: { id: string; name: string; value: number }[];
      formatValue: (n: number) => string;
    }) => (
      <div data-testid="chart">
        {data.map((item) => (
          <p key={item.id}>
            {item.name}: {formatValue(item.value)}
          </p>
        ))}
      </div>
    ),
  };
});
import { useDashboardSpendingByCategory } from "@/hooks/useDashboard";
const mockHook = vi.mocked(useDashboardSpendingByCategory);
const dateRange = { startDate: "2024-01-01", endDate: "2024-01-31" };
function item(
  overrides: Partial<components["schemas"]["SpendingCategoryItem"]> = {},
): components["schemas"]["SpendingCategoryItem"] {
  return {
    categoryName: "Food",
    categoryId: "food",
    pathRevisionId: "r1",
    currency: "USD",
    state: "classified",
    purchaseCount: 1,
    path: [
      { categoryId: "root", name: "Home", archived: false },
      { categoryId: "food", name: "Food", archived: false },
    ],
    amount: 100,
    percentage: 25,
    ...overrides,
  };
}
function mock(
  items: components["schemas"]["SpendingCategoryItem"][] = [],
  extras = {},
) {
  mockHook.mockReturnValue({
    data: {
      items,
      view: "asClassified",
      rollup: "assignedCategory",
      assignmentSelection: "currentPointer",
    },
    isLoading: false,
    isError: false,
    ...extras,
  } as unknown as ReturnType<typeof useDashboardSpendingByCategory>);
}
describe("SpendingByCategoryWidget", () => {
  it("keeps full paths, historical revisions and currencies distinct with full currency percentages", () => {
    mock([
      item(),
      item({ pathRevisionId: "r2", amount: 20, percentage: 5 }),
      item({
        currency: "EUR",
        amount: 50,
        percentage: 100,
        path: [{ categoryId: "food", name: "Food", archived: true }],
      }),
    ]);
    renderWithQueryClient(<SpendingByCategoryWidget dateRange={dateRange} />);
    expect(
      screen.getByText(/Home \/ Food \(r1\): \$100.00 \(25.0% of USD\)/),
    ).toBeInTheDocument();
    expect(screen.getByText(/Home \/ Food \(r2\)/)).toBeInTheDocument();
    expect(
      screen.getByText(/Food \(archived\): €50.00 \(100.0% of EUR\)/),
    ).toBeInTheDocument();
    expect(screen.getAllByTestId("chart")).toHaveLength(2);
  });
  it("includes unresolved amounts and identifies partial history", () => {
    mock([
      item({
        state: "beforeMigrationHistory",
        categoryId: null,
        pathRevisionId: null,
        path: [],
      }),
    ]);
    renderWithQueryClient(<SpendingByCategoryWidget dateRange={dateRange} />);
    expect(screen.getByRole("status")).toHaveTextContent(
      "Partial category history",
    );
    expect(screen.getAllByText(/Before recorded history/)).toHaveLength(2);
  });
  it("groups Other within each currency while retaining detailed paths", () => {
    mock(
      Array.from({ length: 7 }, (_, i) =>
        item({
          categoryId: `c${i}`,
          path: [
            { categoryId: `c${i}`, name: `Category ${i}`, archived: false },
          ],
        }),
      ),
    );
    renderWithQueryClient(<SpendingByCategoryWidget dateRange={dateRange} />);
    expect(screen.getByText(/Other: \$300.00/)).toBeInTheDocument();
    expect(screen.getByText("Category 6")).toBeInTheDocument();
  });
  it("shows loading, errors and empty states", () => {
    mock([], { isLoading: true });
    const { unmount } = renderWithQueryClient(
      <SpendingByCategoryWidget dateRange={dateRange} />,
    );
    expect(screen.getByRole("status")).toHaveTextContent("Loading");
    unmount();
    mock([], { isError: true });
    const error = renderWithQueryClient(
      <SpendingByCategoryWidget dateRange={dateRange} />,
    );
    expect(screen.getByRole("alert")).toHaveTextContent("could not be loaded");
    error.unmount();
    mock();
    renderWithQueryClient(<SpendingByCategoryWidget dateRange={dateRange} />);
    expect(screen.getByText("No data available")).toBeInTheDocument();
  });
});
