import { CategoryPathPicker } from "@/components/categories/CategoryPathPicker";
import { useCategoryTaxonomy } from "@/hooks/useCategoryTaxonomy";
import {
  categoryLegacyLabels,
  type CategoryChoice,
} from "@/lib/category-paths";
import { receiptItemDraftSchema } from "@/lib/receipt-item-draft";
import { ReceiptNormalizationField } from "@/components/ReceiptNormalizationField";
import {
  currentNormalizationChoice,
  receiptNormalizationSource,
  type ReceiptNormalizationChoice,
} from "@/lib/receipt-normalization";
import type { Dispatch, SetStateAction } from "react";
import { RequestFailure } from "@/components/RequestFailure";
import {
  calculateSubtotal,
  calculateLineTotal,
} from "@/lib/receipt-arithmetic";
import { useState, useMemo, useCallback, useRef, useEffect } from "react";
import { generateId } from "@/lib/id";
import { useForm } from "react-hook-form";
import { z } from "zod/v4";
import { zodResolver } from "@hookform/resolvers/zod";
import { useSimilarItems } from "@/hooks/useSimilarItems";
import { useReceiptItemSuggestions } from "@/hooks/useReceiptItemSuggestions";
import { usePromoteToTemplate } from "@/hooks/usePromoteToTemplate";
import { usePreferences } from "@/hooks/usePreferences";
import { formatUnitPrice, formatCurrency } from "@/lib/format";
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
  Pencil,
  Check,
  X,
  BookmarkPlus,
} from "lucide-react";

const itemSchema = receiptItemDraftSchema;

type ItemFormValues = z.output<typeof itemSchema>;

export interface ReceiptLineItem {
  id: string;
  receiptItemCode: string;
  description: string;
  quantity: number;
  unitPrice: number;
  category: string;
  subcategory: string;
  normalizationChoice?: ReceiptNormalizationChoice;
  categoryChoice?: CategoryChoice;
  categoryOperationId?: string;
}

interface LineItemsSectionProps {
  items: ReceiptLineItem[];
  onChange: Dispatch<SetStateAction<ReceiptLineItem[]>>;
  location?: string | null;
}

export function LineItemsSection({
  items,
  onChange,
  location,
}: LineItemsSectionProps) {
  const { preferences } = usePreferences();
  const [draftRowId, setDraftRowId] = useState(generateId);
  const [normalizationChoice, setNormalizationChoice] =
    useState<ReceiptNormalizationChoice>();
  const [showSuggestions, setShowSuggestions] = useState(false);
  const suggestionsListId = "new-receipt-suggestions-list";
  // Index of the keyboard-highlighted row in the description lookup.
  const [descriptionActiveIndex, setDescriptionActiveIndex] = useState(0);
  const taxonomy = useCategoryTaxonomy();
  const [categoryChoice, setCategoryChoice] = useState<CategoryChoice>();
  const [categoryOperationId, setCategoryOperationId] = useState(generateId);

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

  // Item code autocomplete
  const [showItemCodeSuggestions, setShowItemCodeSuggestions] = useState(false);
  const itemCodeSuggestionsListId = "new-receipt-item-code-suggestions-list";
  const itemCodeInputRef = useRef<HTMLInputElement>(null);
  // Index of the keyboard-highlighted row in the item code lookup.
  const [itemCodeActiveIndex, setItemCodeActiveIndex] = useState(0);

  const {
    data: itemCodeSuggestions,
    isFetching: isFetchingItemCodeSuggestions,
    isError: itemCodeSuggestionsError,
    isDebouncing: itemCodeDebouncing,
    refetch: retryItemCodeSuggestions,
  } = useReceiptItemSuggestions(itemCode ?? "", location, {
    enabled: showItemCodeSuggestions && (itemCode ?? "").length >= 1,
  });

  const hasItemCodeResults =
    !!itemCodeSuggestions && itemCodeSuggestions.length > 0;
  const hasNoItemCodeResultsMessage =
    (itemCode ?? "").length >= 1 &&
    !isFetchingItemCodeSuggestions &&
    !itemCodeSuggestionsError &&
    !!itemCodeSuggestions &&
    itemCodeSuggestions.length === 0;
  const isItemCodeSuggestionsOpen =
    showItemCodeSuggestions &&
    (hasItemCodeResults || hasNoItemCodeResultsMessage);

  const applyItemCodeSuggestion = useCallback(
    (suggestion: NonNullable<typeof itemCodeSuggestions>[number]) => {
      form.setValue("receiptItemCode", suggestion.itemCode);
      form.setValue("description", suggestion.description);
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

  const {
    data: similarItems,
    isFetching: isFetchingSimilar,
    isError: similarItemsError,
    isDebouncing: similarItemsDebouncing,
    refetch: retrySimilarItems,
  } = useSimilarItems(description, { enabled: showSuggestions });

  const hasResults = !!similarItems && similarItems.length > 0;
  const hasNoResultsMessage =
    description.length >= 2 &&
    !isFetchingSimilar &&
    !similarItemsError &&
    !!similarItems &&
    similarItems.length === 0;
  const isSuggestionsOpen =
    showSuggestions && (hasResults || hasNoResultsMessage);

  const subtotal = useMemo(() => calculateSubtotal(items), [items]);

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
          // Shift+Enter saves a highlighted history suggestion as a
          // template instead of applying it, mirroring the pointer-only
          // "Save as template" button for keyboard/screen-reader users.
          if (e.shiftKey && suggestion.source.toLowerCase() === "history") {
            promoteSuggestion(suggestion);
          } else {
            applySuggestion(suggestion);
          }
        }
      }
    },
    [
      isSuggestionsOpen,
      similarItems,
      descriptionActiveIndex,
      applySuggestion,
      promoteSuggestion,
    ],
  );

  const handleAdd = useCallback(
    (values: ItemFormValues) => {
      const newItem: ReceiptLineItem = {
        id: draftRowId,
        receiptItemCode: values.receiptItemCode ?? "",
        description: values.description,
        quantity: values.quantity,
        unitPrice: values.unitPrice,
        categoryChoice,
        categoryOperationId,
        category: values.category,
        subcategory: values.subcategory ?? "",
        normalizationChoice: currentNormalizationChoice(
          normalizationChoice,
          receiptNormalizationSource(location, values),
        ),
      };
      onChange([...items, newItem]);
      setDraftRowId(generateId());
      setNormalizationChoice(undefined);
      setCategoryChoice(undefined);
      setCategoryOperationId(generateId());
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
    [
      form,
      items,
      onChange,
      draftRowId,
      normalizationChoice,
      location,
      categoryChoice,
      categoryOperationId,
    ],
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
    categoryChoice?: CategoryChoice;
    categoryOperationId?: string;
  }>({
    description: "",
    quantity: 1,
    unitPrice: 0,
    category: "",
    subcategory: "",
  });

  const startEditing = useCallback((item: ReceiptLineItem) => {
    setEditingItemId(item.id);
    setEditDraft({
      description: item.description,
      quantity: item.quantity,
      unitPrice: item.unitPrice,
      categoryChoice: item.categoryChoice,
      categoryOperationId: item.categoryOperationId,
      category: item.category,
      subcategory: item.subcategory,
    });
  }, []);

  const cancelEditing = useCallback(() => {
    setEditingItemId(null);
  }, []);

  const saveEditing = useCallback(() => {
    if (!editingItemId) return;
    const parsed = itemSchema.safeParse(editDraft);
    if (!parsed.success) return;
    const draft = parsed.data;

    onChange(
      items.map((item) =>
        item.id === editingItemId
          ? {
              ...item,
              description: draft.description,
              quantity: draft.quantity,
              unitPrice: draft.unitPrice,
              categoryChoice: editDraft.categoryChoice,
              categoryOperationId: editDraft.categoryOperationId,
              category: draft.category,
              subcategory: draft.subcategory,
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
          <form onSubmit={form.handleSubmit(handleAdd)} className="space-y-4">
            {/* Row 1: Item Code, Description, Category */}
            <div className="flex flex-wrap items-start gap-4">
              <FormField
                control={form.control}
                name="receiptItemCode"
                render={({ field }) => (
                  <FormItem className="min-w-[12rem] flex-1">
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
                            {isFetchingItemCodeSuggestions &&
                              (itemCode ?? "").length >= 1 && (
                                <>
                                  <Loader2
                                    className="absolute right-2 top-1/2 h-4 w-4 -translate-y-1/2 animate-spin text-muted-foreground"
                                    aria-hidden="true"
                                  />
                                  <span className="sr-only" role="status">
                                    Loading suggestions...
                                  </span>
                                </>
                              )}
                          </div>
                        </FormControl>
                      </PopoverAnchor>
                      <PopoverContent
                        className="w-[--radix-popover-trigger-width] p-0"
                        align="start"
                        onOpenAutoFocus={(e) => e.preventDefault()}
                        onInteractOutside={() =>
                          setShowItemCodeSuggestions(false)
                        }
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
                                onSelect={() =>
                                  applyItemCodeSuggestion(suggestion)
                                }
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
                                        ? ` · ${formatUnitPrice(Number(suggestion.unitPrice))}`
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
                    {itemCodeSuggestionsError &&
                      (itemCode ?? "").length >= 1 &&
                      !itemCodeDebouncing && (
                        <RequestFailure
                          message="Item code suggestions unavailable. You can enter the item manually."
                          retry={() => {
                            void retryItemCodeSuggestions();
                          }}
                          isRetrying={isFetchingItemCodeSuggestions}
                        />
                      )}
                  </FormItem>
                )}
              />

              <FormField
                control={form.control}
                name="description"
                render={({ field }) => (
                  <FormItem className="w-full min-w-0 flex-none sm:w-auto sm:min-w-[16rem] sm:flex-[2]">
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
                              aria-describedby={
                                preferences.showKeyboardHints
                                  ? "description-shortcuts-hint"
                                  : undefined
                              }
                              autoComplete="off"
                              {...field}
                              onChange={(e) => {
                                field.onChange(e);
                                setShowSuggestions(true);
                              }}
                              onFocus={() => setShowSuggestions(true)}
                              onBlur={() => {
                                field.onBlur();
                                // While suggestions are mounted, Popover owns pointer
                                // interactions with its portalled content. Before they
                                // arrive there is no outside layer to clear this flag;
                                // release ownership when focus has already moved on so a
                                // late response cannot open over the next field.
                                if (!isSuggestionsOpen)
                                  setShowSuggestions(false);
                              }}
                              onKeyDown={handleDescriptionKeyDown}
                            />
                            {preferences.showKeyboardHints && (
                              <span
                                id="description-shortcuts-hint"
                                className="sr-only"
                              >
                                When a suggestion from your history is
                                highlighted, press Shift+Enter to save it as a
                                template.
                              </span>
                            )}
                            {isFetchingSimilar && description.length >= 2 && (
                              <>
                                <Loader2
                                  className="absolute right-2 top-1/2 h-4 w-4 -translate-y-1/2 animate-spin text-muted-foreground"
                                  aria-hidden="true"
                                />
                                <span className="sr-only" role="status">
                                  Loading suggestions...
                                </span>
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
                            <p className="px-3 py-2 text-xs text-muted-foreground">
                              Receipt history � raw descriptions
                            </p>
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
                                          ? ` · ${formatUnitPrice(Number(item.defaultUnitPrice))}`
                                          : ""}
                                      </span>
                                    )}
                                  </div>
                                  <div className="flex items-center gap-1.5">
                                    <Badge
                                      variant="outline"
                                      className="text-[10px] px-1.5 py-0"
                                    >
                                      {item.source.toLowerCase() === "template"
                                        ? "Template"
                                        : "History"}
                                    </Badge>
                                    <span className="text-[10px] text-muted-foreground">
                                      {Math.round(
                                        Number(item.combinedScore ?? 0) * 100,
                                      )}
                                      %
                                    </span>
                                    {item.source.toLowerCase() === "history" ? (
                                      <Button
                                        type="button"
                                        variant="ghost"
                                        size="icon"
                                        className="h-6 w-6 shrink-0"
                                        aria-label={`Save "${item.name}" as template`}
                                        disabled={isPromoting}
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
                    <ReceiptNormalizationField
                      source={receiptNormalizationSource(location, {
                        description,
                        receiptItemCode: itemCode,
                      })}
                      clientRowId={draftRowId}
                      value={normalizationChoice}
                      onChange={setNormalizationChoice}
                    />
                    {similarItemsError &&
                      description.length >= 2 &&
                      !similarItemsDebouncing && (
                        <RequestFailure
                          message="Description suggestions unavailable. You can enter the item manually."
                          retry={() => {
                            void retrySimilarItems();
                          }}
                          isRetrying={isFetchingSimilar}
                        />
                      )}
                  </FormItem>
                )}
              />

              <FormField
                control={form.control}
                name="category"
                render={({ field }) => (
                  <FormItem className="min-w-[12rem] flex-1">
                    <FormLabel>Category</FormLabel>
                    <FormControl>
                      <CategoryPathPicker
                        ref={field.ref}
                        onBlur={field.onBlur}
                        name={field.name}
                        revision={taxonomy.data}
                        value={categoryChoice}
                        loading={taxonomy.isLoading}
                        error={taxonomy.isError}
                        onValueChange={(choice) => {
                          setCategoryChoice(choice);
                          setCategoryOperationId(generateId());
                          const labels = categoryLegacyLabels(choice);
                          field.onChange(labels.category);
                          form.setValue(
                            "subcategory",
                            labels.subcategory ?? "",
                          );
                        }}
                      />
                    </FormControl>
                    <FormMessage />
                    {taxonomy.isError && (
                      <RequestFailure
                        message="Categories unavailable. Your draft is retained."
                        retry={() => {
                          void taxonomy.refetch();
                        }}
                        isRetrying={taxonomy.isFetching}
                      />
                    )}
                  </FormItem>
                )}
              />
            </div>

            <div className="flex flex-wrap items-end gap-3">
              <FormField
                control={form.control}
                name="quantity"
                render={({ field }) => (
                  <FormItem className="min-w-[5rem] flex-1">
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
                  <FormItem className="min-w-[8rem] flex-1">
                    <FormLabel required>Unit Price</FormLabel>
                    <FormControl>
                      <CurrencyInput {...field} precision={4} />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />

              <div className="flex justify-end sm:pt-[22px]">
                <Button
                  type="submit"
                  variant="secondary"
                  size="sm"
                  className="h-9"
                >
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
                        precision={4}
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
                      {formatCurrency(
                        calculateLineTotal(
                          editDraft.quantity,
                          editDraft.unitPrice,
                        ),
                      )}
                    </TableCell>
                    <TableCell>
                      <div className="flex min-w-[12rem] flex-col gap-1">
                        <CategoryPathPicker
                          aria-label="Edit category"
                          revision={taxonomy.data}
                          value={editDraft.categoryChoice}
                          loading={taxonomy.isLoading}
                          error={taxonomy.isError}
                          onValueChange={(choice) => {
                            const labels = categoryLegacyLabels(choice);
                            setEditDraft((d) => ({
                              ...d,
                              categoryChoice: choice,
                              categoryOperationId: generateId(),
                              category: labels.category,
                              subcategory: labels.subcategory ?? "",
                            }));
                          }}
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
                      <div>{item.description}</div>
                      <ReceiptNormalizationField
                        inlineSuggestions={false}
                        source={receiptNormalizationSource(location, item)}
                        clientRowId={item.id}
                        value={item.normalizationChoice}
                        onChange={(choice) =>
                          onChange((current) =>
                            current.map((row) =>
                              row.id === item.id
                                ? { ...row, normalizationChoice: choice }
                                : row,
                            ),
                          )
                        }
                      />
                    </TableCell>
                    <TableCell>{item.quantity}</TableCell>
                    <TableCell>{formatUnitPrice(item.unitPrice)}</TableCell>
                    <TableCell>
                      {formatCurrency(
                        calculateLineTotal(item.quantity, item.unitPrice),
                      )}
                    </TableCell>
                    <TableCell>
                      {item.categoryChoice
                        ? item.categoryChoice.path
                            .map((node) => node.name)
                            .join(" / ") || "Unclassified"
                        : `${item.category}${item.subcategory ? ` / ${item.subcategory}` : ""}`}
                    </TableCell>
                    <TableCell>
                      <div className="flex gap-1">
                        <Button
                          variant="ghost"
                          size="icon"
                          onClick={() => promoteLineItem(item)}
                          disabled={isPromoting}
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
