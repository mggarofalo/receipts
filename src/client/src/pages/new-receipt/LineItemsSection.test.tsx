import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import { mockQueryResult, mockMutationResult } from "@/test/mock-hooks";
import "@/test/setup-combobox-polyfills";
import { useSimilarItems } from "@/hooks/useSimilarItems";
import { usePromoteToTemplate } from "@/hooks/usePromoteToTemplate";
import { LineItemsSection, type ReceiptLineItem } from "./LineItemsSection";

vi.mock("@/hooks/useCategories", () => ({
  useAllCategories: vi.fn(() =>
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
  useAllSubcategoriesByCategoryId: vi.fn(() =>
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

vi.mock("@/hooks/usePromoteToTemplate", () => ({
  usePromoteToTemplate: vi.fn(() => mockMutationResult()),
}));

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
        quantity: 2,
        unitPrice: 3.5,
        category: "Food",
        subcategory: "",
      },
    ];
    renderWithProviders(
      <LineItemsSection {...defaultProps} items={items} />,
    );
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
        quantity: 2,
        unitPrice: 3.5,
        category: "Food",
        subcategory: "",
      },
      {
        id: "2",
        receiptItemCode: "",
        description: "Bread",
        quantity: 1,
        unitPrice: 4.0,
        category: "Food",
        subcategory: "",
      },
    ];
    renderWithProviders(
      <LineItemsSection {...defaultProps} items={items} />,
    );
    expect(screen.getByText("Subtotal: $11.00")).toBeInTheDocument();
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
        quantity: 10,
        unitPrice: 0.09,
        category: "Food",
        subcategory: "",
      },
    ];
    renderWithProviders(
      <LineItemsSection {...defaultProps} items={items} />,
    );
    expect(screen.getByText("Subtotal: $0.90")).toBeInTheDocument();
  });

  it("calls onChange when an item is removed", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const items: ReceiptLineItem[] = [
      {
        id: "1",
        receiptItemCode: "",
        description: "Milk",
        quantity: 1,
        unitPrice: 3.5,
        category: "Food",
        subcategory: "",
      },
    ];
    renderWithProviders(
      <LineItemsSection items={items} onChange={onChange} />,
    );

    await user.click(screen.getByRole("button", { name: /remove/i }));
    expect(onChange).toHaveBeenCalledWith([]);
  });

  it("shows category/subcategory for items", () => {
    const items: ReceiptLineItem[] = [
      {
        id: "1",
        receiptItemCode: "",
        description: "Soap",
        quantity: 1,
        unitPrice: 5,
        category: "Household",
        subcategory: "Cleaning",
      },
    ];
    renderWithProviders(
      <LineItemsSection {...defaultProps} items={items} />,
    );
    expect(screen.getByText("Household / Cleaning")).toBeInTheDocument();
  });

  it("wraps long descriptions so the table cannot force page horizontal scroll (WCAG 1.4.10)", () => {
    const longDescription = "A".repeat(200);
    const items: ReceiptLineItem[] = [
      {
        id: "1",
        receiptItemCode: "",
        description: longDescription,
        quantity: 1,
        unitPrice: 2.5,
        category: "Food",
        subcategory: "",
      },
    ];
    renderWithProviders(
      <LineItemsSection {...defaultProps} items={items} />,
    );
    const cell = screen.getByText(longDescription).closest("td");
    expect(cell).not.toBeNull();
    expect(cell).toHaveClass("whitespace-normal");
    expect(cell).toHaveClass("break-words");
    expect(cell).toHaveClass("max-w-[32ch]");
  });

  // --- Inline editing tests ---

  it("shows edit button for each item row", () => {
    const items: ReceiptLineItem[] = [
      {
        id: "1",
        receiptItemCode: "",
        description: "Milk",
        quantity: 2,
        unitPrice: 3.5,
        category: "Food",
        subcategory: "",
      },
    ];
    renderWithProviders(
      <LineItemsSection {...defaultProps} items={items} />,
    );
    expect(screen.getByRole("button", { name: /edit/i })).toBeInTheDocument();
  });

  it("enters edit mode when edit button is clicked", async () => {
    const user = userEvent.setup();
    const items: ReceiptLineItem[] = [
      {
        id: "1",
        receiptItemCode: "",
        description: "Milk",
        quantity: 2,
        unitPrice: 3.5,
        category: "Food",
        subcategory: "",
      },
    ];
    renderWithProviders(
      <LineItemsSection {...defaultProps} items={items} />,
    );

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
        quantity: 2,
        unitPrice: 3.5,
        category: "Food",
        subcategory: "",
      },
    ];
    renderWithProviders(
      <LineItemsSection items={items} onChange={onChange} />,
    );

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
        quantity: 2,
        unitPrice: 3.5,
        category: "Food",
        subcategory: "",
      },
    ];
    renderWithProviders(
      <LineItemsSection items={items} onChange={onChange} />,
    );

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
        quantity: 2,
        unitPrice: 3.5,
        category: "Food",
        subcategory: "",
      },
    ];
    renderWithProviders(
      <LineItemsSection items={items} onChange={onChange} />,
    );

    await user.click(screen.getByRole("button", { name: /edit/i }));

    const descInput = screen.getByLabelText("Edit description");
    await user.clear(descInput);

    await user.click(screen.getByRole("button", { name: /save/i }));

    // Should still be in edit mode (save rejected)
    expect(screen.getByLabelText("Edit description")).toBeInTheDocument();
    expect(onChange).not.toHaveBeenCalled();
  });

  // --- Save-as-template promotion tests ---

  describe("promote to template", () => {
    const historySuggestion = {
      name: "Milk (gallon)",
      similarity: 0.9,
      semanticSimilarity: null,
      combinedScore: 0.9,
      source: "history" as const,
      defaultCategory: "Food",
      defaultSubcategory: "Dairy",
      defaultUnitPrice: 3.5,
      defaultItemCode: "MILK-GAL",
    };
    const templateSuggestion = {
      name: "Milk chocolate",
      similarity: 0.8,
      semanticSimilarity: null,
      combinedScore: 0.8,
      source: "template" as const,
      defaultCategory: "Food",
      defaultSubcategory: null,
      defaultUnitPrice: null,
      defaultItemCode: null,
    };

    function mockSuggestions() {
      vi.mocked(useSimilarItems).mockReturnValue(
        mockQueryResult({
          data: [historySuggestion, templateSuggestion],
          isFetching: false,
          isSuccess: true,
        }),
      );
    }

    function mockPromote() {
      const mutate = vi.fn();
      vi.mocked(usePromoteToTemplate).mockReturnValue(
        mockMutationResult({ mutate }),
      );
      return mutate;
    }

    it("promotes a history suggestion without selecting it or closing the popover", async () => {
      const user = userEvent.setup();
      mockSuggestions();
      const mutate = mockPromote();
      renderWithProviders(<LineItemsSection {...defaultProps} />);

      const input = screen.getByPlaceholderText("Item description");
      await user.type(input, "mi");

      const promoteButton = await screen.findByRole("button", {
        name: 'Save "Milk (gallon)" as template',
      });
      await user.click(promoteButton);

      expect(mutate).toHaveBeenCalledWith({
        name: "Milk (gallon)",
        defaultCategory: "Food",
        defaultSubcategory: "Dairy",
        defaultUnitPrice: 3.5,
        defaultItemCode: "MILK-GAL",
      });
      // The click must not apply the suggestion to the form...
      expect(input).toHaveValue("mi");
      // ...and must not close the popover.
      expect(
        screen.getByRole("option", { name: /milk \(gallon\)/i }),
      ).toBeInTheDocument();
    });

    it("does not render a promote button on template-source suggestions", async () => {
      const user = userEvent.setup();
      mockSuggestions();
      mockPromote();
      renderWithProviders(<LineItemsSection {...defaultProps} />);

      await user.type(screen.getByPlaceholderText("Item description"), "mi");

      await screen.findByRole("button", {
        name: 'Save "Milk (gallon)" as template',
      });
      expect(
        screen.queryByRole("button", {
          name: 'Save "Milk chocolate" as template',
        }),
      ).not.toBeInTheDocument();
    });

    it("promotes an entered line item from the table row action", async () => {
      const user = userEvent.setup();
      const mutate = mockPromote();
      const items: ReceiptLineItem[] = [
        {
          id: "1",
          receiptItemCode: "MILK-GAL",
          description: "Milk (gallon)",
          quantity: 2,
          unitPrice: 3.5,
          category: "Food",
          subcategory: "Dairy",
        },
      ];
      renderWithProviders(
        <LineItemsSection {...defaultProps} items={items} />,
      );

      await user.click(
        screen.getByRole("button", { name: "Save as template" }),
      );

      expect(mutate).toHaveBeenCalledWith({
        name: "Milk (gallon)",
        defaultCategory: "Food",
        defaultSubcategory: "Dairy",
        defaultUnitPrice: 3.5,
        defaultItemCode: "MILK-GAL",
      });
    });

    it("does not fire another promote while one is pending", async () => {
      const user = userEvent.setup();
      const mutate = vi.fn();
      vi.mocked(usePromoteToTemplate).mockReturnValue(
        mockMutationResult({ mutate, isPending: true, status: "pending" }),
      );
      const items: ReceiptLineItem[] = [
        {
          id: "1",
          receiptItemCode: "",
          description: "Milk",
          quantity: 1,
          unitPrice: 3.5,
          category: "Food",
          subcategory: "",
        },
      ];
      renderWithProviders(
        <LineItemsSection {...defaultProps} items={items} />,
      );

      await user.click(
        screen.getByRole("button", { name: "Save as template" }),
      );

      expect(mutate).not.toHaveBeenCalled();
    });

    it("disables the line-item promote button while a promotion is pending", () => {
      vi.mocked(usePromoteToTemplate).mockReturnValue(
        mockMutationResult({ mutate: vi.fn(), isPending: true, status: "pending" }),
      );
      const items: ReceiptLineItem[] = [
        {
          id: "1",
          receiptItemCode: "",
          description: "Milk",
          quantity: 1,
          unitPrice: 3.5,
          category: "Food",
          subcategory: "",
        },
      ];
      renderWithProviders(
        <LineItemsSection {...defaultProps} items={items} />,
      );

      expect(
        screen.getByRole("button", { name: "Save as template" }),
      ).toBeDisabled();
    });

    it("disables the popover promote button while a promotion is pending", async () => {
      const user = userEvent.setup();
      mockSuggestions();
      vi.mocked(usePromoteToTemplate).mockReturnValue(
        mockMutationResult({ mutate: vi.fn(), isPending: true, status: "pending" }),
      );
      renderWithProviders(<LineItemsSection {...defaultProps} />);

      await user.type(screen.getByPlaceholderText("Item description"), "mi");

      expect(
        await screen.findByRole("button", {
          name: 'Save "Milk (gallon)" as template',
        }),
      ).toBeDisabled();
    });
  });
});
