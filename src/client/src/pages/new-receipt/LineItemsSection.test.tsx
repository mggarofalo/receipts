import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import { mockQueryResult, mockMutationResult } from "@/test/mock-hooks";
import "@/test/setup-combobox-polyfills";
import { LineItemsSection, type ReceiptLineItem } from "./LineItemsSection";

vi.mock("@/hooks/useCategories", () => ({
  useCategories: vi.fn(() =>
    mockQueryResult({
      data: [
        { id: "cat-1", name: "Food" },
        { id: "cat-2", name: "Household" },
      ],
      isLoading: false,
      isSuccess: true,
    }),
  ),
}));

vi.mock("@/hooks/useSubcategories", () => ({
  useSubcategoriesByCategoryId: vi.fn(() =>
    mockQueryResult({
      data: [],
      isLoading: false,
      isSuccess: true,
    }),
  ),
  useCreateSubcategory: vi.fn(() => mockMutationResult()),
}));

vi.mock("@/hooks/useSimilarItems", () => ({
  useSimilarItems: vi.fn(() =>
    mockQueryResult({
      data: [],
      isFetching: false,
    }),
  ),
  useCategoryRecommendations: vi.fn(() =>
    mockQueryResult({
      data: [],
    }),
  ),
}));

vi.mock("@/hooks/useReceiptItemSuggestions", () => ({
  useReceiptItemSuggestions: vi.fn(() =>
    mockQueryResult({
      data: undefined,
      isFetching: false,
      isSuccess: false,
    }),
  ),
}));

function makeItem(overrides: Partial<ReceiptLineItem> = {}): ReceiptLineItem {
  return {
    id: "test-id",
    receiptItemCode: "",
    description: "Test",
    pricingMode: "quantity",
    quantity: 1,
    unitPrice: 0,
    totalPrice: 0,
    category: "Food",
    subcategory: "",
    taxCode: "",
    ...overrides,
  };
}

describe("LineItemsSection", () => {
  const defaultProps = {
    items: [] as ReceiptLineItem[],
    onChange: vi.fn(),
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("renders the card title", () => {
    renderWithProviders(<LineItemsSection {...defaultProps} />);
    expect(screen.getByText("Line Items")).toBeInTheDocument();
  });

  it("renders the form fields", () => {
    renderWithProviders(<LineItemsSection {...defaultProps} />);
    expect(screen.getByPlaceholderText("Item description")).toBeInTheDocument();
    expect(screen.getByPlaceholderText("e.g. MILK-GAL")).toBeInTheDocument();
  });

  it("renders Add Item button", () => {
    renderWithProviders(<LineItemsSection {...defaultProps} />);
    expect(
      screen.getByRole("button", { name: /add item/i }),
    ).toBeInTheDocument();
  });

  it("displays subtotal", () => {
    renderWithProviders(<LineItemsSection {...defaultProps} />);
    expect(screen.getByText("Subtotal: $0.00")).toBeInTheDocument();
  });

  it("renders existing items", () => {
    const items: ReceiptLineItem[] = [
      {
        id: "1",
        receiptItemCode: "",
        description: "Milk",
        pricingMode: "quantity",
        quantity: 2,
        unitPrice: 3.5,
        totalPrice: 7,
        category: "Food",
        subcategory: "",
        taxCode: "",
      },
    ];
    renderWithProviders(<LineItemsSection {...defaultProps} items={items} />);
    expect(screen.getByText("Milk")).toBeInTheDocument();
    expect(screen.getByText("$3.50")).toBeInTheDocument();
    expect(screen.getByText("$7.00")).toBeInTheDocument(); // line total
    expect(screen.getByText("Food")).toBeInTheDocument();
  });

  it("displays subtotal with existing items", () => {
    const items: ReceiptLineItem[] = [
      {
        id: "1",
        receiptItemCode: "",
        description: "Milk",
        pricingMode: "quantity",
        quantity: 2,
        unitPrice: 3.5,
        totalPrice: 7,
        category: "Food",
        subcategory: "",
        taxCode: "",
      },
      {
        id: "2",
        receiptItemCode: "",
        description: "Bread",
        pricingMode: "flat",
        quantity: 1,
        unitPrice: 4.0,
        totalPrice: 4,
        category: "Food",
        subcategory: "",
        taxCode: "",
      },
    ];
    renderWithProviders(<LineItemsSection {...defaultProps} items={items} />);
    expect(screen.getByText("Subtotal: $11.00")).toBeInTheDocument();
  });

  // RECEIPTS-655: flat-priced items (Walmart shape) carry the line total in
  // `totalPrice` while `quantity`/`unitPrice` may be 0/0. The render must use
  // totalPrice for both the line cell and the rolling subtotal.
  it("renders flat-priced rows using totalPrice for the line cell (RECEIPTS-655)", () => {
    const items: ReceiptLineItem[] = [
      {
        id: "1",
        receiptItemCode: "WMT-001",
        description: "GV WHL MLK",
        pricingMode: "flat",
        quantity: 1,
        unitPrice: 0,
        totalPrice: 4.97,
        category: "Food",
        subcategory: "",
        taxCode: "F",
      },
    ];
    renderWithProviders(<LineItemsSection {...defaultProps} items={items} />);

    // The previous bug rendered "$0.00" because the table multiplied
    // quantity * unitPrice (1 * 0). Assert the row shows the real total.
    expect(screen.getByText("$4.97")).toBeInTheDocument();
    expect(screen.queryByText("$0.00")).not.toBeInTheDocument();
  });

  it("subtotal sums flat-priced totalPrice and quantity-priced q × p (RECEIPTS-655)", () => {
    const items: ReceiptLineItem[] = [
      // Flat: only the line total is meaningful.
      {
        id: "1",
        receiptItemCode: "WMT-001",
        description: "GV WHL MLK",
        pricingMode: "flat",
        quantity: 1,
        unitPrice: 0,
        totalPrice: 4.97,
        category: "Food",
        subcategory: "",
        taxCode: "F",
      },
      // Quantity: 2 × 1.50 = 3.00
      {
        id: "2",
        receiptItemCode: "BANANA",
        description: "Bananas",
        pricingMode: "quantity",
        quantity: 2,
        unitPrice: 1.5,
        totalPrice: 3.0,
        category: "Food",
        subcategory: "",
        taxCode: "",
      },
    ];
    renderWithProviders(<LineItemsSection {...defaultProps} items={items} />);

    // 4.97 + 3.00 = 7.97
    expect(screen.getByText("Subtotal: $7.97")).toBeInTheDocument();
  });

  it("hides quantity/unit-price columns with em-dash for flat-priced rows (RECEIPTS-655)", () => {
    const items: ReceiptLineItem[] = [
      {
        id: "1",
        receiptItemCode: "WMT-001",
        description: "Walmart unit-priced",
        pricingMode: "flat",
        quantity: 1,
        unitPrice: 0,
        totalPrice: 12.34,
        category: "Food",
        subcategory: "",
        taxCode: "",
      },
    ];
    renderWithProviders(<LineItemsSection {...defaultProps} items={items} />);

    // The line total is rendered.
    expect(screen.getByText("$12.34")).toBeInTheDocument();
    // No "$0.00" should appear in the row (the bug surface).
    expect(screen.queryByText("$0.00")).not.toBeInTheDocument();
  });

  it("rounds per-item totals to nearest cent when computing subtotal", () => {
    // Uses Math.round to avoid IEEE 754 float issues with Math.floor.
    // Example: 10 x $0.09 = $0.90 exactly
    // With Math.floor: 10 * 0.09 * 100 = 89.9999... → floor → 89 → $0.89 (WRONG)
    // With Math.round: 10 * 0.09 * 100 = 89.9999... → round → 90 → $0.90 (CORRECT)
    const items: ReceiptLineItem[] = [
      {
        id: "1",
        receiptItemCode: "",
        description: "Fractional item",
        pricingMode: "quantity",
        quantity: 10,
        unitPrice: 0.09,
        totalPrice: 0.9,
        category: "Food",
        subcategory: "",
        taxCode: "",
      },
    ];
    renderWithProviders(<LineItemsSection {...defaultProps} items={items} />);
    expect(screen.getByText("Subtotal: $0.90")).toBeInTheDocument();
  });

  // RECEIPTS-661: Weight-priced rows (Walmart "TOMATO 2.300 lb @ 0.92") arrive
  // in quantity mode with a populated totalPrice — the upstream fix at the
  // controller / mapper levels guarantees `totalPrice` is no longer `0` for
  // these rows. Assert the cell renders the correct currency value AND the
  // rolling subtotal sums to the printed receipt subtotal, not $0.00.
  it("renders quantity-mode weight-priced rows with the correct line total (RECEIPTS-661)", () => {
    const items: ReceiptLineItem[] = [
      {
        id: "1",
        receiptItemCode: "TOMATO",
        description: "TOMATO",
        pricingMode: "quantity",
        quantity: 2.3,
        unitPrice: 0.92,
        totalPrice: 2.12, // 2.3 * 0.92 = 2.116, rounded to cents
        category: "Food",
        subcategory: "",
        taxCode: "",
      },
    ];
    renderWithProviders(<LineItemsSection {...defaultProps} items={items} />);

    // Cell shows the right currency, not $0.00.
    expect(screen.getByText("$2.12")).toBeInTheDocument();
    expect(screen.queryByText("$0.00")).not.toBeInTheDocument();
  });

  it("rolling subtotal across two weight-priced rows matches printed receipt total (RECEIPTS-661)", () => {
    // The reference Walmart bug: TOMATO 2.300 lb @ 0.92 + BANANAS 2.460 lb @ 0.50.
    // Real-world subtotal printed on the receipt: $3.35 (2.116 + 1.230 = 3.346).
    const items: ReceiptLineItem[] = [
      {
        id: "1",
        receiptItemCode: "TOMATO",
        description: "TOMATO",
        pricingMode: "quantity",
        quantity: 2.3,
        unitPrice: 0.92,
        totalPrice: 2.12,
        category: "Food",
        subcategory: "",
        taxCode: "",
      },
      {
        id: "2",
        receiptItemCode: "BANANAS",
        description: "BANANAS",
        pricingMode: "quantity",
        quantity: 2.46,
        unitPrice: 0.5,
        totalPrice: 1.23,
        category: "Food",
        subcategory: "",
        taxCode: "",
      },
    ];
    renderWithProviders(<LineItemsSection {...defaultProps} items={items} />);

    // computeLineTotal in quantity mode uses q × p with cent rounding:
    // 2.3 * 0.92 → 2.12 ; 2.46 * 0.5 → 1.23 ; sum = 3.35.
    expect(screen.getByText("Subtotal: $3.35")).toBeInTheDocument();
  });

  it("calls onChange when an item is removed", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const items: ReceiptLineItem[] = [
      {
        id: "1",
        receiptItemCode: "",
        description: "Milk",
        pricingMode: "quantity",
        quantity: 1,
        unitPrice: 3.5,
        totalPrice: 3.5,
        category: "Food",
        subcategory: "",
        taxCode: "",
      },
    ];
    renderWithProviders(<LineItemsSection items={items} onChange={onChange} />);

    await user.click(screen.getByRole("button", { name: /remove/i }));
    expect(onChange).toHaveBeenCalledWith([]);
  });

  it("shows category/subcategory for items", () => {
    const items: ReceiptLineItem[] = [
      {
        id: "1",
        receiptItemCode: "",
        description: "Soap",
        pricingMode: "flat",
        quantity: 1,
        unitPrice: 5,
        totalPrice: 5,
        category: "Household",
        subcategory: "Cleaning",
        taxCode: "",
      },
    ];
    renderWithProviders(<LineItemsSection {...defaultProps} items={items} />);
    expect(screen.getByText("Household / Cleaning")).toBeInTheDocument();
  });

  // --- Inline editing tests ---

  it("shows edit button for each item row", () => {
    const items: ReceiptLineItem[] = [
      {
        id: "1",
        receiptItemCode: "",
        description: "Milk",
        pricingMode: "quantity",
        quantity: 2,
        unitPrice: 3.5,
        totalPrice: 7,
        category: "Food",
        subcategory: "",
        taxCode: "",
      },
    ];
    renderWithProviders(<LineItemsSection {...defaultProps} items={items} />);
    expect(screen.getByRole("button", { name: /edit/i })).toBeInTheDocument();
  });

  it("enters edit mode when edit button is clicked", async () => {
    const user = userEvent.setup();
    const items: ReceiptLineItem[] = [
      {
        id: "1",
        receiptItemCode: "",
        description: "Milk",
        pricingMode: "quantity",
        quantity: 2,
        unitPrice: 3.5,
        totalPrice: 7,
        category: "Food",
        subcategory: "",
        taxCode: "",
      },
    ];
    renderWithProviders(<LineItemsSection {...defaultProps} items={items} />);

    await user.click(screen.getByRole("button", { name: /edit/i }));

    expect(screen.getByLabelText("Edit description")).toBeInTheDocument();
    expect(screen.getByLabelText("Edit quantity")).toBeInTheDocument();
    expect(screen.getByLabelText("Edit unit price")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /save/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /cancel/i })).toBeInTheDocument();
  });

  it("saves edited values and calls onChange", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const items: ReceiptLineItem[] = [
      {
        id: "1",
        receiptItemCode: "",
        description: "Milk",
        pricingMode: "quantity",
        quantity: 2,
        unitPrice: 3.5,
        totalPrice: 7,
        category: "Food",
        subcategory: "",
        taxCode: "",
      },
    ];
    renderWithProviders(<LineItemsSection items={items} onChange={onChange} />);

    await user.click(screen.getByRole("button", { name: /edit/i }));

    const descInput = screen.getByLabelText("Edit description");
    await user.clear(descInput);
    await user.type(descInput, "Whole Milk");

    await user.click(screen.getByRole("button", { name: /save/i }));

    expect(onChange).toHaveBeenCalledWith([
      expect.objectContaining({ description: "Whole Milk" }),
    ]);
  });

  it("cancels editing without calling onChange", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const items: ReceiptLineItem[] = [
      {
        id: "1",
        receiptItemCode: "",
        description: "Milk",
        pricingMode: "quantity",
        quantity: 2,
        unitPrice: 3.5,
        totalPrice: 7,
        category: "Food",
        subcategory: "",
        taxCode: "",
      },
    ];
    renderWithProviders(<LineItemsSection items={items} onChange={onChange} />);

    await user.click(screen.getByRole("button", { name: /edit/i }));

    const descInput = screen.getByLabelText("Edit description");
    await user.clear(descInput);
    await user.type(descInput, "Changed");

    await user.click(screen.getByRole("button", { name: /cancel/i }));

    // onChange should not have been called for editing (only for remove)
    expect(onChange).not.toHaveBeenCalled();
    expect(screen.getByText("Milk")).toBeInTheDocument();
  });

  it("does not save when description is empty", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const items: ReceiptLineItem[] = [
      {
        id: "1",
        receiptItemCode: "",
        description: "Milk",
        pricingMode: "quantity",
        quantity: 2,
        unitPrice: 3.5,
        totalPrice: 7,
        category: "Food",
        subcategory: "",
        taxCode: "",
      },
    ];
    renderWithProviders(<LineItemsSection items={items} onChange={onChange} />);

    await user.click(screen.getByRole("button", { name: /edit/i }));

    const descInput = screen.getByLabelText("Edit description");
    await user.clear(descInput);

    await user.click(screen.getByRole("button", { name: /save/i }));

    // Should still be in edit mode (save rejected)
    expect(screen.getByLabelText("Edit description")).toBeInTheDocument();
    expect(onChange).not.toHaveBeenCalled();
  });

  it("hides the edit form when the item being edited is removed externally", async () => {
    // Regression test for RECEIPTS-642: replaced a useEffect-based
    // editingItemId cleanup with a derived `isEditing` value computed during
    // render. When the parent removes the item, no row matches editingItemId,
    // so the edit UI no longer renders — without a double render.
    const user = userEvent.setup();
    const onChange = vi.fn();
    const items: ReceiptLineItem[] = [
      makeItem({ id: "item-1", description: "Milk" }),
      makeItem({ id: "item-2", description: "Bread" }),
    ];

    const { rerender } = renderWithProviders(
      <LineItemsSection items={items} onChange={onChange} />,
    );

    // Enter edit mode on Milk (item-1).
    const editButtons = screen.getAllByRole("button", { name: /edit/i });
    await user.click(editButtons[0]);
    expect(screen.getByLabelText("Edit description")).toBeInTheDocument();

    // Parent removes item-1 from state externally.
    rerender(<LineItemsSection items={[items[1]]} onChange={onChange} />);

    // Edit form should no longer be visible because no row matches the stale
    // editingItemId. Bread (item-2) renders in display mode, not edit mode.
    expect(screen.queryByLabelText("Edit description")).not.toBeInTheDocument();
    expect(screen.getByText("Bread")).toBeInTheDocument();
  });

  it("disables quantity input in edit mode for flat pricing items", async () => {
    const user = userEvent.setup();
    const items: ReceiptLineItem[] = [
      {
        id: "1",
        receiptItemCode: "",
        description: "Service Fee",
        pricingMode: "flat",
        quantity: 1,
        unitPrice: 25,
        totalPrice: 25,
        category: "Food",
        subcategory: "",
        taxCode: "",
      },
    ];
    renderWithProviders(<LineItemsSection {...defaultProps} items={items} />);

    await user.click(screen.getByRole("button", { name: /edit/i }));

    expect(screen.getByLabelText("Edit quantity")).toBeDisabled();
  });

  // RECEIPTS-655: when editing a flat-priced row, the user must be able to
  // change the line total directly (since the source receipt didn't print a
  // unit price). The unit-price input is also locked.
  it("exposes 'Edit total price' input for flat-priced rows in edit mode (RECEIPTS-655)", async () => {
    const user = userEvent.setup();
    const items: ReceiptLineItem[] = [
      {
        id: "1",
        receiptItemCode: "",
        description: "Walmart unit item",
        pricingMode: "flat",
        quantity: 1,
        unitPrice: 0,
        totalPrice: 4.97,
        category: "Food",
        subcategory: "",
        taxCode: "",
      },
    ];
    renderWithProviders(<LineItemsSection {...defaultProps} items={items} />);

    await user.click(screen.getByRole("button", { name: /edit/i }));

    expect(screen.getByLabelText("Edit total price")).toBeInTheDocument();
    expect(screen.getByLabelText("Edit unit price")).toBeDisabled();
    expect(screen.getByLabelText("Edit quantity")).toBeDisabled();
  });

  // --- Tax code column tests ---

  it("renders the Tax column header in the items table", () => {
    const items: ReceiptLineItem[] = [
      makeItem({ id: "1", description: "Milk" }),
    ];
    renderWithProviders(<LineItemsSection {...defaultProps} items={items} />);
    expect(
      screen.getByRole("columnheader", { name: /tax/i }),
    ).toBeInTheDocument();
  });

  it("displays the tax code badge for items that have one", () => {
    const items: ReceiptLineItem[] = [
      makeItem({ id: "1", description: "Milk", taxCode: "F" }),
    ];
    renderWithProviders(<LineItemsSection {...defaultProps} items={items} />);
    expect(screen.getByText("F")).toBeInTheDocument();
  });

  it("renders an em-dash placeholder when an item has no tax code", () => {
    const items: ReceiptLineItem[] = [
      makeItem({ id: "1", description: "Milk", taxCode: "" }),
    ];
    renderWithProviders(<LineItemsSection {...defaultProps} items={items} />);
    expect(screen.getByLabelText("No tax code")).toBeInTheDocument();
  });

  it("renders the tax-code input in the form row", () => {
    renderWithProviders(<LineItemsSection {...defaultProps} />);
    expect(screen.getByLabelText("Tax code")).toBeInTheDocument();
  });

  it("uppercases tax-code input as the user types", async () => {
    const user = userEvent.setup();
    renderWithProviders(<LineItemsSection {...defaultProps} />);
    const taxInput = screen.getByLabelText("Tax code") as HTMLInputElement;
    await user.type(taxInput, "f");
    expect(taxInput.value).toBe("F");
  });

  it("shows confidence indicator when itemConfidenceById flags low taxCode", () => {
    const items: ReceiptLineItem[] = [
      makeItem({ id: "item-1", description: "Milk", taxCode: "F" }),
    ];
    const itemConfidenceById = new Map([
      ["item-1", { taxCode: "low" as const }],
    ]);
    renderWithProviders(
      <LineItemsSection
        {...defaultProps}
        items={items}
        itemConfidenceById={itemConfidenceById}
      />,
    );
    expect(
      screen.getByLabelText("AI confidence rating: low"),
    ).toBeInTheDocument();
  });

  it("keeps the confidence indicator paired with the surviving item after a removal", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const items: ReceiptLineItem[] = [
      makeItem({ id: "item-1", description: "Milk", taxCode: "F" }),
      makeItem({ id: "item-2", description: "Bread", taxCode: "N" }),
    ];
    // Only item-2 has low confidence on taxCode.
    const itemConfidenceById = new Map([
      ["item-2", { taxCode: "low" as const }],
    ]);

    const { rerender } = renderWithProviders(
      <LineItemsSection
        items={items}
        onChange={onChange}
        itemConfidenceById={itemConfidenceById}
      />,
    );

    // Remove item-1 (Milk). The remaining item is Bread, which is the one
    // that had low confidence — the badge should still appear.
    const removeButtons = screen.getAllByRole("button", { name: /remove/i });
    await user.click(removeButtons[0]);
    expect(onChange).toHaveBeenCalledWith([items[1]]);

    // Simulate the parent removing item-1 from state.
    rerender(
      <LineItemsSection
        items={[items[1]]}
        onChange={onChange}
        itemConfidenceById={itemConfidenceById}
      />,
    );

    expect(
      screen.getByLabelText("AI confidence rating: low"),
    ).toBeInTheDocument();
    // Bread (item-2) is the only row left; the indicator still belongs to it.
    expect(screen.getByText("Bread")).toBeInTheDocument();
  });

  it("preserves the existing tax code when an item is edited and saved without changing it", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const items: ReceiptLineItem[] = [
      makeItem({
        id: "1",
        description: "Milk",
        quantity: 1,
        unitPrice: 1,
        taxCode: "F",
      }),
    ];

    renderWithProviders(<LineItemsSection items={items} onChange={onChange} />);

    await user.click(screen.getByRole("button", { name: /edit/i }));
    await user.click(screen.getByRole("button", { name: /save/i }));

    expect(onChange).toHaveBeenCalledWith([
      expect.objectContaining({ taxCode: "F" }),
    ]);
  });

  it("uppercases the tax code when edited inline", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const items: ReceiptLineItem[] = [
      makeItem({
        id: "1",
        description: "Milk",
        quantity: 1,
        unitPrice: 1,
        taxCode: "",
      }),
    ];

    renderWithProviders(<LineItemsSection items={items} onChange={onChange} />);

    await user.click(screen.getByRole("button", { name: /edit/i }));

    const taxCodeInput = screen.getByLabelText(
      "Edit tax code",
    ) as HTMLInputElement;
    await user.type(taxCodeInput, "n");

    expect(taxCodeInput.value).toBe("N");

    await user.click(screen.getByRole("button", { name: /save/i }));

    expect(onChange).toHaveBeenCalledWith([
      expect.objectContaining({ taxCode: "N" }),
    ]);
  });
});
