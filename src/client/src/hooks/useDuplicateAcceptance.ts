import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
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

function useInvalidateDuplicates() {
  const queryClient = useQueryClient();
  return () => {
    queryClient.invalidateQueries({ queryKey: ["reports", "duplicates"] });
    queryClient.invalidateQueries({ queryKey: ACCEPTED_DUPLICATES_QUERY_KEY });
  };
}

/** Accept a group: record every pair of its receipts as "not a duplicate". */
export function useAcceptDuplicateGroup() {
  const invalidate = useInvalidateDuplicates();
  return useMutation({
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
  return useMutation({
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
