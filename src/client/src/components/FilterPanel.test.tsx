import { describe, it, expect, vi } from "vitest";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import {
  FilterPanel,
  type FilterField,
  type FilterValues,
} from "./FilterPanel";
import { renderWithProviders } from "@/test/test-utils";

const selectField: FilterField = {
  type: "select",
  key: "status",
  label: "Status",
  options: ["Active", "Inactive"],
};

const booleanField: FilterField = {
  type: "boolean",
  key: "verified",
  label: "Verified",
};

const dateRangeField: FilterField = {
  type: "dateRange",
  key: "dateRange",
  label: "Date Range",
};

const numberRangeField: FilterField = {
  type: "numberRange",
  key: "amount",
  label: "Amount",
};

const allFields: FilterField[] = [
  selectField,
  booleanField,
  dateRangeField,
  numberRangeField,
];

describe("FilterPanel", () => {
  it("gives each filter control an unambiguous accessible name", async () => {
    renderWithProviders(
      <FilterPanel fields={allFields} values={{}} onChange={vi.fn()} />,
    );
    await userEvent.click(screen.getByRole("button", { name: /filters/i }));
    expect(
      screen.getByRole("combobox", { name: "Status" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("combobox", { name: "Verified" }),
    ).toBeInTheDocument();
    for (const name of [
      "Date Range from",
      "Date Range to",
      "Amount minimum",
      "Amount maximum",
    ])
      expect(screen.getByLabelText(name)).toBeInTheDocument();
  });
  it("renders the Filters button", () => {
    const onChange = vi.fn();
    renderWithProviders(
      <FilterPanel fields={allFields} values={{}} onChange={onChange} />,
    );
    expect(
      screen.getByRole("button", { name: /filters/i }),
    ).toBeInTheDocument();
  });

  it("shows active filter count badge when filters are set", () => {
    const onChange = vi.fn();
    const values: FilterValues = { status: "Active" };
    renderWithProviders(
      <FilterPanel
        fields={[selectField]}
        values={values}
        onChange={onChange}
      />,
    );
    expect(screen.getByText("1")).toBeInTheDocument();
  });

  it("does not show badge or clear button when no filters are active", () => {
    const onChange = vi.fn();
    const values: FilterValues = { status: "all" };
    renderWithProviders(
      <FilterPanel
        fields={[selectField]}
        values={values}
        onChange={onChange}
      />,
    );
    expect(screen.queryByText("Clear all")).not.toBeInTheDocument();
  });

  it("shows Clear all button when filters are active", () => {
    const onChange = vi.fn();
    const values: FilterValues = { status: "Active" };
    renderWithProviders(
      <FilterPanel
        fields={[selectField]}
        values={values}
        onChange={onChange}
      />,
    );
    expect(
      screen.getByRole("button", { name: /clear all/i }),
    ).toBeInTheDocument();
  });

  it("calls onChange with cleared values when Clear all is clicked", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const values: FilterValues = { status: "Active" };
    renderWithProviders(
      <FilterPanel
        fields={[selectField]}
        values={values}
        onChange={onChange}
      />,
    );
    await user.click(screen.getByRole("button", { name: /clear all/i }));
    expect(onChange).toHaveBeenCalledWith({ status: "all" });
  });

  it("renders save preset input and button when onSaveFilter is provided", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const onSaveFilter = vi.fn();
    renderWithProviders(
      <FilterPanel
        fields={[selectField]}
        values={{}}
        onChange={onChange}
        onSaveFilter={onSaveFilter}
      />,
    );
    // Expand the collapsible first
    await user.click(screen.getByRole("button", { name: /filters/i }));
    expect(screen.getByPlaceholderText("Preset name...")).toBeInTheDocument();
    // Save button should be disabled when input is empty
    expect(screen.getByRole("button", { name: /save/i })).toBeDisabled();
  });

  it("counts active filters correctly for dateRange fields", () => {
    const onChange = vi.fn();
    const values: FilterValues = { dateRange: { from: "2024-01-01" } };
    renderWithProviders(
      <FilterPanel
        fields={[dateRangeField]}
        values={values}
        onChange={onChange}
      />,
    );
    expect(screen.getByText("1")).toBeInTheDocument();
  });

  it("counts active filters correctly for numberRange fields", () => {
    const onChange = vi.fn();
    const values: FilterValues = { amount: { min: 10 } };
    renderWithProviders(
      <FilterPanel
        fields={[numberRangeField]}
        values={values}
        onChange={onChange}
      />,
    );
    expect(screen.getByText("1")).toBeInTheDocument();
  });

  it("counts active filters for boolean fields", () => {
    const onChange = vi.fn();
    const values: FilterValues = { verified: "true" };
    renderWithProviders(
      <FilterPanel
        fields={[booleanField]}
        values={values}
        onChange={onChange}
      />,
    );
    expect(screen.getByText("1")).toBeInTheDocument();
  });

  it("does not count boolean field set to 'all' as active", () => {
    const onChange = vi.fn();
    const values: FilterValues = { verified: "all" };
    renderWithProviders(
      <FilterPanel
        fields={[booleanField]}
        values={values}
        onChange={onChange}
      />,
    );
    expect(screen.queryByText("Clear all")).not.toBeInTheDocument();
  });

  it("clears all field types correctly when Clear all is clicked", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const values: FilterValues = {
      status: "Active",
      verified: "true",
      dateRange: { from: "2024-01-01" },
      amount: { min: 10 },
    };
    renderWithProviders(
      <FilterPanel fields={allFields} values={values} onChange={onChange} />,
    );
    await user.click(screen.getByRole("button", { name: /clear all/i }));
    expect(onChange).toHaveBeenCalledWith({
      status: "all",
      verified: "all",
      dateRange: undefined,
      amount: undefined,
    });
  });

  it("renders select field with current value when panel is expanded", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithProviders(
      <FilterPanel
        fields={[selectField]}
        values={{ status: "Active" }}
        onChange={onChange}
      />,
    );
    await user.click(screen.getByRole("button", { name: /filters/i }));
    expect(screen.getByText("Status")).toBeInTheDocument();
    // The select trigger should show the current value
    expect(screen.getByRole("combobox")).toBeInTheDocument();
  });

  it("renders boolean field with select trigger when panel is expanded", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithProviders(
      <FilterPanel
        fields={[booleanField]}
        values={{ verified: "all" }}
        onChange={onChange}
      />,
    );
    await user.click(screen.getByRole("button", { name: /filters/i }));
    expect(screen.getByText("Verified")).toBeInTheDocument();
    expect(screen.getByRole("combobox")).toBeInTheDocument();
  });

  it("renders dateRange fields and calls onChange on from date change", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithProviders(
      <FilterPanel fields={[dateRangeField]} values={{}} onChange={onChange} />,
    );
    await user.click(screen.getByRole("button", { name: /filters/i }));
    const fromInput = screen.getByPlaceholderText("From");
    await user.type(fromInput, "2024-01-15");
    await user.tab(); // Commit on blur
    expect(onChange).toHaveBeenCalled();
  });

  it("renders numberRange fields and calls onChange on min value change", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithProviders(
      <FilterPanel
        fields={[numberRangeField]}
        values={{}}
        onChange={onChange}
      />,
    );
    await user.click(screen.getByRole("button", { name: /filters/i }));
    const minInput = screen.getByPlaceholderText("Min");
    await user.type(minInput, "5");
    expect(onChange).toHaveBeenCalled();
  });

  it("calls onSaveFilter with trimmed name and clears input", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const onSaveFilter = vi.fn();
    renderWithProviders(
      <FilterPanel
        fields={[selectField]}
        values={{}}
        onChange={onChange}
        onSaveFilter={onSaveFilter}
      />,
    );
    await user.click(screen.getByRole("button", { name: /filters/i }));
    const input = screen.getByPlaceholderText("Preset name...");
    await user.type(input, "My Preset");
    const saveButton = screen.getByRole("button", { name: /save/i });
    expect(saveButton).not.toBeDisabled();
    await user.click(saveButton);
    expect(onSaveFilter).toHaveBeenCalledWith("My Preset");
    // Input should be cleared after save
    expect(input).toHaveValue("");
  });

  it("renders saved filter presets and calls onLoadFilter when clicked", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const onLoadFilter = vi.fn();
    const savedFilters = [
      {
        id: "p1",
        name: "Preset 1",
        entityType: "receipt",
        values: { status: "Active" },
        createdAt: "2024-01-01",
      },
    ];
    renderWithProviders(
      <FilterPanel
        fields={[selectField]}
        values={{}}
        onChange={onChange}
        savedFilters={savedFilters}
        onLoadFilter={onLoadFilter}
      />,
    );
    await user.click(screen.getByRole("button", { name: /filters/i }));
    await user.click(screen.getByRole("button", { name: "Preset 1" }));
    expect(onLoadFilter).toHaveBeenCalledWith(savedFilters[0]);
  });

  it("calls onDeleteFilter when delete preset button is clicked", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const onDeleteFilter = vi.fn();
    const savedFilters = [
      {
        id: "p1",
        name: "Preset 1",
        entityType: "receipt",
        values: {},
        createdAt: "2024-01-01",
      },
    ];
    renderWithProviders(
      <FilterPanel
        fields={[selectField]}
        values={{}}
        onChange={onChange}
        savedFilters={savedFilters}
        onDeleteFilter={onDeleteFilter}
      />,
    );
    await user.click(screen.getByRole("button", { name: /filters/i }));
    await user.click(screen.getByLabelText("Delete preset Preset 1"));
    expect(onDeleteFilter).toHaveBeenCalledWith("p1");
  });

  it("toggles collapsible open and closed", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithProviders(
      <FilterPanel fields={[selectField]} values={{}} onChange={onChange} />,
    );
    const filtersButton = screen.getByRole("button", { name: /filters/i });
    // Initially closed — filter content not visible
    expect(screen.queryByText("Status")).not.toBeInTheDocument();
    // Open
    await user.click(filtersButton);
    expect(screen.getByText("Status")).toBeInTheDocument();
    // Close
    await user.click(filtersButton);
    // Content should be hidden again
    expect(screen.queryByText("Status")).not.toBeInTheDocument();
  });
});
