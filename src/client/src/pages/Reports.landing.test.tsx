import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import Reports from "./Reports";
vi.mock("@/hooks/usePageTitle", () => ({ usePageTitle: vi.fn() }));
vi.mock("@/hooks/useReportsHealthSummary", () => ({
  useReportsHealthSummary: () => ({ data: undefined }),
}));
function renderReports() {
  return render(
    <MemoryRouter initialEntries={["/reports"]}>
      <Reports />
    </MemoryRouter>,
  );
}

it("makes purchase history and working reports discoverable", () => {
  renderReports();
  expect(
    screen.getByRole("heading", { level: 1, name: "Reports" }),
  ).toBeInTheDocument();
  expect(screen.getByText("What did I pay?")).toBeInTheDocument();
  expect(
    screen.getByRole("link", { name: "Search purchase history" }),
  ).toHaveAttribute("href", "/search");
  expect(
    screen.getByRole("link", { name: /Item Cost Over Time/ }),
  ).toHaveAttribute("href", "/reports?report=item-cost-over-time");
  expect(
    screen.getByRole("heading", { name: "Operational reports" }),
  ).toBeInTheDocument();
});
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
