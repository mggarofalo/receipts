import { classificationChange } from "@/lib/category-paths";
import { RequestFailure } from "@/components/RequestFailure";
import { ReceiptNormalizationError } from "@/components/ReceiptNormalizationError";
import {
  currentNormalizationChoice,
  receiptNormalizationSource,
} from "@/lib/receipt-normalization";
import { generateId } from "@/lib/id";
import { calculateSubtotal, sumAmounts } from "@/lib/receipt-arithmetic";
import {
  assertSessionCurrent,
  getSessionVersion,
  isAbortError,
} from "@/lib/auth";
import {
  useState,
  useCallback,
  useMemo,
  useEffect,
  useRef,
  useId,
} from "react";
import { useNavigate, useBlocker } from "react-router";
import { useForm } from "react-hook-form";
import { z } from "zod/v4";
import { zodResolver } from "@hookform/resolvers/zod";
import { usePageTitle } from "@/hooks/usePageTitle";
import { useEnvelopes, useOccasions } from "@/hooks/useEnvelopes";
import {
  OccasionChip,
  PaidFromSelect,
} from "@/components/envelopes/BudgetChoices";
import { useCreateCompleteReceipt } from "@/hooks/useReceipts";
import { useLocationHistory } from "@/hooks/useLocationHistory";
import {
  TransactionsSection,
  type ReceiptTransaction,
} from "./TransactionsSection";
import { LineItemsSection, type ReceiptLineItem } from "./LineItemsSection";
import {
  AdjustmentsSection,
  type ReceiptAdjustment,
} from "./AdjustmentsSection";
import { BalanceSidebar } from "./BalanceSidebar";
import { Combobox } from "@/components/ui/combobox";
import { DateInput } from "@/components/ui/date-input";
import { CurrencyInput } from "@/components/ui/currency-input";
import { isFutureDate } from "@/lib/format";
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
import { extractErrorMessage } from "@/lib/problem-details";

const headerSchema = z.object({
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

type HeaderFormValues = z.output<typeof headerSchema>;

export default function NewReceiptPage() {
  usePageTitle("New Receipt");
  const navigate = useNavigate();
  const locationRef = useRef<HTMLButtonElement>(null);
  const {
    options: locationOptions,
    add: addLocation,
    isError: locationsError,
    isFetching: locationsFetching,
    refetch: retryLocations,
  } = useLocationHistory();

  const [transactions, setTransactions] = useState<ReceiptTransaction[]>([]);
  const [items, setItems] = useState<ReceiptLineItem[]>([]);
  const [adjustments, setAdjustments] = useState<ReceiptAdjustment[]>([]);
  const [paidFromEnvelopeId, setPaidFromEnvelopeId] = useState<string | null>(
    null,
  );
  const [occasionId, setOccasionId] = useState<string | null>(null);
  const { envelopes } = useEnvelopes();
  const { occasions } = useOccasions();
  const [showDiscard, setShowDiscard] = useState(false);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [submitErrorSummary, setSubmitErrorSummary] = useState<string | null>(
    null,
  );
  const [saveError, setSaveError] = useState<unknown>();
  const errorSummaryId = useId();
  const saveAttempt = useRef<{ fingerprint: string; id: string } | null>(null);

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
  const subtotal = useMemo(() => calculateSubtotal(items), [items]);

  const transactionTotal = useMemo(
    () => sumAmounts(transactions.map((transaction) => transaction.amount)),
    [transactions],
  );

  const adjustmentTotal = useMemo(
    () => sumAmounts(adjustments.map((adjustment) => adjustment.amount)),
    [adjustments],
  );

  const hasData =
    location !== "" ||
    receiptDate !== "" ||
    taxAmount !== 0 ||
    transactions.length > 0 ||
    items.length > 0 ||
    adjustments.length > 0;

  // Tracks a successful save (so we don't block the post-save redirect) and an
  // in-flight discard decision (so the dialog's close handler doesn't cancel a
  // navigation we deliberately let through). RECEIPTS-785.
  const hasSavedRef = useRef(false);
  const proceedingRef = useRef(false);
  const discardingRef = useRef(false);

  // Intercept in-app navigation (sidebar links, the g>r chord, browser
  // back/forward) whenever there's unsaved data, so a long manual entry isn't
  // silently destroyed. Inert when the form is empty or after a successful save.
  const blocker = useBlocker(
    useCallback(
      ({
        currentLocation,
        nextLocation,
      }: {
        currentLocation: { pathname: string };
        nextLocation: { pathname: string };
      }) =>
        hasData &&
        !hasSavedRef.current &&
        !discardingRef.current &&
        currentLocation.pathname !== nextLocation.pathname,
      [hasData],
    ),
  );

  // Surface the existing discard dialog when the blocker trips.
  useEffect(() => {
    if (blocker.state === "blocked") {
      setShowDiscard(true);
    }
  }, [blocker.state]);

  // Warn on hard exits the in-app blocker can't see: refresh, tab/window close,
  // and full-page browser navigation.
  useEffect(() => {
    if (!hasData) return;
    function onBeforeUnload(e: BeforeUnloadEvent) {
      e.preventDefault();
      e.returnValue = "";
    }
    window.addEventListener("beforeunload", onBeforeUnload);
    return () => window.removeEventListener("beforeunload", onBeforeUnload);
  }, [hasData]);

  const handleCancel = useCallback(() => {
    if (hasData) {
      setShowDiscard(true);
    } else {
      navigate("/receipts");
    }
  }, [hasData, navigate]);

  const handleDiscard = useCallback(() => {
    setShowDiscard(false);
    if (blocker.state === "blocked") {
      // A real navigation is pending (sidebar link, back button) — let it go.
      proceedingRef.current = true;
      blocker.proceed();
    } else {
      // Dialog was opened by the Cancel button; navigate past the blocker.
      discardingRef.current = true;
      navigate("/receipts");
    }
  }, [blocker, navigate]);

  const handleDiscardDialogOpenChange = useCallback(
    (open: boolean) => {
      setShowDiscard(open);
      // Closing without choosing "Discard" (Continue editing, Escape) means the
      // user is staying — release any pending navigation block.
      if (!open && !proceedingRef.current && blocker.state === "blocked") {
        blocker.reset();
      }
      if (!open) proceedingRef.current = false;
    },
    [blocker],
  );

  const handleSubmit = useCallback(async () => {
    // Validate header form first
    const valid = await form.trigger(undefined, { shouldFocus: true });
    if (!valid) {
      // The custom location trigger uses its own ref; other fields are registered
      // directly with React Hook Form and receive focus through shouldFocus.
      if (form.getFieldState("location").invalid) {
        locationRef.current?.focus();
      }
      setSubmitErrorSummary(
        "Please fix the highlighted fields before submitting.",
      );
      return;
    }
    // Clear any previous error summary on a valid attempt
    setSubmitErrorSummary(null);
    setSaveError(undefined);

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

      const sessionVersion = getSessionVersion();
      const body = {
        receipt: {
          location: headerValues.location,
          date: headerValues.date,
          taxAmount: headerValues.taxAmount,
          paidFromEnvelopeId,
          occasionId,
        },
        transactions: transactions.map((txn) => ({
          cardId: txn.cardId,
          amount: txn.amount,
          date: txn.date,
        })),
        items: items.map((item) => ({
          receiptItemCode: item.receiptItemCode,
          description: item.description,
          quantity: item.quantity,
          unitPrice: item.unitPrice,
          envelopeId: item.envelopeId ?? null,
          classification: item.categoryOperationId
            ? classificationChange(
                item.categoryChoice,
                undefined,
                item.categoryOperationId,
              )
            : undefined,
          normalizationIntent: currentNormalizationChoice(
            item.normalizationChoice,
            receiptNormalizationSource(headerValues.location, item),
          )?.intent,
        })),
        adjustments: adjustments.map((adjustment) => ({
          type: adjustment.type,
          amount: adjustment.amount,
          description: adjustment.description || null,
        })),
      };
      const fingerprint = JSON.stringify(body);
      if (saveAttempt.current?.fingerprint !== fingerprint) {
        saveAttempt.current = { fingerprint, id: generateId() };
      }
      const result = await createCompleteReceiptAsync({
        ...body,
        clientRequestId: saveAttempt.current.id,
      });

      assertSessionCurrent(sessionVersion);
      const receiptId = (result as { receipt: { id: string } }).receipt.id;

      // Mark saved BEFORE navigating so the unsaved-work blocker lets the
      // success redirect through (RECEIPTS-785).
      hasSavedRef.current = true;
      toast.success("Receipt saved");
      navigate(`/receipts/${receiptId}`);
    } catch (error) {
      if (isAbortError(error)) return;
      const message = extractErrorMessage(error) ?? "Failed to create receipt.";
      setSubmitErrorSummary(message);
      setSaveError(error);
      toast.error(message);
    } finally {
      setIsSubmitting(false);
    }
  }, [
    form,
    transactions,
    items,
    adjustments,
    paidFromEnvelopeId,
    occasionId,
    createCompleteReceiptAsync,
    addLocation,
    navigate,
    setSubmitErrorSummary,
  ]);

  return (
    <div className="min-w-0 space-y-4">
      <h1 className="text-2xl font-semibold tracking-tight">New Receipt</h1>
      <ReceiptNormalizationError error={saveError} />
      {/* aria-live region: announced to screen readers when validation fails */}
      <div
        id={errorSummaryId}
        aria-live="polite"
        aria-atomic="true"
        className="sr-only"
      >
        {submitErrorSummary}
      </div>

      <Form {...form}>
        <div className="flex min-w-0 flex-wrap gap-4 rounded-xl border bg-card p-4">
          <FormField
            control={form.control}
            name="location"
            render={({ field }) => (
              <FormItem className="min-w-[220px] flex-[2_1_220px]">
                <FormLabel required>Location</FormLabel>
                <FormControl>
                  <Combobox
                    ref={locationRef}
                    options={locationOptions}
                    value={field.value}
                    onValueChange={(value) => {
                      field.onChange(value);
                      setItems((current) =>
                        current.map((item) => ({
                          ...item,
                          normalizationChoice: undefined,
                        })),
                      );
                    }}
                    placeholder="e.g. Walmart, Target, Costco"
                    searchPlaceholder="Search locations..."
                    emptyMessage={
                      locationsError
                        ? "Location suggestions unavailable."
                        : locationsFetching
                          ? "Loading location suggestions..."
                          : "No saved locations."
                    }
                    allowCustom
                    aria-required="true"
                  />
                </FormControl>
                <FormMessage />
                {locationsError && (
                  <RequestFailure
                    message="Location suggestions unavailable. You can enter a location manually."
                    retry={() => {
                      void retryLocations();
                    }}
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

          <div className="flex w-full flex-wrap items-center gap-2">
            <PaidFromSelect
              envelopes={envelopes}
              value={paidFromEnvelopeId}
              onChange={setPaidFromEnvelopeId}
            />
            <OccasionChip
              occasions={occasions}
              value={occasionId}
              onChange={setOccasionId}
            />
          </div>
        </div>
      </Form>

      <div className="min-w-0">
        <TransactionsSection
          transactions={transactions}
          defaultDate={receiptDate}
          onChange={setTransactions}
        />
      </div>

      <LineItemsSection
        items={items}
        onChange={setItems}
        location={location}
        paidFromEnvelopeId={paidFromEnvelopeId}
      />

      <AdjustmentsSection adjustments={adjustments} onChange={setAdjustments} />

      <div className="sticky bottom-0 z-10 rounded-t-xl border bg-background/95 p-3 backdrop-blur [@media(max-width:900px)]:bottom-[60px] supports-[backdrop-filter]:bg-background/90">
        <BalanceSidebar
          subtotal={subtotal}
          taxAmount={taxAmount}
          adjustmentTotal={adjustmentTotal}
          transactionTotal={transactionTotal}
          isSubmitting={isSubmitting}
          onSubmit={handleSubmit}
          onCancel={handleCancel}
        />
      </div>

      <AlertDialog
        open={showDiscard}
        onOpenChange={handleDiscardDialogOpenChange}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Discard receipt?</AlertDialogTitle>
            <AlertDialogDescription>
              You have unsaved receipt data. Are you sure you want to discard
              it? This action cannot be undone.
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
