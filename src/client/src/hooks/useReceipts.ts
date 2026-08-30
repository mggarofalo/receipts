import { useMemo } from "react";
import { useStableQuery } from "@/hooks/useStableQuery";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { toast } from "sonner";

export function useReceipts(
  offset = 0,
  limit = 50,
  sortBy?: string | null,
  sortDirection?: string | null,
  accountId?: string | null,
  cardId?: string | null,
  q?: string | null,
  options: { enabled?: boolean; location?: string | null } = {},
) {
  const { enabled = true, location } = options;
  const trimmedQ = q?.trim() || undefined;
  // Exact-match location filter, distinct from `q`'s substring search. Set by
  // report drill-downs (RECEIPTS-841) so the list shows exactly the receipts
  // the aggregate row counted. Passed through verbatim — NOT trimmed like `q` —
  // because the server matches it byte-for-byte against the same raw Location
  // value the report grouped on, whitespace included.
  const exactLocation = location || undefined;
  const query = useQuery({
    queryKey: [
      "receipts",
      "list",
      offset,
      limit,
      sortBy,
      sortDirection,
      accountId,
      cardId,
      trimmedQ,
      exactLocation,
    ],
    enabled,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/receipts", {
        params: {
          query: {
            offset,
            limit,
            sortBy: sortBy ?? undefined,
            sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined,
            accountId: accountId ?? undefined,
            cardId: cardId ?? undefined,
            q: trimmedQ,
            location: exactLocation,
          },
        },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: Number(query.data?.total ?? 0) }), [base, query.data]);
}

/** Fetches the complete receipt list for pickers. */
export function useAllReceipts(options: { enabled?: boolean } = {}) {
  return useQuery({
    queryKey: ["receipts", "all"],
    enabled: options.enabled ?? true,
    queryFn: async ({ signal }) => {
      const pageSize = 500;
      const fetchPage = async (offset: number) => {
        const { data, error } = await client.GET("/api/receipts", {
          params: {
            query: {
              offset,
              limit: pageSize,
              sortBy: "date",
              sortDirection: "desc",
            },
          },
          signal,
        });
        if (error) throw error;
        return data;
      };

      const first = await fetchPage(0);
      const all = [...(first?.data ?? [])];
      const total = Number(first?.total ?? all.length);
      for (let offset = pageSize; offset < total; offset += pageSize) {
        const page = await fetchPage(offset);
        all.push(...(page?.data ?? []));
      }
      return all;
    },
  });
}

export function useReceipt(id: string | null) {
  return useQuery({
    queryKey: ["receipts", id],
    enabled: !!id,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/receipts/{id}", {
        params: { path: { id: id! } },
      });
      if (error) throw error;
      return data;
    },
  });
}

export function useCreateReceipt() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: {
      description?: string | null;
      location: string;
      date: string;
      taxAmount: number;
    }) => {
      const { data, error } = await client.POST("/api/receipts", { body });
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["receipts"] });
      toast.success("Receipt created");
    },
  });
}

export function useUpdateReceipt() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: {
      id: string;
      description?: string | null;
      location: string;
      date: string;
      taxAmount: number;
    }) => {
      const { error } = await client.PUT("/api/receipts/{id}", {
        params: { path: { id: body.id } },
        body,
      });
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["receipts"] });
      queryClient.invalidateQueries({ queryKey: ["trips"] });
      toast.success("Receipt updated");
    },
  });
}

export function useDeleteReceipts() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (ids: string[]) => {
      const { error } = await client.DELETE("/api/receipts", { body: ids });
      if (error) throw error;
    },
    onMutate: async (ids) => {
      await queryClient.cancelQueries({ queryKey: ["receipts"] });
      const previous = queryClient.getQueriesData<{ data: { id: string }[]; total: number }>({ queryKey: ["receipts", "list"] });
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
      queryClient.invalidateQueries({ queryKey: ["receipts"] });
      queryClient.invalidateQueries({ queryKey: ["receipts", "deleted"] });
    },
    onSuccess: () => {
      toast.success("Receipt(s) deleted");
    },
  });
}

export function useDeletedReceipts(offset = 0, limit = 50, sortBy?: string | null, sortDirection?: string | null) {
  const query = useQuery({
    queryKey: ["receipts", "deleted", offset, limit, sortBy, sortDirection],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/receipts/deleted", {
        params: { query: { offset, limit, sortBy: sortBy ?? undefined, sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined } },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: Number(query.data?.total ?? 0) }), [base, query.data]);
}

export function useCreateCompleteReceipt() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: {
      receipt: { location: string; date: string; taxAmount: number };
      transactions: { amount: number; date: string; accountId: string; cardId: string }[];
      items: {
        receiptItemCode: string;
        description: string;
        quantity: number;
        unitPrice: number;
        category: string;
        subcategory: string;
      }[];
    }) => {
      const { data, error } = await client.POST("/api/receipts/complete", { body });
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["receipts"] });
      queryClient.invalidateQueries({ queryKey: ["transactions"] });
      queryClient.invalidateQueries({ queryKey: ["receipt-items"] });
    },
  });
}

export function useLocationSuggestions(query: string) {
  return useQuery({
    queryKey: ["receipts", "locations", query],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/receipts/locations", {
        params: { query: { q: query || undefined, limit: 20 } },
      });
      if (error) throw error;
      return data?.locations ?? [];
    },
    staleTime: 5 * 60 * 1000,
  });
}

export function useRestoreReceipt() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      const { error } = await client.POST("/api/receipts/{id}/restore", {
        params: { path: { id } },
      });
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["receipts"] });
      queryClient.invalidateQueries({ queryKey: ["receipts", "deleted"] });
      toast.success("Receipt restored");
    },
  });
}
