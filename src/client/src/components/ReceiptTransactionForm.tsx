import { useRef, useEffect } from "react";
import { useForm, useController } from "react-hook-form";
import { z } from "zod/v4";
import { zodResolver } from "@hookform/resolvers/zod";
import { useFormShortcuts } from "@/hooks/useFormShortcuts";
import { AccountCardSelector } from "@/components/AccountCardSelector";
import { useAccountCardSelection } from "@/hooks/useAccountCardSelection";
import { Button } from "@/components/ui/button";
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
import { Spinner } from "@/components/ui/spinner";

const transactionSchema = z.object({
  cardId: z.string().min(1, "Card is required"),
  accountId: z.string().min(1, "Account is required"),
  amount: z.number().refine((v) => v !== 0, "Amount is required"),
  date: z.string().min(1, "Date is required"),
});

export type ReceiptTransactionFormValues = z.output<typeof transactionSchema>;

interface ReceiptTransactionFormProps {
  mode: "create" | "edit";
  defaultValues?: Partial<ReceiptTransactionFormValues>;
  onSubmit: (values: ReceiptTransactionFormValues) => void;
  onCancel: () => void;
  isSubmitting?: boolean;
  serverErrors?: Record<string, string>;
}

export function ReceiptTransactionForm({
  mode,
  defaultValues,
  onSubmit,
  onCancel,
  isSubmitting,
  serverErrors,
}: ReceiptTransactionFormProps) {
  const formRef = useRef<HTMLFormElement>(null);
  useFormShortcuts({ formRef });

  const form = useForm<ReceiptTransactionFormValues>({
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    resolver: zodResolver(transactionSchema) as any,
    defaultValues: {
      cardId: "",
      accountId: "",
      amount: 0,
      date: "",
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
      Object.entries(serverErrors) as [
        keyof ReceiptTransactionFormValues,
        string,
      ][]
    ).forEach(([field, message]) => {
      form.setError(field, { type: "server", message });
    });
  }, [serverErrors, form]);

  const account = useController({ control: form.control, name: "accountId" });
  const card = useController({ control: form.control, name: "cardId" });
  const selection = useAccountCardSelection({ accountId: account.field.value, cardId: card.field.value });

  function submit(values: ReceiptTransactionFormValues) {
    const message = selection.validate(values);
    if (message) {
      form.setError("cardId", { type: "validate", message });
      return;
    }
    onSubmit(values);
  }


  return (
    <Form {...form}>
      <form
        ref={formRef}
        onSubmit={form.handleSubmit(submit)}
        className="space-y-4"
      >
        <AccountCardSelector
          selection={selection}
          onChange={(value) => {
            account.field.onChange(value.accountId);
            card.field.onChange(value.cardId);
            form.clearErrors("cardId");
          }}
          accountInput={{
            ref: account.field.ref,
            onBlur: account.field.onBlur,
            error: account.fieldState.error?.message,
          }}
          cardInput={{
            ref: card.field.ref,
            onBlur: card.field.onBlur,
            error: card.fieldState.error?.message,
          }}
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

        <div className="flex justify-end gap-2 pt-4">
          <Button type="button" variant="outline" onClick={onCancel}>
            Cancel
          </Button>
          <Button type="submit" disabled={isSubmitting}>
            {isSubmitting && <Spinner size="sm" />}
            {isSubmitting
              ? "Saving..."
              : mode === "create"
                ? "Add Transaction"
                : "Update Transaction"}
          </Button>
        </div>
      </form>
    </Form>
  );
}
