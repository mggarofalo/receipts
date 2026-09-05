import { useCallback } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import { toast } from "sonner";
import client from "@/lib/api-client";

export const ACCEPTED_DUPLICATES_QUERY_KEY = [
  "reports",
  "accepted-duplicates",
] as const;

/** Groups the user has accepted as genuinely separate purchases. */
export function useAcceptedDuplicates() {
  return useQuery({
    queryKey: ACCEPTED_DUPLICATES_QUERY_KEY,
    queryFn: async () => {
      const { data, error } = await client.GET(
        "/api/reports/duplicates/accepted",
      );
      if (error) throw error;
      return data;
    },
  });
}

/**
 * Both mutations refresh the same two caches. Memoized because functions returned from a custom
 * hook must be referentially stable (docs/react/custom-hooks.md) — an unstable reference that
 * later reaches a dependency array is how render loops start.
 */
function useInvalidateDuplicates() {
  const queryClient = useQueryClient();
  return useCallback(() => {
    queryClient.invalidateQueries({ queryKey: ["reports", "duplicates"] });
    queryClient.invalidateQueries({ queryKey: ACCEPTED_DUPLICATES_QUERY_KEY });
  }, [queryClient]);
}

/** Accept a group: record every pair of its receipts as "not a duplicate". */
export function useAcceptDuplicateGroup() {
  const invalidate = useInvalidateDuplicates();
  return useSessionMutation({
    mutationFn: async (receiptIds: string[]) => {
      const { data, error } = await client.POST(
        "/api/reports/duplicates/accepted",
        { body: { receiptIds } },
      );
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      invalidate();
      toast.success("Marked as not duplicates — this group won't be reported again");
    },
  });
}

/** Undo an acceptance so the group is reported again. */
export function useUnacceptDuplicateGroup() {
  const invalidate = useInvalidateDuplicates();
  return useSessionMutation({
    mutationFn: async (receiptIds: string[]) => {
      const { data, error } = await client.POST(
        "/api/reports/duplicates/accepted/remove",
        { body: { receiptIds } },
      );
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      invalidate();
      toast.success("Acceptance undone — this group will be reported again");
    },
  });
}
