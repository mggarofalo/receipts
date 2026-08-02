import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { format, subMonths } from "date-fns";
import { renderWithProviders } from "@/test/test-utils";
import { DateRangeSelector } from "./DateRangeSelector";
import { matchPreset } from "./date-range-presets";
import type { DateRange } from "@/hooks/useDashboard";

const ALL_PRESET_BUTTON_LABELS = [
  "1M",
  "3M",
  "1Y",
  "5Y",
  "MTD",
  "QTD",
  "YTD",
  "All",
];

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

  it("highlights the preset button matching the current value (RECEIPTS-840)", () => {
    const onChange = vi.fn();
    const oneYearRange: DateRange = {
      startDate: format(subMonths(new Date(), 12), "yyyy-MM-dd"),
      endDate: format(new Date(), "yyyy-MM-dd"),
    };
    renderWithProviders(
      <DateRangeSelector value={oneYearRange} onChange={onChange} />,
    );
    expect(screen.getByRole("button", { name: "1Y" })).toHaveAttribute(
      "data-variant",
      "default",
    );
    expect(screen.getByRole("button", { name: "1M" })).toHaveAttribute(
      "data-variant",
      "outline",
    );
  });

  it("highlights All when the value has no dates", () => {
    const onChange = vi.fn();
    renderWithProviders(
      <DateRangeSelector
        value={{ startDate: undefined, endDate: undefined }}
        onChange={onChange}
      />,
    );
    expect(screen.getByRole("button", { name: "All" })).toHaveAttribute(
      "data-variant",
      "default",
    );
  });

  it("highlights no preset and shows the literal range for a custom, non-matching value", () => {
    const onChange = vi.fn();
    renderWithProviders(
      <DateRangeSelector value={defaultRange} onChange={onChange} />,
    );
    for (const label of ALL_PRESET_BUTTON_LABELS) {
      expect(screen.getByRole("button", { name: label })).toHaveAttribute(
        "data-variant",
        "outline",
      );
    }
    expect(screen.getByText("2024-01-01 - 2024-01-31")).toBeInTheDocument();
  });

  it("re-syncs the highlighted preset when the value prop changes externally", () => {
    // Regression guard: activePreset used to be seeded once from a static
    // initialPreset prop and never resynced, so a value change originating
    // outside this component (URL search params, browser back/forward, a
    // shared link) left the highlighted preset lying about the applied
    // range. See PR #639 code review.
    const onChange = vi.fn();
    const { rerender } = renderWithProviders(
      <DateRangeSelector
        value={{ startDate: undefined, endDate: undefined }}
        onChange={onChange}
      />,
    );
    expect(screen.getByRole("button", { name: "All" })).toHaveAttribute(
      "data-variant",
      "default",
    );

    const oneYearRange: DateRange = {
      startDate: format(subMonths(new Date(), 12), "yyyy-MM-dd"),
      endDate: format(new Date(), "yyyy-MM-dd"),
    };
    rerender(<DateRangeSelector value={oneYearRange} onChange={onChange} />);

    expect(screen.getByRole("button", { name: "1Y" })).toHaveAttribute(
      "data-variant",
      "default",
    );
    expect(screen.getByRole("button", { name: "All" })).toHaveAttribute(
      "data-variant",
      "outline",
    );
  });
});

describe("matchPreset", () => {
  it("matches an open-ended range to 'all'", () => {
    expect(
      matchPreset({ startDate: undefined, endDate: undefined }),
    ).toEqual({ preset: "all", year: null });
  });

  it("matches a relative preset's current computed range", () => {
    const range: DateRange = {
      startDate: format(subMonths(new Date(), 3), "yyyy-MM-dd"),
      endDate: format(new Date(), "yyyy-MM-dd"),
    };
    expect(matchPreset(range)).toEqual({ preset: "3M", year: null });
  });

  it("matches a full-calendar-year range to 'year'", () => {
    expect(
      matchPreset({ startDate: "2021-01-01", endDate: "2021-12-31" }),
    ).toEqual({ preset: "year", year: 2021 });
  });

  it("does not match a partial year range", () => {
    expect(
      matchPreset({ startDate: "2021-01-01", endDate: "2021-06-30" }),
    ).toEqual({ preset: null, year: null });
  });

  it("returns null for a range that matches no preset", () => {
    expect(
      matchPreset({ startDate: "2024-01-01", endDate: "2024-01-31" }),
    ).toEqual({ preset: null, year: null });
  });

  it("returns null when only one side of the range is set (malformed)", () => {
    expect(
      matchPreset({ startDate: "2024-01-01", endDate: undefined }),
    ).toEqual({ preset: null, year: null });
  });
});
