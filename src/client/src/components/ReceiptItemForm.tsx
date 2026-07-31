import { useState, useMemo, useRef, useCallback, useEffect } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod/v4";
import { zodResolver } from "@hookform/resolvers/zod";
import Fuse from "fuse.js";
import { useFormShortcuts } from "@/hooks/useFormShortcuts";
import { useFieldHistory } from "@/hooks/useFieldHistory";
import { useReceipts } from "@/hooks/useReceipts";
import { useAllCategories } from "@/hooks/useCategories";
import {
  useAllSubcategoriesByCategoryId,
  useCreateSubcategory,
} from "@/hooks/useSubcategories";
import { useItemTemplates } from "@/hooks/useItemTemplates";
import { useReceiptItemSuggestions } from "@/hooks/useReceiptItemSuggestions";
import {
  itemDescriptionHistory,
  itemCodeHistory,
} from "@/lib/field-history";
import { receiptToOption } from "@/lib/combobox-options";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Combobox } from "@/components/ui/combobox";
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
import { formatCurrency } from "@/lib/format";
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

const receiptItemSchema = z.object({
  receiptId: z.string().min(1, "Receipt is required"),
  receiptItemCode: z.string().min(1, "Item code is required"),
  description: z.string().min(1, "Description is required"),
  quantity: z.number().positive("Quantity must be positive"),
  unitPrice: z.number().min(0, "Unit price must be non-negative"),
  category: z.string().min(1, "Category is required"),
  subcategory: z.string().min(1, "Subcategory is required"),
});

type ReceiptItemSchemaValues = z.output<typeof receiptItemSchema>;

export type ReceiptItemFormValues = ReceiptItemSchemaValues;

interface ReceiptItemFormProps {
  mode: "create" | "edit";
  defaultValues?: Partial<ReceiptItemFormValues>;
  onSubmit: (values: ReceiptItemFormValues) => void;
  onCancel: () => void;
  isSubmitting?: boolean;
  serverErrors?: Record<string, string>;
  hideReceiptField?: boolean;
  location?: string | null;
}

export function ReceiptItemForm({
  mode,
  defaultValues,
  onSubmit,
  onCancel,
  isSubmitting,
  serverErrors,
  hideReceiptField,
  location,
}: ReceiptItemFormProps) {
  const formRef = useRef<HTMLFormElement>(null);
  useFormShortcuts({ formRef });
  const { options: descriptionHistoryOptions, add: addDescriptionHistory } =
    useFieldHistory(itemDescriptionHistory);
  const { options: itemCodeOptions, add: addItemCodeHistory } =
    useFieldHistory(itemCodeHistory);

  const { data: receipts, isLoading: receiptsLoading } = useReceipts();
  const { data: categories } = useAllCategories(true);
  const { data: itemTemplatesData } = useItemTemplates();
  const templates = useMemo(
    () => (itemTemplatesData as ItemTemplate[] | undefined) ?? [],
    [itemTemplatesData],
  );

  const receiptOptions = useMemo(
    () =>
      (
        (receipts as
          | {
              id: string;
              location: string;
              date: string;
            }[]
          | undefined) ?? []
      ).map(receiptToOption),
    [receipts],
  );

  const categoryOptions = useMemo(
    () =>
      (
        (categories as { id: string; name: string; isActive: boolean }[] | undefined) ?? []
      )
        .filter((c) => c.isActive)
        .map((c) => ({
          value: c.name,
          label: c.name,
        })),
    [categories],
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
    (Object.entries(serverErrors) as [keyof ReceiptItemSchemaValues, string][]).forEach(
      ([field, message]) => {
        form.setError(field, { type: "server", message });
      },
    );
  }, [serverErrors, form]);

  // eslint-disable-next-line react-hooks/incompatible-library
  const watchedCategory = form.watch("category");

  const selectedCategoryId = useMemo(() => {
    if (!categories || !watchedCategory) return null;
    return categories?.find((c) => c.name === watchedCategory)?.id ?? null;
  }, [categories, watchedCategory]);

  const { data: subcategoriesData } =
    useAllSubcategoriesByCategoryId(selectedCategoryId, true);
  const createSubcategory = useCreateSubcategory();

  const subcategoryOptions = useMemo(
    () =>
      (
        (subcategoriesData as { id: string; name: string; isActive: boolean }[] | undefined) ?? []
      )
        .filter((s) => s.isActive)
        .map((s) => ({
          value: s.name,
          label: s.name,
        })),
    [subcategoriesData],
  );

  const handleCategoryChange = useCallback(
    (value: string) => {
      form.setValue("category", value, { shouldValidate: true });
      form.setValue("subcategory", "");
    },
    [form],
  );

  const watchedQuantity = form.watch("quantity");
  const watchedUnitPrice = form.watch("unitPrice");

  // Resolve location: use prop if provided, otherwise derive from selected receipt
  const watchedReceiptId = form.watch("receiptId");
  const resolvedLocation = useMemo(() => {
    if (location) return location;
    if (!watchedReceiptId || !receipts) return null;
    const receipt = (receipts as { id: string; location: string }[])?.find(
      (r) => r.id === watchedReceiptId,
    );
    return receipt?.location ?? null;
  }, [location, watchedReceiptId, receipts]);

  // Item code autocomplete state
  const [itemCodeInput, setItemCodeInput] = useState(
    defaultValues?.receiptItemCode ?? "",
  );
  const [itemCodeAutocompleteOpen, setItemCodeAutocompleteOpen] = useState(false);
  const itemCodeListId = "item-code-autocomplete-list";

  const { data: itemCodeSuggestions, isFetching: isFetchingItemCodeSuggestions } =
    useReceiptItemSuggestions(itemCodeInput, resolvedLocation, {
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
        const firstItem = list?.querySelector("[cmdk-item]") as HTMLElement | null;
        firstItem?.focus();
      }
    },
    [isItemCodePopoverOpen, itemCodeListId],
  );

  function applySuggestion(suggestion: NonNullable<typeof itemCodeSuggestions>[number]) {
    form.setValue("receiptItemCode", suggestion.itemCode);
    setItemCodeInput(suggestion.itemCode);
    form.setValue("description", suggestion.description);
    setDescriptionInput(suggestion.description);
    form.setValue("category", suggestion.category);
    if (suggestion.subcategory) {
      form.setValue("subcategory", suggestion.subcategory);
    }
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

    const isCustomSubcategory =
      values.subcategory &&
      !subcategoryOptions.some((opt) => opt.value === values.subcategory);

    if (isCustomSubcategory && selectedCategoryId) {
      await createSubcategory.mutateAsync({
        name: values.subcategory,
        categoryId: selectedCategoryId,
        isActive: true,
      });
    }

    onSubmit(values);
  }

  const computedTotal = (watchedQuantity ?? 0) * (watchedUnitPrice ?? 0);

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
        const firstItem = list?.querySelector("[cmdk-item]") as HTMLElement | null;
        firstItem?.focus();
      }
    },
    [isDescriptionPopoverOpen, descriptionListId],
  );

  function applyTemplate(template: ItemTemplate) {
    form.setValue("description", template.name);
    setDescriptionInput(template.name);
    if (template.defaultCategory) {
      form.setValue("category", template.defaultCategory);
    }
    if (template.defaultSubcategory) {
      form.setValue("subcategory", template.defaultSubcategory);
    }
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
                  <Combobox
                    options={receiptOptions}
                    value={field.value}
                    onValueChange={field.onChange}
                    placeholder="Select a receipt..."
                    searchPlaceholder="Search receipts..."
                    emptyMessage="No receipts found."
                    disabled={mode === "edit"}
                    loading={receiptsLoading}
                    aria-required="true"
                  />
                </FormControl>
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
                          if (itemCodeInput.length >= 1 || itemCodeHistoryMatches.length > 0) {
                            setItemCodeAutocompleteOpen(true);
                          }
                        }}
                        onKeyDown={handleItemCodeKeyDown}
                        autoComplete="off"
                      />
                      {isFetchingItemCodeSuggestions && itemCodeInput.length >= 1 && (
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
                      {itemCodeSuggestions && itemCodeSuggestions.length > 0 && (
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
                        </CommandGroup>
                      )}
                    </CommandList>
                  </Command>
                </PopoverContent>
              </Popover>
              <FormMessage />
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
                        setAutocompleteOpen(true);
                      }}
                      onFocus={() => {
                        if (descriptionInput.length >= 2 || descriptionHistoryMatches.length > 0) {
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
                      <CommandEmpty>No suggestions found.</CommandEmpty>
                      {descriptionHistoryMatches.length > 0 && (
                        <CommandGroup heading="Recent Descriptions">
                          {descriptionHistoryMatches.slice(0, 5).map((opt) => (
                            <CommandItem
                              key={`history-${opt.value}`}
                              value={`history: ${opt.label}`}
                              onSelect={() => {
                                setDescriptionInput(opt.value);
                                field.onChange(opt.value);
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
                <FormLabel required>Category</FormLabel>
                <FormControl>
                  <Combobox
                    options={categoryOptions}
                    value={field.value}
                    onValueChange={handleCategoryChange}
                    placeholder="Select category..."
                    searchPlaceholder="Search categories..."
                  />
                </FormControl>
                <FormMessage />
              </FormItem>
            )}
          />

          <FormField
            control={form.control}
            name="subcategory"
            render={({ field }) => (
              <FormItem className="min-w-[200px] flex-1">
                <FormLabel required>Subcategory</FormLabel>
                <FormControl>
                  <Combobox
                    options={subcategoryOptions}
                    value={field.value}
                    onValueChange={field.onChange}
                    placeholder="Select subcategory..."
                    searchPlaceholder="Search subcategories..."
                    allowCustom
                    disabled={!watchedCategory}
                  />
                </FormControl>
                <FormMessage />
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
                  <CurrencyInput {...field} />
                </FormControl>
                <FormMessage />
              </FormItem>
            )}
          />
        </div>

        <div className="text-sm text-muted-foreground">
          Total: {formatCurrency(computedTotal)}
        </div>

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
