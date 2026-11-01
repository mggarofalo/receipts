import { render, screen } from "@testing-library/react";
import { AreaTimeChart } from "./AreaTimeChart";

vi.mock("recharts", () => ({
  ResponsiveContainer: ({ children }: { children: React.ReactNode }) => (
    <div data-testid="responsive-container">{children}</div>
  ),
  AreaChart: ({
    children,
    data,
  }: {
    children: React.ReactNode;
    data: unknown[];
  }) => (
    <div data-testid="area-chart" data-count={data.length}>
      {children}
    </div>
  ),
  Area: ({ dataKey, strokeDasharray }: { dataKey: string; strokeDasharray?: string }) => (
    <div
      data-testid={dataKey === "trendline" ? "trendline-area" : "area"}
      data-dasharray={strokeDasharray ?? ""}
    />
  ),
  XAxis: () => <div data-testid="x-axis" />,
  YAxis: () => <div data-testid="y-axis" />,
  CartesianGrid: () => <div data-testid="cartesian-grid" />,
  Tooltip: () => <div data-testid="tooltip" />,
}));

const mockData = [
  { period: "Jan", amount: 100 },
  { period: "Feb", amount: 200 },
  { period: "Mar", amount: 150 },
];

const mockTrendline = [
  { period: "Jan", amount: 100 },
  { period: "Feb", amount: 150 },
  { period: "Mar", amount: 150 },
];

describe("AreaTimeChart", () => {
  it("renders the chart with data", () => {
    render(<AreaTimeChart data={mockData} />);
    expect(screen.getByTestId("responsive-container")).toBeInTheDocument();
    expect(screen.getByTestId("area-chart")).toHaveAttribute("data-count", "3");
    expect(screen.getByTestId("area")).toBeInTheDocument();
  });

  it("renders axis and grid elements", () => {
    render(<AreaTimeChart data={mockData} />);
    expect(screen.getByTestId("x-axis")).toBeInTheDocument();
    expect(screen.getByTestId("y-axis")).toBeInTheDocument();
    expect(screen.getByTestId("cartesian-grid")).toBeInTheDocument();
  });

  it("renders with empty data", () => {
    render(<AreaTimeChart data={[]} />);
    expect(screen.getByTestId("area-chart")).toHaveAttribute("data-count", "0");
  });

  it("does not render trendline when trendlineData is not provided", () => {
    render(<AreaTimeChart data={mockData} />);
    expect(screen.queryByTestId("trendline-area")).not.toBeInTheDocument();
  });

  it("renders trendline area when trendlineData is provided", () => {
    render(<AreaTimeChart data={mockData} trendlineData={mockTrendline} />);
    expect(screen.getByTestId("trendline-area")).toBeInTheDocument();
  });

  it("renders trendline with dashed stroke", () => {
    render(<AreaTimeChart data={mockData} trendlineData={mockTrendline} />);
    expect(screen.getByTestId("trendline-area")).toHaveAttribute(
      "data-dasharray",
      "5 5",
    );
  });

  it("does not render trendline when trendlineData is empty", () => {
    render(<AreaTimeChart data={mockData} trendlineData={[]} />);
    expect(screen.queryByTestId("trendline-area")).not.toBeInTheDocument();
  });

  // Accessibility tests
  it("renders a visually-hidden data table with period and amount columns", () => {
    render(<AreaTimeChart data={mockData} />);
    const table = document.querySelector("table.sr-only");
    expect(table).toBeInTheDocument();
    expect(screen.getByText("Jan")).toBeInTheDocument();
    expect(screen.getByText("Feb")).toBeInTheDocument();
    expect(screen.getByText("Mar")).toBeInTheDocument();
  });

  it("renders chart container with role=img and accessible name", () => {
    render(<AreaTimeChart data={mockData} aria-label="Monthly spending" />);
    expect(screen.getByRole("img", { name: "Monthly spending" })).toBeInTheDocument();
  });

  it("uses aria-labelledby when provided", () => {
    render(
      <div>
        <h2 id="area-title">Spending over time</h2>
        <AreaTimeChart data={mockData} aria-labelledby="area-title" />
      </div>,
    );
    const img = document.querySelector("[role='img']");
    expect(img).toHaveAttribute("aria-labelledby", "area-title");
    expect(img).not.toHaveAttribute("aria-label");
  });

  it("falls back to a default aria-label when neither aria-label nor aria-labelledby is provided", () => {
    render(<AreaTimeChart data={mockData} />);
    expect(screen.getByRole("img", { name: "Area time chart" })).toBeInTheDocument();
  });

  it("includes a Rolling Average column in the table when trendline data is provided", () => {
    render(<AreaTimeChart data={mockData} trendlineData={mockTrendline} />);
    const table = document.querySelector("table.sr-only");
    const headers = table?.querySelectorAll("th");
    const headerTexts = Array.from(headers ?? []).map((h) => h.textContent);
    expect(headerTexts).toContain("Rolling Average");
  });

  it("does not include Rolling Average column when trendline data is absent", () => {
    render(<AreaTimeChart data={mockData} />);
    const table = document.querySelector("table.sr-only");
    const headers = table?.querySelectorAll("th");
    const headerTexts = Array.from(headers ?? []).map((h) => h.textContent);
    expect(headerTexts).not.toContain("Rolling Average");
  });

  it("lists every point when several share a period, without a duplicate-key warning", () => {
    const consoleError = vi.spyOn(console, "error").mockImplementation(() => {});
    try {
      render(
        <AreaTimeChart
          data={[
            { period: "2026-10-01", amount: 12 },
            { period: "2026-10-01", amount: 30 },
          ]}
        />,
      );
      const rows = document.querySelectorAll("table.sr-only tbody tr");
      expect(Array.from(rows, (row) => row.textContent)).toEqual([
        "2026-10-0112",
        "2026-10-0130",
      ]);
      expect(
        consoleError.mock.calls.some((args) =>
          args.some((arg) => String(arg).includes("same key")),
        ),
      ).toBe(false);
    } finally {
      consoleError.mockRestore();
    }
  });
});
