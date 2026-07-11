import { useMutation, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
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
      const { data, error } = await client.POST(
        "/api/normalized-descriptions/{id}/merge",
        {
          params: { path: { id } },
          body: { discardId },
        },
      );
      if (error) throw error;
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
      const { data, error } = await client.POST(
        "/api/normalized-descriptions/{id}/split",
        {
          params: { path: { id } },
          body: { receiptItemId },
        },
      );
      if (error) throw error;
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
      const { error } = await client.PATCH(
        "/api/normalized-descriptions/{id}/status",
        {
          params: { path: { id } },
          body: { status },
        },
      );
      if (error) throw error;
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
