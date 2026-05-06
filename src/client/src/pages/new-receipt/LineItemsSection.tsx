import { useState, useMemo, useCallback, useRef } from "react";
import { generateId } from "@/lib/id";
import { useForm } from "react-hook-form";
import { z } from "zod/v4";
import { zodResolver } from "@hookform/resolvers/zod";
import { useCategories } from "@/hooks/useCategories";
import {
  useSubcategoriesByCategoryId,
  useCreateSubcategory,
} from "@/hooks/useSubcategories";
import {
  useSimilarItems,
  useCategoryRecommendations,
} from "@/hooks/useSimilarItems";
import { useReceiptItemSuggestions } from "@/hooks/useReceiptItemSuggestions";
import { computeLineTotal } from "./computeLineTotal";
import { formatCurrency } from "@/lib/format";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import {
  Popover,
  PopoverContent,
  PopoverAnchor,
} from "@/components/ui/popover";
import {
  Command,
  CommandEmpty,
  CommandItem,
  CommandList,
} from "@/components/ui/command";
import { Combobox } from "@/components/ui/combobox";
import { CurrencyInput } from "@/components/ui/currency-input";
import { Switch } from "@/components/ui/switch";
import { Badge } from "@/components/ui/badge";
import {
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { Plus, Trash2, Loader2, Sparkles, Pencil, Check, X } from "lucide-react";
import { ConfidenceIndicator } from "@/pages/scan-receipt/ConfidenceIndicator";
import type { ConfidenceLevel } from "@/pages/scan-receipt/types";

const TAX_CODE_PATTERN = /^[A-Za-z]?$/;

const itemSchema = z
  .object({
    receiptItemCode: z.string().optional().default(""),
    description: z.string().min(1, "Description is required"),
    pricingMode: z.enum(["quantity", "flat"]).default("quantity"),
    quantity: z.number().positive("Quantity must be positive"),
    unitPrice: z.number().min(0, "Unit price must be non-negative"),
    totalPrice: z.number().min(0, "Total price must be non-negative"),
    category: z.string().min(1, "Category is required"),
    subcategory: z.string().optional().default(""),
    taxCode: z
      .string()
      .optional()
      .default("")
      .refine((v) => !v || TAX_CODE_PATTERN.test(v), {
        message: "Tax code must be a single letter",
      }),
  })
  .superRefine((value, ctx) => {
    // Flat mode requires a positive line total (the unit-priced source receipt
    // doesn't print a per-unit price, so the total carries the dollar value).
    // Quantity mode requires a positive unit price (the legacy contract).
    if (value.pricingMode === "flat") {
      if (value.totalPrice <= 0) {
        ctx.addIssue({
          code: "custom",
          path: ["totalPrice"],
          message: "Total price must be positive",
        });
      }
    } else {
      if (value.unitPrice <= 0) {
        ctx.addIssue({
          code: "custom",
          path: ["unitPrice"],
          message: "Unit price must be positive",
        });
      }
    }
  });

type ItemFormValues = z.output<typeof itemSchema>;

export interface ReceiptLineItem {
  id: string;
  receiptItemCode: string;
  description: string;
  pricingMode: "quantity" | "flat";
  quantity: number;
  unitPrice: number;
  /**
   * Persisted line total. For "quantity" mode this equals quantity x unitPrice
   * (the rolling subtotal still computes from those); for "flat" mode this is
   * the receipt-printed line total and the source of truth for the row.
   */
  totalPrice: number;
  category: string;
  subcategory: string;
  taxCode: string;
}

interface LineItemsSectionProps {
  items: ReceiptLineItem[];
  onChange: (items: ReceiptLineItem[]) => void;
  location?: string | null;
  /**
   * Per-item confidence levels keyed by stable item id (not index). Keying
   * by id preserves correctness after rows are added or removed — an
   * index-based lookup would misalign confidence with the wrong row after
   * a deletion.
   */
  itemConfidenceById?: Map<string, { taxCode?: ConfidenceLevel }>;
}

export function LineItemsSection({
  items,
  onChange,
  location,
  itemConfidenceById,
}: LineItemsSectionProps) {
  const [showSuggestions, setShowSuggestions] = useState(false);
  const suggestionsListId = "new-receipt-suggestions-list";
  const { data: categories } = useCategories(0, 50, undefined, undefined, true);

  const categoryOptions = useMemo(
    () =>
      (
        (categories as { id: string; name: string }[] | undefined) ?? []
      ).map((c) => ({ value: c.name, label: c.name })),
    [categories],
  );

  const form = useForm<ItemFormValues>({
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    resolver: zodResolver(itemSchema) as any,
    defaultValues: {
      receiptItemCode: "",
      description: "",
      pricingMode: "quantity",
      quantity: 1,
      unitPrice: 0,
      totalPrice: 0,
      category: "",
      subcategory: "",
      taxCode: "",
    },
  });

  // eslint-disable-next-line react-hooks/incompatible-library
  const itemCode = form.watch("receiptItemCode");
  const description = form.watch("description");
  const selectedCategory = form.watch("category");
  const formPricingMode = form.watch("pricingMode");

  // Item code autocomplete
  const [showItemCodeSuggestions, setShowItemCodeSuggestions] = useState(false);
  const itemCodeSuggestionsListId = "new-receipt-item-code-suggestions-list";

  const { data: itemCodeSuggestions, isFetching: isFetchingItemCodeSuggestions } =
    useReceiptItemSuggestions(itemCode ?? "", location, {
      enabled: showItemCodeSuggestions && (itemCode ?? "").length >= 1,
    });

  const hasItemCodeResults = itemCodeSuggestions && itemCodeSuggestions.length > 0;
  const hasNoItemCodeResultsMessage =
    (itemCode ?? "").length >= 1 &&
    !isFetchingItemCodeSuggestions &&
    itemCodeSuggestions &&
    itemCodeSuggestions.length === 0;
  const isItemCodeSuggestionsOpen =
    showItemCodeSuggestions && (hasItemCodeResults || hasNoItemCodeResultsMessage);

  const applyItemCodeSuggestion = useCallback(
    (suggestion: NonNullable<typeof itemCodeSuggestions>[number]) => {
      form.setValue("receiptItemCode", suggestion.itemCode);
      form.setValue("description", suggestion.description);
      if (suggestion.category) {
        form.setValue("category", suggestion.category);
      }
      if (suggestion.subcategory) {
        form.setValue("subcategory", suggestion.subcategory);
      }
      if (suggestion.unitPrice != null) {
        form.setValue("unitPrice", Number(suggestion.unitPrice));
      }
      setShowItemCodeSuggestions(false);
    },
    [form],
  );

  const handleItemCodeKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLInputElement>) => {
      if (e.key === "Escape") {
        setShowItemCodeSuggestions(false);
      } else if (e.key === "ArrowDown" && isItemCodeSuggestionsOpen) {
        e.preventDefault();
        const list = document.getElementById(itemCodeSuggestionsListId);
        const firstItem = list?.querySelector(
          "[cmdk-item]",
        ) as HTMLElement | null;
        firstItem?.focus();
      }
    },
    [isItemCodeSuggestionsOpen, itemCodeSuggestionsListId],
  );

  const { data: similarItems, isFetching: isFetchingSimilar } =
    useSimilarItems(description, { enabled: showSuggestions });

  const hasResults = similarItems && similarItems.length > 0;
  const hasNoResultsMessage =
    description.length >= 2 &&
    !isFetchingSimilar &&
    similarItems &&
    similarItems.length === 0;
  const isSuggestionsOpen =
    showSuggestions && (hasResults || hasNoResultsMessage);

  const { data: categoryRecs } = useCategoryRecommendations(description, {
    enabled: description.length >= 2 && !selectedCategory,
  });

  const selectedCategoryObj = useMemo(
    () =>
      (
        (categories as { id: string; name: string }[] | undefined) ?? []
      ).find((c) => c.name === selectedCategory),
    [categories, selectedCategory],
  );

  const { data: subcategories } = useSubcategoriesByCategoryId(
    selectedCategoryObj?.id ?? "",
    0, 200, undefined, undefined, true,
  );
  const createSubcategory = useCreateSubcategory();
  const pendingSubcategories = useRef(new Set<string>());

  const subcategoryOptions = useMemo(
    () =>
      (
        (subcategories as { id: string; name: string }[] | undefined) ?? []
      ).map((s) => ({ value: s.name, label: s.name })),
    [subcategories],
  );

  const subtotal = useMemo(
    () => items.reduce((sum, item) => sum + computeLineTotal(item), 0),
    [items],
  );

  const applySuggestion = useCallback(
    (suggestion: NonNullable<typeof similarItems>[number]) => {
      form.setValue("description", suggestion.name);
      if (suggestion.defaultCategory) {
        form.setValue("category", suggestion.defaultCategory);
      }
      if (suggestion.defaultSubcategory) {
        form.setValue("subcategory", suggestion.defaultSubcategory);
      }
      if (suggestion.defaultUnitPrice != null) {
        form.setValue("unitPrice", Number(suggestion.defaultUnitPrice));
      }
      if (suggestion.defaultItemCode) {
        form.setValue("receiptItemCode", suggestion.defaultItemCode);
      }
      setShowSuggestions(false);
    },
    [form],
  );

  const applyCategoryRec = useCallback(
    (rec: NonNullable<typeof categoryRecs>[number]) => {
      form.setValue("category", rec.category);
      if (rec.subcategory) {
        form.setValue("subcategory", rec.subcategory);
      }
    },
    [form],
  );

  const handleDescriptionKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLInputElement>) => {
      if (e.key === "Escape") {
        setShowSuggestions(false);
      } else if (e.key === "ArrowDown" && isSuggestionsOpen) {
        e.preventDefault();
        const list = document.getElementById(suggestionsListId);
        const firstItem = list?.querySelector(
          "[cmdk-item]",
        ) as HTMLElement | null;
        firstItem?.focus();
      }
    },
    [isSuggestionsOpen, suggestionsListId],
  );

  const handleAdd = useCallback(
    (values: ItemFormValues) => {
      const isFlat = values.pricingMode === "flat";
      // Domain rule: flat-priced items must have quantity == 1.
      const quantity = isFlat ? 1 : values.quantity;
      const unitPrice = isFlat ? 0 : values.unitPrice;
      const totalPrice = isFlat
        ? values.totalPrice
        : Math.round(quantity * unitPrice * 100) / 100;
      const newItem: ReceiptLineItem = {
        id: generateId(),
        receiptItemCode: values.receiptItemCode ?? "",
        description: values.description,
        pricingMode: values.pricingMode,
        quantity,
        unitPrice,
        totalPrice,
        category: values.category,
        subcategory: values.subcategory ?? "",
        taxCode: (values.taxCode ?? "").toUpperCase(),
      };
      onChange([...items, newItem]);
      form.reset({
        receiptItemCode: "",
        description: "",
        pricingMode: values.pricingMode,
        quantity: 1,
        unitPrice: 0,
        totalPrice: 0,
        category: values.category,
        subcategory: "",
        taxCode: "",
      });
      setShowSuggestions(false);
    },
    [form, items, onChange],
  );

  const handleRemove = useCallback(
    (id: string) => {
      onChange(items.filter((item) => item.id !== id));
    },
    [items, onChange],
  );

  const [editingItemId, setEditingItemId] = useState<string | null>(null);
  const [editDraft, setEditDraft] = useState<{
    description: string;
    pricingMode: "quantity" | "flat";
    quantity: number;
    unitPrice: number;
    totalPrice: number;
    taxCode: string;
  }>({
    description: "",
    pricingMode: "quantity",
    quantity: 1,
    unitPrice: 0,
    totalPrice: 0,
    taxCode: "",
  });

  const startEditing = useCallback((item: ReceiptLineItem) => {
    setEditingItemId(item.id);
    setEditDraft({
      description: item.description,
      pricingMode: item.pricingMode,
      quantity: item.quantity,
      unitPrice: item.unitPrice,
      totalPrice: item.totalPrice,
      taxCode: item.taxCode ?? "",
    });
  }, []);

  const cancelEditing = useCallback(() => {
    setEditingItemId(null);
  }, []);

  const saveEditing = useCallback(() => {
    if (!editingItemId) return;
    if (!editDraft.description.trim()) return;
    const isFlat = editDraft.pricingMode === "flat";
    if (isFlat) {
      if (!Number.isFinite(editDraft.totalPrice) || editDraft.totalPrice <= 0) {
        return;
      }
    } else {
      if (!Number.isFinite(editDraft.quantity) || editDraft.quantity <= 0) return;
      if (!Number.isFinite(editDraft.unitPrice) || editDraft.unitPrice <= 0) {
        return;
      }
    }
    if (editDraft.taxCode && !TAX_CODE_PATTERN.test(editDraft.taxCode)) return;

    onChange(
      items.map((item) =>
        item.id === editingItemId
          ? {
              ...item,
              description: editDraft.description.trim(),
              // Flat-priced items: domain requires quantity == 1, persist
              // unitPrice = 0 since the source receipt didn't print one. The
              // line total is the source of truth.
              quantity: isFlat ? 1 : editDraft.quantity,
              unitPrice: isFlat ? 0 : editDraft.unitPrice,
              totalPrice: isFlat
                ? editDraft.totalPrice
                : Math.round(
                    editDraft.quantity * editDraft.unitPrice * 100,
                  ) / 100,
              taxCode: editDraft.taxCode.toUpperCase(),
            }
          : item,
      ),
    );
    setEditingItemId(null);
  }, [editingItemId, editDraft, items, onChange]);

  // Derived during render: stale `editingItemId` (e.g. parent removed the
  // edited item) becomes harmless because no row matches and no edit UI shows.
  // See docs/react/effects.md rule 4 — replaces a "form field cascade via
  // Effects" anti-pattern that caused a double render on external removal.
  const isEditing =
    editingItemId !== null && items.some((i) => i.id === editingItemId);

  return (
    <Card>
      <CardHeader className="pb-3">
        <div className="flex items-center justify-between">
          <CardTitle className="text-lg">Line Items</CardTitle>
          <span className="text-sm text-muted-foreground">
            Subtotal: {formatCurrency(subtotal)}
          </span>
        </div>
      </CardHeader>
      <CardContent className="space-y-4">
        <Form {...form}>
          <form
            onSubmit={form.handleSubmit(handleAdd)}
            className="space-y-4"
          >
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
              <FormField
                control={form.control}
                name="receiptItemCode"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>Item Code</FormLabel>
                    <Popover
                      open={isItemCodeSuggestionsOpen}
                      onOpenChange={setShowItemCodeSuggestions}
                    >
                      <PopoverAnchor asChild>
                        <FormControl>
                          <div className="relative">
                            <Input
                              role="combobox"
                              placeholder="e.g. MILK-GAL"
                              aria-autocomplete="list"
                              aria-expanded={isItemCodeSuggestionsOpen}
                              aria-controls={itemCodeSuggestionsListId}
                              autoComplete="off"
                              {...field}
                              onFocus={() => setShowItemCodeSuggestions(true)}
                              onKeyDown={handleItemCodeKeyDown}
                            />
                            {isFetchingItemCodeSuggestions && (itemCode ?? "").length >= 1 && (
                              <>
                                <Loader2 className="absolute right-2 top-1/2 h-4 w-4 -translate-y-1/2 animate-spin text-muted-foreground" aria-hidden="true" />
                                <span className="sr-only" role="status">Loading suggestions...</span>
                              </>
                            )}
                          </div>
                        </FormControl>
                      </PopoverAnchor>
                      <PopoverContent
                        className="w-[--radix-popover-trigger-width] p-0"
                        align="start"
                        onOpenAutoFocus={(e) => e.preventDefault()}
                        onInteractOutside={() => setShowItemCodeSuggestions(false)}
                      >
                        <Command shouldFilter={false}>
                          <CommandList id={itemCodeSuggestionsListId}>
                            <CommandEmpty>No suggestions found</CommandEmpty>
                            {itemCodeSuggestions?.map((suggestion) => (
                              <CommandItem
                                key={`${suggestion.itemCode}-${suggestion.matchType}`}
                                value={`${suggestion.itemCode} ${suggestion.description}`}
                                onSelect={() => applyItemCodeSuggestion(suggestion)}
                              >
                                <div className="flex w-full items-center justify-between">
                                  <div className="flex flex-col gap-0.5">
                                    <span className="font-medium font-mono">
                                      {suggestion.itemCode}
                                    </span>
                                    <span className="text-xs text-muted-foreground">
                                      {suggestion.description}
                                      {suggestion.category
                                        ? ` · ${suggestion.category}`
                                        : ""}
                                      {suggestion.unitPrice != null
                                        ? ` · ${formatCurrency(Number(suggestion.unitPrice))}`
                                        : ""}
                                    </span>
                                  </div>
                                  <Badge
                                    variant="outline"
                                    className="text-[10px] px-1.5 py-0"
                                  >
                                    {suggestion.matchType === "location"
                                      ? "Location"
                                      : "Global"}
                                  </Badge>
                                </div>
                              </CommandItem>
                            ))}
                          </CommandList>
                        </Command>
                      </PopoverContent>
                    </Popover>
                    <FormMessage />
                  </FormItem>
                )}
              />

              <FormField
                control={form.control}
                name="description"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel required>Description</FormLabel>
                    <Popover
                      open={isSuggestionsOpen}
                      onOpenChange={setShowSuggestions}
                    >
                      <PopoverAnchor asChild>
                        <FormControl>
                          <div className="relative">
                            <Input
                              role="combobox"
                              placeholder="Item description"
                              aria-required="true"
                              aria-autocomplete="list"
                              aria-expanded={isSuggestionsOpen}
                              aria-controls={suggestionsListId}
                              autoComplete="off"
                              {...field}
                              onFocus={() => setShowSuggestions(true)}
                              onKeyDown={handleDescriptionKeyDown}
                            />
                            {isFetchingSimilar && description.length >= 2 && (
                              <>
                                <Loader2 className="absolute right-2 top-1/2 h-4 w-4 -translate-y-1/2 animate-spin text-muted-foreground" aria-hidden="true" />
                                <span className="sr-only" role="status">Loading suggestions...</span>
                              </>
                            )}
                          </div>
                        </FormControl>
                      </PopoverAnchor>
                      <PopoverContent
                        className="w-[--radix-popover-trigger-width] p-0"
                        align="start"
                        onOpenAutoFocus={(e) => e.preventDefault()}
                        onInteractOutside={() => setShowSuggestions(false)}
                      >
                        <Command shouldFilter={false}>
                          <CommandList id={suggestionsListId}>
                            <CommandEmpty>No similar items found</CommandEmpty>
                            {similarItems?.map((item) => (
                              <CommandItem
                                key={`${item.name}-${item.source}`}
                                value={`${item.name} ${item.source}`}
                                onSelect={() => applySuggestion(item)}
                              >
                                <div className="flex w-full items-center justify-between">
                                  <div className="flex flex-col gap-0.5">
                                    <span className="font-medium">
                                      {item.name}
                                    </span>
                                    {item.defaultCategory && (
                                      <span className="text-xs text-muted-foreground">
                                        {item.defaultCategory}
                                        {item.defaultSubcategory
                                          ? ` / ${item.defaultSubcategory}`
                                          : ""}
                                        {item.defaultUnitPrice != null
                                          ? ` · ${formatCurrency(Number(item.defaultUnitPrice))}`
                                          : ""}
                                      </span>
                                    )}
                                  </div>
                                  <div className="flex items-center gap-1.5">
                                    <Badge
                                      variant="outline"
                                      className="text-[10px] px-1.5 py-0"
                                    >
                                      {item.source === "template"
                                        ? "Template"
                                        : "History"}
                                    </Badge>
                                    <span className="text-[10px] text-muted-foreground">
                                      {Math.round(Number(item.combinedScore ?? 0) * 100)}%
                                    </span>
                                  </div>
                                </div>
                              </CommandItem>
                            ))}
                          </CommandList>
                        </Command>
                      </PopoverContent>
                    </Popover>
                    <FormMessage />
                  </FormItem>
                )}
              />

              <FormField
                control={form.control}
                name="category"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel required>Category</FormLabel>
                    <FormControl>
                      <Combobox
                        options={categoryOptions}
                        value={field.value}
                        onValueChange={(v) => {
                          field.onChange(v);
                          form.setValue("subcategory", "");
                        }}
                        placeholder="Select category..."
                        searchPlaceholder="Search categories..."
                        emptyMessage="No categories found."
                        aria-required="true"
                      />
                    </FormControl>
                    {categoryRecs &&
                      categoryRecs.length > 0 &&
                      !selectedCategory && (
                        <div className="flex flex-wrap gap-1 pt-1" role="group" aria-label="Suggested categories">
                          <Sparkles className="h-3 w-3 text-muted-foreground mt-1" aria-hidden="true" />
                          {categoryRecs.map((rec) => (
                            <button
                              key={`${rec.category}-${rec.subcategory ?? ""}`}
                              type="button"
                              className="inline-flex items-center rounded-full border px-3 py-1 text-xs min-h-[24px] text-muted-foreground hover:bg-accent hover:text-accent-foreground transition-colors"
                              onClick={() => applyCategoryRec(rec)}
                            >
                              {rec.category}
                              {rec.subcategory ? ` / ${rec.subcategory}` : ""}
                            </button>
                          ))}
                        </div>
                      )}
                    <FormMessage />
                  </FormItem>
                )}
              />

            </div>

            {/* Row 2: Subcategory, Tax Code, Quantity, Unit Price, Add Item */}
            <div className="grid grid-cols-2 gap-4 sm:grid-cols-[1fr_auto_auto_auto_auto] sm:items-end">
              <FormField
                control={form.control}
                name="subcategory"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>Subcategory (optional)</FormLabel>
                    <FormControl>
                      <Combobox
                        options={subcategoryOptions}
                        value={field.value ?? ""}
                        onValueChange={(v: string) => {
                          field.onChange(v);
                          const isExisting = subcategoryOptions.some(
                            (o) => o.value === v,
                          );
                          if (
                            !isExisting &&
                            v &&
                            selectedCategoryObj?.id &&
                            !pendingSubcategories.current.has(v)
                          ) {
                            pendingSubcategories.current.add(v);
                            createSubcategory.mutate(
                              {
                                categoryId: selectedCategoryObj.id,
                                name: v,
                                isActive: true,
                              },
                              {
                                onSettled: () => {
                                  pendingSubcategories.current.delete(v);
                                },
                                onError: () => {
                                  field.onChange("");
                                },
                              },
                            );
                          }
                        }}
                        placeholder="Select subcategory..."
                        searchPlaceholder="Search subcategories..."
                        emptyMessage="No subcategories found."
                        allowCustom
                        disabled={!selectedCategory}
                      />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />

              <FormField
                control={form.control}
                name="taxCode"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>Tax</FormLabel>
                    <FormControl>
                      <Input
                        type="text"
                        maxLength={1}
                        className="w-12 text-center font-mono uppercase"
                        placeholder="—"
                        aria-label="Tax code"
                        autoComplete="off"
                        {...field}
                        value={field.value ?? ""}
                        onChange={(e) => {
                          const next = e.target.value.toUpperCase();
                          if (TAX_CODE_PATTERN.test(next)) {
                            field.onChange(next);
                          }
                        }}
                      />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />

              <FormField
                control={form.control}
                name="pricingMode"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>Mode</FormLabel>
                    <FormControl>
                      <div className="flex items-center gap-2 text-sm h-9 px-2 rounded-md border bg-background select-none whitespace-nowrap">
                        <Switch
                          aria-label="Flat price"
                          checked={field.value === "flat"}
                          onCheckedChange={(checked) =>
                            field.onChange(checked ? "flat" : "quantity")
                          }
                        />
                        <span>Flat</span>
                      </div>
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />

              {formPricingMode === "flat" ? (
                <FormField
                  control={form.control}
                  name="totalPrice"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel required>Total Price</FormLabel>
                      <FormControl>
                        <CurrencyInput aria-label="Total price" {...field} />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  )}
                />
              ) : (
                <>
                  <FormField
                    control={form.control}
                    name="quantity"
                    render={({ field }) => (
                      <FormItem>
                        <FormLabel required>Qty</FormLabel>
                        <FormControl>
                          <Input
                            type="number"
                            step="any"
                            min="0.01"
                            className="w-20"
                            {...field}
                            onChange={(e) =>
                              field.onChange(Number(e.target.value))
                            }
                          />
                        </FormControl>
                        <FormMessage />
                      </FormItem>
                    )}
                  />

                  <FormField
                    control={form.control}
                    name="unitPrice"
                    render={({ field }) => (
                      <FormItem>
                        <FormLabel required>Unit Price</FormLabel>
                        <FormControl>
                          <CurrencyInput
                            aria-label="Unit price"
                            {...field}
                          />
                        </FormControl>
                        <FormMessage />
                      </FormItem>
                    )}
                  />
                </>
              )}

              <div className="flex justify-end sm:mb-0.5">
                <Button type="submit" variant="secondary" size="sm">
                  <Plus className="mr-1 h-4 w-4" />
                  Add Item
                </Button>
              </div>
            </div>
          </form>
        </Form>

        {items.length > 0 && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Description</TableHead>
                <TableHead>Qty</TableHead>
                <TableHead>Unit Price</TableHead>
                <TableHead>Line Total</TableHead>
                <TableHead>Category</TableHead>
                <TableHead>Tax</TableHead>
                <TableHead className="w-12">
                  <span className="sr-only">Actions</span>
                </TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {items.map((item) => {
                const taxCodeConfidence = itemConfidenceById?.get(item.id)?.taxCode;
                const lineTotal = computeLineTotal(item);
                const editLineTotal =
                  editDraft.pricingMode === "flat"
                    ? editDraft.totalPrice
                    : Math.round(
                        editDraft.quantity * editDraft.unitPrice * 100,
                      ) / 100;
                return isEditing && editingItemId === item.id ? (
                  <TableRow key={item.id}>
                    <TableCell>
                      <Input
                        value={editDraft.description}
                        onChange={(e) =>
                          setEditDraft((d) => ({
                            ...d,
                            description: e.target.value,
                          }))
                        }
                        aria-label="Edit description"
                        className="h-8"
                      />
                    </TableCell>
                    <TableCell>
                      <Input
                        type="number"
                        step="any"
                        min="0.01"
                        value={editDraft.quantity}
                        onChange={(e) =>
                          setEditDraft((d) => ({
                            ...d,
                            quantity: Number(e.target.value),
                          }))
                        }
                        aria-label="Edit quantity"
                        className="h-8 w-20"
                        disabled={editDraft.pricingMode === "flat"}
                      />
                    </TableCell>
                    <TableCell>
                      <CurrencyInput
                        value={editDraft.unitPrice}
                        onChange={(v) =>
                          setEditDraft((d) => ({ ...d, unitPrice: v }))
                        }
                        aria-label="Edit unit price"
                        className="h-8"
                        disabled={editDraft.pricingMode === "flat"}
                      />
                    </TableCell>
                    <TableCell>
                      {editDraft.pricingMode === "flat" ? (
                        <CurrencyInput
                          value={editDraft.totalPrice}
                          onChange={(v) =>
                            setEditDraft((d) => ({ ...d, totalPrice: v }))
                          }
                          aria-label="Edit total price"
                          className="h-8"
                        />
                      ) : (
                        formatCurrency(editLineTotal)
                      )}
                    </TableCell>
                    <TableCell>
                      {item.category}
                      {item.subcategory ? ` / ${item.subcategory}` : ""}
                    </TableCell>
                    <TableCell>
                      <Input
                        type="text"
                        maxLength={1}
                        value={editDraft.taxCode}
                        onChange={(e) => {
                          const next = e.target.value.toUpperCase();
                          if (TAX_CODE_PATTERN.test(next)) {
                            setEditDraft((d) => ({
                              ...d,
                              taxCode: next,
                            }));
                          }
                        }}
                        aria-label="Edit tax code"
                        className="h-8 w-10 text-center font-mono uppercase"
                      />
                    </TableCell>
                    <TableCell>
                      <div className="flex gap-1">
                        <Button
                          variant="ghost"
                          size="icon"
                          onClick={saveEditing}
                          aria-label="Save"
                        >
                          <Check className="h-4 w-4" />
                        </Button>
                        <Button
                          variant="ghost"
                          size="icon"
                          onClick={cancelEditing}
                          aria-label="Cancel"
                        >
                          <X className="h-4 w-4" />
                        </Button>
                      </div>
                    </TableCell>
                  </TableRow>
                ) : (
                  <TableRow key={item.id}>
                    <TableCell>{item.description}</TableCell>
                    <TableCell>
                      {item.pricingMode === "flat" ? (
                        <span className="text-xs text-muted-foreground">—</span>
                      ) : (
                        item.quantity
                      )}
                    </TableCell>
                    <TableCell>
                      {item.pricingMode === "flat" ? (
                        <span className="text-xs text-muted-foreground">—</span>
                      ) : (
                        formatCurrency(item.unitPrice)
                      )}
                    </TableCell>
                    <TableCell>{formatCurrency(lineTotal)}</TableCell>
                    <TableCell>
                      {item.category}
                      {item.subcategory ? ` / ${item.subcategory}` : ""}
                    </TableCell>
                    <TableCell>
                      <div className="flex items-center gap-1">
                        {item.taxCode ? (
                          <Badge variant="outline" className="font-mono">
                            {item.taxCode}
                          </Badge>
                        ) : (
                          <span
                            className="text-xs text-muted-foreground"
                            aria-label="No tax code"
                          >
                            —
                          </span>
                        )}
                        <ConfidenceIndicator confidence={taxCodeConfidence} />
                      </div>
                    </TableCell>
                    <TableCell>
                      <div className="flex gap-1">
                        <Button
                          variant="ghost"
                          size="icon"
                          onClick={() => startEditing(item)}
                          aria-label="Edit"
                        >
                          <Pencil className="h-4 w-4" />
                        </Button>
                        <Button
                          variant="ghost"
                          size="icon"
                          onClick={() => handleRemove(item.id)}
                          aria-label="Remove"
                        >
                          <Trash2 className="h-4 w-4" />
                        </Button>
                      </div>
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </Card>
  );
}
