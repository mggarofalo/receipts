import { useRef } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod/v4";
import { zodResolver } from "@hookform/resolvers/zod";
import { useFormShortcuts } from "@/hooks/useFormShortcuts";
import { useLocationHistory } from "@/hooks/useLocationHistory";
import { isFutureDate } from "@/lib/format";
import { Button } from "@/components/ui/button";
import { SubmitButton } from "@/components/ui/submit-button";
import { Combobox } from "@/components/ui/combobox";
import { DateInput } from "@/components/ui/date-input";
import { CurrencyInput } from "@/components/ui/currency-input";
import {
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form";

const receiptSchema = z.object({
  location: z
    .string()
    .min(1, "Location is required")
    .max(200, "Location must be 200 characters or fewer"),
  date: z
    .string()
    .min(1, "Date is required")
    .refine((v) => !isFutureDate(v), "Date cannot be in the future"),
  taxAmount: z.number().min(0, "Tax amount must be non-negative"),
});

type ReceiptFormValues = z.output<typeof receiptSchema>;

interface ReceiptFormProps {
  mode: "create" | "edit";
  defaultValues?: ReceiptFormValues;
  onSubmit: (values: ReceiptFormValues) => void;
  onCancel: () => void;
  isSubmitting?: boolean;
}

export function ReceiptForm({
  mode,
  defaultValues,
  onSubmit,
  onCancel,
  isSubmitting,
}: ReceiptFormProps) {
  const formRef = useRef<HTMLFormElement>(null);
  useFormShortcuts({ formRef });
  const { options: locationOptions, add: addLocation } = useLocationHistory();

  const form = useForm<ReceiptFormValues>({
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    resolver: zodResolver(receiptSchema) as any,
    defaultValues: defaultValues ?? {
      location: "",
      date: "",
      taxAmount: 0,
    },
  });

  const handleSubmit = (values: ReceiptFormValues) => {
    // Persist location before calling onSubmit intentionally: the location string
    // is valid user input regardless of whether the server mutation succeeds.
    // The user typed a real location name; saving it for future autocomplete
    // suggestions is correct even if the receipt save ultimately fails.
    addLocation(values.location);
    onSubmit(values);
  };

  return (
    <Form {...form}>
      <form ref={formRef} onSubmit={form.handleSubmit(handleSubmit)} className="space-y-4">
        <FormField
          control={form.control}
          name="location"
          render={({ field }) => (
            <FormItem>
              <FormLabel required>Location</FormLabel>
              <FormControl>
                <Combobox
                  options={locationOptions}
                  value={field.value}
                  onValueChange={field.onChange}
                  placeholder="Select or type a location..."
                  searchPlaceholder="Search locations..."
                  emptyMessage="No saved locations."
                  allowCustom
                  aria-required="true"
                />
              </FormControl>
              <FormMessage />
            </FormItem>
          )}
        />

        <FormField
          control={form.control}
          name="date"
          render={({ field }) => (
            <FormItem>
              <FormLabel required>Date</FormLabel>
              <FormControl>
                <DateInput aria-required="true" {...field} />
              </FormControl>
              <FormMessage />
            </FormItem>
          )}
        />

        <FormField
          control={form.control}
          name="taxAmount"
          render={({ field }) => (
            <FormItem>
              <FormLabel>Tax Amount</FormLabel>
              <FormControl>
                <CurrencyInput {...field} />
              </FormControl>
              <FormMessage />
            </FormItem>
          )}
        />

        <div className="flex justify-end gap-2 pt-4">
          <Button type="button" variant="outline" onClick={onCancel}>
            Cancel
          </Button>
          <SubmitButton
            isSubmitting={isSubmitting ?? false}
            label={mode === "create" ? "Create Receipt" : "Update Receipt"}
            loadingLabel="Saving..."
          />
        </div>
      </form>
    </Form>
  );
}
