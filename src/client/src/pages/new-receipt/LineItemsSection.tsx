import { useState, useMemo, useCallback, useRef, useEffect } from "react";
import { generateId } from "@/lib/id";
import { useForm } from "react-hook-form";
import { z } from "zod/v4";
import { zodResolver } from "@hookform/resolvers/zod";
import { useAllCategories } from "@/hooks/useCategories";
import {
  useAllSubcategoriesByCategoryId,
  useCreateSubcategory,
} from "@/hooks/useSubcategories";
import {
  useSimilarItems,
  useCategoryRecommendations,
} from "@/hooks/useSimilarItems";
import { useReceiptItemSuggestions } from "@/hooks/useReceiptItemSuggestions";
import { usePromoteToTemplate } from "@/hooks/usePromoteToTemplate";
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
import { DecimalInput } from "@/components/ui/decimal-input";
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
import {
  Plus,
  Trash2,
  Loader2,
  Sparkles,
  Pencil,
  Check,
  X,
  BookmarkPlus,
} from "lucide-react";

const itemSchema = z.object({
  receiptItemCode: z.string().optional().default(""),
  description: z.string().min(1, "Description is required"),
  quantity: z.number().positive("Quantity must be positive"),
  unitPrice: z.number().min(0, "Unit price must be non-negative"),
  category: z.string().min(1, "Category is required"),
  subcategory: z.string().optional().default(""),
});

type ItemFormValues = z.output<typeof itemSchema>;

export interface ReceiptLineItem {
  id: string;
  receiptItemCode: string;
  description: string;
  quantity: number;
  unitPrice: number;
  category: string;
  subcategory: string;
}

interface LineItemsSectionProps {
  items: ReceiptLineItem[];
  onChange: (items: ReceiptLineItem[]) => void;
  location?: string | null;
}

export function LineItemsSection({ items, onChange, location }: LineItemsSectionProps) {
  const [showSuggestions, setShowSuggestions] = useState(false);
  const suggestionsListId = "new-receipt-suggestions-list";
  // Index of the keyboard-highlighted row in the description lookup.
  const [descriptionActiveIndex, setDescriptionActiveIndex] = useState(0);
  const { data: categories } = useAllCategories(true);

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
      quantity: 1,
      unitPrice: 0,
      category: "",
      subcategory: "",
    },
  });

  // eslint-disable-next-line react-hooks/incompatible-library
  const itemCode = form.watch("receiptItemCode");
  const description = form.watch("description");
  const selectedCategory = form.watch("category");

  // Item code autocomplete
  const [showItemCodeSuggestions, setShowItemCodeSuggestions] = useState(false);
  const itemCodeSuggestionsListId = "new-receipt-item-code-suggestions-list";
  const itemCodeInputRef = useRef<HTMLInputElement>(null);
  // Index of the keyboard-highlighted row in the item code lookup.
  const [itemCodeActiveIndex, setItemCodeActiveIndex] = useState(0);

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

  // Reset the highlight to the first row whenever the result set changes.
  useEffect(() => {
    setItemCodeActiveIndex(0);
  }, [itemCodeSuggestions]);

  const handleItemCodeKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLInputElement>) => {
      if (e.key === "Escape") {
        setShowItemCodeSuggestions(false);
        return;
      }
      if (!isItemCodeSuggestionsOpen) return;
      const count = itemCodeSuggestions?.length ?? 0;
      if (e.key === "ArrowDown") {
        e.preventDefault();
        setItemCodeActiveIndex((i) => Math.min(i + 1, count - 1));
      } else if (e.key === "ArrowUp") {
        e.preventDefault();
        setItemCodeActiveIndex((i) => Math.max(i - 1, 0));
      } else if (e.key === "Enter") {
        const suggestion = itemCodeSuggestions?.[itemCodeActiveIndex];
        if (suggestion) {
          // Prevent the form from submitting — Enter picks the suggestion.
          e.preventDefault();
          applyItemCodeSuggestion(suggestion);
        }
      }
    },
    [
      isItemCodeSuggestionsOpen,
      itemCodeSuggestions,
      itemCodeActiveIndex,
      applyItemCodeSuggestion,
    ],
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

  const { data: subcategories } = useAllSubcategoriesByCategoryId(
    selectedCategoryObj?.id ?? null,
    true,
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

  // Shared subcategory selection: applies the value and creates the
  // subcategory on the fly when the user enters a name that doesn't exist.
  const handleSubcategorySelect = useCallback(
    (
      next: string,
      categoryId: string | undefined,
      existing: { value: string }[],
      setValue: (v: string) => void,
    ) => {
      setValue(next);
      const isExisting = existing.some((o) => o.value === next);
      if (
        !isExisting &&
        next &&
        categoryId &&
        !pendingSubcategories.current.has(next)
      ) {
        pendingSubcategories.current.add(next);
        createSubcategory.mutate(
          { categoryId, name: next, isActive: true },
          {
            onSettled: () => pendingSubcategories.current.delete(next),
            onError: () => setValue(""),
          },
        );
      }
    },
    [createSubcategory],
  );

  const subtotal = useMemo(
    () =>
      items.reduce(
        (sum, item) =>
          sum + Math.round(item.quantity * item.unitPrice * 100) / 100,
        0,
      ),
    [items],
  );

  const applySuggestion = useCallback(
    (suggestion: NonNullable<typeof similarItems>[number]) => {
      // Description lookup only fills the description field — the other
      // fields are left for the user (or category recommendations) to set.
      form.setValue("description", suggestion.name, { shouldValidate: true });
      setShowSuggestions(false);
    },
    [form],
  );

  const promoteToTemplate = usePromoteToTemplate();
  const { mutate: promote, isPending: isPromoting } = promoteToTemplate;

  const promoteSuggestion = useCallback(
    (suggestion: NonNullable<typeof similarItems>[number]) => {
      if (isPromoting) return;
      promote({
        name: suggestion.name,
        defaultCategory: suggestion.defaultCategory,
        defaultSubcategory: suggestion.defaultSubcategory,
        defaultUnitPrice: suggestion.defaultUnitPrice,
        defaultItemCode: suggestion.defaultItemCode,
      });
    },
    [promote, isPromoting],
  );

  const promoteLineItem = useCallback(
    (item: ReceiptLineItem) => {
      if (isPromoting) return;
      promote({
        name: item.description,
        defaultCategory: item.category,
        defaultSubcategory: item.subcategory,
        defaultUnitPrice: item.unitPrice,
        defaultItemCode: item.receiptItemCode,
      });
    },
    [promote, isPromoting],
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

  // Reset the highlight to the first row whenever the result set changes.
  useEffect(() => {
    setDescriptionActiveIndex(0);
  }, [similarItems]);

  const handleDescriptionKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLInputElement>) => {
      if (e.key === "Escape") {
        setShowSuggestions(false);
        return;
      }
      if (!isSuggestionsOpen) return;
      const count = similarItems?.length ?? 0;
      if (e.key === "ArrowDown") {
        e.preventDefault();
        setDescriptionActiveIndex((i) => Math.min(i + 1, count - 1));
      } else if (e.key === "ArrowUp") {
        e.preventDefault();
        setDescriptionActiveIndex((i) => Math.max(i - 1, 0));
      } else if (e.key === "Enter") {
        const suggestion = similarItems?.[descriptionActiveIndex];
        if (suggestion) {
          // Prevent the form from submitting — Enter picks the suggestion.
          e.preventDefault();
          applySuggestion(suggestion);
        }
      }
    },
    [isSuggestionsOpen, similarItems, descriptionActiveIndex, applySuggestion],
  );

  const handleAdd = useCallback(
    (values: ItemFormValues) => {
      const newItem: ReceiptLineItem = {
        id: generateId(),
        receiptItemCode: values.receiptItemCode ?? "",
        description: values.description,
        quantity: values.quantity,
        unitPrice: values.unitPrice,
        category: values.category,
        subcategory: values.subcategory ?? "",
      };
      onChange([...items, newItem]);
      form.reset({
        receiptItemCode: "",
        description: "",
        quantity: 1,
        unitPrice: 0,
        category: "",
        subcategory: "",
      });
      setShowSuggestions(false);
      setShowItemCodeSuggestions(false);
      // Return focus to the item code field for the next entry.
      setTimeout(() => itemCodeInputRef.current?.focus(), 0);
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
    quantity: number;
    unitPrice: number;
    category: string;
    subcategory: string;
  }>({
    description: "",
    quantity: 1,
    unitPrice: 0,
    category: "",
    subcategory: "",
  });

  // Subcategory options for the row currently being edited.
  const editCategoryObj = useMemo(
    () =>
      (
        (categories as { id: string; name: string }[] | undefined) ?? []
      ).find((c) => c.name === editDraft.category),
    [categories, editDraft.category],
  );

  const { data: editSubcategories } = useAllSubcategoriesByCategoryId(
    editCategoryObj?.id ?? null,
    true,
  );

  const editSubcategoryOptions = useMemo(
    () =>
      (
        (editSubcategories as { id: string; name: string }[] | undefined) ?? []
      ).map((s) => ({ value: s.name, label: s.name })),
    [editSubcategories],
  );

  const startEditing = useCallback((item: ReceiptLineItem) => {
    setEditingItemId(item.id);
    setEditDraft({
      description: item.description,
      quantity: item.quantity,
      unitPrice: item.unitPrice,
      category: item.category,
      subcategory: item.subcategory,
    });
  }, []);

  const cancelEditing = useCallback(() => {
    setEditingItemId(null);
  }, []);

  const saveEditing = useCallback(() => {
    if (!editingItemId) return;
    if (!editDraft.description.trim()) return;
    if (!editDraft.category.trim()) return;
    if (!Number.isFinite(editDraft.quantity) || editDraft.quantity <= 0) return;
    if (!Number.isFinite(editDraft.unitPrice) || editDraft.unitPrice < 0) return;

    onChange(
      items.map((item) =>
        item.id === editingItemId
          ? {
              ...item,
              description: editDraft.description.trim(),
              quantity: editDraft.quantity,
              unitPrice: editDraft.unitPrice,
              category: editDraft.category,
              subcategory: editDraft.subcategory,
            }
          : item,
      ),
    );
    setEditingItemId(null);
  }, [editingItemId, editDraft, items, onChange]);

  const handleEditKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === "Enter") {
        e.preventDefault();
        saveEditing();
      }
    },
    [saveEditing],
  );

  // Clear stale editing state when the edited item is removed externally
  useEffect(() => {
    if (editingItemId && !items.some((i) => i.id === editingItemId)) {
      setEditingItemId(null);
    }
  }, [items, editingItemId]);

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
            {/* Row 1: Item Code, Description, Category */}
            <div className="flex flex-wrap gap-4">
              <FormField
                control={form.control}
                name="receiptItemCode"
                render={({ field }) => (
                  <FormItem className="min-w-[180px] flex-1">
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
                              ref={(el) => {
                                field.ref(el);
                                itemCodeInputRef.current = el;
                              }}
                              onChange={(e) => {
                                field.onChange(e);
                                // Re-open the lookup on every keystroke
                                // (including backspace) so the search keeps
                                // firing without needing to blur and refocus.
                                setShowItemCodeSuggestions(true);
                              }}
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
                        <Command
                          shouldFilter={false}
                          value={`idx-${itemCodeActiveIndex}`}
                          onValueChange={(v) => {
                            const n = Number(v.replace("idx-", ""));
                            if (!Number.isNaN(n)) setItemCodeActiveIndex(n);
                          }}
                        >
                          <CommandList id={itemCodeSuggestionsListId}>
                            <CommandEmpty>No suggestions found</CommandEmpty>
                            {itemCodeSuggestions?.map((suggestion, index) => (
                              <CommandItem
                                key={`${suggestion.itemCode}-${suggestion.matchType}`}
                                value={`idx-${index}`}
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
                  <FormItem className="min-w-[200px] flex-[2]">
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
                              onChange={(e) => {
                                field.onChange(e);
                                setShowSuggestions(true);
                              }}
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
                        <Command
                          shouldFilter={false}
                          value={`idx-${descriptionActiveIndex}`}
                          onValueChange={(v) => {
                            const n = Number(v.replace("idx-", ""));
                            if (!Number.isNaN(n)) setDescriptionActiveIndex(n);
                          }}
                        >
                          <CommandList id={suggestionsListId}>
                            <CommandEmpty>No similar items found</CommandEmpty>
                            {similarItems?.map((item, index) => (
                              <CommandItem
                                key={`${item.name}-${item.source}`}
                                value={`idx-${index}`}
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
                                    {item.source === "history" ? (
                                      <Button
                                        type="button"
                                        variant="ghost"
                                        size="icon"
                                        className="h-6 w-6 shrink-0"
                                        aria-label={`Save "${item.name}" as template`}
                                        onPointerDown={(e) => {
                                          // Keep the press from stealing focus
                                          // or registering on the cmdk item.
                                          e.preventDefault();
                                          e.stopPropagation();
                                        }}
                                        onClick={(e) => {
                                          // Must not select the suggestion or
                                          // close the popover.
                                          e.preventDefault();
                                          e.stopPropagation();
                                          promoteSuggestion(item);
                                        }}
                                      >
                                        <BookmarkPlus
                                          className="h-3.5 w-3.5"
                                          aria-hidden="true"
                                        />
                                      </Button>
                                    ) : (
                                      // Placeholder keeps badge/score columns
                                      // aligned across rows (no layout shift).
                                      <span
                                        className="h-6 w-6 shrink-0"
                                        aria-hidden="true"
                                      />
                                    )}
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
                  <FormItem className="min-w-[200px] flex-1">
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

            {/* Row 2: Subcategory, Quantity, Unit Price, Add Item */}
            <div className="flex flex-wrap items-end gap-4">
              <FormField
                control={form.control}
                name="subcategory"
                render={({ field }) => (
                  <FormItem className="min-w-[200px] flex-1">
                    <FormLabel>Subcategory (optional)</FormLabel>
                    <FormControl>
                      <Combobox
                        options={subcategoryOptions}
                        value={field.value ?? ""}
                        onValueChange={(v: string) =>
                          handleSubcategorySelect(
                            v,
                            selectedCategoryObj?.id,
                            subcategoryOptions,
                            field.onChange,
                          )
                        }
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
                name="quantity"
                render={({ field }) => (
                  <FormItem className="min-w-[110px]">
                    <FormLabel required>Qty</FormLabel>
                    <FormControl>
                      <DecimalInput
                        className="w-full"
                        name={field.name}
                        value={field.value}
                        onChange={field.onChange}
                        onBlur={field.onBlur}
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
                  <FormItem className="min-w-[150px]">
                    <FormLabel required>Unit Price</FormLabel>
                    <FormControl>
                      <CurrencyInput {...field} />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />

              <div className="mb-0.5 flex justify-end">
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
                <TableHead className="min-w-[16ch]">Description</TableHead>
                <TableHead>Qty</TableHead>
                <TableHead>Unit Price</TableHead>
                <TableHead>Line Total</TableHead>
                <TableHead>Category</TableHead>
                <TableHead className="w-12">
                  <span className="sr-only">Actions</span>
                </TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {items.map((item) =>
                editingItemId === item.id ? (
                  <TableRow key={item.id}>
                    <TableCell className="max-w-[32ch]">
                      <Input
                        value={editDraft.description}
                        onChange={(e) =>
                          setEditDraft((d) => ({
                            ...d,
                            description: e.target.value,
                          }))
                        }
                        onKeyDown={handleEditKeyDown}
                        aria-label="Edit description"
                        className="h-8"
                      />
                    </TableCell>
                    <TableCell>
                      <DecimalInput
                        value={editDraft.quantity}
                        onChange={(v) =>
                          setEditDraft((d) => ({ ...d, quantity: v }))
                        }
                        onKeyDown={handleEditKeyDown}
                        aria-label="Edit quantity"
                        className="h-8 w-20"
                      />
                    </TableCell>
                    <TableCell>
                      <CurrencyInput
                        value={editDraft.unitPrice}
                        onChange={(v) =>
                          setEditDraft((d) => ({ ...d, unitPrice: v }))
                        }
                        onKeyDown={handleEditKeyDown}
                        aria-label="Edit unit price"
                        className="h-8"
                      />
                    </TableCell>
                    <TableCell>
                      {formatCurrency(editDraft.quantity * editDraft.unitPrice)}
                    </TableCell>
                    <TableCell>
                      <div className="flex min-w-[12rem] flex-col gap-1">
                        <Combobox
                          options={categoryOptions}
                          value={editDraft.category}
                          onValueChange={(v) =>
                            setEditDraft((d) => ({
                              ...d,
                              category: v,
                              subcategory: "",
                            }))
                          }
                          placeholder="Category..."
                          searchPlaceholder="Search categories..."
                          emptyMessage="No categories found."
                          className="h-8"
                          aria-label="Edit category"
                        />
                        <Combobox
                          options={editSubcategoryOptions}
                          value={editDraft.subcategory}
                          onValueChange={(v) =>
                            handleSubcategorySelect(
                              v,
                              editCategoryObj?.id,
                              editSubcategoryOptions,
                              (next) =>
                                setEditDraft((d) => ({
                                  ...d,
                                  subcategory: next,
                                })),
                            )
                          }
                          placeholder="Subcategory..."
                          searchPlaceholder="Search subcategories..."
                          emptyMessage="No subcategories found."
                          allowCustom
                          disabled={!editDraft.category}
                          className="h-8"
                          aria-label="Edit subcategory"
                        />
                      </div>
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
                    <TableCell className="whitespace-normal break-words max-w-[32ch]">
                      {item.description}
                    </TableCell>
                    <TableCell>{item.quantity}</TableCell>
                    <TableCell>{formatCurrency(item.unitPrice)}</TableCell>
                    <TableCell>
                      {formatCurrency(item.quantity * item.unitPrice)}
                    </TableCell>
                    <TableCell>
                      {item.category}
                      {item.subcategory ? ` / ${item.subcategory}` : ""}
                    </TableCell>
                    <TableCell>
                      <div className="flex gap-1">
                        <Button
                          variant="ghost"
                          size="icon"
                          onClick={() => promoteLineItem(item)}
                          aria-label="Save as template"
                        >
                          <BookmarkPlus className="h-4 w-4" />
                        </Button>
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
                ),
              )}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </Card>
  );
}
