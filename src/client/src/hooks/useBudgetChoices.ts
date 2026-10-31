import { useQueryClient } from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import { toastErrorPolicy } from "@/lib/request-error-policy";
import { invalidateDomainChange } from "@/lib/query-invalidation";
import client from "@/lib/api-client";

/**
 * Null envelope is automatic; null occasion is none. Both are replaced, so the
 * mutation stays pending until the refetch lands: a follow-up choice must not
 * resend the other choice from stale data.
 */
export function useSetReceiptBudgetChoices() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async ({
      receiptId,
      paidFromEnvelopeId,
      occasionId,
    }: {
      receiptId: string;
      paidFromEnvelopeId: string | null;
      occasionId: string | null;
    }) => {
      const { error } = await client.PUT("/api/receipts/{id}/budget-choices", {
        ...toastErrorPolicy.request,
        params: { path: { id: receiptId } },
        body: { paidFromEnvelopeId, occasionId },
      });
      if (error) throw error;
    },
    onSuccess: () => invalidateDomainChange(queryClient, "receipt"),
  });
}

/** Null envelope returns the item to automatic; null occasion is none. Pending until refetched. */
export function useSetReceiptItemBudgetChoices() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async ({
      itemId,
      envelopeId,
      occasionId,
    }: {
      itemId: string;
      envelopeId: string | null;
      occasionId: string | null;
    }) => {
      const { error } = await client.PUT(
        "/api/receipt-items/{id}/budget-choices",
        {
          ...toastErrorPolicy.request,
          params: { path: { id: itemId } },
          body: { envelopeId, occasionId },
        },
      );
      if (error) throw error;
    },
    onSuccess: () => invalidateDomainChange(queryClient, "receipt-item"),
  });
}
