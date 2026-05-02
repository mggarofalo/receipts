import { useMemo, useCallback, useRef, useEffect } from "react";
import { toast } from "sonner";
import { generateId } from "@/lib/id";
import { useForm } from "react-hook-form";
import { z } from "zod/v4";
import { zodResolver } from "@hookform/resolvers/zod";
import { useFormShortcuts } from "@/hooks/useFormShortcuts";
import { useAccounts } from "@/hooks/useAccounts";
import { useCards } from "@/hooks/useCards";
import { accountToOption, cardToOption } from "@/lib/combobox-options";
import { formatCurrency } from "@/lib/format";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { DateInput } from "@/components/ui/date-input";
import { Combobox } from "@/components/ui/combobox";
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
import { ConfidenceIndicator } from "@/pages/scan-receipt/ConfidenceIndicator";
import type { ReceiptConfidenceMap } from "@/pages/scan-receipt/types";

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

type TransactionConfidenceEntry = NonNullable<
  ReceiptConfidenceMap["transactions"]
>[number];

interface TransactionsSectionProps {
  transactions: ReceiptTransaction[];
  defaultDate: string;
  onChange: (transactions: ReceiptTransaction[]) => void;
  /**
   * Per-transaction confidence levels keyed by stable transaction id (not
   * index). Set on first mount from the scan-proposal mapping in NewReceiptPage
   * so confidence stays correctly paired with rows after additions or deletions.
   * An index-based lookup would misalign confidence with the wrong row after
   * a deletion. See {@link initialTransactionsAndConfidence}.
   */
  confidenceById?: Map<string, TransactionConfidenceEntry>;
}

export function TransactionsSection({
  transactions,
  defaultDate,
  onChange,
  confidenceById,
}: TransactionsSectionProps) {
  const formRef = useRef<HTMLFormElement>(null);
  const cardRef = useRef<HTMLButtonElement>(null);
  const { data: accounts } = useAccounts(0, 50, undefined, undefined, true);
  const { data: cards } = useCards(0, 500, undefined, undefined, true);
  useFormShortcuts({ formRef });

  const accountOptions = useMemo(
    () => (accounts ?? []).map(accountToOption),
    [accounts],
  );

  const cardOptions = useMemo(
    () => (cards ?? []).map(cardToOption),
    [cards],
  );

  const cardById = useMemo(() => {
    const map = new Map<string, { id: string; accountId?: string | null }>();
    for (const c of cards ?? []) map.set(c.id, c);
    return map;
  }, [cards]);

  // Lookup for resolving Account names when surfacing the Card-overrode-Account
  // toast (RECEIPTS-659). The toast message references the new account by name
  // so the user sees what the row was switched to.
  const accountById = useMemo(() => {
    const map = new Map<string, { id: string; name: string }>();
    for (const a of accounts ?? []) map.set(a.id, a);
    return map;
  }, [accounts]);

  // Show a non-blocking toast when picking a Card overrides an already-selected
  // Account on a row. Only fires when the prior Account differs from the new
  // card.accountId — same-account swaps and clear-card flows stay silent. This
  // is invoked exclusively from user-driven onValueChange handlers, so it never
  // fires during scan pre-population (which seeds rows through the
  // `transactions` prop, not these handlers).
  const notifyAccountOverride = useCallback(
    (priorAccountId: string, newAccountId: string) => {
      if (!priorAccountId) return;
      if (priorAccountId === newAccountId) return;
      const newAccount = accountById.get(newAccountId);
      toast.info(
        newAccount?.name
          ? `Account changed to ${newAccount.name} to match the selected card.`
          : "Account changed to match the selected card.",
      );
    },
    [accountById],
  );

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

  // Whether the Account field should be locked (driven by an explicit Card
  // pick whose Card carries an accountId). Card is the source of truth: when
  // a Card with a known accountId is selected the Account dropdown auto-fills
  // and becomes read-only; clearing the Card re-enables manual selection.
  const watchedCardId = form.watch("cardId");
  const isAccountLockedByCard = useMemo(() => {
    if (!watchedCardId) return false;
    const card = cardById.get(watchedCardId);
    return !!card?.accountId;
  }, [watchedCardId, cardById]);

  function handleCardChange(value: string) {
    form.setValue("cardId", value, { shouldValidate: true });
    if (!value) {
      // Clearing the card releases the account lock and clears the auto-fill.
      // Clearing rather than preserving the previous accountId avoids leaving
      // a stale read-only value in a now-editable field. No toast — the
      // re-enabled dropdown is self-explanatory (RECEIPTS-659).
      form.setValue("accountId", "", { shouldValidate: true });
      return;
    }
    const card = cardById.get(value);
    if (card?.accountId) {
      // Card wins: even if the user had picked an account first, the resolved
      // card's FK overwrites it. The Account dropdown then renders read-only.
      // If the prior Account differs from the new one, surface a toast so the
      // overwrite is not silent (RECEIPTS-659).
      const priorAccountId = form.getValues("accountId");
      notifyAccountOverride(priorAccountId, card.accountId);
      form.setValue("accountId", card.accountId, { shouldValidate: true });
    }
  }

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
      const newTxn: ReceiptTransaction = {
        id: generateId(),
        ...values,
      };
      onChange([...transactions, newTxn]);
      (document.activeElement as HTMLElement)?.blur?.();
      form.reset({ cardId: "", accountId: "", amount: 0, date: defaultDate });
    },
    [form, defaultDate, transactions, onChange],
  );

  // Focus card field after adding a transaction for rapid entry
  const prevCountRef = useRef(transactions.length);
  useEffect(() => {
    if (transactions.length > prevCountRef.current) {
      cardRef.current?.focus();
    }
    prevCountRef.current = transactions.length;
  }, [transactions.length]);

  const handleRemove = useCallback(
    (id: string) => {
      onChange(transactions.filter((t) => t.id !== id));
    },
    [transactions, onChange],
  );

  // Inline-edit handlers for pre-populated rows. Card change cascades to
  // accountId for the target row when the resolved Card carries one — the
  // same "Card wins" rule as the new-row form above. Without this an account
  // override would persist after a card swap, contradicting the FK. When the
  // cascade overrides a non-empty prior Account, surface a sonner toast so the
  // overwrite is visible (RECEIPTS-659).
  const handleRowCardChange = useCallback(
    (id: string, cardId: string) => {
      const card = cardId ? cardById.get(cardId) : undefined;
      const targetRow = transactions.find((t) => t.id === id);
      if (cardId && card?.accountId && targetRow) {
        notifyAccountOverride(targetRow.accountId, card.accountId);
      }
      onChange(
        transactions.map((t) =>
          t.id === id
            ? {
                ...t,
                cardId,
                accountId: !cardId
                  ? ""
                  : card?.accountId
                    ? card.accountId
                    : t.accountId,
              }
            : t,
        ),
      );
    },
    [transactions, onChange, cardById, notifyAccountOverride],
  );

  const handleRowField = useCallback(
    <K extends keyof Omit<ReceiptTransaction, "id" | "cardId">>(
      id: string,
      field: K,
      value: ReceiptTransaction[K],
    ) => {
      onChange(
        transactions.map((t) =>
          t.id === id ? { ...t, [field]: value } : t,
        ),
      );
    },
    [transactions, onChange],
  );

  return (
    <Card>
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
            className="grid grid-cols-1 gap-4 sm:grid-cols-[1fr_1fr_auto_auto_auto] sm:items-end"
          >
            <FormField
              control={form.control}
              name="cardId"
              render={({ field }) => (
                <FormItem>
                  <FormLabel required>Card</FormLabel>
                  <FormControl>
                    <Combobox
                      ref={cardRef}
                      options={cardOptions}
                      value={field.value}
                      onValueChange={handleCardChange}
                      placeholder="Select card..."
                      searchPlaceholder="Search cards..."
                      emptyMessage="No cards found."
                      aria-required="true"
                    />
                  </FormControl>
                  <FormMessage />
                </FormItem>
              )}
            />

            <FormField
              control={form.control}
              name="accountId"
              render={({ field }) => (
                <FormItem>
                  <FormLabel required>Account</FormLabel>
                  <FormControl>
                    <Combobox
                      options={accountOptions}
                      value={field.value}
                      onValueChange={field.onChange}
                      placeholder="Select account..."
                      searchPlaceholder="Search accounts..."
                      emptyMessage="No accounts found."
                      aria-required="true"
                      disabled={isAccountLockedByCard}
                    />
                  </FormControl>
                  {isAccountLockedByCard && (
                    <p className="text-xs text-muted-foreground">
                      Account is set by the selected card.
                    </p>
                  )}
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

            <Button type="submit" variant="secondary" size="sm" className="sm:mb-0.5">
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
              {transactions.map((txn) => {
                const fieldConfidence = confidenceById?.get(txn.id);
                const card = txn.cardId
                  ? cardById.get(txn.cardId)
                  : undefined;
                const rowAccountLockedByCard = !!card?.accountId;
                return (
                  <TableRow key={txn.id} data-testid={`txn-row-${txn.id}`}>
                    <TableCell>
                      <div className="flex items-center gap-2">
                        <Combobox
                          options={cardOptions}
                          value={txn.cardId}
                          onValueChange={(v) =>
                            handleRowCardChange(txn.id, v)
                          }
                          placeholder="Select card..."
                          searchPlaceholder="Search cards..."
                          emptyMessage="No cards found."
                          aria-label={`Card for transaction ${txn.id}`}
                        />
                        <ConfidenceIndicator
                          confidence={fieldConfidence?.cardId}
                        />
                      </div>
                    </TableCell>
                    <TableCell>
                      <Combobox
                        options={accountOptions}
                        value={txn.accountId}
                        onValueChange={(v) =>
                          handleRowField(txn.id, "accountId", v)
                        }
                        placeholder="Select account..."
                        searchPlaceholder="Search accounts..."
                        emptyMessage="No accounts found."
                        aria-label={`Account for transaction ${txn.id}`}
                        disabled={rowAccountLockedByCard}
                      />
                      {rowAccountLockedByCard && (
                        <p className="mt-1 text-xs text-muted-foreground">
                          Account is set by the selected card.
                        </p>
                      )}
                    </TableCell>
                    <TableCell>
                      <div className="flex items-center gap-2">
                        <CurrencyInput
                          value={txn.amount}
                          onChange={(v) =>
                            handleRowField(txn.id, "amount", v)
                          }
                          aria-label={`Amount for transaction ${txn.id}`}
                        />
                        <ConfidenceIndicator
                          confidence={fieldConfidence?.amount}
                        />
                      </div>
                    </TableCell>
                    <TableCell>
                      <div className="flex items-center gap-2">
                        <DateInput
                          value={txn.date}
                          onChange={(v) => handleRowField(txn.id, "date", v)}
                          aria-label={`Date for transaction ${txn.id}`}
                        />
                        <ConfidenceIndicator
                          confidence={fieldConfidence?.date}
                        />
                      </div>
                    </TableCell>
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
                );
              })}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </Card>
  );
}
