import { useCallback, useMemo, useState } from "react";
import { useNavigate } from "react-router";
import type { UseFormReturn } from "react-hook-form";
import { toast } from "sonner";
import { useCreateCompleteReceipt } from "@/hooks/useReceipts";
import { useLocationHistory } from "@/hooks/useLocationHistory";
import type { ReceiptTransaction } from "./TransactionsSection";
import type { ReceiptLineItem } from "./LineItemsSection";
import type { HeaderFormValues } from "./headerSchema";

interface UseReceiptSubmitOptions {
  form: UseFormReturn<HeaderFormValues>;
  transactions: ReceiptTransaction[];
  items: ReceiptLineItem[];
}

interface UseReceiptSubmitResult {
  isSubmitting: boolean;
  submit: () => Promise<void>;
}

/**
 * Encapsulates the multi-step submit flow for the new-receipt wizard:
 * header validation, transaction/item presence checks, persisted-location
 * history, the create-complete-receipt mutation, success/failure toasts,
 * and the final navigate to the new receipt.
 *
 * NOTE: storeAddress / storePhone / payments / metadata / taxCode are
 * accepted and reviewable in the UI but not yet persisted by the
 * `CreateCompleteReceipt` API. They will round-trip once the backend is
 * extended (tracked by separate issues under the VLM epic).
 */
export function useReceiptSubmit({
  form,
  transactions,
  items,
}: UseReceiptSubmitOptions): UseReceiptSubmitResult {
  const navigate = useNavigate();
  const { add: addLocation } = useLocationHistory();
  const { mutateAsync: createCompleteReceiptAsync } =
    useCreateCompleteReceipt();
  const [isSubmitting, setIsSubmitting] = useState(false);

  const submit = useCallback(async () => {
    const valid = await form.trigger();
    if (!valid) return;

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
          // Send totalPrice as a first-class field so flat-priced items (where
          // unitPrice is 0) round-trip with the correct dollar value. For
          // quantity-priced items the server still computes the same value, so
          // sending it explicitly is harmless and keeps the wire shape uniform.
          // See RECEIPTS-655.
          totalPrice: item.totalPrice,
          category: item.category,
          subcategory: item.subcategory,
          pricingMode: item.pricingMode,
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
  ]);

  return useMemo(() => ({ isSubmitting, submit }), [isSubmitting, submit]);
}
