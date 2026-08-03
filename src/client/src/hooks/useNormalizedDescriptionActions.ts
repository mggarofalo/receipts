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
      // Checking `error` alone would report the merge as successful.
      if (!response.ok) throw toApiError(response.status, error);
      return data;
    },
    onSuccess: (data) => {
      queryClient.invalidateQueries({ queryKey: ["normalized-descriptions"] });
      queryClient.invalidateQueries({ queryKey: ["receipt-items"] });
      const count = data?.itemsRelinkedCount ?? 0;
      if (count > 0) {
        toast.success(`Merged — ${count} items re-linked`);
      } else {
        toast.success("Merge completed");
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
      toast.success(
        variables.status === "active"
          ? "Approved as active"
          : "Moved to pending review",
      );
    },
  });
}
