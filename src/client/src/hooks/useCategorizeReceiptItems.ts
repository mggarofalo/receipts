import { toast } from "sonner";
import { localErrorPolicy } from "@/lib/request-error-policy";
import { useQueryClient } from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import type { components } from "@/generated/api";
import type { CategoryChoice } from "@/lib/category-paths";
import { generateId } from "@/lib/id";
import client from "@/lib/api-client";
import { invalidateDomainChange } from "@/lib/query-invalidation";

interface CategorizeReceiptItemsInput {
  items: components["schemas"]["UncategorizedItem"][];
  choice: CategoryChoice;
}

interface CategorizeReceiptItemsCallbacks {
  onSuccess?: () => void;
  onError?: (error: unknown) => void;
}

/** Own the grouped write and repair projections even when only some groups commit. */
export function useCategorizeReceiptItems(
  callbacks: CategorizeReceiptItemsCallbacks = {},
) {
  const queryClient = useQueryClient();
  return useSessionMutation({
    ...localErrorPolicy.mutation,
    mutationFn: async ({ items, choice }: CategorizeReceiptItemsInput) => {
      // Each item has its own current-assignment fence. Observe all outcomes
      // before refreshing the report so partial successes remain visible.
      const results = await Promise.allSettled(
        items.map(async (item) => {
          const { error } = await client.PUT(
            "/api/receipt-items/{id}/classification",
            {
              ...localErrorPolicy.request,
              params: { path: { id: item.id } },
              body: {
                categoryId: choice.categoryId,
                taxonomyRevisionId: choice.taxonomyRevisionId,
                expectedAssignmentId: item.assignmentId ?? null,
                operationId: generateId(),
                reason: "Category selected from unclassified items report",
              },
            },
          );
          if (error) throw error;
        }),
      );
      const failures = results.filter((result) => result.status === "rejected");
      if (failures.length)
        throw new Error(
          `${failures.length} item classifications could not be confirmed. Refresh and review before retrying.`,
        );
    },
    onSettled: () => invalidateDomainChange(queryClient, "receipt-item"),
    onSuccess: callbacks.onSuccess,
    onError: (error: unknown) => {
      toast.error(
        error instanceof Error
          ? error.message
          : "Item classifications could not be confirmed. Refresh and review before retrying.",
      );
      callbacks.onError?.(error);
    },
  });
}
