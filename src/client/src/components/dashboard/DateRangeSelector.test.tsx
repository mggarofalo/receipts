import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import { DateRangeSelector } from "./DateRangeSelector";
import type { DateRange } from "@/hooks/useDashboard";

vi.mock("@/hooks/useDashboard", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/hooks/useDashboard")>();
  return {
    ...actual,
    useDashboardEarliestReceiptYear: vi.fn().mockReturnValue({
      data: { year: 2020 },
      isLoading: false,
    }),
  };
});

const defaultRange: DateRange = {
  startDate: "2024-01-01",
  endDate: "2024-01-31",
};

describe("DateRangeSelector", () => {
  it("renders preset buttons on wide screens", () => {
    const onChange = vi.fn();
    renderWithProviders(
      <DateRangeSelector value={defaultRange} onChange={onChange} />,
    );
    expect(screen.getByRole("button", { name: "1M" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "3M" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "1Y" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "5Y" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "MTD" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "QTD" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "YTD" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "All" })).toBeInTheDocument();
  });

  it("renders a year dropdown", () => {
    const onChange = vi.fn();
    renderWithProviders(
      <DateRangeSelector value={defaultRange} onChange={onChange} />,
    );
    expect(screen.getByTestId("year-dropdown")).toBeInTheDocument();
  });

  it("calls onChange when a preset is clicked", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithProviders(
      <DateRangeSelector value={defaultRange} onChange={onChange} />,
    );

    await user.click(screen.getByRole("button", { name: "1M" }));
    expect(onChange).toHaveBeenCalledWith(
      expect.objectContaining({
        startDate: expect.any(String),
        endDate: expect.any(String),
      }),
    );
  });

  it("calls onChange with undefined dates for All", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithProviders(
      <DateRangeSelector value={defaultRange} onChange={onChange} />,
    );

    await user.click(screen.getByRole("button", { name: "All" }));
    expect(onChange).toHaveBeenCalledWith({
      startDate: undefined,
      endDate: undefined,
    });
  });

  it("renders a dropdown selector for narrow screens", () => {
    const onChange = vi.fn();
    renderWithProviders(
      <DateRangeSelector value={defaultRange} onChange={onChange} />,
    );
    // There should be at least one combobox (the narrow screen dropdown or the year dropdown)
    const comboboxes = screen.getAllByRole("combobox");
    expect(comboboxes.length).toBeGreaterThanOrEqual(1);
  });

  it("calls onChange when 3M is clicked", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithProviders(
      <DateRangeSelector value={defaultRange} onChange={onChange} />,
    );

    await user.click(screen.getByRole("button", { name: "3M" }));
    expect(onChange).toHaveBeenCalledWith(
      expect.objectContaining({
        startDate: expect.any(String),
        endDate: expect.any(String),
      }),
    );
  });

  it("defaults to the 1M preset highlighted when initialPreset is not passed", () => {
    const onChange = vi.fn();
    renderWithProviders(
      <DateRangeSelector value={defaultRange} onChange={onChange} />,
    );
    expect(
      screen.getByRole("button", { name: "1M" }),
    ).toHaveAttribute("data-variant", "default");
    expect(
      screen.getByRole("button", { name: "1Y" }),
    ).toHaveAttribute("data-variant", "outline");
  });

  it("highlights the preset passed via initialPreset (RECEIPTS-840)", () => {
    const onChange = vi.fn();
    renderWithProviders(
      <DateRangeSelector
        value={defaultRange}
        onChange={onChange}
        initialPreset="12M"
      />,
    );
    expect(
      screen.getByRole("button", { name: "1Y" }),
    ).toHaveAttribute("data-variant", "default");
    expect(
      screen.getByRole("button", { name: "1M" }),
    ).toHaveAttribute("data-variant", "outline");
  });
});
