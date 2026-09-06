import { RequestFailure } from "@/components/RequestFailure";
import { useRef, useEffect } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod/v4";
import { zodResolver } from "@hookform/resolvers/zod";
import { useFormShortcuts } from "@/hooks/useFormShortcuts";
import { useLocationHistory } from "@/hooks/useLocationHistory";
import { Button } from "@/components/ui/button";
import { DateInput } from "@/components/ui/date-input";
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

const receiptHeaderSchema = z.object({
  location: z.string().min(1, "Location is required"),
  date: z.string().min(1, "Date is required"),
  taxAmount: z.number().min(0, "Tax amount must be zero or positive"),
});

export type ReceiptHeaderFormValues = z.output<typeof receiptHeaderSchema>;

interface ReceiptHeaderFormProps {
  defaultValues?: Partial<ReceiptHeaderFormValues>;
  onSubmit: (values: ReceiptHeaderFormValues) => void;
  onCancel: () => void;
  isSubmitting?: boolean;
  serverErrors?: Record<string, string>;
}

export function ReceiptHeaderForm({
  defaultValues,
  onSubmit,
  onCancel,
  isSubmitting,
  serverErrors,
}: ReceiptHeaderFormProps) {
  const formRef = useRef<HTMLFormElement>(null);
  useFormShortcuts({ formRef });
  const { options: locationOptions, isError: locationsError, isFetching: locationsFetching, refetch: retryLocations } = useLocationHistory();

  const form = useForm<ReceiptHeaderFormValues>({
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    resolver: zodResolver(receiptHeaderSchema) as any,
    defaultValues: {
      location: "",
      date: "",
      taxAmount: 0,
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
    (Object.entries(serverErrors) as [keyof ReceiptHeaderFormValues, string][]).forEach(
      ([field, message]) => {
        form.setError(field, { type: "server", message });
      },
    );
  }, [serverErrors, form]);

  return (
    <Form {...form}>
      <form
        ref={formRef}
        onSubmit={form.handleSubmit(onSubmit)}
        className="space-y-4"
      >
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
                  placeholder="Store name or location"
                  searchPlaceholder="Search locations..."
                  emptyMessage={locationsError ? "Location suggestions unavailable." : locationsFetching ? "Loading location suggestions..." : "No saved locations."}
                  allowCustom
                  aria-required="true"
                />
              </FormControl>
              <FormMessage />
              {locationsError && (
                <RequestFailure
                  message="Location suggestions unavailable. You can enter a location manually."
                  retry={() => { void retryLocations(); }}
                  isRetrying={locationsFetching}
                />
              )}
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
          <Button type="submit" disabled={isSubmitting}>
            {isSubmitting && <Spinner size="sm" />}
            {isSubmitting ? "Saving..." : "Update Receipt"}
          </Button>
        </div>
      </form>
    </Form>
  );
}
