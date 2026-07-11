import { useMemo } from "react";
import { useStableQuery } from "@/hooks/useStableQuery";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { toast } from "sonner";

export function useReceiptItems(offset = 0, limit = 50, sortBy?: string | null, sortDirection?: string | null, q?: string | null) {
  const trimmedQ = q?.trim() || undefined;
  const query = useQuery({
    queryKey: ["receipt-items", "list", offset, limit, sortBy, sortDirection, trimmedQ],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/receipt-items", {
        params: { query: { offset, limit, sortBy: sortBy ?? undefined, sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined, q: trimmedQ } },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: query.data?.total ?? 0 }), [base, query.data]);
}

export function useReceiptItem(id: string | null) {
  return useQuery({
    queryKey: ["receipt-items", id],
    enabled: !!id,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/receipt-items/{id}", {
        params: { path: { id: id! } },
      });
      if (error) throw error;
      return data;
    },
  });
}

export function useReceiptItemsByReceiptId(receiptId: string | null, offset = 0, limit = 200, sortBy?: string | null, sortDirection?: string | null) {
  const query = useQuery({
    queryKey: ["receipt-items", "by-receipt", receiptId, offset, limit, sortBy, sortDirection],
    enabled: !!receiptId,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/receipt-items", {
        params: { query: { receiptId: receiptId!, offset, limit, sortBy: sortBy ?? undefined, sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined } },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: query.data?.total ?? 0 }), [base, query.data]);
}

export function useCreateReceiptItem() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({
      receiptId,
      body,
    }: {
      receiptId: string;
      body: {
        receiptItemCode: string;
        description: string;
        quantity: number;
        unitPrice: number;
        category: string;
        subcategory: string;
      };
    }) => {
      const { data, error } = await client.POST(
        "/api/receipts/{receiptId}/receipt-items",
        { params: { path: { receiptId } }, body },
      );
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["receipt-items"] });
      toast.success("Receipt item created");
    },
  });
}

export function useCreateReceiptItemsBatch() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({
      receiptId,
      body,
    }: {
      receiptId: string;
      body: {
        receiptItemCode: string;
        description: string;
        quantity: number;
        unitPrice: number;
        category: string;
        subcategory: string;
      }[];
    }) => {
      const { data, error } = await client.POST(
        "/api/receipts/{receiptId}/receipt-items/batch",
        { params: { path: { receiptId } }, body },
      );
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["receipt-items"] });
    },
  });
}

export function useUpdateReceiptItem() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({
      body,
    }: {
      body: {
        id: string;
        receiptItemCode: string;
        description: string;
        quantity: number;
        unitPrice: number;
        category: string;
        subcategory: string;
      };
    }) => {
      const { error } = await client.PUT("/api/receipt-items/{id}", {
        params: { path: { id: body.id } },
        body,
      });
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["receipt-items"] });
      toast.success("Receipt item updated");
    },
  });
}

export function useDeleteReceiptItems() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (ids: string[]) => {
      const { error } = await client.DELETE("/api/receipt-items", {
        body: ids,
      });
      if (error) throw error;
    },
    onMutate: async (ids) => {
      await queryClient.cancelQueries({ queryKey: ["receipt-items"] });
      const previous = queryClient.getQueriesData<{ data: { id: string }[]; total: number }>({ queryKey: ["receipt-items", "list"] });
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
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ["receipt-items"] });
      queryClient.invalidateQueries({ queryKey: ["receipt-items", "deleted"] });
    },
    onSuccess: () => {
      toast.success("Receipt item(s) deleted");
    },
  });
}

export function useDeletedReceiptItems(offset = 0, limit = 50, sortBy?: string | null, sortDirection?: string | null) {
  const query = useQuery({
    queryKey: ["receipt-items", "deleted", offset, limit, sortBy, sortDirection],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/receipt-items/deleted", {
        params: { query: { offset, limit, sortBy: sortBy ?? undefined, sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined } },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: query.data?.total ?? 0 }), [base, query.data]);
}

export function useRestoreReceiptItem() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      const { error } = await client.POST("/api/receipt-items/{id}/restore", {
        params: { path: { id } },
      });
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["receipt-items"] });
      queryClient.invalidateQueries({ queryKey: ["receipt-items", "deleted"] });
      toast.success("Receipt item restored");
    },
  });
}
