vi.mock("@/hooks/useCategoryTaxonomy", () => ({
  useCategoryTaxonomy: () => ({
    data: {
      id: "revision-1",
      nodes: [
        { id: "food", name: "Food", archived: false },
        { id: "house", name: "Household", archived: false },
        { id: "fruit", name: "Fruit", parentId: "food", archived: false },
      ],
    },
    isLoading: false,
    isError: false,
    isFetching: false,
    refetch: vi.fn(),
  }),
}));
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import { mockQueryResult, mockMutationResult } from "@/test/mock-hooks";
import "@/test/setup-combobox-polyfills";
import { useSimilarItems } from "@/hooks/useSimilarItems";
import { useReceiptItemSuggestions } from "@/hooks/useReceiptItemSuggestions";
import type { components } from "@/generated/api";
import { LineItemsSection, type ReceiptLineItem } from "./LineItemsSection";
import { chooseCategoryOption } from "@/test/category-picker";

vi.mock("@/hooks/useReceiptNormalizationPreview", () => ({
  useReceiptNormalizationPreview: () => ({
    ...mockQueryResult({
      data: {
        sourceFingerprint: "line-source",
        sourceAmbiguous: false,
        excluded: false,
        candidates: [],
        total: 0,
        offset: 0,
        limit: 10,
      },
    }),
    isSuccess: true,
    isFetching: false,
    isDebouncing: false,
  }),
}));
vi.mock("@/hooks/useReceiptNormalizationCandidates", () => ({
  useReceiptNormalizationCandidates: () =>
    mockQueryResult({
      isPending: false,
      isFetching: false,
      data: [
        {
          id: "22222222-2222-4222-8222-222222222222",
          revision: "33333333-3333-4333-8333-333333333333",
          canonicalName: "Beef franks",
          displayLabel: null,
          channels: ["lexical"],
        },
      ],
    }),
}));

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
}));

// Empty unless a test configures envelopes, as when none have been created.
const budget = vi.hoisted(() => ({
  envelopes: [] as { id: string; name: string; isActive: boolean }[],
  defaults: [] as { categoryId: string; envelopeId: string; updatedAt: string }[],
}));
vi.mock("@/hooks/useEnvelopes", () => ({
  useEnvelopes: vi.fn(() => ({
    ...mockQueryResult({
      data: { data: budget.envelopes },
      isSuccess: true,
      isLoading: false,
    }),
    envelopes: budget.envelopes,
  })),
  useItemTypeEnvelopeDefaults: vi.fn(() => ({
    ...mockQueryResult({
      data: { data: budget.defaults },
      isSuccess: true,
      isLoading: false,
    }),
    defaults: budget.defaults,
  })),
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


describe("LineItemsSection", () => {
  it("shows rounded midpoint lines and sums the rounded lines in the subtotal", () => {
    const items = [0, 1].map((index) => ({
      id: `line-${index}`,
      receiptItemCode: "",
      description: `Half item ${index}`,
      quantity: 0.5,
      unitPrice: 2.01,
      category: "Food",
      subcategory: "",
    }));
    renderWithProviders(<LineItemsSection items={items} onChange={vi.fn()} />);
    expect(screen.getAllByText("$1.01")).toHaveLength(2);
    expect(screen.getByText("Subtotal: $2.02")).toBeInTheDocument();
  });

  const defaultProps = {
    items: [] as ReceiptLineItem[],
    onChange: vi.fn(),
  };

  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    window.dispatchEvent(new StorageEvent("storage", { key: null }));
  });

  it("does not advertise retired template shortcuts", () => {
    renderWithProviders(<LineItemsSection {...defaultProps} />);
    const description = screen.getByPlaceholderText("Item description");
    expect(description).not.toHaveAttribute("aria-describedby", "description-shortcuts-hint");
    expect(screen.queryByText(/Shift\+Enter to save it as a template/)).not.toBeInTheDocument();
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
    renderWithProviders(<LineItemsSection {...defaultProps} items={items} />);
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
    renderWithProviders(<LineItemsSection {...defaultProps} items={items} />);
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
        quantity: 1,
        unitPrice: 5,
        category: "Household",
        subcategory: "Cleaning",
      },
    ];
    renderWithProviders(<LineItemsSection {...defaultProps} items={items} />);
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
    renderWithProviders(<LineItemsSection {...defaultProps} items={items} />);
    const cell = screen.getByText(longDescription).closest("td");
    expect(cell).not.toBeNull();
    expect(cell).toHaveClass("whitespace-normal");
    expect(cell).toHaveClass("break-words");
    expect(cell).toHaveClass("max-w-[32ch]");
  });

  it("adds a new receipt line with four-decimal unit price", async () => {
    const user = userEvent.setup();
    renderWithProviders(<LineItemsSection {...defaultProps} />);
    await user.type(
      screen.getByPlaceholderText("Item description"),
      "Gasoline",
    );
    await user.click(screen.getByRole("combobox", { name: /^Category/ }));
    await chooseCategoryOption(user, "Food");
    await user.click(screen.getByLabelText(/^Unit Price/));
    await user.paste("3.459");
    await user.tab();
    await user.click(screen.getByRole("button", { name: /add item/i }));
    await waitFor(() =>
      expect(defaultProps.onChange).toHaveBeenCalledWith([
        expect.objectContaining({ unitPrice: 3.459, description: "Gasoline" }),
      ]),
    );
  });

  it("displays subcent unit prices and preserves them through inline focus, blur and save", async () => {
    const user = userEvent.setup();
    const items: ReceiptLineItem[] = [
      {
        id: "precision",
        receiptItemCode: "",
        description: "Gasoline",
        quantity: 2,
        unitPrice: 3.459,
        category: "Food",
        subcategory: "",
      },
    ];
    renderWithProviders(<LineItemsSection {...defaultProps} items={items} />);
    expect(screen.getByText("$3.459")).toBeInTheDocument();
    expect(screen.getByText("$6.92")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /edit/i }));
    await user.click(screen.getByLabelText("Edit unit price"));
    await user.tab();
    await user.click(screen.getByRole("button", { name: /save/i }));
    await waitFor(() =>
      expect(defaultProps.onChange).toHaveBeenCalledWith([
        expect.objectContaining({ unitPrice: 3.459 }),
      ]),
    );
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
        quantity: 2,
        unitPrice: 3.5,
        category: "Food",
        subcategory: "",
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
        quantity: 2,
        unitPrice: 3.5,
        category: "Food",
        subcategory: "",
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
        quantity: 2,
        unitPrice: 3.5,
        category: "Food",
        subcategory: "",
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
        quantity: 2,
        unitPrice: 3.5,
        category: "Food",
        subcategory: "",
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

  // --- Save-as-template promotion tests ---

  describe("receipt history suggestions", () => {
    const historySuggestion = {
      name: "Milk (gallon)",
      similarity: 0.9,
      semanticSimilarity: null,
      combinedScore: 0.9,
      source: "history" as const,
      defaultCategory: "Legacy Food",
      defaultSubcategory: "Legacy Dairy",
      defaultUnitPrice: 3.5,
      defaultItemCode: "MILK-GAL",
    };

    it("keeps raw description selection while hiding legacy defaults and promotion", async () => {
      const user = userEvent.setup();
      vi.mocked(useSimilarItems).mockReturnValue(
        mockQueryResult({ data: [historySuggestion], isFetching: false, isSuccess: true }),
      );
      renderWithProviders(<LineItemsSection {...defaultProps} />);

      const input = screen.getByPlaceholderText("Item description");
      await user.type(input, "mi");
      const option = await screen.findByRole("option", { name: /milk \(gallon\)/i });
      expect(option).not.toHaveTextContent("Legacy Food");
      expect(option).not.toHaveTextContent("Legacy Dairy");
      expect(screen.queryByRole("button", { name: /save.*as template/i })).not.toBeInTheDocument();
      await user.keyboard("{Enter}");
      expect(input).toHaveValue("Milk (gallon)");
    });

    it("does not expose template promotion on entered rows", () => {
      const items: ReceiptLineItem[] = [{
        id: "1", receiptItemCode: "MILK-GAL", description: "Milk (gallon)",
        quantity: 2, unitPrice: 3.5, category: "Food", subcategory: "Dairy",
      }];
      renderWithProviders(<LineItemsSection {...defaultProps} items={items} />);
      expect(screen.queryByRole("button", { name: /save as template/i })).not.toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Edit" })).toBeInTheDocument();
    });
  });
});

describe("line item draft normalization", () => {
  it("normalizes added descriptions and rejects whitespace-only descriptions", async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderWithProviders(<LineItemsSection items={[]} onChange={onChange} />);
    await user.type(screen.getByPlaceholderText("Item description"), "   ");
    await user.click(screen.getByRole("button", { name: /add item/i }));
    expect(
      await screen.findByText("Description is required"),
    ).toBeInTheDocument();
    expect(onChange).not.toHaveBeenCalled();
  });
});

it("normalizes equivalent add and edit drafts identically while preserving item identity", async () => {
  const user = userEvent.setup();
  const onChange = vi.fn();
  const { rerender } = renderWithProviders(
    <LineItemsSection items={[]} onChange={onChange} />,
  );
  await user.type(screen.getByPlaceholderText("Item description"), "  Milk  ");
  await user.type(screen.getByPlaceholderText("e.g. MILK-GAL"), " CODE ");
  await user.click(screen.getByRole("combobox", { name: /^Category/ }));
  await chooseCategoryOption(user, "Food");
  await user.click(screen.getByRole("button", { name: /add item/i }));
  await waitFor(() => expect(onChange).toHaveBeenCalledTimes(1));
  const added = onChange.mock.calls[0][0][0] as ReceiptLineItem;
  expect(added).toMatchObject({
    id: expect.any(String),
    description: "Milk",
    receiptItemCode: "CODE",
    category: "Food",
    subcategory: "",
    quantity: 1,
    unitPrice: 0,
  });
  rerender(<LineItemsSection items={[added]} onChange={onChange} />);
  await user.click(screen.getByRole("button", { name: /edit/i }));
  await user.clear(screen.getByLabelText("Edit description"));
  await user.type(screen.getByLabelText("Edit description"), "  Milk  ");
  await user.click(screen.getByRole("button", { name: /save/i }));
  expect(onChange).toHaveBeenLastCalledWith([added]);
});

it("retains an explicit nested category identity and operation while editing unrelated facts", async () => {
  const user = userEvent.setup();
  const onChange = vi.fn();
  const { rerender } = renderWithProviders(
    <LineItemsSection items={[]} onChange={onChange} />,
  );
  await user.type(screen.getByPlaceholderText("Item description"), "Apple");
  await user.click(screen.getByRole("combobox", { name: /^Category/ }));
  await chooseCategoryOption(user, "Food / Fruit");
  await user.click(screen.getByRole("button", { name: /add item/i }));
  await waitFor(() => expect(onChange).toHaveBeenCalledTimes(1));
  const added = onChange.mock.calls[0][0][0] as ReceiptLineItem;
  expect(added).toMatchObject({
    category: "Food",
    subcategory: "Fruit",
    categoryChoice: {
      categoryId: "fruit",
      taxonomyRevisionId: "revision-1",
      path: expect.any(Array),
    },
    categoryOperationId: expect.any(String),
  });
  rerender(<LineItemsSection items={[added]} onChange={onChange} />);
  await user.click(screen.getByRole("button", { name: /edit/i }));
  await user.clear(screen.getByLabelText("Edit description"));
  await user.type(screen.getByLabelText("Edit description"), "Apple slices");
  await user.click(screen.getByRole("button", { name: /save/i }));
  expect(onChange.mock.calls[1][0][0]).toMatchObject({
    description: "Apple slices",
    categoryChoice: added.categoryChoice,
    categoryOperationId: added.categoryOperationId,
  });
});

it("selects a product in the optional dialog while retaining the raw receipt description on the added line", async () => {
  const user = userEvent.setup();
  const onChange = vi.fn();
  renderWithProviders(
    <LineItemsSection items={[]} onChange={onChange} location="Market" />,
  );
  await user.type(screen.getByPlaceholderText("Item description"), "BF FRANKS");
  expect(screen.queryByRole("dialog", { name: "Choose a product" })).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: /^Choose suggested/ })).not.toBeInTheDocument();
  await user.click(await screen.findByRole("button", { name: "Choose product" }));
  await user.click(
    await screen.findByRole("button", { name: "Choose suggested Beef franks" }),
  );
  expect(screen.getByPlaceholderText("Item description")).toHaveValue(
    "BF FRANKS",
  );
  expect(screen.queryByRole("dialog", { name: "Choose a product" })).not.toBeInTheDocument();
  await user.click(screen.getByRole("combobox", { name: /^Category/ }));
  await chooseCategoryOption(user, "Food");
  await user.click(screen.getByRole("button", { name: /add item/i }));
  await waitFor(() =>
    expect(onChange).toHaveBeenCalledWith([
      expect.objectContaining({
        description: "BF FRANKS",
        normalizationChoice: expect.objectContaining({
          label: "Beef franks",
          intent: expect.objectContaining({
            kind: "confirmExisting",
            targetId: "22222222-2222-4222-8222-222222222222",
            expectedTargetRevision: "33333333-3333-4333-8333-333333333333",
            sourceFingerprint: "line-source",
            rememberSource: false,
          }),
        }),
      }),
    ]),
  );
});

describe("envelope display", () => {
  const food = { id: "food", name: "Food", archived: false };
  const fruit = { id: "fruit", name: "Fruit", parentId: "food", archived: false };
  const house = { id: "house", name: "Household", archived: false };
  const typed = (
    id: string,
    description: string,
    path: (typeof food)[],
    envelopeId?: string,
  ): ReceiptLineItem => ({
    id,
    receiptItemCode: "",
    description,
    quantity: 1,
    unitPrice: 1,
    category: path[0].name,
    subcategory: "",
    categoryChoice: {
      categoryId: path[path.length - 1].id,
      taxonomyRevisionId: "revision-1",
      path,
    },
    envelopeId,
  });
  const items: ReceiptLineItem[] = [
    typed("1", "Apples", [food, fruit]),
    typed("2", "Bananas", [food, fruit]),
    typed("3", "Dish soap", [house]),
    typed("4", "Gift bag", [food], "env-gifts"),
    {
      id: "5",
      receiptItemCode: "",
      description: "Legacy line",
      quantity: 1,
      unitPrice: 1,
      category: "Food",
      subcategory: "",
    },
  ];
  const row = (description: string) =>
    screen.getByText(description).closest("tr") as HTMLElement;

  beforeAll(() => {
    Element.prototype.hasPointerCapture ??= () => false;
    Element.prototype.setPointerCapture ??= () => {};
    Element.prototype.releasePointerCapture ??= () => {};
  });
  beforeEach(() => {
    budget.envelopes = [
      { id: "env-groceries", name: "Groceries", isActive: true },
      { id: "env-gifts", name: "Gifts", isActive: true },
    ];
    budget.defaults = [
      {
        categoryId: "food",
        envelopeId: "env-groceries",
        updatedAt: "2026-01-01T00:00:00Z",
      },
    ];
  });
  afterEach(() => {
    budget.envelopes = [];
    budget.defaults = [];
  });

  it("badges only items whose envelope differs from the receipt's", () => {
    renderWithProviders(<LineItemsSection items={items} onChange={vi.fn()} />);

    // Most items fall to Groceries through the Food default, so it is the
    // receipt's envelope and those rows carry no badge.
    expect(within(row("Apples")).queryByText("Groceries")).toBeNull();
    expect(within(row("Bananas")).queryByText("Groceries")).toBeNull();
    expect(within(row("Gift bag")).getByText("Gifts")).toBeVisible();
    expect(within(row("Dish soap")).getByText("No envelope")).toBeVisible();
    // No item type chosen yet: the server may still fill it, so no badge.
    expect(within(row("Legacy line")).queryByText("No envelope")).toBeNull();
    expect(screen.getAllByText("No envelope")).toHaveLength(1);
  });

  it("follows the receipt's paid-from envelope instead of item-type defaults", () => {
    renderWithProviders(
      <LineItemsSection
        items={items}
        onChange={vi.fn()}
        paidFromEnvelopeId="env-gifts"
      />,
    );

    // Every automatic item is now paid from Gifts, including the one with no
    // default and the untyped one; only an override would differ.
    expect(screen.queryByText("No envelope")).not.toBeInTheDocument();
    expect(screen.queryByText("Groceries")).not.toBeInTheDocument();
    expect(within(row("Gift bag")).queryByText("Gifts")).toBeNull();
  });

  it("sets a row's envelope override from its menu", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithProviders(<LineItemsSection items={items} onChange={onChange} />);

    await user.click(
      screen.getByRole("button", { name: "More actions for Apples" }),
    );
    await user.click(screen.getByRole("menuitemradio", { name: "Gifts" }));

    expect(onChange).toHaveBeenCalledTimes(1);
    const update = onChange.mock.calls[0][0] as (
      current: ReceiptLineItem[],
    ) => ReceiptLineItem[];
    const next = update(items);
    expect(next[0]).toEqual({ ...items[0], envelopeId: "env-gifts" });
    expect(next.slice(1)).toEqual(items.slice(1));
  });

  describe.each([
    ["Steak, Bleach, Apples", ["Steak", "Bleach", "Apples"]],
    ["Apples, Bleach, Steak", ["Apples", "Bleach", "Steak"]],
  ])("an override in order %s", (_order, descriptions) => {
    it("does not move the receipt's envelope", () => {
      budget.envelopes = [
        { id: "env-groceries", name: "Groceries", isActive: true },
        { id: "env-cleaning", name: "Cleaning", isActive: true },
        { id: "env-party", name: "Party", isActive: true },
      ];
      budget.defaults = [
        {
          categoryId: "food",
          envelopeId: "env-groceries",
          updatedAt: "2026-01-01T00:00:00Z",
        },
        {
          categoryId: "house",
          envelopeId: "env-cleaning",
          updatedAt: "2026-01-01T00:00:00Z",
        },
      ];
      const byName: Record<string, ReceiptLineItem> = {
        Steak: typed("s", "Steak", [food], "env-party"),
        Bleach: typed("b", "Bleach", [house]),
        Apples: typed("a", "Apples", [food, fruit]),
      };
      renderWithProviders(
        <LineItemsSection
          items={descriptions.map((d) => byName[d])}
          onChange={vi.fn()}
        />,
      );

      expect(within(row("Steak")).getByText("Party")).toBeVisible();
      expect(within(row("Bleach")).getByText("Cleaning")).toBeVisible();
      expect(within(row("Apples")).queryByText("Groceries")).toBeNull();
    });
  });

  it("returns an overridden row to automatic", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithProviders(<LineItemsSection items={items} onChange={onChange} />);

    await user.click(
      screen.getByRole("button", { name: "More actions for Gift bag" }),
    );
    await user.click(screen.getByRole("menuitemradio", { name: "Automatic" }));

    const update = onChange.mock.calls[0][0] as (
      current: ReceiptLineItem[],
    ) => ReceiptLineItem[];
    expect(update(items)[3]).toEqual({ ...items[3], envelopeId: null });
  });
});

describe("item-code suggestion category", () => {
  type Suggestion = components["schemas"]["ReceiptItemSuggestionResponse"];
  const suggestion = (categoryId?: string | null): Suggestion => ({
    itemCode: "MILK-GAL",
    description: "Whole milk",
    category: "Food",
    subcategory: "Fruit",
    categoryId,
    unitPrice: 3.5,
    matchType: "location",
  });
  const suggest = (item: Suggestion) =>
    vi.mocked(useReceiptItemSuggestions).mockReturnValue(
      mockQueryResult({ data: [item], isFetching: false, isSuccess: true }),
    );
  const categoryPicker = () =>
    screen.getByRole("combobox", { name: /^Category/ });
  async function pickSuggestion(user: ReturnType<typeof userEvent.setup>) {
    await user.type(screen.getByPlaceholderText("e.g. MILK-GAL"), "MI");
    await user.click(await screen.findByRole("option", { name: /MILK-GAL/ }));
    expect(screen.getByPlaceholderText("Item description")).toHaveValue(
      "Whole milk",
    );
  }

  beforeEach(() => vi.clearAllMocks());

  it("fills an empty category with the suggested item type and saves its classification", async () => {
    suggest(suggestion("fruit"));
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithProviders(<LineItemsSection items={[]} onChange={onChange} />);
    await pickSuggestion(user);
    expect(categoryPicker()).toHaveAttribute("title", "Food / Fruit");
    await user.click(screen.getByRole("button", { name: /add item/i }));
    await waitFor(() => expect(onChange).toHaveBeenCalledTimes(1));
    expect(onChange.mock.calls[0][0][0]).toMatchObject({
      receiptItemCode: "MILK-GAL",
      category: "Food",
      subcategory: "Fruit",
      categoryChoice: {
        categoryId: "fruit",
        taxonomyRevisionId: "revision-1",
        path: [
          expect.objectContaining({ id: "food" }),
          expect.objectContaining({ id: "fruit" }),
        ],
      },
      categoryOperationId: expect.any(String),
    });
  });

  it("keeps a category already chosen", async () => {
    suggest(suggestion("fruit"));
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithProviders(<LineItemsSection items={[]} onChange={onChange} />);
    await user.click(categoryPicker());
    await chooseCategoryOption(user, "Household");
    await pickSuggestion(user);
    expect(categoryPicker()).toHaveAttribute("title", "Household");
    await user.click(screen.getByRole("button", { name: /add item/i }));
    await waitFor(() => expect(onChange).toHaveBeenCalledTimes(1));
    expect(onChange.mock.calls[0][0][0]).toMatchObject({
      category: "Household",
      categoryChoice: { categoryId: "house" },
    });
  });

  it("fills a category explicitly set to Unclassified", async () => {
    suggest(suggestion("fruit"));
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithProviders(<LineItemsSection items={[]} onChange={onChange} />);
    await user.click(categoryPicker());
    await chooseCategoryOption(user, "Unclassified");
    await pickSuggestion(user);
    expect(categoryPicker()).toHaveAttribute("title", "Food / Fruit");
    await user.click(screen.getByRole("button", { name: /add item/i }));
    await waitFor(() => expect(onChange).toHaveBeenCalledTimes(1));
    expect(onChange.mock.calls[0][0][0]).toMatchObject({
      category: "Food",
      subcategory: "Fruit",
      categoryChoice: { categoryId: "fruit" },
    });
  });

  it.each([
    ["has no item type", undefined],
    ["has a null item type", null],
    ["names an item type the current tree does not hold", "retired"],
  ])("leaves the category empty when the suggestion %s", async (_, id) => {
    suggest(suggestion(id));
    const user = userEvent.setup();
    renderWithProviders(<LineItemsSection items={[]} onChange={vi.fn()} />);
    await pickSuggestion(user);
    expect(categoryPicker()).toHaveAttribute("title", "Choose category");
  });
});
