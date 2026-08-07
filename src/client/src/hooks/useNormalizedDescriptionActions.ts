import { useMutation, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { toApiError } from "@/lib/problem-details";
import { toast } from "sonner";
import type { components } from "@/generated/api";

type NormalizedDescriptionStatus =
  components["schemas"]["NormalizedDescriptionStatus"];

export function useMergeMutation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({
      id,
      discardId,
    }: {
      id: string;
      discardId: string;
    }) => {
      const { data, error, response } = await client.POST(
        "/api/normalized-descriptions/{id}/merge",
        {
          params: { path: { id } },
          body: { discardId },
        },
      );
      // Branch on status: the endpoint is admin-gated, and a 403 arrives with
      // no body at all, which openapi-fetch surfaces as `error: undefined`.
      // Checking `error` alone would report the merge as successful. A 404 —
      // one of the two ids no longer exists — comes through the same path and
      // reaches the global handler as the server's problem document.
      if (!response.ok) throw toApiError(response.status, error);
      return data;
    },
    onSuccess: (data) => {
      queryClient.invalidateQueries({ queryKey: ["normalized-descriptions"] });
      queryClient.invalidateQueries({ queryKey: ["receipt-items"] });
      // Re-pointing items moves spend from one bucket to another, so any cached
      // spending-by-description page is now wrong.
      queryClient.invalidateQueries({ queryKey: ["reports"] });
      const count = data?.itemsRelinkedCount ?? 0;
      if (count > 0) {
        toast.success(`Merged — ${count} item${count === 1 ? "" : "s"} re-linked`);
      } else {
        // Zero now means one thing: the merge happened and the discarded row had
        // no live items to move. It used to also mean "one of those ids does not
        // exist", which the server answers with a 404 since RECEIPTS-891 — so
        // this message can finally claim the merge took place.
        toast.success("Merged — no items needed re-linking");
      }
    },
  });
}

export function useSplitMutation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({
      id,
      receiptItemId,
    }: {
      id: string;
      receiptItemId: string;
    }) => {
      const { data, error, response } = await client.POST(
        "/api/normalized-descriptions/{id}/split",
        {
          params: { path: { id } },
          body: { receiptItemId },
        },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["normalized-descriptions"] });
      queryClient.invalidateQueries({ queryKey: ["receipt-items"] });
      // A split creates a new bucket and shrinks the one it came out of.
      queryClient.invalidateQueries({ queryKey: ["reports"] });
      toast.success("Receipt item split into a new normalized description");
    },
  });
}

export function useUpdateStatusMutation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({
      id,
      status,
    }: {
      id: string;
      status: NormalizedDescriptionStatus;
    }) => {
      const { error, response } = await client.PATCH(
        "/api/normalized-descriptions/{id}/status",
        {
          params: { path: { id } },
          body: { status },
        },
      );
      // Success here is 204 (no body), so `error` is undefined on BOTH the
      // success and the bodiless-failure path — status is the only signal.
      if (!response.ok) throw toApiError(response.status, error);
    },
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ["normalized-descriptions"] });
      // Approval's whole point is that the spending report stops rendering this bucket as
      // provisional (RECEIPTS-875). Without this the report keeps its cached copy and the
      // badge lingers, which is precisely the "approve changes nothing you can see"
      // complaint the issue is about.
      queryClient.invalidateQueries({ queryKey: ["reports"] });
      toast.success(
        variables.status === "active"
          ? "Approved as active"
          : "Moved to pending review",
      );
    },
  });
}
