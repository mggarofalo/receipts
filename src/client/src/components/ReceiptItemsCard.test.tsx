import { describe, it, expect, vi } from "vitest";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ReceiptItemsCard } from "./ReceiptItemsCard";
import { renderWithQueryClient } from "@/test/test-utils";
import { mockMutationResult } from "@/test/mock-hooks";

vi.mock("@/hooks/useReceipts", () => ({
  useAllReceipts: vi.fn(() => ({
    data: [],
    isLoading: false,
  })),
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
  it("renders empty state when there are no items", () => {
    renderWithQueryClient(
      <ReceiptItemsCard
        receiptId="receipt-1"
        items={[]}
        subtotal={0}
      />,
    );
    expect(
      screen.getByText("No items for this receipt."),
    ).toBeInTheDocument();
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
    expect(screen.getByText("Hardware")).toBeInTheDocument();
    expect(screen.getByText("Fasteners")).toBeInTheDocument();
    expect(screen.getByText("ITEM002")).toBeInTheDocument();
    expect(screen.getByText("Widget B")).toBeInTheDocument();
    expect(screen.getByText("Electronics")).toBeInTheDocument();
    expect(screen.getByText("Components")).toBeInTheDocument();
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
    expect(screen.getByText("Subcategory")).toBeInTheDocument();
    expect(screen.getByText("Actions")).toBeInTheDocument();
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
    const { useDeleteReceiptItems } = await import(
      "@/hooks/useReceiptItems"
    );
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
    await user.click(
      screen.getByRole("button", { name: /delete \(1\)/i }),
    );
    expect(screen.getByText("Delete Items")).toBeInTheDocument();
    expect(
      screen.getByText(
        /are you sure you want to delete 1 item/i,
      ),
    ).toBeInTheDocument();
    const confirmDelete = screen.getByRole("button", { name: "Delete" });
    await user.click(confirmDelete);
    expect(mockDeleteMutate).toHaveBeenCalledWith(
      ["item-1"],
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
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
    await user.click(
      screen.getByRole("button", { name: /add item/i }),
    );
    expect(
      screen.getByText("Add Item", { selector: "[id]" }),
    ).toBeInTheDocument();
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
  });

  it("renders create form with submit button in create dialog", async () => {
    const { useCreateReceiptItem } = await import(
      "@/hooks/useReceiptItems"
    );
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
    await user.click(
      screen.getByRole("button", { name: /add item/i }),
    );
    const submitButton = screen.getByRole("button", {
      name: "Create Item",
    });
    expect(submitButton).toBeInTheDocument();
  });

  it("renders edit form with update button in edit dialog", async () => {
    const { useUpdateReceiptItem } = await import(
      "@/hooks/useReceiptItems"
    );
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
    const { useDeleteReceiptItems } = await import(
      "@/hooks/useReceiptItems"
    );
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
    await user.click(
      screen.getByRole("button", { name: /delete \(1\)/i }),
    );
    await user.click(screen.getByRole("button", { name: "Cancel" }));
    expect(mockDeleteMutate).not.toHaveBeenCalled();
  });
});
