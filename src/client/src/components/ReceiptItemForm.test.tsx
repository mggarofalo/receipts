import { useCategoryTaxonomy } from "@/hooks/useCategoryTaxonomy";
vi.mock("@/hooks/usePermission", () => ({
  usePermission: () => ({ isAdmin: () => false }),
}));
vi.mock("@/hooks/useCategoryTaxonomy", () => ({
  useCategoryTaxonomy: vi.fn(() => ({
    data: {
      id: "revision-1",
      nodes: [
        { id: "root", name: "Food", archived: false },
        { id: "produce", parentId: "root", name: "Produce", archived: false },
        { id: "fruit", parentId: "produce", name: "Fruit", archived: false },
      ],
    },
    isLoading: false,
    isError: false,
    isFetching: false,
    refetch: vi.fn(),
  })),
}));
import "@/test/setup-combobox-polyfills";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ReceiptItemForm } from "./ReceiptItemForm";
import { useReceipt } from "@/hooks/useReceipts";
import { useReceiptItemSuggestions } from "@/hooks/useReceiptItemSuggestions";
import type { components } from "@/generated/api";
import { Combobox } from "@/components/ui/combobox";
import type { ComponentProps } from "react";
import type { ReceiptPicker } from "./ReceiptPicker";
import { chooseCategoryOption } from "@/test/category-picker";

const templateFixture = vi.hoisted(() => ({ unitPrice: 3.99 }));

// These field-validation unit tests do not choose a product; the real preview
// and choice lifecycle are exercised by ReceiptNormalizationField.test.tsx.
vi.mock("./ReceiptNormalizationField", () => ({
  ReceiptNormalizationField: () => null,
}));

vi.mock("@/hooks/useFormShortcuts", () => ({
  useFormShortcuts: vi.fn(),
}));

vi.mock("@/hooks/useReceipts", () => ({
  useReceipt: vi.fn((id: string | null) => ({
    data: id
      ? { id, location: "Walmart", date: "2024-01-15", taxAmount: 0 }
      : undefined,
    isLoading: false,
    isError: false,
    isFetching: false,
    refetch: vi.fn(),
  })),
}));

// These form-unit cases isolate field validation/submission; the dedicated picker
// has actual QueryClient/MSW integration coverage in ReceiptPicker.errors.test.tsx.
vi.mock("./ReceiptPicker", () => ({
  ReceiptPicker: ({
    selectedReceipt,
    ...props
  }: ComponentProps<typeof ReceiptPicker>) => (
    <Combobox
      {...props}
      options={[
        {
          value: selectedReceipt?.id ?? "r-1",
          label: "Walmart",
          sublabel: "Walmart — 2024-01-15",
        },
      ]}
      placeholder="Select a receipt..."
    />
  ),
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
        defaultUnitPrice: templateFixture.unitPrice,
        defaultItemCode: "MLK-001",
      },
    ],
    total: 1,
  })),
}));

vi.mock("@/lib/combobox-options", () => ({
  receiptToOption: vi.fn(
    (r: { id: string; location: string; date: string }) => ({
      value: r.id,
      label: r.location,
      sublabel: `${r.location} — ${r.date}`,
    }),
  ),
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
  it("previews a fractional midpoint line using the server rounding policy", () => {
    render(
      <ReceiptItemForm
        mode="edit"
        onSubmit={vi.fn()}
        onCancel={vi.fn()}
        defaultValues={{ quantity: 0.5, unitPrice: 2.01 }}
      />,
    );
    expect(screen.getByText("Total: $1.01")).toBeInTheDocument();
  });

  const defaultProps = {
    mode: "create" as const,
    onSubmit: vi.fn(),
    onCancel: vi.fn(),
  };

  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    templateFixture.unitPrice = 3.99;
  });

  it("renders in create mode with correct submit button text and all field labels", () => {
    render(<ReceiptItemForm {...defaultProps} />);

    expect(screen.getByText(/^Receipt/)).toBeInTheDocument();
    expect(screen.getByText(/^Item Code/)).toBeInTheDocument();
    expect(screen.getByText(/^Description/)).toBeInTheDocument();
    expect(screen.getByLabelText(/^Quantity/)).toBeInTheDocument();
    expect(screen.getByLabelText(/^Unit Price/)).toBeInTheDocument();
    // The picker is no longer a form field, but it is still labelled "Category".
    expect(
      screen.getByLabelText("Category", { selector: "button" }),
    ).toHaveAttribute("role", "combobox");
    expect(screen.queryByText(/^Subcategory/)).not.toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: /create item/i }),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /cancel/i })).toBeInTheDocument();
  });

  it("disables the receipt query when the receipt field is hidden", () => {
    render(<ReceiptItemForm {...defaultProps} hideReceiptField />);

    expect(useReceipt).toHaveBeenCalledWith(null, { enabled: false });
    expect(screen.queryByText(/^Receipt/)).not.toBeInTheDocument();
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
        }}
      />,
    );

    // Item Code is now an Input; check its value
    expect(screen.getByPlaceholderText("Enter item code...")).toHaveValue(
      "ITM-001",
    );
    expect(
      screen.getByRole("button", { name: /update item/i }),
    ).toBeInTheDocument();
  });

  it("shows validation errors when required fields are empty", async () => {
    const user = userEvent.setup();
    render(<ReceiptItemForm {...defaultProps} />);

    await user.click(screen.getByRole("button", { name: /create item/i }));

    await waitFor(() => {
      expect(screen.getByText("Receipt is required")).toBeInTheDocument();
      expect(screen.getByText("Item code is required")).toBeInTheDocument();
      expect(screen.getByText("Description is required")).toBeInTheDocument();
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
          unitPrice: 2.5,
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
        }),
      );
    });
    // Legacy labels are retired: a create carries no category text at all.
    const submitted = defaultProps.onSubmit.mock.calls[0][0];
    expect(submitted).not.toHaveProperty("category");
    expect(submitted).not.toHaveProperty("subcategory");
    expect(submitted.classification).toBeUndefined();
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
    const templates = screen.getByText("Item Templates").closest("[cmdk-group]")!;
    expect(templates).not.toHaveTextContent("Groceries");
    expect(templates).not.toHaveTextContent("Dairy");
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

  it("retains the exact template unit price through picker prefill, focus, blur and submission", async () => {
    const user = userEvent.setup();
    templateFixture.unitPrice = 3.459;
    render(
      <ReceiptItemForm
        {...defaultProps}
        defaultValues={{
          receiptId: "r-1",
          quantity: 1,
          unitPrice: 0,
        }}
      />,
    );
    await pickMilkTemplate(user);
    await user.click(screen.getByLabelText(/^Unit Price/));
    await user.tab();
    await user.click(screen.getByRole("button", { name: /create item/i }));
    await waitFor(() =>
      expect(defaultProps.onSubmit).toHaveBeenCalledWith(
        expect.objectContaining({ unitPrice: 3.459, itemTemplateId: "tmpl-1" }),
      ),
    );
  });

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
    const searchInput = screen.getByPlaceholderText("Search category paths…");
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

it("normalizes persisted item text with the same draft rules", async () => {
  const onSubmit = vi.fn();
  const user = userEvent.setup();
  render(
    <ReceiptItemForm
      mode="edit"
      onSubmit={onSubmit}
      onCancel={vi.fn()}
      defaultValues={{
        receiptId: "r-1",
        receiptItemCode: " CODE ",
        description: " Milk ",
        quantity: 1,
        unitPrice: 0,
      }}
    />,
  );
  await user.click(screen.getByRole("button", { name: /update item/i }));
  await waitFor(() => expect(onSubmit).toHaveBeenCalled());
  expect(onSubmit.mock.calls[0][0]).toMatchObject({
    receiptItemCode: "CODE",
    description: "Milk",
    quantity: 1,
    unitPrice: 0,
  });
  expect(onSubmit.mock.calls[0][0].classification).toBeUndefined();
  // The item type is unchanged and the stored labels are write-once history,
  // so the edit carries no labels.
  expect(onSubmit.mock.calls[0][0]).not.toHaveProperty("category");
  expect(onSubmit.mock.calls[0][0]).not.toHaveProperty("subcategory");
});

it.each(["description", "receiptItemCode"])(
  "rejects whitespace-only persisted-item %s",
  async (field) => {
    const onSubmit = vi.fn();
    const user = userEvent.setup();
    render(
      <ReceiptItemForm
        mode="edit"
        onSubmit={onSubmit}
        onCancel={vi.fn()}
        defaultValues={{
          receiptId: "r-1",
          receiptItemCode: "CODE",
          description: "Milk",
          quantity: 1,
          unitPrice: 0,
          [field]: "   ",
        }}
      />,
    );
    await user.click(screen.getByRole("button", { name: /update item/i }));
    await waitFor(() =>
      expect(screen.getByText(/is required/i)).toBeInTheDocument(),
    );
    expect(onSubmit).not.toHaveBeenCalled();
  },
);

const classificationDefaults = {
  receiptId: "r-1",
  receiptItemCode: "A",
  description: "Apple",
  quantity: 1,
  unitPrice: 2,
};
const recorded = {
  assignmentId: "assignment-1",
  categoryId: "fruit",
  taxonomyRevisionId: "revision-old",
  recordedPath: [
    { id: "root", name: "Old Food", archived: false },
    { id: "produce", name: "Old Produce", archived: false },
    { id: "fruit", name: "Old Fruit", archived: true },
  ],
};

describe("category hint", () => {
  it("asks for a category when editing an item with no category recorded", () => {
    render(
      <ReceiptItemForm
        mode="edit"
        defaultValues={classificationDefaults}
        onSubmit={vi.fn()}
        onCancel={vi.fn()}
      />,
    );
    expect(
      screen.getByText(
        "No category recorded yet. Choose one to classify this item.",
      ),
    ).toBeInTheDocument();
    // The legacy labels are never shown as if they were a category.
    expect(screen.queryByText(/Saved category/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Food \/ Produce/)).not.toBeInTheDocument();
  });

  it("does not show the hint when the item has a recorded category", () => {
    render(
      <ReceiptItemForm
        mode="edit"
        defaultValues={classificationDefaults}
        currentClassification={recorded}
        onSubmit={vi.fn()}
        onCancel={vi.fn()}
      />,
    );
    expect(
      screen.queryByText(/No category recorded yet/),
    ).not.toBeInTheDocument();
  });

  it("does not show the hint while creating an item", () => {
    render(
      <ReceiptItemForm mode="create" onSubmit={vi.fn()} onCancel={vi.fn()} />,
    );
    expect(
      screen.queryByText(/No category recorded yet/),
    ).not.toBeInTheDocument();
  });

  it("hides the hint once a category is chosen", async () => {
    const user = userEvent.setup();
    render(
      <ReceiptItemForm
        mode="edit"
        defaultValues={classificationDefaults}
        onSubmit={vi.fn()}
        onCancel={vi.fn()}
      />,
    );
    await user.click(screen.getByRole("combobox", { name: /Category/ }));
    await chooseCategoryOption(user, "Food / Produce");
    expect(
      screen.queryByText(/No category recorded yet/),
    ).not.toBeInTheDocument();
  });
});

describe("classification submissions", () => {
  const sameRevisionRecorded = {
    assignmentId: "assignment-1",
    categoryId: "produce",
    taxonomyRevisionId: "revision-1",
    recordedPath: [
      { id: "root", name: "Food", archived: false },
      { id: "produce", name: "Produce", parentId: "root", archived: false },
    ],
  };
  it("sends neither a classification nor labels when the same category is re-picked", async () => {
    const user = userEvent.setup();
    const submit = vi.fn();
    render(
      <ReceiptItemForm
        mode="edit"
        defaultValues={classificationDefaults}
        currentClassification={sameRevisionRecorded}
        onSubmit={submit}
        onCancel={vi.fn()}
      />,
    );
    await user.click(screen.getByRole("combobox", { name: /Category/ }));
    await chooseCategoryOption(user, "Food / Produce");
    await user.click(screen.getByRole("button", { name: "Update Item" }));
    await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));
    const submitted = submit.mock.calls[0][0];
    expect(submitted).not.toHaveProperty("category");
    expect(submitted).not.toHaveProperty("subcategory");
    expect(submitted.classification).toBeUndefined();
  });

  it("sends only a classification, never labels, when a different category is picked", async () => {
    const user = userEvent.setup();
    const submit = vi.fn();
    render(
      <ReceiptItemForm
        mode="edit"
        defaultValues={classificationDefaults}
        currentClassification={sameRevisionRecorded}
        onSubmit={submit}
        onCancel={vi.fn()}
      />,
    );
    await user.click(screen.getByRole("combobox", { name: /Category/ }));
    await chooseCategoryOption(user, "Food / Produce / Fruit");
    await user.click(screen.getByRole("button", { name: "Update Item" }));
    await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));
    expect(submit.mock.calls[0][0]).toMatchObject({
      classification: {
        categoryId: "fruit",
        taxonomyRevisionId: "revision-1",
        expectedAssignmentId: "assignment-1",
      },
    });
    expect(submit.mock.calls[0][0]).not.toHaveProperty("category");
    expect(submit.mock.calls[0][0]).not.toHaveProperty("subcategory");
  });

  it("preserves a deep recorded path and omits untouched assignment mutation even when taxonomy is unavailable", async () => {
    const user = userEvent.setup();
    const submit = vi.fn();
    vi.mocked(useCategoryTaxonomy).mockReturnValueOnce({
      ...vi.mocked(useCategoryTaxonomy)(),
      data: undefined,
      isError: true,
    } as ReturnType<typeof useCategoryTaxonomy>);
    render(
      <ReceiptItemForm
        mode="edit"
        defaultValues={classificationDefaults}
        currentClassification={recorded}
        onSubmit={submit}
        onCancel={vi.fn()}
      />,
    );
    expect(
      screen.getByRole("combobox", { name: /Category/ }),
    ).toHaveAttribute("title", "Old Food / Old Produce / Old Fruit");
    expect(screen.getByText("Old Fruit")).toBeVisible();
    expect(screen.getByText("Old Food / Old Produce")).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Update Item" }));
    await waitFor(() => expect(submit).toHaveBeenCalled());
    expect(submit.mock.calls[0][0].classification).toBeUndefined();
  });
  it.each([
    ["Food", "root"],
    ["Food / Produce", "produce"],
    ["Unclassified", null],
    ["Food / Produce / Fruit", "fruit"],
  ])(
    "submits %s with exact identity and stable operation across retry",
    async (label, categoryId) => {
      const user = userEvent.setup();
      const submit = vi.fn();
      const { rerender } = render(
        <ReceiptItemForm
          mode="edit"
          defaultValues={classificationDefaults}
          currentClassification={recorded}
          onSubmit={submit}
          onCancel={vi.fn()}
        />,
      );
      await user.click(screen.getByRole("combobox", { name: /Category/ }));
      await chooseCategoryOption(user, label);
      await user.click(screen.getByRole("button", { name: "Update Item" }));
      await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));
      const first = submit.mock.calls[0][0];
      expect(first).not.toHaveProperty("category");
      expect(first).not.toHaveProperty("subcategory");
      expect(first).toMatchObject({
        classification: {
          categoryId,
          taxonomyRevisionId: "revision-1",
          expectedAssignmentId: "assignment-1",
          reason: expect.any(String),
          operationId: expect.any(String),
        },
      });
      rerender(
        <ReceiptItemForm
          mode="edit"
          defaultValues={classificationDefaults}
          currentClassification={{
            ...recorded,
            assignmentId: "assignment-from-background-refresh",
          }}
          serverErrors={{ description: "Taxonomy changed; review your selection" }}
          onSubmit={submit}
          onCancel={vi.fn()}
        />,
      );
      expect(
        screen.getByText("Taxonomy changed; review your selection"),
      ).toBeInTheDocument();
      expect(screen.getByPlaceholderText("Enter item code...")).toHaveValue(
        "A",
      );
      await user.click(screen.getByRole("button", { name: "Update Item" }));
      await waitFor(() => expect(submit).toHaveBeenCalledTimes(2));
      expect(submit.mock.calls[1][0].classification).toEqual(
        first.classification,
      );
    },
  );
});

describe("item-code suggestion category", () => {
  type Suggestion = components["schemas"]["ReceiptItemSuggestionResponse"];
  const suggestion = (categoryId?: string | null): Suggestion => ({
    itemCode: "MILK-GAL",
    description: "Whole milk",
    category: "Food",
    subcategory: "Produce",
    categoryId,
    unitPrice: 3.5,
    matchType: "location",
  });
  const originalSuggestions = vi
    .mocked(useReceiptItemSuggestions)
    .getMockImplementation()!;
  const originalTaxonomy = vi
    .mocked(useCategoryTaxonomy)
    .getMockImplementation()!;
  const suggest = (item: Suggestion) =>
    vi.mocked(useReceiptItemSuggestions).mockImplementation(
      () =>
        ({
          data: [item],
          isFetching: false,
          isSuccess: true,
          isLoading: false,
          isError: false,
          isDebouncing: false,
          refetch: vi.fn(),
        }) as unknown as ReturnType<typeof useReceiptItemSuggestions>,
    );
  const categoryPicker = () => screen.getByRole("combobox", { name: /Category/ });
  async function pickSuggestion(user: ReturnType<typeof userEvent.setup>) {
    await user.type(screen.getByPlaceholderText("Enter item code..."), "MI");
    await user.click(await screen.findByRole("option", { name: /MILK-GAL/ }));
    expect(screen.getByPlaceholderText("Enter item code...")).toHaveValue(
      "MILK-GAL",
    );
  }

  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
  });
  afterEach(() => {
    vi.mocked(useReceiptItemSuggestions).mockImplementation(originalSuggestions);
    vi.mocked(useCategoryTaxonomy).mockImplementation(originalTaxonomy);
  });

  it("fills an empty category with the suggested item type and submits its classification", async () => {
    suggest(suggestion("fruit"));
    const user = userEvent.setup();
    const submit = vi.fn();
    render(
      <ReceiptItemForm
        mode="create"
        defaultValues={{ receiptId: "r-1", quantity: 1 }}
        onSubmit={submit}
        onCancel={vi.fn()}
      />,
    );
    await pickSuggestion(user);
    expect(categoryPicker()).toHaveAttribute("title", "Food / Produce / Fruit");
    await user.click(screen.getByRole("button", { name: /create item/i }));
    await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));
    expect(submit.mock.calls[0][0]).toMatchObject({
      receiptItemCode: "MILK-GAL",
      description: "Whole milk",
      unitPrice: 3.5,
      classification: {
        categoryId: "fruit",
        taxonomyRevisionId: "revision-1",
        expectedAssignmentId: null,
        operationId: expect.any(String),
      },
    });
  });

  it("fills an edited item that has no category recorded", async () => {
    suggest(suggestion("produce"));
    const user = userEvent.setup();
    render(
      <ReceiptItemForm
        mode="edit"
        defaultValues={classificationDefaults}
        onSubmit={vi.fn()}
        onCancel={vi.fn()}
      />,
    );
    await user.clear(screen.getByPlaceholderText("Enter item code..."));
    await pickSuggestion(user);
    expect(categoryPicker()).toHaveAttribute("title", "Food / Produce");
    expect(
      screen.queryByText(/No category recorded yet/),
    ).not.toBeInTheDocument();
  });

  it("fills an edited item saved as Unclassified and submits its classification", async () => {
    suggest(suggestion("fruit"));
    const user = userEvent.setup();
    const submit = vi.fn();
    render(
      <ReceiptItemForm
        mode="edit"
        defaultValues={classificationDefaults}
        currentClassification={{
          assignmentId: "assignment-1",
          categoryId: null,
          taxonomyRevisionId: "revision-1",
          recordedPath: [],
        }}
        onSubmit={submit}
        onCancel={vi.fn()}
      />,
    );
    await user.clear(screen.getByPlaceholderText("Enter item code..."));
    await pickSuggestion(user);
    expect(categoryPicker()).toHaveAttribute("title", "Food / Produce / Fruit");
    await user.click(screen.getByRole("button", { name: "Update Item" }));
    await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));
    expect(submit.mock.calls[0][0]).toMatchObject({
      classification: {
        categoryId: "fruit",
        taxonomyRevisionId: "revision-1",
        expectedAssignmentId: "assignment-1",
        operationId: expect.any(String),
      },
    });
  });

  it("keeps a recorded category and sends no classification", async () => {
    suggest(suggestion("fruit"));
    const user = userEvent.setup();
    const submit = vi.fn();
    render(
      <ReceiptItemForm
        mode="edit"
        defaultValues={classificationDefaults}
        currentClassification={{
          assignmentId: "assignment-1",
          categoryId: "produce",
          taxonomyRevisionId: "revision-1",
          recordedPath: [
            { id: "root", name: "Food", archived: false },
            { id: "produce", name: "Produce", parentId: "root", archived: false },
          ],
        }}
        onSubmit={submit}
        onCancel={vi.fn()}
      />,
    );
    await user.clear(screen.getByPlaceholderText("Enter item code..."));
    await pickSuggestion(user);
    expect(categoryPicker()).toHaveAttribute("title", "Food / Produce");
    await user.click(screen.getByRole("button", { name: "Update Item" }));
    await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));
    expect(submit.mock.calls[0][0].classification).toBeUndefined();
  });

  it("keeps a category picked before the suggestion", async () => {
    suggest(suggestion("fruit"));
    const user = userEvent.setup();
    render(
      <ReceiptItemForm
        mode="create"
        defaultValues={{ receiptId: "r-1", quantity: 1 }}
        onSubmit={vi.fn()}
        onCancel={vi.fn()}
      />,
    );
    await user.click(categoryPicker());
    await chooseCategoryOption(user, "Food");
    await pickSuggestion(user);
    expect(categoryPicker()).toHaveAttribute("title", "Food");
  });

  it.each([
    ["has no item type", undefined],
    ["has a null item type", null],
    ["names an item type the current tree does not hold", "retired"],
    ["names an archived item type", "archived"],
  ])("leaves the category empty when the suggestion %s", async (_, id) => {
    vi.mocked(useCategoryTaxonomy).mockImplementation(() => {
      const base = originalTaxonomy();
      return {
        ...base,
        data: base.data && {
          ...base.data,
          nodes: [
            ...base.data.nodes,
            { id: "archived", name: "Old", archived: true },
          ],
        },
      } as ReturnType<typeof useCategoryTaxonomy>;
    });
    suggest(suggestion(id));
    const user = userEvent.setup();
    render(
      <ReceiptItemForm
        mode="create"
        defaultValues={{ receiptId: "r-1", quantity: 1 }}
        onSubmit={vi.fn()}
        onCancel={vi.fn()}
      />,
    );
    await pickSuggestion(user);
    expect(categoryPicker()).toHaveAttribute("title", "Choose category");
  });
});
