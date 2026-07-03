import { useMemo } from "react";
import { useStableQuery } from "@/hooks/useStableQuery";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { toast } from "sonner";

export function useAdjustments(offset = 0, limit = 50, sortBy?: string | null, sortDirection?: string | null) {
  const query = useQuery({
    queryKey: ["adjustments", "list", offset, limit, sortBy, sortDirection],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/adjustments", {
        params: { query: { offset, limit, sortBy: sortBy ?? undefined, sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined } },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: query.data?.total ?? 0 }), [base, query.data]);
}

export function useAdjustment(id: string | null) {
  return useQuery({
    queryKey: ["adjustments", id],
    enabled: !!id,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/adjustments/{id}", {
        params: { path: { id: id! } },
      });
      if (error) throw error;
      return data;
    },
  });
}

export function useAdjustmentsByReceiptId(receiptId: string | null, offset = 0, limit = 200, sortBy?: string | null, sortDirection?: string | null) {
  const query = useQuery({
    queryKey: ["adjustments", "by-receipt", receiptId, offset, limit, sortBy, sortDirection],
    enabled: !!receiptId,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/adjustments", {
        params: { query: { receiptId: receiptId!, offset, limit, sortBy: sortBy ?? undefined, sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined } },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: query.data?.total ?? 0 }), [base, query.data]);
}

export function useCreateAdjustment() {
  const queryClient = useQueryClient();
  return useMutation({
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
        { params: { path: { receiptId } }, body },
      );
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["adjustments"] });
      queryClient.invalidateQueries({ queryKey: ["receipts-with-items"] });
      queryClient.invalidateQueries({ queryKey: ["trips"] });
      toast.success("Adjustment created");
    },
    onError: () => {
      toast.error("Failed to create adjustment");
    },
  });
}

export function useUpdateAdjustment() {
  const queryClient = useQueryClient();
  return useMutation({
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
        params: { path: { id: body.id } },
        body,
      });
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["adjustments"] });
      queryClient.invalidateQueries({ queryKey: ["receipts-with-items"] });
      queryClient.invalidateQueries({ queryKey: ["trips"] });
      toast.success("Adjustment updated");
    },
    onError: () => {
      toast.error("Failed to update adjustment");
    },
  });
}

export function useDeleteAdjustments() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (ids: string[]) => {
      const { error } = await client.DELETE("/api/adjustments", {
        body: ids,
      });
      if (error) throw error;
    },
    onMutate: async (ids) => {
      await queryClient.cancelQueries({ queryKey: ["adjustments"] });
      const previous = queryClient.getQueriesData<{ data: { id: string }[]; total: number }>({ queryKey: ["adjustments", "list"] });
      for (const [key] of previous) {
        queryClient.setQueryData(key, (old: { data: { id: string }[]; total: number; offset: number; limit: number } | undefined) => {
          if (!old?.data) return old;
          const filtered = old.data.filter((item) => !ids.includes(item.id));
          return { ...old, data: filtered, total: old.total - (old.data.length - filtered.length) };
        });
      }
      return { previous };
    },
    onError: (_err, _ids, context) => {
      for (const [key, data] of context?.previous ?? []) {
        queryClient.setQueryData(key, data);
      }
      toast.error("Failed to delete adjustment(s)");
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ["adjustments"] });
      queryClient.invalidateQueries({ queryKey: ["adjustments", "deleted"] });
      queryClient.invalidateQueries({ queryKey: ["receipts-with-items"] });
      queryClient.invalidateQueries({ queryKey: ["trips"] });
    },
    onSuccess: () => {
      toast.success("Adjustment(s) deleted");
    },
  });
}

export function useDeletedAdjustments(offset = 0, limit = 50, sortBy?: string | null, sortDirection?: string | null) {
  const query = useQuery({
    queryKey: ["adjustments", "deleted", offset, limit, sortBy, sortDirection],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/adjustments/deleted", {
        params: { query: { offset, limit, sortBy: sortBy ?? undefined, sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined } },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: query.data?.total ?? 0 }), [base, query.data]);
}

export function useRestoreAdjustment() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      const { error } = await client.POST("/api/adjustments/{id}/restore", {
        params: { path: { id } },
      });
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["adjustments"] });
      queryClient.invalidateQueries({ queryKey: ["adjustments", "deleted"] });
      queryClient.invalidateQueries({ queryKey: ["receipts-with-items"] });
      queryClient.invalidateQueries({ queryKey: ["trips"] });
      toast.success("Adjustment restored");
    },
    onError: () => {
      toast.error("Failed to restore adjustment");
    },
  });
}
