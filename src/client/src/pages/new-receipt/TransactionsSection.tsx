import { useMemo, useCallback, useRef, useEffect } from "react";
import { generateId } from "@/lib/id";
import { useForm, useController } from "react-hook-form";
import { z } from "zod/v4";
import { zodResolver } from "@hookform/resolvers/zod";
import { useFormShortcuts } from "@/hooks/useFormShortcuts";
import { useAllAccounts, useAccountCards } from "@/hooks/useAccounts";
import { accountToOption } from "@/lib/combobox-options";
import { formatCurrency } from "@/lib/format";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { DateInput } from "@/components/ui/date-input";
import { AccountCardSelector } from "@/components/AccountCardSelector";
import { useAccountCardSelection } from "@/hooks/useAccountCardSelection";
import { CurrencyInput } from "@/components/ui/currency-input";
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
import { Plus, Trash2 } from "lucide-react";

const txnSchema = z.object({
  cardId: z.string().min(1, "Card is required"),
  accountId: z.string().min(1, "Account is required"),
  amount: z.number().refine((v) => v !== 0, "Amount is required"),
  date: z.string().min(1, "Date is required"),
});

type TxnFormValues = z.output<typeof txnSchema>;

export interface ReceiptTransaction {
  id: string;
  cardId: string;
  accountId: string;
  amount: number;
  date: string;
}

interface TransactionsSectionProps {
  transactions: ReceiptTransaction[];
  defaultDate: string;
  onChange: (transactions: ReceiptTransaction[]) => void;
}

export function TransactionsSection({
  transactions,
  defaultDate,
  onChange,
}: TransactionsSectionProps) {
  const formRef = useRef<HTMLFormElement>(null);
  const sectionRef = useRef<HTMLDivElement>(null);
  const focusScopeActiveRef = useRef(false);
  const accountRef = useRef<HTMLButtonElement>(null);
  const { data: accounts } = useAllAccounts();
  useFormShortcuts({ formRef });

  const accountOptions = useMemo(
    () => (accounts ?? []).map(accountToOption),
    [accounts],
  );

  const accountNameMap = useMemo(() => {
    const map = new Map<string, string>();
    for (const opt of accountOptions) {
      map.set(opt.value, opt.label);
    }
    return map;
  }, [accountOptions]);

  const form = useForm<TxnFormValues>({
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    resolver: zodResolver(txnSchema) as any,
    defaultValues: {
      cardId: "",
      accountId: "",
      amount: 0,
      date: defaultDate,
    },
  });

  const account = useController({ control: form.control, name: "accountId" });
  const card = useController({ control: form.control, name: "cardId" });
  const selection = useAccountCardSelection({ accountId: account.field.value, cardId: card.field.value });
  const validateSelection = selection.validate;


  // Sync the date field when the receipt date changes and the field is empty
  const prevDefaultDateRef = useRef(defaultDate);
  useEffect(() => {
    const currentDate = form.getValues("date");
    if (
      defaultDate !== prevDefaultDateRef.current &&
      (currentDate === "" || currentDate === prevDefaultDateRef.current)
    ) {
      form.setValue("date", defaultDate, { shouldValidate: true });
    }
    prevDefaultDateRef.current = defaultDate;
  }, [defaultDate, form]);

  const runningTotal = useMemo(
    () => transactions.reduce((sum, t) => sum + t.amount, 0),
    [transactions],
  );

  const handleAdd = useCallback(
    (values: TxnFormValues) => {
      const message = validateSelection(values);
      if (message) {
        form.setError("cardId", { type: "validate", message });
        return;
      }
      const newTxn: ReceiptTransaction = {
        id: generateId(),
        ...values,
      };
      onChange([...transactions, newTxn]);
      (document.activeElement as HTMLElement)?.blur?.();
      form.reset({ cardId: "", accountId: "", amount: 0, date: defaultDate });
    },
    [form, defaultDate, transactions, onChange, validateSelection],
  );

  // Focus account field after adding a transaction for rapid entry
  const prevCountRef = useRef(transactions.length);
  useEffect(() => {
    if (transactions.length > prevCountRef.current) {
      accountRef.current?.focus();
    }
    prevCountRef.current = transactions.length;
  }, [transactions.length]);

  const handleRemove = useCallback(
    (id: string) => {
      onChange(transactions.filter((t) => t.id !== id));
    },
    [transactions, onChange],
  );

  const isInTransactionEditor = useCallback((target: EventTarget | null) => {
    if (!(target instanceof Element)) return false;

    const section = sectionRef.current;
    if (!section) return false;
    if (section.contains(target)) return true;

    // Combobox popovers are portaled to document.body. Radix links each one to
    // its in-card trigger with aria-controls, so include an open popover in the
    // same logical focus boundary without treating unrelated popovers as ours.
    const popover = target.closest('[data-slot="popover-content"]');
    if (!popover?.id) return false;

    return Array.from(section.querySelectorAll("[aria-controls]")).some(
      (trigger) => trigger.getAttribute("aria-controls") === popover.id,
    );
  }, []);

  const clearDraftValidation = useCallback(() => {
    if (!focusScopeActiveRef.current) return;

    focusScopeActiveRef.current = false;
    // clearErrors alone leaves isSubmitted set, which makes RHF revalidate on
    // change after re-entry. Reset the validation lifecycle while preserving
    // exactly what the user typed in the draft.
    form.reset(form.getValues());
  }, [form]);

  useEffect(() => {
    function handleDocumentFocus(event: FocusEvent) {
      if (isInTransactionEditor(event.target)) return;
      clearDraftValidation();
    }

    document.addEventListener("focusin", handleDocumentFocus);
    return () => document.removeEventListener("focusin", handleDocumentFocus);
  }, [clearDraftValidation, isInTransactionEditor]);

  function handleSectionBlur() {
    // A focusin event normally identifies the destination. This fallback also
    // handles focus being cleared to body (where no focusin event is emitted).
    queueMicrotask(() => {
      if (!isInTransactionEditor(document.activeElement)) {
        clearDraftValidation();
      }
    });
  }

  return (
    <Card
      ref={sectionRef}
      onFocusCapture={() => {
        focusScopeActiveRef.current = true;
      }}
      onBlur={handleSectionBlur}
    >
      <CardHeader className="pb-3">
        <div className="flex items-center justify-between">
          <CardTitle className="text-lg">Transactions</CardTitle>
          <span className="text-sm text-muted-foreground">
            Total: {formatCurrency(runningTotal)}
          </span>
        </div>
      </CardHeader>
      <CardContent className="space-y-4">
        <Form {...form}>
          <form
            ref={formRef}
            onSubmit={form.handleSubmit(handleAdd)}
            className="flex flex-wrap items-end gap-4"
          >
            <AccountCardSelector
              selection={selection}
              onChange={(value) => {
                account.field.onChange(value.accountId);
                card.field.onChange(value.cardId);
                form.clearErrors("cardId");
              }}
              accountInput={{
                ref: (element) => {
                  account.field.ref(element);
                  accountRef.current = element;
                },
                onBlur: account.field.onBlur,
                error: account.fieldState.error?.message,
              }}
              cardInput={{
                ref: card.field.ref,
                onBlur: card.field.onBlur,
                error: card.fieldState.error?.message,
              }}
              fieldClassName="min-w-[160px] flex-1"
            />

            <FormField
              control={form.control}
              name="amount"
              render={({ field }) => (
                <FormItem className="min-w-[120px] flex-1">
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
                <FormItem className="min-w-[160px] flex-1">
                  <FormLabel required>Date</FormLabel>
                  <FormControl>
                    <DateInput aria-required="true" {...field} />
                  </FormControl>
                  <FormMessage />
                </FormItem>
              )}
            />

            <Button
              type="submit"
              variant="secondary"
              size="sm"
              className="mb-0.5 shrink-0"
            >
              <Plus className="mr-1 h-4 w-4" />
              Add
            </Button>
          </form>
        </Form>

        {transactions.length > 0 && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Card</TableHead>
                <TableHead>Account</TableHead>
                <TableHead>Amount</TableHead>
                <TableHead>Date</TableHead>
                <TableHead className="w-12">
                  <span className="sr-only">Actions</span>
                </TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {transactions.map((txn) => (
                <TableRow key={txn.id}>
                  <TableCell>
                    <TransactionCardName
                      accountId={txn.accountId}
                      cardId={txn.cardId}
                    />
                  </TableCell>
                  <TableCell>
                    {accountNameMap.get(txn.accountId) ?? txn.accountId}
                  </TableCell>
                  <TableCell>{formatCurrency(txn.amount)}</TableCell>
                  <TableCell>{txn.date}</TableCell>
                  <TableCell>
                    <Button
                      variant="ghost"
                      size="icon"
                      onClick={() => handleRemove(txn.id)}
                    >
                      <Trash2 className="h-4 w-4" />
                      <span className="sr-only">Remove</span>
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </Card>
  );
}

function TransactionCardName({
  accountId,
  cardId,
}: {
  accountId: string;
  cardId: string;
}) {
  const { data: cards } = useAccountCards(accountId);
  return cards?.find((card) => card.id === cardId)?.name ?? cardId;
}
