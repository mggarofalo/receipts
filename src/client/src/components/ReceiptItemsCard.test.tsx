import { describe, it, expect, vi } from "vitest";
import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ReceiptItemsCard } from "./ReceiptItemsCard";
import { renderWithQueryClient } from "@/test/test-utils";
import { mockMutationResult, mockQueryResult } from "@/test/mock-hooks";
import { useReceipt } from "@/hooks/useReceipts";
vi.mock("@/hooks/useReceiptNormalizationPreview", () => ({
  useReceiptNormalizationPreview: () => ({
    isSuccess: true,
    isFetching: false,
    isDebouncing: false,
    isError: false,
    data: {
      sourceFingerprint: "fixture",
      candidates: [],
      total: 0,
      offset: 0,
      limit: 10,
      sourceAmbiguous: false,
      excluded: false,
    },
    refetch: vi.fn(),
  }),
}));

vi.mock("@/hooks/useReceipts", () => ({
  // Both dialogs hide the receipt field; model a disabled detail query without
  // inventing receipt data or replacing the picker/query behavior globally.
  useReceipt: vi.fn(() => mockQueryResult({ isLoading: false })),
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
  useCreateSubcategory: vi.fn(() => ({
    mutateAsync: vi.fn(),
  })),
}));

vi.mock("@/hooks/useItemTemplates", () => ({
  useItemTemplates: vi.fn(() => ({
    data: [],
    total: 0,
  })),
}));

vi.mock("@/hooks/useReceiptItems", () => ({
  useCreateReceiptItem: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
  useUpdateReceiptItem: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
  useDeleteReceiptItems: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
}));

// Empty unless a test configures envelopes, as when none have been created.
const budget = vi.hoisted(() => ({
  envelopes: [] as { id: string; name: string; isActive: boolean }[],
  occasions: [] as { id: string; name: string; isActive: boolean }[],
  defaults: [] as {
    categoryId: string;
    envelopeId: string;
    updatedAt: string;
  }[],
  setReceipt: vi.fn(),
  setItem: vi.fn(),
}));
vi.mock("@/hooks/useEnvelopes", () => ({
  useEnvelopes: () => ({
    ...mockQueryResult({
      data: { data: budget.envelopes },
      isSuccess: true,
      isLoading: false,
    }),
    envelopes: budget.envelopes,
  }),
  useOccasions: () => ({
    ...mockQueryResult({
      data: { data: budget.occasions },
      isSuccess: true,
      isLoading: false,
    }),
    occasions: budget.occasions,
  }),
  useItemTypeEnvelopeDefaults: () => ({
    ...mockQueryResult({
      data: { data: budget.defaults },
      isSuccess: true,
      isLoading: false,
    }),
    defaults: budget.defaults,
  }),
}));
vi.mock("@/hooks/useBudgetChoices", () => ({
  useSetReceiptBudgetChoices: () =>
    mockMutationResult({ mutate: budget.setReceipt }),
  useSetReceiptItemBudgetChoices: () =>
    mockMutationResult({ mutate: budget.setItem }),
}));

const mockItems = [
  {
    id: "item-1",
    receiptItemCode: "ITEM001",
    description: "Widget A",
    quantity: 2,
    unitPrice: 10.5,
    category: "Hardware",
    subcategory: "Fasteners",
  },
  {
    id: "item-2",
    receiptItemCode: "ITEM002",
    description: "Widget B",
    quantity: 1,
    unitPrice: 25.0,
    category: "Electronics",
    subcategory: "Components",
  },
];

describe("ReceiptItemsCard", () => {
  it("does not present a concurrent new-source identity as reviewed evidence for the retained edit draft", async () => {
    const original = {
      ...mockItems[0],
      normalizationReviewState: "verified" as const,
      normalizationRevision: "11111111-1111-4111-8111-111111111111",
      normalizedDescriptionName: "Original product",
    };
    const view = renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        location="Market"
        items={[original]}
        subtotal={21}
      />,
    );
    await userEvent.click(screen.getByRole("button", { name: /edit/i }));
    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByText(/Original product/)).toBeVisible();
    view.rerender(
      <ReceiptItemsCard
        receiptId="receipt-1"
        location="Market"
        items={[
          {
            ...original,
            description: "Concurrent different source",
            normalizedDescriptionName: "Foreign product",
            normalizationRevision: "22222222-2222-4222-8222-222222222222",
          },
        ]}
        subtotal={21}
      />,
    );
    expect(within(dialog).getByDisplayValue("Widget A")).toBeVisible();
    expect(
      within(dialog).queryByText(/Foreign product/),
    ).not.toBeInTheDocument();
    expect(
      within(dialog).queryByText(/Original product/),
    ).not.toBeInTheDocument();
    expect(
      within(dialog).getByRole("button", { name: "Choose product" }),
    ).toBeEnabled();
  });
  it.each(["success", "error"] as const)(
    "ignores a late %s from a previous edit dialog session",
    async (outcome) => {
      const { useUpdateReceiptItem } = await import("@/hooks/useReceiptItems");
      const mutate = vi.fn();
      vi.mocked(useUpdateReceiptItem).mockReturnValue(
        mockMutationResult({ mutate, isPending: false }),
      );
      renderWithQueryClient(
        <ReceiptItemsCard
          receiptId="receipt-1"
          items={mockItems}
          subtotal={46}
        />,
      );
      const user = userEvent.setup();
      await user.click(screen.getAllByRole("button", { name: /edit/i })[0]);
      await user.click(screen.getByRole("button", { name: "Update Item" }));
      await waitFor(() => expect(mutate).toHaveBeenCalledTimes(1));
      const callbacks = mutate.mock.calls[0][1];
      await user.click(screen.getByRole("button", { name: "Close" }));
      await user.click(
        (await screen.findAllByRole("button", { name: /edit/i }))[1],
      );
      expect(screen.getByDisplayValue("Widget B")).toBeVisible();
      await act(async () => {
        if (outcome === "success") callbacks.onSuccess();
        else callbacks.onError({ status: 409, detail: "Old save failed" });
      });
      expect(screen.getByDisplayValue("Widget B")).toBeVisible();
      expect(screen.queryByText("Old save failed")).not.toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Update Item" })).toBeVisible();
    },
  );
  it("sends an edit without the stored item's legacy labels", async () => {
    const { useUpdateReceiptItem } = await import("@/hooks/useReceiptItems");
    const mutate = vi.fn();
    vi.mocked(useUpdateReceiptItem).mockReturnValue(
      mockMutationResult({ mutate, isPending: false }),
    );
    renderWithQueryClient(
      <ReceiptItemsCard receiptId="receipt-1" items={mockItems} subtotal={46} />,
    );
    const user = userEvent.setup();
    await user.click(screen.getAllByRole("button", { name: /edit/i })[0]);
    await user.click(screen.getByRole("button", { name: "Update Item" }));
    await waitFor(() => expect(mutate).toHaveBeenCalledTimes(1));
    const { body } = mutate.mock.calls[0][0];
    expect(body).toMatchObject({ id: "item-1", description: "Widget A" });
    expect(body).not.toHaveProperty("category");
    expect(body).not.toHaveProperty("subcategory");
  });
  it("shows subcent unit price while the line total and subtotal stay at cents", () => {
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={[{ ...mockItems[0], unitPrice: 3.459 }]}
        subtotal={6.92}
      />,
    );
    expect(screen.getByText("$3.459")).toBeInTheDocument();
    expect(screen.getAllByText("$6.92")).toHaveLength(2);
  });

  it("rounds the line preview but preserves the authoritative server subtotal", () => {
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={[{ ...mockItems[0], quantity: 0.5, unitPrice: 2.01 }]}
        subtotal={1.02}
      />,
    );
    expect(screen.getByText("$1.01")).toBeInTheDocument();
    expect(screen.getByText("$1.02")).toBeInTheDocument();
  });

  it("renders empty state when there are no items", () => {
    renderWithQueryClient(
      <ReceiptItemsCard receiptId="receipt-1" items={[]} subtotal={0} />,
    );
    expect(screen.getByText("No items for this receipt.")).toBeInTheDocument();
    expect(screen.getByText("Items (0)")).toBeInTheDocument();
  });

  it("renders item rows with data", () => {
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={mockItems}
        subtotal={46}
      />,
    );
    expect(screen.getByText("Items (2)")).toBeInTheDocument();
    expect(screen.getByText("ITEM001")).toBeInTheDocument();
    expect(screen.getByText("Widget A")).toBeInTheDocument();
    expect(screen.getByText("ITEM002")).toBeInTheDocument();
    expect(screen.getByText("Widget B")).toBeInTheDocument();
  });

  it("shows an item without a current classification as unresolved, never its legacy labels", () => {
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={mockItems}
        subtotal={46}
      />,
    );
    expect(screen.getAllByText("Unresolved category")).toHaveLength(2);
    expect(screen.queryByText(/Legacy:/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Hardware/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Electronics/)).not.toBeInTheDocument();
  });

  it("renders table headers when items exist", () => {
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={mockItems}
        subtotal={46}
      />,
    );
    expect(screen.getByText("Code")).toBeInTheDocument();
    expect(screen.getByText("Description")).toBeInTheDocument();
    expect(screen.getByText("Qty")).toBeInTheDocument();
    expect(screen.getByText("Unit Price")).toBeInTheDocument();
    expect(screen.getByText("Total")).toBeInTheDocument();
    expect(screen.getByText("Category")).toBeInTheDocument();
    expect(
      screen.queryByRole("columnheader", { name: "Subcategory" }),
    ).not.toBeInTheDocument();
    expect(screen.getByText("Actions")).toBeInTheDocument();
  });

  it("shows the full recorded path and honest non-path states in one column", () => {
    const recorded = {
      assignmentId: "a1",
      categoryId: "leaf",
      taxonomyRevisionId: "r1",
      recordedPath: [
        { id: "root", name: "Old Food", archived: false },
        { id: "middle", name: "Dairy", parentId: "root", archived: false },
        { id: "leaf", name: "Milk", parentId: "middle", archived: false },
      ],
    };
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={[
          { ...mockItems[0], currentClassification: recorded },
          {
            ...mockItems[1],
            currentClassification: {
              ...recorded,
              categoryId: null,
              recordedPath: [],
            },
          },
          {
            ...mockItems[0],
            id: "item-3",
            description: "Unknown",
            currentClassification: { ...recorded, recordedPath: [] },
          },
          {
            ...mockItems[0],
            id: "item-4",
            description: "Archived",
            currentClassification: {
              ...recorded,
              recordedPath: [{ ...recorded.recordedPath[0], archived: true }],
            },
          },
        ]}
        subtotal={46}
      />,
    );
    expect(screen.getByText("Old Food / Dairy / Milk")).toBeInTheDocument();
    expect(screen.getByText("Unclassified")).toBeInTheDocument();
    expect(screen.getByText("Unresolved category path")).toBeInTheDocument();
    expect(
      screen.getByText("Old Food (archived in recorded revision)"),
    ).toBeInTheDocument();
    expect(screen.queryByText(/Legacy:/)).not.toBeInTheDocument();
  });

  it("renders the subtotal in the footer", () => {
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={mockItems}
        subtotal={46}
      />,
    );
    expect(screen.getByText("Subtotal")).toBeInTheDocument();
    expect(screen.getByText("$46.00")).toBeInTheDocument();
  });

  it("renders Add Item button", () => {
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={mockItems}
        subtotal={46}
      />,
    );
    expect(
      screen.getByRole("button", { name: /add item/i }),
    ).toBeInTheDocument();
  });

  it("renders edit buttons for each item row", () => {
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={mockItems}
        subtotal={46}
      />,
    );
    const editButtons = screen.getAllByRole("button", { name: /edit/i });
    expect(editButtons).toHaveLength(2);
  });

  it("toggles individual row selection checkbox", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={mockItems}
        subtotal={46}
      />,
    );
    const widgetACheckbox = screen.getByLabelText("Select Widget A item");
    expect(widgetACheckbox).not.toBeChecked();
    await user.click(widgetACheckbox);
    expect(widgetACheckbox).toBeChecked();
    await user.click(widgetACheckbox);
    expect(widgetACheckbox).not.toBeChecked();
  });

  it("select-all checkbox selects and deselects all items", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={mockItems}
        subtotal={46}
      />,
    );
    const selectAll = screen.getByLabelText("Select all items");
    expect(selectAll).not.toBeChecked();
    await user.click(selectAll);
    expect(selectAll).toBeChecked();
    expect(screen.getByLabelText("Select Widget A item")).toBeChecked();
    expect(screen.getByLabelText("Select Widget B item")).toBeChecked();
    await user.click(selectAll);
    expect(selectAll).not.toBeChecked();
    expect(screen.getByLabelText("Select Widget A item")).not.toBeChecked();
    expect(screen.getByLabelText("Select Widget B item")).not.toBeChecked();
  });

  it("shows Delete button with count when items are selected", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={mockItems}
        subtotal={46}
      />,
    );
    expect(
      screen.queryByRole("button", { name: /delete/i }),
    ).not.toBeInTheDocument();
    await user.click(screen.getByLabelText("Select Widget A item"));
    expect(
      screen.getByRole("button", { name: /delete \(1\)/i }),
    ).toBeInTheDocument();
  });

  it("opens delete confirmation dialog and calls deleteReceiptItems.mutate on confirm", async () => {
    const { useDeleteReceiptItems } = await import("@/hooks/useReceiptItems");
    const mockDeleteMutate = vi.fn();
    vi.mocked(useDeleteReceiptItems).mockReturnValue(
      mockMutationResult({
        mutate: mockDeleteMutate,
        isPending: false,
      }),
    );

    const user = userEvent.setup();
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={mockItems}
        subtotal={46}
      />,
    );
    await user.click(screen.getByLabelText("Select Widget A item"));
    await user.click(screen.getByRole("button", { name: /delete \(1\)/i }));
    expect(screen.getByText("Delete Items")).toBeInTheDocument();
    expect(
      screen.getByText(/are you sure you want to delete 1 item/i),
    ).toBeInTheDocument();
    const confirmDelete = screen.getByRole("button", { name: "Delete" });
    await user.click(confirmDelete);
    expect(mockDeleteMutate).toHaveBeenCalledWith(["item-1"]);
  });

  it("opens create dialog when Add Item is clicked", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={mockItems}
        subtotal={46}
      />,
    );
    await user.click(screen.getByRole("button", { name: /add item/i }));
    expect(
      screen.getByText("Add Item", { selector: "[id]" }),
    ).toBeInTheDocument();
    expect(useReceipt).toHaveBeenLastCalledWith("receipt-1", {
      enabled: false,
    });
  });

  it("opens edit dialog when Edit button is clicked", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={mockItems}
        subtotal={46}
      />,
    );
    const editButtons = screen.getAllByRole("button", { name: /edit/i });
    await user.click(editButtons[0]);
    expect(screen.getByText("Edit Item")).toBeInTheDocument();
    expect(useReceipt).toHaveBeenLastCalledWith("receipt-1", {
      enabled: false,
    });
  });

  it("renders create form with submit button in create dialog", async () => {
    const { useCreateReceiptItem } = await import("@/hooks/useReceiptItems");
    const mockCreateMutate = vi.fn();
    vi.mocked(useCreateReceiptItem).mockReturnValue(
      mockMutationResult({
        mutate: mockCreateMutate,
        isPending: false,
      }),
    );

    const user = userEvent.setup();
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={mockItems}
        subtotal={46}
      />,
    );
    await user.click(screen.getByRole("button", { name: /add item/i }));
    const submitButton = screen.getByRole("button", {
      name: "Create Item",
    });
    expect(submitButton).toBeInTheDocument();
  });

  it("renders edit form with update button in edit dialog", async () => {
    const { useUpdateReceiptItem } = await import("@/hooks/useReceiptItems");
    const mockUpdateMutate = vi.fn();
    vi.mocked(useUpdateReceiptItem).mockReturnValue(
      mockMutationResult({
        mutate: mockUpdateMutate,
        isPending: false,
      }),
    );

    const user = userEvent.setup();
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={mockItems}
        subtotal={46}
      />,
    );
    const editButtons = screen.getAllByRole("button", { name: /edit/i });
    await user.click(editButtons[0]);
    const submitButton = screen.getByRole("button", {
      name: "Update Item",
    });
    expect(submitButton).toBeInTheDocument();
  });

  it("renders 'normalized as' label when normalizedDescriptionName is present and differs from description", () => {
    const itemsWithNormalized = [
      {
        id: "item-norm-1",
        receiptItemCode: "ITEM-NORM-1",
        description: "Red Seedless Grapes 2LB",
        quantity: 1,
        unitPrice: 5.99,
        category: "Groceries",
        subcategory: "Produce",
        normalizedDescriptionName: "Grapes",
        normalizationReviewState: "verified" as const,
      },
    ];
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={itemsWithNormalized}
        subtotal={5.99}
      />,
    );
    expect(screen.getByText("Red Seedless Grapes 2LB")).toBeInTheDocument();
    expect(screen.getByText(/normalized as Grapes/i)).toBeInTheDocument();
  });

  it("does not render 'normalized as' label when normalizedDescriptionName is null", () => {
    const itemsWithoutNormalized = [
      {
        id: "item-plain-1",
        receiptItemCode: "ITEM-PLAIN-1",
        description: "Eggs",
        quantity: 1,
        unitPrice: 3.49,
        category: "Groceries",
        subcategory: "Dairy",
        normalizedDescriptionName: null,
      },
    ];
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={itemsWithoutNormalized}
        subtotal={3.49}
      />,
    );
    expect(screen.getByText("Eggs")).toBeInTheDocument();
    expect(screen.queryByText(/normalized as/i)).not.toBeInTheDocument();
  });

  it("does not render 'normalized as' label when normalizedDescriptionName equals description", () => {
    const itemsWithSameNormalized = [
      {
        id: "item-same-1",
        receiptItemCode: "ITEM-SAME-1",
        description: "Milk",
        quantity: 1,
        unitPrice: 4.29,
        category: "Groceries",
        subcategory: "Dairy",
        normalizedDescriptionName: "Milk",
      },
    ];
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={itemsWithSameNormalized}
        subtotal={4.29}
      />,
    );
    expect(screen.getByText("Milk")).toBeInTheDocument();
    expect(screen.queryByText(/normalized as/i)).not.toBeInTheDocument();
  });

  it("wraps long descriptions so the table cannot force page horizontal scroll (WCAG 1.4.10)", () => {
    const longDescription = "A".repeat(200);
    const itemsWithLongDescription = [
      {
        id: "item-long-1",
        receiptItemCode: "ITEM-LONG-1",
        description: longDescription,
        quantity: 1,
        unitPrice: 9.99,
        category: "Groceries",
        subcategory: "Produce",
      },
    ];
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={itemsWithLongDescription}
        subtotal={9.99}
      />,
    );
    const cell = screen.getByText(longDescription).closest("td");
    expect(cell).not.toBeNull();
    expect(cell).toHaveClass("whitespace-normal");
    expect(cell).toHaveClass("break-words");
    expect(cell).toHaveClass("max-w-[32ch]");
  });

  it("cancels delete dialog without deleting", async () => {
    const { useDeleteReceiptItems } = await import("@/hooks/useReceiptItems");
    const mockDeleteMutate = vi.fn();
    vi.mocked(useDeleteReceiptItems).mockReturnValue(
      mockMutationResult({
        mutate: mockDeleteMutate,
        isPending: false,
      }),
    );

    const user = userEvent.setup();
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={mockItems}
        subtotal={46}
      />,
    );
    await user.click(screen.getByLabelText("Select Widget A item"));
    await user.click(screen.getByRole("button", { name: /delete \(1\)/i }));
    await user.click(screen.getByRole("button", { name: "Cancel" }));
    expect(mockDeleteMutate).not.toHaveBeenCalled();
  });
});

it("labels pending associations without claiming they are normalized", () => {
  renderWithQueryClient(
    <ReceiptItemsCard
      receiptId="receipt-1"
      subtotal={21}
      items={[
        {
          ...mockItems[0],
          normalizedDescriptionName: "Proposed widget",
          normalizationReviewState: "needsReview",
        },
      ]}
    />,
  );
  expect(screen.queryByText("Needs review")).not.toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "Review product" }),
  ).not.toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Edit" })).toBeEnabled();
  expect(
    screen.getByText(/Unreviewed association: Proposed widget/),
  ).toBeVisible();
  expect(screen.queryByText(/normalized as/i)).not.toBeInTheDocument();
});

describe("budget choices", () => {
  beforeAll(() => {
    Element.prototype.hasPointerCapture ??= () => false;
    Element.prototype.setPointerCapture ??= () => {};
    Element.prototype.releasePointerCapture ??= () => {};
  });
  beforeEach(() => {
    budget.setReceipt.mockClear();
    budget.setItem.mockClear();
    budget.envelopes = [
      { id: "env-groceries", name: "Groceries", isActive: true },
      { id: "env-gifts", name: "Gifts", isActive: true },
    ];
    budget.occasions = [
      { id: "occ-birthday", name: "Birthday", isActive: true },
      { id: "occ-trip", name: "Beach trip", isActive: true },
    ];
  });
  afterEach(() => {
    budget.envelopes = [];
    budget.occasions = [];
    budget.defaults = [];
  });

  const items = [
    {
      ...mockItems[0],
      envelopeId: "env-groceries",
      envelopeSource: "default" as const,
      occasionId: null,
    },
    {
      ...mockItems[1],
      envelopeId: "env-gifts",
      envelopeSource: "item" as const,
      occasionId: "occ-trip",
    },
  ];

  it("saves the receipt's paid-from envelope immediately, keeping its occasion", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={items}
        subtotal={46}
        paidFromEnvelopeId={null}
        occasionId="occ-birthday"
      />,
    );

    await user.click(screen.getByRole("combobox", { name: "Paid from" }));
    await user.click(screen.getByRole("option", { name: "Gifts" }));

    expect(budget.setReceipt).toHaveBeenCalledWith({
      receiptId: "receipt-1",
      paidFromEnvelopeId: "env-gifts",
      occasionId: "occ-birthday",
    });
    expect(budget.setItem).not.toHaveBeenCalled();
  });

  it("saves the receipt's occasion immediately, keeping its paid-from envelope", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={items}
        subtotal={46}
        paidFromEnvelopeId="env-groceries"
        occasionId="occ-birthday"
      />,
    );

    await user.click(screen.getByRole("button", { name: "Remove occasion" }));

    expect(budget.setReceipt).toHaveBeenCalledWith({
      receiptId: "receipt-1",
      paidFromEnvelopeId: "env-groceries",
      occasionId: null,
    });
  });

  it("badges only the item paid from a different envelope", () => {
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={items}
        subtotal={46}
        paidFromEnvelopeId="env-groceries"
      />,
    );
    const row = (description: string) =>
      screen.getByText(description).closest("tr") as HTMLElement;
    expect(within(row("Widget A")).queryByText("Groceries")).toBeNull();
    expect(within(row("Widget B")).getByText("Gifts")).toBeVisible();
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
      // Item types Food and Household default to Groceries and Cleaning.
      budget.defaults = [
        {
          categoryId: "food",
          envelopeId: "env-groceries",
          updatedAt: "2026-01-01T00:00:00Z",
        },
        {
          categoryId: "household",
          envelopeId: "env-cleaning",
          updatedAt: "2026-01-01T00:00:00Z",
        },
      ];
      const food = { id: "food", name: "Food", archived: false };
      const household = { id: "household", name: "Household", archived: false };
      const line = (
        id: string,
        description: string,
        type: typeof food,
        envelopeId: string,
        envelopeSource: "default" | "item",
      ) => ({
        ...mockItems[0],
        id,
        description,
        currentClassification: {
          assignmentId: `assignment-${id}`,
          categoryId: type.id,
          taxonomyRevisionId: "revision-1",
          recordedPath: [type],
        },
        envelopeId,
        envelopeSource,
        occasionId: null,
      });
      // Steak is a Food item (automatically Groceries) overridden to Party.
      const byName = {
        Steak: line("s", "Steak", food, "env-party", "item"),
        Bleach: line("b", "Bleach", household, "env-cleaning", "default"),
        Apples: line("a", "Apples", food, "env-groceries", "default"),
      } as Record<string, ReturnType<typeof line>>;
      renderWithQueryClient(
        <ReceiptItemsCard
          receiptId="receipt-1"
          items={descriptions.map((d) => byName[d])}
          subtotal={63}
          paidFromEnvelopeId={null}
        />,
      );
      const row = (description: string) =>
        screen.getByText(description).closest("tr") as HTMLElement;

      expect(within(row("Steak")).getByText("Party")).toBeVisible();
      expect(within(row("Bleach")).getByText("Cleaning")).toBeVisible();
      expect(within(row("Apples")).queryByText("Groceries")).toBeNull();
    });
  });

  it("overrides one item's envelope from its row menu, keeping the item's occasion", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={items}
        subtotal={46}
        paidFromEnvelopeId={null}
      />,
    );

    await user.click(
      screen.getByRole("button", { name: "More actions for Widget B" }),
    );
    // Widget B's stored override is the checked choice.
    expect(
      screen.getByRole("menuitemradio", { name: "Gifts" }),
    ).toHaveAttribute("aria-checked", "true");
    await user.click(screen.getByRole("menuitemradio", { name: "Groceries" }));

    expect(budget.setItem).toHaveBeenCalledWith({
      itemId: "item-2",
      envelopeId: "env-groceries",
      occasionId: "occ-trip",
    });
    expect(budget.setReceipt).not.toHaveBeenCalled();
  });

  it("shows an item-type envelope as automatic in the row menu", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={items}
        subtotal={46}
        paidFromEnvelopeId={null}
      />,
    );

    await user.click(
      screen.getByRole("button", { name: "More actions for Widget A" }),
    );
    expect(
      screen.getByRole("menuitemradio", { name: "Automatic" }),
    ).toHaveAttribute("aria-checked", "true");
    await user.click(screen.getByRole("menuitemradio", { name: "Gifts" }));
    expect(budget.setItem).toHaveBeenCalledWith({
      itemId: "item-1",
      envelopeId: "env-gifts",
      occasionId: null,
    });
  });
});
