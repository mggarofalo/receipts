import { CategoryPathPicker } from "@/components/categories/CategoryPathPicker";
import { useCategoryTaxonomy } from "@/hooks/useCategoryTaxonomy";
import {
  categoryLegacyLabels,
  classificationChange,
  type CategoryChoice,
  type CurrentClassification,
  type ClassificationRequest,
} from "@/lib/category-paths";
import { receiptItemDraftSchema } from "@/lib/receipt-item-draft";
import { ReceiptNormalizationField } from "@/components/ReceiptNormalizationField";
import {
  currentNormalizationChoice,
  receiptNormalizationSource,
  type ReceiptNormalizationChoice,
  type ReceiptNormalizationIntent,
} from "@/lib/receipt-normalization";
import { generateId } from "@/lib/id";
import { RequestFailure } from "@/components/RequestFailure";
import { calculateLineTotal } from "@/lib/receipt-arithmetic";
import { useState, useMemo, useRef, useCallback, useEffect } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod/v4";
import { zodResolver } from "@hookform/resolvers/zod";
import Fuse from "fuse.js";
import { useFormShortcuts } from "@/hooks/useFormShortcuts";
import { useFieldHistory } from "@/hooks/useFieldHistory";
import { useReceipt } from "@/hooks/useReceipts";
import { useItemTemplates } from "@/hooks/useItemTemplates";
import { useReceiptItemSuggestions } from "@/hooks/useReceiptItemSuggestions";
import { itemDescriptionHistory, itemCodeHistory } from "@/lib/field-history";
import { ReceiptPicker } from "@/components/ReceiptPicker";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { CurrencyInput } from "@/components/ui/currency-input";
import {
  Popover,
  PopoverContent,
  PopoverAnchor,
} from "@/components/ui/popover";
import {
  Command,
  CommandEmpty,
  CommandGroup,
  CommandItem,
  CommandList,
} from "@/components/ui/command";
import { Badge } from "@/components/ui/badge";
import {
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form";
import { Spinner } from "@/components/ui/spinner";
import { formatUnitPrice, formatCurrency } from "@/lib/format";
import { Loader2 } from "lucide-react";

interface ItemTemplate {
  id: string;
  name: string;
  description?: string | null;
  defaultCategory?: string | null;
  defaultSubcategory?: string | null;
  defaultUnitPrice?: number | null;
  defaultUnitPriceCurrency?: string | null;
  defaultItemCode?: string | null;
}

const receiptItemSchema = receiptItemDraftSchema.extend({
  receiptId: z.string().min(1, "Receipt is required"),
  receiptItemCode: z.string().trim().min(1, "Item code is required"),
  // Provenance, not user input — there is no control bound to it (RECEIPTS-881). Set when a
  // template is applied and cleared the moment the description stops matching that template's
  // name, so the server never stamps an item with a canonical entry the text no longer means.
  itemTemplateId: z.string().optional(),
});

type ReceiptItemSchemaValues = z.output<typeof receiptItemSchema>;

export type ReceiptItemFormValues = ReceiptItemSchemaValues & {
  normalizationIntent?: ReceiptNormalizationIntent;
  classification?: ClassificationRequest;
};

interface ReceiptItemFormProps {
  mode: "create" | "edit";
  currentClassification?: CurrentClassification | null;
  defaultValues?: Partial<ReceiptItemFormValues>;
  onSubmit: (values: ReceiptItemFormValues) => void;
  onCancel: () => void;
  isSubmitting?: boolean;
  serverErrors?: Record<string, string>;
  hideReceiptField?: boolean;
  location?: string | null;
  normalizationRevision?: string;
  normalizationReviewState?: "verified" | "excluded" | "needsReview";
  normalizedDescriptionName?: string | null;
  normalizationSourceKey?: string;
}

export function ReceiptItemForm({
  mode,
  currentClassification,
  defaultValues,
  onSubmit,
  onCancel,
  isSubmitting,
  serverErrors,
  hideReceiptField,
  location,
  normalizationRevision,
  normalizationReviewState,
  normalizedDescriptionName,
  normalizationSourceKey: savedSourceKey,
}: ReceiptItemFormProps) {
  const formRef = useRef<HTMLFormElement>(null);
  const [clientRowId] = useState(generateId);
  const [normalizationChoice, setNormalizationChoice] =
    useState<ReceiptNormalizationChoice>();
  useFormShortcuts({ formRef });
  const { options: descriptionHistoryOptions, add: addDescriptionHistory } =
    useFieldHistory(itemDescriptionHistory);
  const { options: itemCodeOptions, add: addItemCodeHistory } =
    useFieldHistory(itemCodeHistory);

  const taxonomy = useCategoryTaxonomy();
  const [initialClassification] = useState(currentClassification);
  const [categoryChoice, setCategoryChoice] = useState<
    CategoryChoice | undefined
  >(() =>
    initialClassification
      ? {
          ...initialClassification,
          categoryId: initialClassification.categoryId ?? null,
          path: initialClassification.recordedPath,
        }
      : undefined,
  );
  const [categoryOperationId, setCategoryOperationId] = useState(generateId);
  const templatesQuery = useItemTemplates();
  const { data: itemTemplatesData } = templatesQuery;
  const templates = useMemo(
    () => (itemTemplatesData as ItemTemplate[] | undefined) ?? [],
    [itemTemplatesData],
  );

  const form = useForm<ReceiptItemSchemaValues>({
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    resolver: zodResolver(receiptItemSchema) as any,
    defaultValues: {
      receiptId: "",
      receiptItemCode: "",
      description: "",
      quantity: 1,
      unitPrice: 0,
      category: "",
      subcategory: "",
      ...defaultValues,
    },
  });

  // Route server errors through react-hook-form so they flow through FormMessage
  // and are announced via aria-describedby (WCAG 3.3.1, 1.3.1).
  // Explicitly clear when serverErrors is absent/empty so stale errors don't linger
  // if the parent resets the prop to {} rather than null.
  useEffect(() => {
    if (!serverErrors || Object.keys(serverErrors).length === 0) {
      form.clearErrors();
      return;
    }
    (
      Object.entries(serverErrors) as [keyof ReceiptItemSchemaValues, string][]
    ).forEach(([field, message]) => {
      form.setError(field, { type: "server", message });
    });
  }, [serverErrors, form]);

  const watchedQuantity = form.watch("quantity");
  const normalizationDescription = form.watch("description");
  const normalizationCode = form.watch("receiptItemCode");
  const watchedUnitPrice = form.watch("unitPrice");

  // Resolve location: use prop if provided, otherwise derive from selected receipt
  const watchedReceiptId = form.watch("receiptId");
  const receiptQuery = useReceipt(watchedReceiptId || null, {
    enabled: !hideReceiptField,
  });
  const resolvedLocation = useMemo(() => {
    if (location) return location;
    if (!watchedReceiptId) return null;
    return receiptQuery.data?.location ?? null;
  }, [location, watchedReceiptId, receiptQuery.data]);

  // Item code autocomplete state
  const [itemCodeInput, setItemCodeInput] = useState(
    defaultValues?.receiptItemCode ?? "",
  );
  const [itemCodeAutocompleteOpen, setItemCodeAutocompleteOpen] =
    useState(false);
  const itemCodeListId = "item-code-autocomplete-list";

  const {
    data: itemCodeSuggestions,
    isFetching: isFetchingItemCodeSuggestions,
    isError: itemCodeSuggestionsError,
    isDebouncing: itemCodeDebouncing,
    refetch: retryItemCodeSuggestions,
  } = useReceiptItemSuggestions(itemCodeInput, resolvedLocation, {
    enabled: itemCodeAutocompleteOpen && itemCodeInput.length >= 1,
  });

  // Filter item code history entries that match the current input
  const itemCodeHistoryMatches = useMemo(() => {
    if (!itemCodeInput) return itemCodeOptions;
    const lower = itemCodeInput.toLowerCase();
    return itemCodeOptions.filter((opt) =>
      opt.label.toLowerCase().includes(lower),
    );
  }, [itemCodeOptions, itemCodeInput]);

  const isItemCodePopoverOpen =
    itemCodeAutocompleteOpen &&
    ((itemCodeSuggestions && itemCodeSuggestions.length > 0) ||
      itemCodeHistoryMatches.length > 0);

  const handleItemCodeKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLInputElement>) => {
      if (e.key === "Escape") {
        setItemCodeAutocompleteOpen(false);
      } else if (e.key === "ArrowDown" && isItemCodePopoverOpen) {
        e.preventDefault();
        const list = document.getElementById(itemCodeListId);
        const firstItem = list?.querySelector(
          "[cmdk-item]",
        ) as HTMLElement | null;
        firstItem?.focus();
      }
    },
    [isItemCodePopoverOpen, itemCodeListId],
  );

  function applySuggestion(
    suggestion: NonNullable<typeof itemCodeSuggestions>[number],
  ) {
    form.setValue("receiptItemCode", suggestion.itemCode);
    setItemCodeInput(suggestion.itemCode);
    form.setValue("description", suggestion.description);
    setDescriptionInput(suggestion.description);
    clearTemplateProvenance(suggestion.description);
    if (suggestion.unitPrice != null) {
      form.setValue("unitPrice", Number(suggestion.unitPrice));
    }
    setItemCodeAutocompleteOpen(false);
  }

  async function handleFormSubmit(values: ReceiptItemSchemaValues) {
    // Persist field values for future autocomplete before calling onSubmit.
    // Like location history, these are valid user input regardless of whether
    // the server mutation succeeds.
    addDescriptionHistory(values.description);
    addItemCodeHistory(values.receiptItemCode);

    onSubmit({
      ...values,
      classification: classificationChange(
        categoryChoice,
        initialClassification,
        categoryOperationId,
      ),
      normalizationIntent: currentNormalizationChoice(
        normalizationChoice,
        receiptNormalizationSource(resolvedLocation, values),
      )?.intent,
    });
  }

  const computedTotal = calculateLineTotal(
    watchedQuantity ?? 0,
    watchedUnitPrice ?? 0,
  );

  // Fuse.js autocomplete for description
  const fuse = useMemo(
    () =>
      new Fuse(templates, {
        keys: ["name", "description", "defaultItemCode"],
        threshold: 0.4,
        includeScore: true,
      }),
    [templates],
  );

  const [descriptionInput, setDescriptionInput] = useState(
    defaultValues?.description ?? "",
  );
  const [autocompleteOpen, setAutocompleteOpen] = useState(false);

  const suggestions = useMemo(() => {
    if (!descriptionInput || descriptionInput.length < 2) return [];
    return fuse.search(descriptionInput, { limit: 5 }).map((r) => r.item);
  }, [fuse, descriptionInput]);

  // Filter description history entries that match the current input
  const descriptionHistoryMatches = useMemo(() => {
    if (!descriptionInput) return descriptionHistoryOptions;
    const lower = descriptionInput.toLowerCase();
    return descriptionHistoryOptions.filter((opt) =>
      opt.label.toLowerCase().includes(lower),
    );
  }, [descriptionHistoryOptions, descriptionInput]);

  const descriptionListId = "description-autocomplete-list";

  const isDescriptionPopoverOpen =
    autocompleteOpen &&
    (suggestions.length > 0 || descriptionHistoryMatches.length > 0);

  const handleDescriptionKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLInputElement>) => {
      if (e.key === "Escape") {
        setAutocompleteOpen(false);
      } else if (e.key === "ArrowDown" && isDescriptionPopoverOpen) {
        e.preventDefault();
        // Move focus into the first item in the CommandList
        const list = document.getElementById(descriptionListId);
        const firstItem = list?.querySelector(
          "[cmdk-item]",
        ) as HTMLElement | null;
        firstItem?.focus();
      }
    },
    [isDescriptionPopoverOpen, descriptionListId],
  );

  /**
   * Drops the template link once the description no longer matches the template's name.
   *
   * Compared case-insensitively and trimmed, so incidental whitespace or capitalisation does not
   * throw away a link the user did not mean to break — but any real edit does. Erring towards
   * dropping is deliberate: an unstamped item just goes through the resolver as it always did,
   * whereas a wrongly-stamped one is silently misfiled in every report with nothing to reveal it.
   */
  function clearTemplateProvenance(nextDescription: string) {
    const templateId = form.getValues("itemTemplateId");
    if (!templateId) return;

    const source = templates.find((t) => t.id === templateId);
    const stillMatches =
      source != null &&
      source.name.trim().toLowerCase() === nextDescription.trim().toLowerCase();

    if (!stillMatches) {
      form.setValue("itemTemplateId", undefined);
    }
  }

  function applyTemplate(template: ItemTemplate) {
    // Recorded so the server can stamp the item with the template's canonical description and
    // skip the resolver (RECEIPTS-881). The user picking a template *is* the classification;
    // re-deriving it from an embedding search could disagree and park the line in the review
    // queue for a human to confirm what a human just said.
    form.setValue("itemTemplateId", template.id);
    form.setValue("description", template.name);
    setDescriptionInput(template.name);
    if (template.defaultUnitPrice != null) {
      form.setValue("unitPrice", template.defaultUnitPrice);
    }
    if (template.defaultItemCode) {
      form.setValue("receiptItemCode", template.defaultItemCode);
    }
    setAutocompleteOpen(false);
  }

  return (
    <Form {...form}>
      <form
        ref={formRef}
        onSubmit={form.handleSubmit(handleFormSubmit)}
        className="space-y-4"
      >
        {!hideReceiptField && (
          <FormField
            control={form.control}
            name="receiptId"
            render={({ field }) => (
              <FormItem>
                <FormLabel required>Receipt</FormLabel>
                <FormControl>
                  <ReceiptPicker
                    value={field.value}
                    selectedReceipt={receiptQuery.data}
                    onValueChange={field.onChange}
                    disabled={mode === "edit"}
                    aria-required="true"
                  />
                </FormControl>
                {receiptQuery.isError && (
                  <RequestFailure
                    message="The selected receipt is unavailable. Your item details have been kept."
                    retry={() => {
                      if (watchedReceiptId) void receiptQuery.refetch();
                    }}
                    isRetrying={receiptQuery.isFetching}
                  />
                )}
                <FormMessage />
              </FormItem>
            )}
          />
        )}

        <FormField
          control={form.control}
          name="receiptItemCode"
          render={({ field }) => (
            <FormItem>
              <FormLabel required>Item Code</FormLabel>
              <Popover
                open={isItemCodePopoverOpen}
                onOpenChange={setItemCodeAutocompleteOpen}
              >
                <PopoverAnchor asChild>
                  <FormControl>
                    <div className="relative">
                      <Input
                        role="combobox"
                        aria-required="true"
                        aria-autocomplete="list"
                        aria-expanded={isItemCodePopoverOpen}
                        aria-controls={itemCodeListId}
                        placeholder="Enter item code..."
                        {...field}
                        value={itemCodeInput}
                        onChange={(e) => {
                          const val = e.target.value;
                          setItemCodeInput(val);
                          field.onChange(val);
                          setItemCodeAutocompleteOpen(true);
                        }}
                        onFocus={() => {
                          if (
                            itemCodeInput.length >= 1 ||
                            itemCodeHistoryMatches.length > 0
                          ) {
                            setItemCodeAutocompleteOpen(true);
                          }
                        }}
                        onKeyDown={handleItemCodeKeyDown}
                        autoComplete="off"
                      />
                      {isFetchingItemCodeSuggestions &&
                        itemCodeInput.length >= 1 && (
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
                  onInteractOutside={() => setItemCodeAutocompleteOpen(false)}
                >
                  <Command>
                    <CommandList id={itemCodeListId}>
                      <CommandEmpty>No suggestions found.</CommandEmpty>
                      {itemCodeHistoryMatches.length > 0 && (
                        <CommandGroup heading="Recent Item Codes">
                          {itemCodeHistoryMatches.slice(0, 5).map((opt) => (
                            <CommandItem
                              key={`history-${opt.value}`}
                              value={`history: ${opt.label}`}
                              onSelect={() => {
                                setItemCodeInput(opt.value);
                                field.onChange(opt.value);
                                setItemCodeAutocompleteOpen(false);
                              }}
                            >
                              <span>{opt.label}</span>
                            </CommandItem>
                          ))}
                        </CommandGroup>
                      )}
                      {itemCodeSuggestions &&
                        itemCodeSuggestions.length > 0 && (
                          <CommandGroup heading="Suggestions">
                            {itemCodeSuggestions.map((suggestion) => (
                              <CommandItem
                                key={`${suggestion.itemCode}-${suggestion.matchType}`}
                                value={`${suggestion.itemCode} ${suggestion.description}`}
                                onSelect={() => applySuggestion(suggestion)}
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
                          </CommandGroup>
                        )}
                    </CommandList>
                  </Command>
                </PopoverContent>
              </Popover>
              <FormMessage />
              {itemCodeSuggestionsError &&
                itemCodeInput.length >= 1 &&
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

        {/* Description uses Input+Popover+Command instead of Combobox because it
            merges two heterogeneous option groups (recent history entries AND fuzzy-
            matched item templates) with grouped headings. The Combobox component only
            supports a single flat option list. Keeping a custom composite here avoids
            over-engineering Combobox with group support that nothing else needs. */}
        <FormField
          control={form.control}
          name="description"
          render={({ field }) => (
            <FormItem>
              <FormLabel required>Description</FormLabel>
              <Popover
                open={isDescriptionPopoverOpen}
                onOpenChange={setAutocompleteOpen}
              >
                <PopoverAnchor asChild>
                  <FormControl>
                    <Input
                      role="combobox"
                      aria-required="true"
                      aria-autocomplete="list"
                      aria-expanded={isDescriptionPopoverOpen}
                      aria-controls={descriptionListId}
                      {...field}
                      value={descriptionInput}
                      onChange={(e) => {
                        const val = e.target.value;
                        setDescriptionInput(val);
                        field.onChange(val);
                        // Typing over a template's name breaks the claim that this line came
                        // from that template, so the provenance goes with it (RECEIPTS-881).
                        // Otherwise "Gallon of Milk" edited to "Orange Juice" would still be
                        // stamped with the milk canonical entry and skip the resolver, and the
                        // spending report would file the juice under milk.
                        clearTemplateProvenance(val);
                        setAutocompleteOpen(true);
                      }}
                      onFocus={() => {
                        if (
                          descriptionInput.length >= 2 ||
                          descriptionHistoryMatches.length > 0
                        ) {
                          setAutocompleteOpen(true);
                        }
                      }}
                      onKeyDown={handleDescriptionKeyDown}
                      autoComplete="off"
                    />
                  </FormControl>
                </PopoverAnchor>
                <PopoverContent
                  className="w-[--radix-popover-trigger-width] p-0"
                  align="start"
                  onOpenAutoFocus={(e) => e.preventDefault()}
                  onInteractOutside={() => setAutocompleteOpen(false)}
                >
                  <Command>
                    <CommandList id={descriptionListId}>
                      <CommandEmpty>
                        {templatesQuery.isError
                          ? "Template suggestions unavailable."
                          : templatesQuery.isLoading
                            ? "Loading template suggestions…"
                            : "No suggestions found."}
                      </CommandEmpty>
                      {descriptionHistoryMatches.length > 0 && (
                        <CommandGroup heading="Recent Descriptions">
                          {descriptionHistoryMatches.slice(0, 5).map((opt) => (
                            <CommandItem
                              key={`history-${opt.value}`}
                              value={`history: ${opt.label}`}
                              onSelect={() => {
                                setDescriptionInput(opt.value);
                                field.onChange(opt.value);
                                clearTemplateProvenance(opt.value);
                                setAutocompleteOpen(false);
                              }}
                            >
                              <span>{opt.label}</span>
                            </CommandItem>
                          ))}
                        </CommandGroup>
                      )}
                      {suggestions.length > 0 && (
                        <CommandGroup heading="Item Templates">
                          {suggestions.map((template) => (
                            <CommandItem
                              key={template.id}
                              value={template.name}
                              onSelect={() => applyTemplate(template)}
                            >
                              <div className="flex flex-col">
                                <span className="font-medium">
                                  {template.name}
                                </span>
                                {template.defaultCategory && (
                                  <span className="text-xs text-muted-foreground">
                                    {template.defaultCategory}
                                    {template.defaultSubcategory
                                      ? ` / ${template.defaultSubcategory}`
                                      : ""}
                                  </span>
                                )}
                              </div>
                            </CommandItem>
                          ))}
                        </CommandGroup>
                      )}
                    </CommandList>
                  </Command>
                </PopoverContent>
              </Popover>
              <FormMessage />
              {templatesQuery.isError && (
                <RequestFailure
                  message="Template suggestions unavailable. You can enter the item manually."
                  retry={() => {
                    void templatesQuery.refetch();
                  }}
                  isRetrying={templatesQuery.isFetching}
                />
              )}
            </FormItem>
          )}
        />

        {/* flex-wrap + per-field min-w so fields wrap instead of colliding. */}
        <div className="flex flex-wrap gap-4">
          <FormField
            control={form.control}
            name="category"
            render={({ field }) => (
              <FormItem className="min-w-[200px] flex-1">
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
                    disabled={isSubmitting}
                    onValueChange={(choice) => {
                      setCategoryChoice(choice);
                      setCategoryOperationId(generateId());
                      const labels = categoryLegacyLabels(choice);
                      field.onChange(labels.category);
                      form.setValue("subcategory", labels.subcategory ?? "");
                    }}
                  />
                </FormControl>
                {!categoryChoice && field.value && (
                  <p className="text-sm text-muted-foreground">
                    Saved category: {field.value}
                    {form.getValues("subcategory")
                      ? ` / ${form.getValues("subcategory")}`
                      : ""}
                    . Choose a category to classify this item.
                  </p>
                )}
                <FormMessage />
                {taxonomy.isError && (
                  <RequestFailure
                    message="Categories unavailable. Your item and saved category are retained."
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

        <div className="flex flex-wrap gap-4">
          <FormField
            control={form.control}
            name="quantity"
            render={({ field }) => (
              <FormItem className="min-w-[110px] flex-1">
                <FormLabel>Quantity</FormLabel>
                <FormControl>
                  <Input
                    type="number"
                    step="1"
                    aria-required="true"
                    {...field}
                    onChange={(e) => field.onChange(e.target.valueAsNumber)}
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
              <FormItem className="min-w-[150px] flex-1">
                <FormLabel>Unit Price</FormLabel>
                <FormControl>
                  <CurrencyInput {...field} precision={4} />
                </FormControl>
                <FormMessage />
              </FormItem>
            )}
          />
        </div>

        <div className="text-sm text-muted-foreground">
          Total: {formatCurrency(computedTotal)}
        </div>

        <ReceiptNormalizationField
          source={receiptNormalizationSource(resolvedLocation, {
            description: normalizationDescription,
            receiptItemCode: normalizationCode,
          })}
          clientRowId={clientRowId}
          value={normalizationChoice}
          onChange={setNormalizationChoice}
          expectedItemRevision={normalizationRevision}
          currentAssignment={
            normalizationReviewState && savedSourceKey
              ? {
                  sourceKey: savedSourceKey,
                  state: normalizationReviewState,
                  name: normalizedDescriptionName,
                }
              : undefined
          }
          disabled={isSubmitting || (mode === "edit" && !normalizationRevision)}
        />
        <p className="text-sm text-muted-foreground">
          You can save this purchase even if its product still needs review.
          {mode === "edit" && !normalizationRevision
            ? " Refresh the saved receipt information before confirming a product."
            : ""}
        </p>

        <div className="flex justify-end gap-2 pt-4">
          <Button type="button" variant="outline" onClick={onCancel}>
            Cancel
          </Button>
          <Button type="submit" disabled={isSubmitting}>
            {isSubmitting && <Spinner size="sm" />}
            {isSubmitting
              ? "Saving..."
              : mode === "create"
                ? "Create Item"
                : "Update Item"}
          </Button>
        </div>
      </form>
    </Form>
  );
}
