import "@/test/setup-combobox-polyfills";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AdjustmentForm } from "./AdjustmentForm";

vi.mock("@/hooks/useFormShortcuts", () => ({
  useFormShortcuts: vi.fn(),
}));

vi.mock("@/hooks/useEnumMetadata", () => ({
  useEnumMetadata: vi.fn(() => ({
    adjustmentTypes: [
      { value: "Tip", label: "Tip" },
      { value: "Discount", label: "Discount" },
      { value: "Other", label: "Other" },
    ],
    authEventTypes: [],
    auditActions: [],
    entityTypes: [],
    adjustmentTypeLabels: { Tip: "Tip", Discount: "Discount", Other: "Other" },
    authEventLabels: {},
    auditActionLabels: {},
    entityTypeLabels: {},
    isLoading: false,
  })),
}));

describe("AdjustmentForm", () => {
  const defaultProps = {
    mode: "create" as const,
    onSubmit: vi.fn(),
    onCancel: vi.fn(),
  };

  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
  });

  it("renders in create mode with correct submit button text and fields", () => {
    render(<AdjustmentForm {...defaultProps} />);

    expect(screen.getByText(/^Type/)).toBeInTheDocument();
    expect(screen.getByText(/^Amount/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /add adjustment/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /cancel/i })).toBeInTheDocument();
  });

  it("renders in edit mode with pre-populated values and correct submit button text", () => {
    render(
      <AdjustmentForm
        {...defaultProps}
        mode="edit"
        defaultValues={{ type: "tip", amount: 5.00, description: "" }}
      />,
    );

    expect(screen.getByRole("button", { name: /update adjustment/i })).toBeInTheDocument();
  });

  it("shows validation error when type is not selected", async () => {
    const user = userEvent.setup();
    render(<AdjustmentForm {...defaultProps} />);

    await user.click(screen.getByRole("button", { name: /add adjustment/i }));

    await waitFor(() => {
      expect(screen.getByText("Type is required")).toBeInTheDocument();
    });
    expect(defaultProps.onSubmit).not.toHaveBeenCalled();
  });

  it("rejects a zero amount before submitting", async () => {
    const user = userEvent.setup();
    render(
      <AdjustmentForm
        {...defaultProps}
        defaultValues={{ type: "tip", amount: 0, description: "" }}
      />,
    );

    await user.click(screen.getByRole("button", { name: /add adjustment/i }));

    expect(await screen.findByText("Amount must be non-zero")).toBeInTheDocument();
    expect(defaultProps.onSubmit).not.toHaveBeenCalled();
  });

  it("calls onCancel when cancel button is clicked", async () => {
    const user = userEvent.setup();
    render(<AdjustmentForm {...defaultProps} />);

    await user.click(screen.getByRole("button", { name: /cancel/i }));

    expect(defaultProps.onCancel).toHaveBeenCalledTimes(1);
  });

  it("disables submit button and shows spinner when isSubmitting is true", () => {
    render(<AdjustmentForm {...defaultProps} isSubmitting={true} />);

    const submitButton = screen.getByRole("button", { name: /saving/i });
    expect(submitButton).toBeDisabled();
  });

  it("displays server errors when provided", () => {
    render(
      <AdjustmentForm
        {...defaultProps}
        serverErrors={{ type: "Invalid adjustment type", amount: "Amount too large" }}
      />,
    );

    expect(screen.getByText("Invalid adjustment type")).toBeInTheDocument();
    expect(screen.getByText("Amount too large")).toBeInTheDocument();
  });

  it("routes server errors through FormMessage so they have role=alert and are field-associated", async () => {
    render(
      <AdjustmentForm
        {...defaultProps}
        serverErrors={{ type: "Server-side type error" }}
      />,
    );

    // The error message element must have role="alert" (set by FormMessage per RECEIPTS-686)
    // and carry data-slot="form-message", meaning it went through FormMessage
    // rather than a bare <p className="text-destructive">.
    await waitFor(() => {
      const errorEl = screen.getByText("Server-side type error");
      expect(errorEl).toHaveAttribute("role", "alert");
      expect(errorEl).toHaveAttribute("data-slot", "form-message");
    });
  });

  it("calls onSubmit with correct data when form has valid non-other type", async () => {
    const user = userEvent.setup();
    render(
      <AdjustmentForm
        {...defaultProps}
        defaultValues={{ type: "tip", amount: 10.00, description: "" }}
      />,
    );

    await user.click(screen.getByRole("button", { name: /add adjustment/i }));

    await waitFor(() => {
      expect(defaultProps.onSubmit).toHaveBeenCalledWith(
        expect.objectContaining({ type: "tip", amount: 10.00 }),
      );
    });
  });

  it("does not show description field when type is not 'other'", () => {
    render(
      <AdjustmentForm
        {...defaultProps}
        defaultValues={{ type: "tip", amount: 0, description: "" }}
      />,
    );

    expect(screen.queryByLabelText("Description")).not.toBeInTheDocument();
  });

  it("shows saved descriptions in the description Combobox when type is 'other'", async () => {
    const user = userEvent.setup();
    localStorage.setItem(
      "receipts:adjustment-description-history",
      JSON.stringify(["Price match", "Manager override"]),
    );

    render(
      <AdjustmentForm
        {...defaultProps}
        defaultValues={{ type: "other", amount: 5, description: "" }}
      />,
    );

    // The description field is a Combobox; click its trigger to open
    const descCombobox = screen.getByLabelText(/^Description/);
    await user.click(descCombobox);

    await waitFor(() => {
      expect(screen.getByText("Price match")).toBeInTheDocument();
      expect(screen.getByText("Manager override")).toBeInTheDocument();
    });
  });

  it("selects a history description and populates the field", async () => {
    const user = userEvent.setup();
    localStorage.setItem(
      "receipts:adjustment-description-history",
      JSON.stringify(["Price match"]),
    );

    render(
      <AdjustmentForm
        {...defaultProps}
        defaultValues={{ type: "other", amount: 5, description: "" }}
      />,
    );

    const descCombobox = screen.getByLabelText(/^Description/);
    await user.click(descCombobox);

    await waitFor(() => {
      expect(screen.getByText("Price match")).toBeInTheDocument();
    });

    await user.click(screen.getByText("Price match"));

    // After selection, the combobox trigger shows the selected value
    expect(descCombobox).toHaveTextContent("Price match");
  });

  it("persists description to history on submit when type is 'other'", async () => {
    const user = userEvent.setup();
    render(
      <AdjustmentForm
        {...defaultProps}
        defaultValues={{ type: "other", amount: 5, description: "" }}
      />,
    );

    // Open the description combobox and type a custom value
    const descCombobox = screen.getByLabelText(/^Description/);
    await user.click(descCombobox);
    const searchInput = screen.getByPlaceholderText("Search descriptions...");
    await user.type(searchInput, "Loyalty bonus");
    await user.click(screen.getByText(/Use "Loyalty bonus"/));

    await user.click(
      screen.getByRole("button", { name: /add adjustment/i }),
    );

    await waitFor(() => {
      expect(defaultProps.onSubmit).toHaveBeenCalled();
    });

    const stored = JSON.parse(
      localStorage.getItem("receipts:adjustment-description-history") ?? "[]",
    ) as string[];
    expect(stored).toContain("Loyalty bonus");
  });
});
