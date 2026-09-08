import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import { toast } from "sonner";
import client from "@/lib/api-client";
import { queryKeys, repairDomainChange } from "@/lib/query-invalidation";
import { getSessionVersion } from "@/lib/auth";

export const ACCEPTED_DUPLICATES_QUERY_KEY = [
  ...queryKeys.acceptedDuplicates,
] as const;

function repairDuplicateAcceptance(queryClient: ReturnType<typeof useQueryClient>) {
  const sessionVersion = getSessionVersion();
  return repairDomainChange(
    queryClient,
    "duplicate-acceptance",
    () => getSessionVersion() === sessionVersion,
  );
}

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

/** Accept a group: record every pair of its receipts as "not a duplicate". */
export function useAcceptDuplicateGroup() {
  const queryClient = useQueryClient();
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
      toast.success("Marked as not duplicates — this group won't be reported again");
      return repairDuplicateAcceptance(queryClient);
    },
  });
}

/** Undo an acceptance so the group is reported again. */
export function useUnacceptDuplicateGroup() {
  const queryClient = useQueryClient();
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
      toast.success("Acceptance undone — this group will be reported again");
      return repairDuplicateAcceptance(queryClient);
    },
  });
}
