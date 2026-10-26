import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import Reports from "./Reports";
vi.mock("@/hooks/usePageTitle", () => ({ usePageTitle: vi.fn() }));
vi.mock("@/hooks/useReportsHealthSummary", () => ({
  useReportsHealthSummary: () => ({ data: undefined }),
}));
function renderReports(initialEntry = "/reports") {
  return render(
    <MemoryRouter initialEntries={[initialEntry]}>
      <Reports />
    </MemoryRouter>,
  );
}

it.each([
  ["/reports", "asClassified"],
  ["/reports?categoryView=currentHierarchy", "currentHierarchy"],
])(
  "makes purchase history and working reports discoverable from %s",
  (entry, categoryView) => {
    renderReports(entry);
    expect(
      screen.getByRole("heading", { level: 1, name: "Reports" }),
    ).toBeInTheDocument();
    expect(screen.getByText("Purchase history")).toBeInTheDocument();
    const searchUrl = new URL(
      screen
        .getByRole("link", { name: "Search purchase history" })
        .getAttribute("href")!,
      window.location.origin,
    );
    expect(searchUrl.pathname).toBe("/search");
    expect(searchUrl.searchParams.get("categoryView")).toBe(categoryView);
    const reportUrl = new URL(
      screen
        .getByRole("link", { name: /Item Cost Over Time/ })
        .getAttribute("href")!,
      window.location.origin,
    );
    expect(reportUrl.pathname).toBe("/reports");
    expect(reportUrl.searchParams.get("report")).toBe("item-cost-over-time");
    expect(reportUrl.searchParams.get("categoryView")).toBe(categoryView);
    expect(
      screen.getByRole("heading", { name: "Operational reports" }),
    ).toBeInTheDocument();
  },
);
it("does not advertise unfinished reports or offer empty exports even with a saved legacy view", () => {
  localStorage.setItem("receipts.reportsView", "comparisons");
  renderReports();
  expect(
    screen.queryByRole("tablist", { name: "Price intelligence views" }),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "Export" }),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: /Choose report month/ }),
  ).not.toBeInTheDocument();
  expect(screen.queryByText(/fills in automatically/)).not.toBeInTheDocument();
  expect(screen.queryByText(/Comparisons appear/)).not.toBeInTheDocument();
  localStorage.removeItem("receipts.reportsView");
});
