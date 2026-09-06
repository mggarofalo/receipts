import { useQueryClient } from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import type { components } from "@/generated/api";
import client from "@/lib/api-client";
import { invalidateDomainChange } from "@/lib/query-invalidation";

interface CategorizeReceiptItemsInput {
  items: components["schemas"]["UncategorizedItem"][];
  category: string;
  subcategory: string | null;
}

interface CategorizeReceiptItemsCallbacks {
  onSuccess?: () => void;
  onError?: (error: unknown) => void;
}

/** Own the grouped write and repair projections even when only some groups commit. */
export function useCategorizeReceiptItems(callbacks: CategorizeReceiptItemsCallbacks = {}) {
  const queryClient = useQueryClient();
  return useSessionMutation({
    mutationFn: async ({ items, category, subcategory }: CategorizeReceiptItemsInput) => {
      const grouped = new Map<string, CategorizeReceiptItemsInput["items"]>();
      for (const item of items) {
        const group = grouped.get(item.receiptId) ?? [];
        group.push(item);
        grouped.set(item.receiptId, group);
      }

      // A transport rejection must not trigger cache repair before another group
      // finishes committing. Keep every request observed and await all settlements.
      const results = await Promise.allSettled(Array.from(grouped.values(), async (groupItems) => {
        const { error } = await client.PUT("/api/receipt-items/batch", {
          body: groupItems.map((item) => ({
            id: item.id,
            receiptItemCode: item.receiptItemCode ?? null,
            description: item.description,
            quantity: item.quantity,
            unitPrice: item.unitPrice,
            category,
            subcategory: subcategory || null,
          })),
        });
        if (error) throw error;
      }));
      const failure = results.find((result) => result.status === "rejected");
      if (failure) throw failure.reason;
    },
    onSettled: () => invalidateDomainChange(queryClient, "receipt-item"),
    onSuccess: callbacks.onSuccess,
    onError: callbacks.onError,
  });
}
