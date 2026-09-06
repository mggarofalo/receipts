import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi, describe, it, expect, beforeAll, beforeEach } from "vitest";
import { ItemTemplateForm } from "./ItemTemplateForm";

// Polyfill ResizeObserver and scrollIntoView for radix-ui / cmdk in jsdom
beforeAll(() => {
  if (typeof window.ResizeObserver === "undefined") {
    window.ResizeObserver = class {
      observe() {}
      unobserve() {}
      disconnect() {}
    } as unknown as typeof ResizeObserver;
  }
  if (!Element.prototype.scrollIntoView) {
    Element.prototype.scrollIntoView = vi.fn();
  }
});

vi.mock("@/hooks/useFormShortcuts", () => ({
  useFormShortcuts: vi.fn(),
}));

vi.mock("@/hooks/useCategories", () => ({
  useAllCategories: vi.fn(() => ({
    data: [
      { id: "cat-1", name: "Groceries", isActive: true },
      { id: "cat-2", name: "Electronics", isActive: true },
    ],
    total: 2,
  })),
}));

vi.mock("@/hooks/useSubcategories", () => ({
  useAllSubcategoriesByCategoryId: vi.fn(() => ({
    data: [
      { id: "sub-1", name: "Dairy", isActive: true },
      { id: "sub-2", name: "Bakery", isActive: true },
    ],
    total: 2,
  })),
}));

describe("ItemTemplateForm", () => {
  const defaultProps = {
    mode: "create" as const,
    onSubmit: vi.fn(),
    onCancel: vi.fn(),
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("preserves a subcent default price when an existing template field is focused and blurred", async () => {
    const user = userEvent.setup();
    render(
      <ItemTemplateForm
        {...defaultProps}
        mode="edit"
        defaultValues={{ name: "Gasoline", defaultUnitPrice: 3.459 }}
      />,
    );
    await user.click(screen.getByLabelText("Default Unit Price (optional)"));
    await user.tab();
    await user.click(screen.getByRole("button", { name: /update template/i }));
    await waitFor(() =>
      expect(defaultProps.onSubmit).toHaveBeenCalledWith(
        expect.objectContaining({ defaultUnitPrice: 3.459 }),
        expect.anything(),
      ),
    );
  });

  it("renders in create mode with empty fields and correct submit button text", () => {
    render(<ItemTemplateForm {...defaultProps} />);

    expect(screen.getByLabelText(/^Name/)).toHaveValue("");
    expect(screen.getByLabelText("Description (optional)")).toHaveValue("");
    expect(screen.getByLabelText("Default Item Code (optional)")).toHaveValue("");
    expect(screen.getByRole("button", { name: /create template/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /cancel/i })).toBeInTheDocument();
  });

  it("renders in edit mode with pre-populated fields and correct submit button text", () => {
    render(
      <ItemTemplateForm
        {...defaultProps}
        mode="edit"
        defaultValues={{
          name: "Milk",
          description: "Whole milk",
          defaultCategory: "Groceries",
          defaultSubcategory: "Dairy",
          defaultUnitPrice: 3.99,
          defaultItemCode: "MLK-001",
        }}
      />,
    );

    expect(screen.getByLabelText(/^Name/)).toHaveValue("Milk");
    expect(screen.getByLabelText("Description (optional)")).toHaveValue("Whole milk");
    expect(screen.getByLabelText("Default Item Code (optional)")).toHaveValue("MLK-001");
    expect(screen.getByRole("button", { name: /update template/i })).toBeInTheDocument();
  });

  it("calls onSubmit with correct data when form is valid", async () => {
    const user = userEvent.setup();
    render(<ItemTemplateForm {...defaultProps} />);

    await user.type(screen.getByLabelText(/^Name/), "Bread");
    await user.click(screen.getByRole("button", { name: /create template/i }));

    await waitFor(() => {
      expect(defaultProps.onSubmit).toHaveBeenCalledWith(
        expect.objectContaining({ name: "Bread" }),
        expect.anything(),
      );
    });
  });

  it("shows validation error when name is empty", async () => {
    const user = userEvent.setup();
    render(<ItemTemplateForm {...defaultProps} />);

    await user.click(screen.getByRole("button", { name: /create template/i }));

    await waitFor(() => {
      expect(screen.getByText("Name is required")).toBeInTheDocument();
    });
    expect(defaultProps.onSubmit).not.toHaveBeenCalled();
  });

  it("calls onCancel when cancel button is clicked", async () => {
    const user = userEvent.setup();
    render(<ItemTemplateForm {...defaultProps} />);

    await user.click(screen.getByRole("button", { name: /cancel/i }));

    expect(defaultProps.onCancel).toHaveBeenCalledTimes(1);
  });

  it("disables submit button and shows spinner when isSubmitting is true", () => {
    render(<ItemTemplateForm {...defaultProps} isSubmitting={true} />);

    const submitButton = screen.getByRole("button", { name: /saving/i });
    expect(submitButton).toBeDisabled();
  });

  it("renders all optional fields", () => {
    render(<ItemTemplateForm {...defaultProps} />);

    expect(screen.getByText("Default Category (optional)")).toBeInTheDocument();
    expect(screen.getByText("Default Subcategory (optional)")).toBeInTheDocument();
    expect(screen.getByText("Default Unit Price (optional)")).toBeInTheDocument();
    expect(screen.getByText("Default Item Code (optional)")).toBeInTheDocument();
  });

  it("renders Hide button in edit mode when onHide is provided", () => {
    render(
      <ItemTemplateForm
        {...defaultProps}
        mode="edit"
        onHide={vi.fn()}
        defaultValues={{ name: "Test" }}
      />,
    );

    expect(screen.getByRole("button", { name: /^hide$/i })).toBeInTheDocument();
  });

  it("does not render Hide button when onHide is not provided", () => {
    render(
      <ItemTemplateForm
        {...defaultProps}
        mode="edit"
        defaultValues={{ name: "Test" }}
      />,
    );

    expect(screen.queryByRole("button", { name: /^hide$/i })).not.toBeInTheDocument();
  });

  it("does not render Hide button in create mode even when onHide is provided", () => {
    render(
      <ItemTemplateForm
        {...defaultProps}
        mode="create"
        onHide={vi.fn()}
      />,
    );

    expect(screen.queryByRole("button", { name: /^hide$/i })).not.toBeInTheDocument();
  });

  it("calls onHide when Hide button is clicked", async () => {
    const onHide = vi.fn();
    const user = userEvent.setup();
    render(
      <ItemTemplateForm
        {...defaultProps}
        mode="edit"
        onHide={onHide}
        defaultValues={{ name: "Test" }}
      />,
    );

    await user.click(screen.getByRole("button", { name: /^hide$/i }));

    expect(onHide).toHaveBeenCalledTimes(1);
  });

  it("disables Hide button and shows spinner when isHiding is true", () => {
    render(
      <ItemTemplateForm
        {...defaultProps}
        mode="edit"
        onHide={vi.fn()}
        isHiding={true}
        defaultValues={{ name: "Test" }}
      />,
    );

    const hideButton = screen.getByRole("button", { name: /hiding/i });
    expect(hideButton).toBeDisabled();
  });

  it("filters category options via typeahead and selects a filtered result", async () => {
    const onSubmit = vi.fn();
    const user = userEvent.setup();
    render(<ItemTemplateForm {...defaultProps} onSubmit={onSubmit} />);

    // Open the category combobox
    const categoryCombobox = screen.getByRole("combobox", { name: /^default category/i });
    await user.click(categoryCombobox);

    // Both categories should be visible
    await waitFor(() => {
      expect(screen.getByText("Groceries")).toBeInTheDocument();
      expect(screen.getByText("Electronics")).toBeInTheDocument();
    });

    // Type to filter — only "Electronics" should match
    await user.type(
      screen.getByPlaceholderText("Search categories..."),
      "elec",
    );

    await waitFor(() => {
      expect(screen.getByText("Electronics")).toBeInTheDocument();
      expect(screen.queryByText("Groceries")).not.toBeInTheDocument();
    });

    // Select the filtered result
    await user.click(screen.getByText("Electronics"));

    // Verify the combobox now shows the selected value
    await waitFor(() => {
      expect(categoryCombobox).toHaveTextContent("Electronics");
    });
  });
});
