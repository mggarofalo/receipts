import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import client from "@/lib/api-client";
import { parseProblemDetails, toApiError } from "@/lib/problem-details";
import { toast } from "sonner";
import { repairDomainChange } from "@/lib/query-invalidation";
import { getSessionVersion } from "@/lib/auth";

export const promotionPreviewQueryKey = [
  "normalized-descriptions",
  "promotions",
  "preview",
] as const;

export const promotionHistoryQueryKey = [
  "normalized-descriptions",
  "promotions",
  "history",
] as const;

function plural(count: number, one: string, many: string) {
  return `${count} ${count === 1 ? one : many}`;
}

/** The pending-review names a promotion would make active (RECEIPTS-1107). */
export function usePendingNamePromotionPreview() {
  return useQuery({
    queryKey: promotionPreviewQueryKey,
    queryFn: async () => {
      const { data, error, response } = await client.GET(
        "/api/normalized-descriptions/promotions/preview",
      );
      // Admin-gated: a bodiless 403 must not read as "nothing to promote".
      if (!response.ok) throw toApiError(response.status, error);
      return data;
    },
  });
}

export function usePendingNamePromotions() {
  return useQuery({
    queryKey: promotionHistoryQueryKey,
    queryFn: async () => {
      const { data, error, response } = await client.GET(
        "/api/normalized-descriptions/promotions",
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data;
    },
  });
}

function repairAfterWrite(queryClient: ReturnType<typeof useQueryClient>) {
  const sessionVersion = getSessionVersion();
  return repairDomainChange(
    queryClient,
    "normalized-description",
    () => getSessionVersion() === sessionVersion,
  );
}

export function usePromotePendingNamesMutation() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    mutationFn: async ({
      expectedFingerprint,
    }: {
      expectedFingerprint: string;
    }) => {
      const { data, error, response } = await client.POST(
        "/api/normalized-descriptions/promotions",
        { body: { expectedFingerprint } },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data;
    },
    onSuccess: (data) => {
      const promoted = data?.promotedCount ?? 0;
      toast.success(
        promoted === 0
          ? "Nothing to promote"
          : `Promoted ${plural(promoted, "name", "names")} covering ${plural(data?.purchaseCount ?? 0, "purchase", "purchases")}`,
      );
    },
    onError: (error: unknown) => {
      // The global handler shows the server's message. A 409 means the eligible set moved,
      // so refetch it before the operator confirms again.
      if (parseProblemDetails(error)?.status === 409) {
        queryClient.invalidateQueries({ queryKey: promotionPreviewQueryKey });
      }
    },
    onSettled: () => repairAfterWrite(queryClient),
  });
}

export function useUndoPendingNamePromotionMutation() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    mutationFn: async ({ id }: { id: string }) => {
      const { data, error, response } = await client.POST(
        "/api/normalized-descriptions/promotions/{id}/undo",
        { params: { path: { id } } },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data;
    },
    onSuccess: (data) => {
      const restored = data?.restoredCount ?? 0;
      const kept = data?.keptCount ?? 0;
      toast.success(
        kept === 0
          ? `Returned ${plural(restored, "name", "names")} to review`
          : `Returned ${plural(restored, "name", "names")} to review; kept ${plural(kept, "changed name", "changed names")} active`,
      );
    },
    onSettled: () => repairAfterWrite(queryClient),
  });
}
