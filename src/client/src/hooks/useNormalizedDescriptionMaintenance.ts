import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { parseProblemDetails, toApiError } from "@/lib/problem-details";
import { toast } from "sonner";

export const requeuePreviewQueryKey = [
  "normalized-descriptions",
  "requeue-pending",
  "preview",
] as const;

export function useRequeuePendingPreview() {
  return useQuery({
    queryKey: requeuePreviewQueryKey,
    queryFn: async () => {
      const { data, error, response } = await client.GET(
        "/api/normalized-descriptions/requeue-pending/preview",
      );
      // Admin-gated: a 403 arrives with no body, which openapi-fetch surfaces as
      // `error: undefined`. Branching on `error` alone would report an empty
      // preview as a successful "nothing to requeue".
      if (!response.ok) throw toApiError(response.status, error);
      return data;
    },
  });
}

export function useRequeuePendingMutation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({
      expectedFingerprint,
    }: {
      expectedFingerprint: string;
    }) => {
      const { data, error, response } = await client.POST(
        "/api/normalized-descriptions/requeue-pending",
        { body: { expectedFingerprint } },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data;
    },
    onSuccess: (data) => {
      // The queue, the registry and every receipt item's normalized name all just
      // changed underneath the cache.
      queryClient.invalidateQueries({ queryKey: ["normalized-descriptions"] });
      queryClient.invalidateQueries({ queryKey: ["receipt-items"] });
      const deleted = data?.deletedDescriptionCount ?? 0;
      const unlinked = data?.unlinkedItemCount ?? 0;
      if (deleted === 0) {
        toast.success("Nothing to requeue — no pending descriptions");
      } else {
        toast.success(
          `Requeued ${deleted} pending ${
            deleted === 1 ? "description" : "descriptions"
          } — ${unlinked} ${unlinked === 1 ? "item" : "items"} awaiting re-resolution`,
        );
      }
    },
    onError: (error: unknown) => {
      // No toast here — the global MutationCache handler already surfaces the server's
      // message, and a second one would double up (RECEIPTS-782). What this hook adds is
      // the refetch: a 409 means the pending set moved, so the rows on screen are stale
      // and "try again" is the wrong instinct. Pull the real ones before the operator
      // confirms anything.
      if (parseProblemDetails(error)?.status === 409) {
        queryClient.invalidateQueries({ queryKey: requeuePreviewQueryKey });
      }
    },
  });
}
