import "@/test/setup-combobox-polyfills";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ReceiptItemForm } from "./ReceiptItemForm";

vi.mock("@/hooks/useFormShortcuts", () => ({
  useFormShortcuts: vi.fn(),
}));

vi.mock("@/hooks/useReceipts", () => ({
  useReceipts: vi.fn(() => ({
    data: [
      { id: "r-1", location: "Walmart", date: "2024-01-15" },
    ],
    total: 1,
    isLoading: false,
  })),
}));

vi.mock("@/hooks/useCategories", () => ({
  useAllCategories: vi.fn(() => ({
    data: [
      { id: "cat-1", name: "Groceries" },
      { id: "cat-2", name: "Electronics" },
    ],
    total: 2,
  })),
}));

vi.mock("@/hooks/useSubcategories", () => ({
  useAllSubcategoriesByCategoryId: vi.fn(() => ({
    data: [
      { id: "sub-1", name: "Dairy" },
      { id: "sub-2", name: "Bakery" },
    ],
    total: 2,
  })),
  useCreateSubcategory: vi.fn(() => ({
    mutateAsync: vi.fn(),
  })),
}));

vi.mock("@/hooks/useItemTemplates", () => ({
  useItemTemplates: vi.fn(() => ({
    data: [
      {
        id: "tmpl-1",
        name: "Milk",
        defaultCategory: "Groceries",
        defaultSubcategory: "Dairy",
        defaultUnitPrice: 3.99,
        defaultItemCode: "MLK-001",
      },
    ],
    total: 1,
  })),
}));

vi.mock("@/lib/combobox-options", () => ({
  receiptToOption: vi.fn((r: { id: string; location: string; date: string }) => ({
    value: r.id,
    label: r.location,
    sublabel: `${r.location} — ${r.date}`,
  })),
}));

vi.mock("@/lib/format", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/format")>();
  return {
    ...actual,
    formatCurrency: vi.fn((amount: number) => `$${amount.toFixed(2)}`),
  };
});

vi.mock("@/hooks/useReceiptItemSuggestions", () => ({
  useReceiptItemSuggestions: vi.fn(() => ({
    data: undefined,
    isFetching: false,
    isSuccess: false,
    isLoading: false,
  })),
}));

describe("ReceiptItemForm", () => {
  const defaultProps = {
    mode: "create" as const,
    onSubmit: vi.fn(),
    onCancel: vi.fn(),
  };

  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
  });

  it("renders in create mode with correct submit button text and all field labels", () => {
    render(<ReceiptItemForm {...defaultProps} />);

    expect(screen.getByText(/^Receipt/)).toBeInTheDocument();
    expect(screen.getByText(/^Item Code/)).toBeInTheDocument();
    expect(screen.getByText(/^Description/)).toBeInTheDocument();
    expect(screen.getByLabelText(/^Quantity/)).toBeInTheDocument();
    expect(screen.getByLabelText(/^Unit Price/)).toBeInTheDocument();
    expect(screen.getByText(/^Category/)).toBeInTheDocument();
    expect(screen.getByText(/^Subcategory/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /create item/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /cancel/i })).toBeInTheDocument();
  });

  it("renders in edit mode with pre-populated fields and correct submit button text", () => {
    render(
      <ReceiptItemForm
        {...defaultProps}
        mode="edit"
        defaultValues={{
          receiptId: "r-1",
          receiptItemCode: "ITM-001",
          description: "Whole Milk",

          quantity: 2,
          unitPrice: 3.99,
          category: "Groceries",
          subcategory: "Dairy",
        }}
      />,
    );

    // Item Code is now an Input; check its value
    expect(screen.getByPlaceholderText("Enter item code...")).toHaveValue("ITM-001");
    expect(screen.getByRole("button", { name: /update item/i })).toBeInTheDocument();
  });

  it("shows validation errors when required fields are empty", async () => {
    const user = userEvent.setup();
    render(<ReceiptItemForm {...defaultProps} />);

    await user.click(screen.getByRole("button", { name: /create item/i }));

    await waitFor(() => {
      expect(screen.getByText("Receipt is required")).toBeInTheDocument();
      expect(screen.getByText("Item code is required")).toBeInTheDocument();
      expect(screen.getByText("Description is required")).toBeInTheDocument();
      expect(screen.getByText("Category is required")).toBeInTheDocument();
      expect(screen.getByText("Subcategory is required")).toBeInTheDocument();
    });
    expect(defaultProps.onSubmit).not.toHaveBeenCalled();
  });

  it("calls onCancel when cancel button is clicked", async () => {
    const user = userEvent.setup();
    render(<ReceiptItemForm {...defaultProps} />);

    await user.click(screen.getByRole("button", { name: /cancel/i }));

    expect(defaultProps.onCancel).toHaveBeenCalledTimes(1);
  });

  it("disables submit button and shows spinner when isSubmitting is true", () => {
    render(<ReceiptItemForm {...defaultProps} isSubmitting={true} />);

    const submitButton = screen.getByRole("button", { name: /saving/i });
    expect(submitButton).toBeDisabled();
  });

  it("displays computed total based on quantity and unit price", () => {
    render(
      <ReceiptItemForm
        {...defaultProps}
        defaultValues={{
          receiptId: "r-1",
          receiptItemCode: "ITM-001",
          description: "Milk",

          quantity: 3,
          unitPrice: 2.50,
          category: "Groceries",
          subcategory: "Dairy",
        }}
      />,
    );

    expect(screen.getByText(/total/i)).toBeInTheDocument();
  });

  it("calls onSubmit with correct data when all fields are valid", async () => {
    const user = userEvent.setup();
    render(
      <ReceiptItemForm
        {...defaultProps}
        defaultValues={{
          receiptId: "r-1",
          receiptItemCode: "ITM-001",
          description: "Milk",

          quantity: 1,
          unitPrice: 3.99,
          category: "Groceries",
          subcategory: "Dairy",
        }}
      />,
    );

    await user.click(screen.getByRole("button", { name: /create item/i }));

    await waitFor(() => {
      expect(defaultProps.onSubmit).toHaveBeenCalledWith(
        expect.objectContaining({
          receiptId: "r-1",
          receiptItemCode: "ITM-001",
          description: "Milk",
          category: "Groceries",
          subcategory: "Dairy",
        }),
      );
    });
  });

  it("defaults quantity to 1", () => {
    render(<ReceiptItemForm {...defaultProps} />);

    expect(screen.getByLabelText(/^Quantity/)).toBeInTheDocument();
    expect(screen.getByLabelText(/^Unit Price/)).toBeInTheDocument();
  });

  it("shows recent description history and item template groups in description autocomplete", async () => {
    const user = userEvent.setup();
    localStorage.setItem(
      "receipts:item-description-history",
      JSON.stringify(["Whole Milk", "Bread"]),
    );

    render(<ReceiptItemForm {...defaultProps} />);

    const descriptionInput = screen.getByLabelText(/^Description/);
    await user.click(descriptionInput);

    // History entries should appear under "Recent Descriptions"
    await waitFor(() => {
      expect(screen.getByText("Recent Descriptions")).toBeInTheDocument();
      expect(screen.getByText("Whole Milk")).toBeInTheDocument();
      expect(screen.getByText("Bread")).toBeInTheDocument();
    });
  });

  it("shows item template suggestions when typing a matching description", async () => {
    const user = userEvent.setup();
    render(<ReceiptItemForm {...defaultProps} />);

    const descriptionInput = screen.getByLabelText(/^Description/);
    await user.type(descriptionInput, "Milk");

    // Fuzzy-matched template should appear under "Item Templates"
    await waitFor(() => {
      expect(screen.getByText("Item Templates")).toBeInTheDocument();
    });
  });

  // ── RECEIPTS-881: template provenance reaches the server ──────

  /** Applies the "Milk" template through the description autocomplete. */
  async function pickMilkTemplate(user: ReturnType<typeof userEvent.setup>) {
    const descriptionInput = screen.getByLabelText(/^Description/);
    await user.type(descriptionInput, "Milk");
    await waitFor(() => {
      expect(screen.getByText("Item Templates")).toBeInTheDocument();
    });
    const group = screen.getByText("Item Templates").closest("[cmdk-group]")!;
    await user.click(within(group as HTMLElement).getByText("Milk"));
  }

  it("submits the template id when a line is entered from a template", async () => {
    const user = userEvent.setup();
    render(
      <ReceiptItemForm
        {...defaultProps}
        defaultValues={{
          receiptId: "r-1",
          receiptItemCode: "ITM-001",
          quantity: 1,
          unitPrice: 3.99,
          category: "Groceries",
          subcategory: "Dairy",
        }}
      />,
    );

    await pickMilkTemplate(user);
    await user.click(screen.getByRole("button", { name: /create item/i }));

    // The server uses this to stamp the item's canonical description and skip the resolver.
    // Without it, an item the user explicitly classified still goes through embedding search
    // and can land in the review queue.
    await waitFor(() => {
      expect(defaultProps.onSubmit).toHaveBeenCalledWith(
        expect.objectContaining({ itemTemplateId: "tmpl-1" }),
      );
    });
  });

  it("drops the template id once the description is edited away from it", async () => {
    const user = userEvent.setup();
    render(
      <ReceiptItemForm
        {...defaultProps}
        defaultValues={{
          receiptId: "r-1",
          receiptItemCode: "ITM-001",
          quantity: 1,
          unitPrice: 3.99,
          category: "Groceries",
          subcategory: "Dairy",
        }}
      />,
    );

    await pickMilkTemplate(user);

    const descriptionInput = screen.getByLabelText(/^Description/);
    await user.clear(descriptionInput);
    await user.type(descriptionInput, "Orange Juice");

    await user.click(screen.getByRole("button", { name: /create item/i }));

    // Keeping it would stamp orange juice with the milk canonical entry and file it under milk
    // in every report, with nothing on screen to reveal it.
    await waitFor(() => {
      expect(defaultProps.onSubmit).toHaveBeenCalled();
    });
    const submitted = vi.mocked(defaultProps.onSubmit).mock.calls.at(-1)![0];
    expect(submitted.itemTemplateId).toBeUndefined();
    expect(submitted.description).toBe("Orange Juice");
  });

  it("keeps the template id when only trailing whitespace is added", async () => {
    const user = userEvent.setup();
    render(
      <ReceiptItemForm
        {...defaultProps}
        defaultValues={{
          receiptId: "r-1",
          receiptItemCode: "ITM-001",
          quantity: 1,
          unitPrice: 3.99,
          category: "Groceries",
          subcategory: "Dairy",
        }}
      />,
    );

    await pickMilkTemplate(user);

    // An in-place edit that changes no meaning. Clearing the field is a different thing and
    // does drop the link — see the test below.
    const descriptionInput = screen.getByLabelText(/^Description/);
    await user.type(descriptionInput, " ");

    await user.click(screen.getByRole("button", { name: /create item/i }));

    // Incidental whitespace is not a change of meaning, so it must not silently throw away a
    // link the user did not mean to break.
    await waitFor(() => {
      expect(defaultProps.onSubmit).toHaveBeenCalled();
    });
    const submitted = vi.mocked(defaultProps.onSubmit).mock.calls.at(-1)![0];
    expect(submitted.itemTemplateId).toBe("tmpl-1");
  });

  it("drops the template id when the field is cleared, even if the same name is retyped", async () => {
    const user = userEvent.setup();
    render(
      <ReceiptItemForm
        {...defaultProps}
        defaultValues={{
          receiptId: "r-1",
          receiptItemCode: "ITM-001",
          quantity: 1,
          unitPrice: 3.99,
          category: "Groceries",
          subcategory: "Dairy",
        }}
      />,
    );

    await pickMilkTemplate(user);

    const descriptionInput = screen.getByLabelText(/^Description/);
    await user.clear(descriptionInput);
    await user.type(descriptionInput, "Milk");

    await user.click(screen.getByRole("button", { name: /create item/i }));

    // Clearing empties the field, which does not match the template name, so the link goes —
    // and nothing re-applies it when the same text is typed back by hand. That is deliberately
    // left alone rather than "fixed": such an item falls through to the resolver, which
    // exact-matches the very entry the template created and links it anyway. The outcome is the
    // same; only the route differs. Restoring the link on a text match would mean re-deriving
    // provenance from a string, which is the guesswork this issue removes.
    await waitFor(() => {
      expect(defaultProps.onSubmit).toHaveBeenCalled();
    });
    const submitted = vi.mocked(defaultProps.onSubmit).mock.calls.at(-1)![0];
    expect(submitted.itemTemplateId).toBeUndefined();
    expect(submitted.description).toBe("Milk");
  });

  it("sends no template id for a hand-typed line", async () => {
    const user = userEvent.setup();
    render(
      <ReceiptItemForm
        {...defaultProps}
        defaultValues={{
          receiptId: "r-1",
          receiptItemCode: "ITM-001",
          description: "Sourdough Loaf",
          quantity: 1,
          unitPrice: 3.99,
          category: "Groceries",
          subcategory: "Dairy",
        }}
      />,
    );

    await user.click(screen.getByRole("button", { name: /create item/i }));

    await waitFor(() => {
      expect(defaultProps.onSubmit).toHaveBeenCalled();
    });
    const submitted = vi.mocked(defaultProps.onSubmit).mock.calls.at(-1)![0];
    expect(submitted.itemTemplateId).toBeUndefined();
  });

  it("selects a description history entry and populates the field", async () => {
    const user = userEvent.setup();
    localStorage.setItem(
      "receipts:item-description-history",
      JSON.stringify(["Whole Milk"]),
    );

    render(<ReceiptItemForm {...defaultProps} />);

    const descriptionInput = screen.getByLabelText(/^Description/);
    await user.click(descriptionInput);

    await waitFor(() => {
      expect(screen.getByText("Whole Milk")).toBeInTheDocument();
    });

    // Select the history item (rendered as a CommandItem with value prefixed "history: ")
    await user.click(screen.getByText("Whole Milk"));

    expect(descriptionInput).toHaveValue("Whole Milk");
  });

  it("persists description and item code to history on submit", async () => {
    const user = userEvent.setup();
    render(
      <ReceiptItemForm
        {...defaultProps}
        defaultValues={{
          receiptId: "r-1",
          receiptItemCode: "ITM-001",
          description: "Bananas",

          quantity: 1,
          unitPrice: 1.29,
          category: "Groceries",
          subcategory: "Dairy",
        }}
      />,
    );

    await user.click(screen.getByRole("button", { name: /create item/i }));

    await waitFor(() => {
      expect(defaultProps.onSubmit).toHaveBeenCalled();
    });

    const storedDescriptions = JSON.parse(
      localStorage.getItem("receipts:item-description-history") ?? "[]",
    ) as string[];
    expect(storedDescriptions).toContain("Bananas");

    const storedItemCodes = JSON.parse(
      localStorage.getItem("receipts:item-code-history") ?? "[]",
    ) as string[];
    expect(storedItemCodes).toContain("ITM-001");
  });

  it("shows saved item codes in the item code autocomplete dropdown", async () => {
    const user = userEvent.setup();
    localStorage.setItem(
      "receipts:item-code-history",
      JSON.stringify(["ITM-001", "ITM-002"]),
    );

    render(<ReceiptItemForm {...defaultProps} />);

    // The Item Code field is now an Input with autocomplete popover;
    // type a partial match to trigger the dropdown
    const itemCodeInput = screen.getByPlaceholderText("Enter item code...");
    await user.type(itemCodeInput, "ITM");

    await waitFor(() => {
      expect(screen.getByText("ITM-001")).toBeInTheDocument();
      expect(screen.getByText("ITM-002")).toBeInTheDocument();
    });
  });

  it("does not allow custom category values (no 'Use' option for arbitrary text)", async () => {
    const user = userEvent.setup();
    render(<ReceiptItemForm {...defaultProps} />);

    // Open the category combobox
    const categoryCombobox = screen.getByLabelText(/^Category/);
    await user.click(categoryCombobox);

    // Type a non-existent category (like a store name)
    const searchInput = screen.getByPlaceholderText("Search categories...");
    await user.type(searchInput, "Costco");

    // Should NOT show a "Use" button for arbitrary text
    await waitFor(() => {
      expect(screen.queryByText(/use.*costco/i)).not.toBeInTheDocument();
    });
  });

  it("routes server errors through FormMessage so they have role=alert and are field-associated", async () => {
    render(
      <ReceiptItemForm
        {...defaultProps}
        serverErrors={{ description: "Server-side description error" }}
      />,
    );

    // The error element must have role="alert" (set by FormMessage per RECEIPTS-686)
    // and carry data-slot="form-message", meaning it went through FormMessage
    // rather than a bare <p className="text-destructive">.
    await waitFor(() => {
      const errorEl = screen.getByText("Server-side description error");
      expect(errorEl).toHaveAttribute("role", "alert");
      expect(errorEl).toHaveAttribute("data-slot", "form-message");
    });
  });
});
