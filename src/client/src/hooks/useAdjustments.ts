import { toastErrorPolicy } from "@/lib/request-error-policy";
import { invalidateDomainChange, queryKeys } from "@/lib/query-invalidation";
import { useQueryClient } from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import client from "@/lib/api-client";
import { toast } from "sonner";

export function useCreateAdjustment() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async ({
      receiptId,
      body,
    }: {
      receiptId: string;
      body: {
        type: string;
        amount: number;
        description?: string | null;
      };
    }) => {
      const { data, error } = await client.POST(
        "/api/receipts/{receiptId}/adjustments",
        { ...toastErrorPolicy.request, params: { path: { receiptId } }, body },
      );
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      invalidateDomainChange(queryClient, "adjustment");
      toast.success("Adjustment created");
    },
  });
}

export function useUpdateAdjustment() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async ({
      body,
    }: {
      body: {
        id: string;
        type: string;
        amount: number;
        description?: string | null;
      };
    }) => {
      const { error } = await client.PUT("/api/adjustments/{id}", {
        ...toastErrorPolicy.request,
        params: { path: { id: body.id } },
        body,
      });
      if (error) throw error;
    },
    onSuccess: () => {
      invalidateDomainChange(queryClient, "adjustment");
      toast.success("Adjustment updated");
    },
  });
}

export function useDeleteAdjustments() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async (ids: string[]) => {
      const { error } = await client.DELETE("/api/adjustments", {
        ...toastErrorPolicy.request,
        body: ids,
      });
      if (error) throw error;
    },
    onMutate: async (ids) => {
      await queryClient.cancelQueries({ queryKey: [...queryKeys.adjustments] });
      const previous = queryClient.getQueriesData<{
        data: { id: string }[];
        total: number;
      }>({ queryKey: [...queryKeys.adjustments, "list"] });
      for (const [key] of previous) {
        queryClient.setQueryData(
          key,
          (
            old:
              | {
                  data: { id: string }[];
                  total: number;
                  offset: number;
                  limit: number;
                }
              | undefined,
          ) => {
            if (!old?.data) return old;
            const filtered = old.data.filter((item) => !ids.includes(item.id));
            return {
              ...old,
              data: filtered,
              total: old.total - (old.data.length - filtered.length),
            };
          },
        );
      }
      return { previous };
    },
    onError: (_err, _ids, context) => {
      for (const [key, data] of context?.previous ?? []) {
        queryClient.setQueryData(key, data);
      }
    },
    onSettled: () => {
      invalidateDomainChange(queryClient, "adjustment");
    },
    onSuccess: () => {
      toast.success("Adjustment(s) deleted");
    },
  });
}
