import { useRef, useEffect } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod/v4";
import { zodResolver } from "@hookform/resolvers/zod";
import { useFormShortcuts } from "@/hooks/useFormShortcuts";
import { useFieldHistory } from "@/hooks/useFieldHistory";
import { adjustmentDescriptionHistory } from "@/lib/field-history";
import { useEnumMetadata } from "@/hooks/useEnumMetadata";
import { Button } from "@/components/ui/button";
import { CurrencyInput } from "@/components/ui/currency-input";
import { Combobox } from "@/components/ui/combobox";
import {
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form";
import { Spinner } from "@/components/ui/spinner";

const adjustmentSchema = z
  .object({
    type: z.string().min(1, "Type is required"),
    amount: z
      .number({ error: "Amount is required" })
      .refine((value) => value !== 0, "Amount must be non-zero"),
    description: z.string().optional(),
  })
  .refine((data) => data.type.toLowerCase() !== "other" || (data.description && data.description.trim().length > 0), {
    message: "Description is required when type is 'other'",
    path: ["description"],
  });

export type AdjustmentFormValues = z.output<typeof adjustmentSchema>;

interface AdjustmentFormProps {
  mode: "create" | "edit";
  defaultValues?: Partial<AdjustmentFormValues>;
  onSubmit: (values: AdjustmentFormValues) => void;
  onCancel: () => void;
  isSubmitting?: boolean;
  serverErrors?: Record<string, string>;
}

export function AdjustmentForm({
  mode,
  defaultValues,
  onSubmit,
  onCancel,
  isSubmitting,
  serverErrors,
}: AdjustmentFormProps) {
  const formRef = useRef<HTMLFormElement>(null);
  useFormShortcuts({ formRef });
  const { adjustmentTypes } = useEnumMetadata();
  const { options: adjustmentDescOptions, add: addAdjustmentDesc } =
    useFieldHistory(adjustmentDescriptionHistory);

  const form = useForm<AdjustmentFormValues>({
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    resolver: zodResolver(adjustmentSchema) as any,
    defaultValues: {
      type: "",
      amount: 0,
      description: "",
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
    (Object.entries(serverErrors) as [keyof AdjustmentFormValues, string][]).forEach(
      ([field, message]) => {
        form.setError(field, { type: "server", message });
      },
    );
  }, [serverErrors, form]);

  // eslint-disable-next-line react-hooks/incompatible-library
  const watchedType = form.watch("type");

  return (
    <Form {...form}>
      <form
        ref={formRef}
        onSubmit={form.handleSubmit((values) => {
          if (values.type.toLowerCase() === "other" && values.description) {
            addAdjustmentDesc(values.description);
          }
          onSubmit(values);
        })}
        className="space-y-4"
      >
        <FormField
          control={form.control}
          name="type"
          render={({ field }) => (
            <FormItem>
              <FormLabel required>Type</FormLabel>
              <FormControl>
                <Combobox
                  options={adjustmentTypes}
                  value={field.value}
                  onValueChange={field.onChange}
                  placeholder="Select adjustment type..."
                  searchPlaceholder="Search types..."
                />
              </FormControl>
              <FormMessage />
            </FormItem>
          )}
        />

        <FormField
          control={form.control}
          name="amount"
          render={({ field }) => (
            <FormItem>
              <FormLabel required>Amount</FormLabel>
              <FormControl>
                <CurrencyInput {...field} />
              </FormControl>
              <FormMessage />
            </FormItem>
          )}
        />

        {watchedType.toLowerCase() === "other" && (
          <FormField
            control={form.control}
            name="description"
            render={({ field }) => (
              <FormItem>
                <FormLabel required>Description</FormLabel>
                <FormControl>
                  <Combobox
                    options={adjustmentDescOptions}
                    value={field.value ?? ""}
                    onValueChange={field.onChange}
                    placeholder="Describe this adjustment..."
                    searchPlaceholder="Search descriptions..."
                    emptyMessage="Type to describe this adjustment"
                    allowCustom
                    aria-required="true"
                  />
                </FormControl>
                <FormMessage />
              </FormItem>
            )}
          />
        )}

        <div className="flex justify-end gap-2 pt-4">
          <Button type="button" variant="outline" onClick={onCancel}>
            Cancel
          </Button>
          <Button type="submit" disabled={isSubmitting}>
            {isSubmitting && <Spinner size="sm" />}
            {isSubmitting
              ? "Saving..."
              : mode === "create"
                ? "Add Adjustment"
                : "Update Adjustment"}
          </Button>
        </div>
      </form>
    </Form>
  );
}
