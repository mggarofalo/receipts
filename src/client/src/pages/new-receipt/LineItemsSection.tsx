import { CategoryPathPicker } from "@/components/categories/CategoryPathPicker";
import { useCategoryTaxonomy } from "@/hooks/useCategoryTaxonomy";
import { usePermission } from "@/hooks/usePermission";
import {
  useEnvelopes,
  useItemTypeEnvelopeDefaults,
} from "@/hooks/useEnvelopes";
import {
  EnvelopeBadge,
  EnvelopeOverrideMenu,
} from "@/components/envelopes/BudgetChoices";
import { previewItemEnvelope, receiptEnvelope } from "@/lib/envelope-display";
import {
  categoryLegacyLabels,
  type CategoryChoice,
} from "@/lib/category-paths";
import { receiptItemDraftSchema } from "@/lib/receipt-item-draft";
import { ReceiptNormalizationField } from "@/components/ReceiptNormalizationField";
import {
  currentNormalizationChoice,
  inlineProductSuggestions,
  normalizationSourceKey,
  receiptNormalizationSource,
  type ReceiptNormalizationChoice,
} from "@/lib/receipt-normalization";
import type { Dispatch, SetStateAction } from "react";
import { RequestFailure } from "@/components/RequestFailure";
import {
  calculateSubtotal,
  calculateLineTotal,
} from "@/lib/receipt-arithmetic";
import { useState, useMemo, useCallback, useRef, useEffect, useLayoutEffect } from "react";
import { generateId } from "@/lib/id";
import { useForm } from "react-hook-form";
import { z } from "zod/v4";
import { zodResolver } from "@hookform/resolvers/zod";
import { useSimilarItems } from "@/hooks/useSimilarItems";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { useReceiptNormalizationCandidates } from "@/hooks/useReceiptNormalizationCandidates";
import { useReceiptNormalizationPreview } from "@/hooks/useReceiptNormalizationPreview";
import { useReceiptItemSuggestions } from "@/hooks/useReceiptItemSuggestions";
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
import { Plus, Trash2, Loader2, Pencil, Check, X } from "lucide-react";

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
  /** Per-item envelope override; absent is automatic. */
  envelopeId?: string | null;
}

interface LineItemsSectionProps {
  items: ReceiptLineItem[];
  onChange: Dispatch<SetStateAction<ReceiptLineItem[]>>;
  location?: string | null;
  paidFromEnvelopeId?: string | null;
}

export function LineItemsSection({
  items,
  onChange,
  location,
  paidFromEnvelopeId = null,
}: LineItemsSectionProps) {
  const [draftRowId, setDraftRowId] = useState(generateId);
  const [normalizationChoice, setNormalizationChoice] =
    useState<ReceiptNormalizationChoice>();
  const [showSuggestions, setShowSuggestions] = useState(false);
  const [descriptionList, setDescriptionList] = useState<HTMLDivElement | null>(null);
  const [itemCodeList, setItemCodeList] = useState<HTMLDivElement | null>(null);
  const [descriptionAria, setDescriptionAria] = useState<{ list: string; active?: string }>();
  const [itemCodeAria, setItemCodeAria] = useState<{ list: string; active?: string }>();
  // Index of the keyboard-highlighted row in the description lookup.
  const [descriptionActiveIndex, setDescriptionActiveIndex] = useState(0);
  const taxonomy = useCategoryTaxonomy();
  const { isAdmin } = usePermission();
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
    !itemCodeDebouncing && !!itemCodeSuggestions && itemCodeSuggestions.length > 0;
  const hasNoItemCodeResultsMessage =
    (itemCode ?? "").length >= 1 &&
    !itemCodeDebouncing &&
    !isFetchingItemCodeSuggestions &&
    !itemCodeSuggestionsError &&
    !!itemCodeSuggestions &&
    itemCodeSuggestions.length === 0;
  const isItemCodeSuggestionsOpen =
    showItemCodeSuggestions &&
    (hasItemCodeResults || hasNoItemCodeResultsMessage);

  useLayoutEffect(() => {
    const list = isItemCodeSuggestionsOpen ? itemCodeList : null;
    const active = list?.querySelectorAll<HTMLElement>('[role="option"]')[itemCodeActiveIndex]?.id;
    setItemCodeAria(previous => {
      if (!list?.id) return previous === undefined ? previous : undefined;
      return previous?.list === list.id && previous.active === active ? previous : { list: list.id, active };
    });
  }, [isItemCodeSuggestionsOpen, itemCodeList, itemCodeActiveIndex, itemCodeSuggestions]);

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
      if (count === 0) return;
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

  const productSource = useMemo(
    () =>
      receiptNormalizationSource(location, {
        description,
        receiptItemCode: itemCode,
      }),
    [location, description, itemCode],
  );
  const productSourceKey = normalizationSourceKey(productSource);
  const settledProductSourceKey = useDebouncedValue(productSourceKey);
  const productEnabled =
    showSuggestions &&
    productSourceKey === settledProductSourceKey &&
    !!productSource.location.trim() &&
    !!productSource.description.trim();
  const productPreview = useReceiptNormalizationPreview(productSource);
  const productLookup = useReceiptNormalizationCandidates(productSource, {
    enabled: productEnabled,
  });
  const productReady =
    productEnabled &&
    productPreview.isSuccess &&
    !!productPreview.data?.sourceFingerprint &&
    productLookup.isSuccess &&
    !productPreview.isFetching &&
    !productPreview.isDebouncing &&
    !productLookup.isFetching;
  const productSuggestions = useMemo(
    () =>
      productReady && !normalizationChoice
        ? inlineProductSuggestions(productLookup.data ?? [])
        : [],
    [productReady, normalizationChoice, productLookup.data],
  );
  const descriptionSuggestions = useMemo(() => {
    if (similarItemsDebouncing) return [];
    const productNames = new Set(productSuggestions.flatMap(item =>
      [item.canonicalName, item.displayLabel].filter((name): name is string => !!name)
        .map(name => name.trim().toLocaleLowerCase())));
    return (similarItems ?? [])
      .filter((item) => !productNames.has(item.name.trim().toLocaleLowerCase()))
      .slice(0, Math.max(0, 5 - productSuggestions.length));
  }, [similarItems, similarItemsDebouncing, productSuggestions]);

  // Results belong to the debounced query, not necessarily the current text.
  const hasResults =
    productSuggestions.length + descriptionSuggestions.length > 0;
  const hasNoResultsMessage =
    description.length >= 2 &&
    !similarItemsDebouncing &&
    !isFetchingSimilar &&
    !similarItemsError &&
    !!similarItems &&
    !hasResults &&
    (!productEnabled ||
      (!productLookup.isPending &&
        !productLookup.isFetching &&
        !productPreview.isFetching &&
        !productPreview.isDebouncing));
  const isSuggestionsOpen =
    showSuggestions && (hasResults || hasNoResultsMessage);

  useLayoutEffect(() => {
    const list = isSuggestionsOpen ? descriptionList : null;
    const active = list?.querySelectorAll<HTMLElement>('[role="option"]')[descriptionActiveIndex]?.id;
    setDescriptionAria(previous => {
      if (!list?.id) return previous === undefined ? previous : undefined;
      return previous?.list === list.id && previous.active === active ? previous : { list: list.id, active };
    });
  }, [isSuggestionsOpen, descriptionList, descriptionActiveIndex, productSuggestions, descriptionSuggestions]);

  const subtotal = useMemo(() => calculateSubtotal(items), [items]);

  const { envelopes } = useEnvelopes();
  const { defaults } = useItemTypeEnvelopeDefaults();
  const itemEnvelopes = useMemo(
    () =>
      new Map(
        items.map((item) => [
          item.id,
          previewItemEnvelope(
            { envelopeId: item.envelopeId, typePath: item.categoryChoice?.path },
            paidFromEnvelopeId,
            defaults,
            envelopes,
          ),
        ]),
      ),
    [items, paidFromEnvelopeId, defaults, envelopes],
  );
  const paidFrom = receiptEnvelope(
    paidFromEnvelopeId,
    // An override counts as the envelope the item would get automatically.
    items.map((item) =>
      previewItemEnvelope(
        { typePath: item.categoryChoice?.path },
        null,
        defaults,
        envelopes,
      ),
    ),
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

  const applyProductSuggestion = useCallback(
    (suggestion: (typeof productSuggestions)[number]) => {
      if (!productReady || !productPreview.data) return;
      setNormalizationChoice({
        sourceKey: productSourceKey,
        label: suggestion.displayLabel ?? suggestion.canonicalName,
        intent: {
          kind: "confirmExisting",
          clientRowId: draftRowId,
          sourceFingerprint: productPreview.data.sourceFingerprint,
          targetId: suggestion.id,
          expectedTargetRevision: suggestion.revision,
          rememberSource: false,
        },
      });
      setShowSuggestions(false);
    },
    [draftRowId, productPreview.data, productReady, productSourceKey],
  );

  // Reset the highlight to the first row whenever the result set changes.
  useEffect(() => {
    setDescriptionActiveIndex(0);
  }, [similarItems, productSuggestions]);

  const handleDescriptionKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLInputElement>) => {
      if (e.key === "Escape") {
        setShowSuggestions(false);
        return;
      }
      if (!isSuggestionsOpen) return;
      const count = productSuggestions.length + descriptionSuggestions.length;
      if (count === 0) return;
      if (e.key === "ArrowDown") {
        e.preventDefault();
        setDescriptionActiveIndex((i) => Math.min(i + 1, count - 1));
      } else if (e.key === "ArrowUp") {
        e.preventDefault();
        setDescriptionActiveIndex((i) => Math.max(i - 1, 0));
      } else if (e.key === "Enter") {
        const product = productSuggestions[descriptionActiveIndex];
        const suggestion =
          descriptionSuggestions[
            descriptionActiveIndex - productSuggestions.length
          ];
        if (product || suggestion) {
          // Prevent the form from submitting — Enter picks the suggestion.
          e.preventDefault();
          if (product) applyProductSuggestion(product);
          else if (suggestion) applySuggestion(suggestion);
        }
      }
    },
    [
      isSuggestionsOpen,
      productSuggestions,
      descriptionSuggestions,
      descriptionActiveIndex,
      applySuggestion,
      applyProductSuggestion,
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
                              aria-controls={itemCodeAria?.list}
                              aria-activedescendant={itemCodeAria?.active}
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
                          <CommandList ref={setItemCodeList}>
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
                              aria-controls={descriptionAria?.list}
                              aria-activedescendant={descriptionAria?.active}
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
                          <CommandList ref={setDescriptionList}>
                            <p className="px-3 py-2 text-xs text-muted-foreground">
                              Matching descriptions
                            </p>
                            <CommandEmpty>No similar items found</CommandEmpty>
                            {productSuggestions.map((item, index) => (
                              <CommandItem
                                key={`product-${item.id}`}
                                value={`idx-${index}`}
                                onSelect={() => applyProductSuggestion(item)}
                              >
                                <div className="flex w-full items-center justify-between gap-2">
                                  <span className="font-medium">
                                    {item.displayLabel ?? item.canonicalName}
                                  </span>
                                  <Badge
                                    variant="outline"
                                    className="text-[10px] px-1.5 py-0"
                                  >
                                    Product
                                  </Badge>
                                </div>
                              </CommandItem>
                            ))}
                            {descriptionSuggestions.map((item, index) => (
                              <CommandItem
                                key={`${item.name}-${item.source}`}
                                value={`idx-${index + productSuggestions.length}`}
                                onSelect={() => applySuggestion(item)}
                              >
                                <div className="flex w-full items-center justify-between">
                                  <div className="flex flex-col gap-0.5">
                                    <span className="font-medium">
                                      {item.name}
                                    </span>
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
                                  </div>
                                </div>
                              </CommandItem>
                            ))}
                          </CommandList>
                        </Command>
                      </PopoverContent>
                    </Popover>
                    <FormMessage />
                    <div className="min-h-9">
                      <ReceiptNormalizationField
                        source={receiptNormalizationSource(location, {
                          description,
                          receiptItemCode: itemCode,
                        })}
                        clientRowId={draftRowId}
                        value={normalizationChoice}
                        onChange={setNormalizationChoice}
                      />
                    </div>
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
                        canCreate={isAdmin()}
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

              <div className="flex justify-end pb-7">
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
                          canCreate={isAdmin()}
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
                      <EnvelopeBadge
                        envelopes={envelopes}
                        envelopeId={itemEnvelopes.get(item.id)}
                        receiptEnvelopeId={paidFrom}
                      />
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
                        <EnvelopeOverrideMenu
                          envelopes={envelopes}
                          value={item.envelopeId ?? null}
                          itemLabel={item.description}
                          onChange={(envelopeId) =>
                            onChange((current) =>
                              current.map((row) =>
                                row.id === item.id ? { ...row, envelopeId } : row,
                              ),
                            )
                          }
                        />
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
