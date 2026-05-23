import { useState, useCallback, useMemo, useEffect, useRef, useId } from "react";
import { useNavigate } from "react-router";
import { useForm } from "react-hook-form";
import { z } from "zod/v4";
import { zodResolver } from "@hookform/resolvers/zod";
import { usePageTitle } from "@/hooks/usePageTitle";
import { useCreateCompleteReceipt } from "@/hooks/useReceipts";
import { useLocationHistory } from "@/hooks/useLocationHistory";
import {
  TransactionsSection,
  type ReceiptTransaction,
} from "./TransactionsSection";
import { LineItemsSection, type ReceiptLineItem } from "./LineItemsSection";
import { BalanceSidebar } from "./BalanceSidebar";
import { Combobox } from "@/components/ui/combobox";
import { DateInput } from "@/components/ui/date-input";
import { CurrencyInput } from "@/components/ui/currency-input";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { formatCurrency } from "@/lib/format";
import {
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { toast } from "sonner";

const headerSchema = z.object({
  location: z
    .string()
    .min(1, "Location is required")
    .max(200, "Location must be 200 characters or fewer"),
  date: z.string().min(1, "Date is required"),
  taxAmount: z.number().min(0, "Tax amount must be non-negative"),
});

type HeaderFormValues = z.output<typeof headerSchema>;

export default function NewReceiptPage() {
  usePageTitle("New Receipt");
  const navigate = useNavigate();
  const locationRef = useRef<HTMLButtonElement>(null);
  const { options: locationOptions, add: addLocation } = useLocationHistory();

  const [transactions, setTransactions] = useState<ReceiptTransaction[]>([]);
  const [items, setItems] = useState<ReceiptLineItem[]>([]);
  const [showDiscard, setShowDiscard] = useState(false);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [submitErrorSummary, setSubmitErrorSummary] = useState<string | null>(null);
  const errorSummaryId = useId();

  const { mutateAsync: createCompleteReceiptAsync } =
    useCreateCompleteReceipt();

  const form = useForm<HeaderFormValues>({
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    resolver: zodResolver(headerSchema) as any,
    defaultValues: {
      location: "",
      date: "",
      taxAmount: 0,
    },
  });

  const location = form.watch("location");
  const taxAmount = form.watch("taxAmount");
  const receiptDate = form.watch("date");

  // Auto-focus location on mount
  useEffect(() => {
    locationRef.current?.focus();
  }, []);

  const subtotal = useMemo(
    () =>
      items.reduce(
        (sum, item) =>
          sum + Math.round(item.quantity * item.unitPrice * 100) / 100,
        0,
      ),
    [items],
  );

  const transactionTotal = useMemo(
    () => transactions.reduce((sum, t) => sum + t.amount, 0),
    [transactions],
  );

  // Mirrors BalanceSidebar's internal balance math so the sticky action bar
  // can show the same status and gate its Submit button.
  const expectedTotal = subtotal + taxAmount;
  const balanceDiff = Math.abs(expectedTotal - transactionTotal);
  const isBalanced = balanceDiff < 0.01;
  const isOver = expectedTotal > transactionTotal;

  const hasData =
    location !== "" ||
    receiptDate !== "" ||
    taxAmount !== 0 ||
    transactions.length > 0 ||
    items.length > 0;

  const handleCancel = useCallback(() => {
    if (hasData) {
      setShowDiscard(true);
    } else {
      navigate("/receipts");
    }
  }, [hasData, navigate]);

  const handleDiscard = useCallback(() => {
    setShowDiscard(false);
    navigate("/receipts");
  }, [navigate]);

  const handleSubmit = useCallback(async () => {
    // Validate header form first
    const valid = await form.trigger();
    if (!valid) {
      // Move focus to the first invalid field so keyboard/AT users can recover
      const firstErrorName = Object.keys(
        form.formState.errors,
      )[0] as keyof HeaderFormValues | undefined;
      if (firstErrorName) {
        form.setFocus(firstErrorName);
      }
      setSubmitErrorSummary("Please fix the highlighted fields before submitting.");
      return;
    }
    // Clear any previous error summary on a valid attempt
    setSubmitErrorSummary(null);

    const headerValues = form.getValues();

    if (transactions.length === 0) {
      toast.error("Add at least one transaction.");
      return;
    }

    if (items.length === 0) {
      toast.error("Add at least one line item.");
      return;
    }

    setIsSubmitting(true);
    try {
      addLocation(headerValues.location);

      const result = await createCompleteReceiptAsync({
        receipt: {
          location: headerValues.location,
          date: headerValues.date,
          taxAmount: headerValues.taxAmount,
        },
        transactions: transactions.map((txn) => ({
          cardId: txn.cardId,
          accountId: txn.accountId,
          amount: txn.amount,
          date: txn.date,
        })),
        items: items.map((item) => ({
          receiptItemCode: item.receiptItemCode,
          description: item.description,
          quantity: item.quantity,
          unitPrice: item.unitPrice,
          category: item.category,
          subcategory: item.subcategory,
        })),
      });

      const receiptId = (result as { receipt: { id: string } }).receipt.id;

      toast.success("Receipt created successfully!");
      navigate(`/receipts/${receiptId}`);
    } catch {
      toast.error("Failed to create receipt.");
    } finally {
      setIsSubmitting(false);
    }
  }, [
    form,
    transactions,
    items,
    createCompleteReceiptAsync,
    addLocation,
    navigate,
    setSubmitErrorSummary,
  ]);

  return (
    <div className="space-y-6">
      <h1 className="text-3xl font-bold tracking-tight">New Receipt</h1>

      {/* aria-live region: announced to screen readers when validation fails */}
      <div
        id={errorSummaryId}
        aria-live="polite"
        aria-atomic="true"
        className="sr-only"
      >
        {submitErrorSummary}
      </div>

      {/* Upper container: receipt metadata + transactions (left), balance panel (right) */}
      <div className="grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,1fr)_minmax(300px,360px)]">
        {/* Upper-left — receipt metadata and transactions */}
        <div className="space-y-6 min-w-0">
          {/* Receipt Header */}
          <Form {...form}>
            <div className="flex flex-wrap gap-4">
              <FormField
                control={form.control}
                name="location"
                render={({ field }) => (
                  <FormItem className="min-w-[220px] flex-1">
                    <FormLabel required>Location</FormLabel>
                    <FormControl>
                      <Combobox
                        ref={locationRef}
                        options={locationOptions}
                        value={field.value}
                        onValueChange={field.onChange}
                        placeholder="e.g. Walmart, Target, Costco"
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
                  <FormItem className="min-w-[170px] flex-1">
                    <FormLabel required>Date</FormLabel>
                    <FormControl>
                      <DateInput
                        aria-required="true"
                        max={new Date().toISOString().split("T")[0]}
                        {...field}
                      />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />

              <FormField
                control={form.control}
                name="taxAmount"
                render={({ field }) => (
                  <FormItem className="min-w-[150px] flex-1">
                    <FormLabel>Tax Amount</FormLabel>
                    <FormControl>
                      <CurrencyInput {...field} />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
            </div>
          </Form>

          {/* Transactions */}
          <TransactionsSection
            transactions={transactions}
            defaultDate={receiptDate}
            onChange={setTransactions}
          />
        </div>

        {/* Upper-right — balance panel */}
        <div>
          <BalanceSidebar
            subtotal={subtotal}
            taxAmount={taxAmount}
            transactionTotal={transactionTotal}
            isSubmitting={isSubmitting}
            onSubmit={handleSubmit}
            onCancel={handleCancel}
          />
        </div>
      </div>

      {/* Full-width line-item entry table, below the upper container */}
      <LineItemsSection items={items} onChange={setItems} location={location} />

      {/* Sticky action bar — keeps the balance status and Submit/Cancel
          reachable while scrolling the full-width line-item table. The upper
          Balance panel scrolls out of view once the line items grow tall. */}
      <div className="sticky bottom-0 z-10 -mx-4 border-t bg-background/95 px-4 py-3 backdrop-blur supports-[backdrop-filter]:bg-background/80">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex items-center gap-2 text-sm">
            <span className="text-muted-foreground">Balance</span>
            <Badge variant={isBalanced ? "default" : "secondary"}>
              {isBalanced
                ? "Balanced"
                : isOver
                  ? `Over by ${formatCurrency(balanceDiff)}`
                  : `Remaining: ${formatCurrency(balanceDiff)}`}
            </Badge>
          </div>
          <div className="flex items-center gap-2">
            <Button variant="ghost" onClick={handleCancel}>
              Cancel
            </Button>
            <Button
              onClick={handleSubmit}
              disabled={isSubmitting || !isBalanced}
            >
              {isSubmitting ? "Submitting..." : "Submit Receipt"}
            </Button>
          </div>
        </div>
      </div>

      <AlertDialog open={showDiscard} onOpenChange={setShowDiscard}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Discard receipt?</AlertDialogTitle>
            <AlertDialogDescription>
              You have unsaved receipt data. Are you sure you want to discard it?
              This action cannot be undone.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Continue editing</AlertDialogCancel>
            <AlertDialogAction onClick={handleDiscard}>
              Discard
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
