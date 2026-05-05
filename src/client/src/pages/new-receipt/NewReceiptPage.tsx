import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useNavigate } from "react-router";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { usePageTitle } from "@/hooks/usePageTitle";
import { useLocationHistory } from "@/hooks/useLocationHistory";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { TransactionsSection } from "./TransactionsSection";
import { LineItemsSection } from "./LineItemsSection";
import { computeLineTotal } from "./computeLineTotal";
import { BalanceSidebar } from "./BalanceSidebar";
import { HeaderSection } from "./HeaderSection";
import { headerSchema, type HeaderFormValues } from "./headerSchema";
import { ReceiptDetailsPanel } from "./ReceiptDetailsPanel";
import { DiscardReceiptDialog } from "./DiscardReceiptDialog";
import { useReceiptSubmit } from "./useReceiptSubmit";
import type {
  ScanInitialValues,
  ReceiptConfidenceMap,
} from "@/pages/scan-receipt/types";
import {
  initialItemsAndConfidence,
  initialTransactionsAndConfidence,
} from "@/pages/scan-receipt/proposalMappers";

interface NewReceiptPageProps {
  initialValues?: ScanInitialValues;
  confidenceMap?: ReceiptConfidenceMap;
  pageTitle?: string;
  /**
   * Number of source pages silently dropped during scan. For multi-page PDFs
   * the VLM only sees page 1; this prop surfaces a banner so the user is not
   * left with the false impression that the proposal covers the whole document.
   * 0 (or undefined) means no banner. Always 0 for single-page sources.
   */
  droppedPageCount?: number;
}

export default function NewReceiptPage({
  initialValues,
  confidenceMap,
  pageTitle,
  droppedPageCount,
}: NewReceiptPageProps = {}) {
  usePageTitle(pageTitle ?? "New Receipt");
  const navigate = useNavigate();
  const locationRef = useRef<HTMLButtonElement>(null);
  const { options: locationOptions } = useLocationHistory();

  // Items and transactions (with their confidence-by-id maps) are initialised
  // together from the scan proposal so confidence stays correctly paired with
  // rows after additions or deletions. The bundles are stored in `useState`
  // with a lazy initializer — React contractually guarantees this runs exactly
  // once per mount and the result is then immutably retained in component
  // state. (`useMemo` is documented as a performance optimization that *may*
  // re-run, which would re-generate row ids and silently break the id ->
  // confidence mapping.) The setters then drive the editable lists while the
  // confidence maps stay immutable: a stale entry for a deleted row is
  // harmless because no row will ever look it up again.
  const [initialItemsBundle] = useState(() =>
    initialItemsAndConfidence(initialValues, confidenceMap),
  );
  const [items, setItems] = useState(initialItemsBundle.items);
  const itemConfidenceById = initialItemsBundle.itemConfidenceById;

  const [initialTransactionsBundle] = useState(() =>
    initialTransactionsAndConfidence(initialValues, confidenceMap),
  );
  const [transactions, setTransactions] = useState(
    initialTransactionsBundle.transactions,
  );
  const transactionConfidenceById =
    initialTransactionsBundle.transactionConfidenceById;

  const [showDiscard, setShowDiscard] = useState(false);

  const form = useForm<HeaderFormValues>({
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    resolver: zodResolver(headerSchema) as any,
    defaultValues: {
      location: initialValues?.header.location ?? "",
      date: initialValues?.header.date ?? "",
      taxAmount: initialValues?.header.taxAmount ?? 0,
      storeAddress: initialValues?.header.storeAddress ?? "",
      storePhone: initialValues?.header.storePhone ?? "",
    },
  });

  const location = form.watch("location");
  const taxAmount = form.watch("taxAmount");
  const receiptDate = form.watch("date");
  const storeAddress = form.watch("storeAddress");
  const storePhone = form.watch("storePhone");

  // Auto-focus location on mount
  useEffect(() => {
    locationRef.current?.focus();
  }, []);

  // Must mirror LineItemsSection's per-row total (which branches on pricingMode):
  // flat-priced rows store the receipt-printed total in `totalPrice` and have
  // quantity=1, unitPrice=0, so a naïve `q × p` would silently drop them and
  // surface a sidebar subtotal that disagrees with the table's footer.
  const subtotal = useMemo(
    () => items.reduce((sum, item) => sum + computeLineTotal(item), 0),
    [items],
  );

  const transactionTotal = useMemo(
    () => transactions.reduce((sum, t) => sum + t.amount, 0),
    [transactions],
  );

  const { isSubmitting, submit: handleSubmit } = useReceiptSubmit({
    form,
    transactions,
    items,
  });

  // Derived: should we show the optional sections?
  const metadata = initialValues?.metadata;
  const hasMetadata =
    !!metadata &&
    (metadata.receiptId !== "" ||
      metadata.storeNumber !== "" ||
      metadata.terminalId !== "");

  const hasData =
    location !== "" ||
    receiptDate !== "" ||
    taxAmount !== 0 ||
    transactions.length > 0 ||
    items.length > 0 ||
    (storeAddress ?? "") !== "" ||
    (storePhone ?? "") !== "";

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

  const droppedPages = droppedPageCount ?? 0;

  return (
    <div className="space-y-6">
      <h1 className="text-3xl font-bold tracking-tight">
        {pageTitle ?? "New Receipt"}
      </h1>

      {droppedPages > 0 && (
        <Alert data-testid="dropped-pages-warning">
          <AlertTitle>Multi-page PDF: only page 1 was scanned</AlertTitle>
          <AlertDescription>
            {droppedPages === 1
              ? "This PDF had 2 pages, but only page 1 was extracted. Review the proposal and add any missing details from page 2 manually."
              : `This PDF had ${droppedPages + 1} pages, but only page 1 was extracted. Review the proposal and add any missing details from pages 2-${droppedPages + 1} manually.`}
          </AlertDescription>
        </Alert>
      )}

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-[1fr_280px]">
        {/* Left column — form sections */}
        <div className="space-y-6">
          <HeaderSection
            form={form}
            locationOptions={locationOptions}
            locationRef={locationRef}
            confidenceMap={confidenceMap}
          />

          {/* Receipt Details — read-only metadata, only shown when populated */}
          {hasMetadata && (
            <ReceiptDetailsPanel
              metadata={metadata!}
              confidenceMap={confidenceMap}
            />
          )}

          {/* Transactions */}
          <TransactionsSection
            transactions={transactions}
            defaultDate={receiptDate}
            onChange={setTransactions}
            confidenceById={transactionConfidenceById}
          />

          {/* Line Items */}
          <LineItemsSection
            items={items}
            onChange={setItems}
            location={location}
            itemConfidenceById={itemConfidenceById}
          />
        </div>

        {/* Right column — sticky balance sidebar */}
        <div className="hidden lg:block">
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

      {/* Mobile-only bottom bar (visible on small screens) */}
      <div className="lg:hidden">
        <BalanceSidebar
          subtotal={subtotal}
          taxAmount={taxAmount}
          transactionTotal={transactionTotal}
          isSubmitting={isSubmitting}
          onSubmit={handleSubmit}
          onCancel={handleCancel}
        />
      </div>

      <DiscardReceiptDialog
        open={showDiscard}
        onOpenChange={setShowDiscard}
        onDiscard={handleDiscard}
      />
    </div>
  );
}
