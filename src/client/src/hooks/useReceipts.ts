import { localErrorPolicy, toastErrorPolicy } from "@/lib/request-error-policy";
import { invalidateDomainChange, queryKeys } from "@/lib/query-invalidation";
import { useMemo } from "react";
import { useStableQuery } from "@/hooks/useStableQuery";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import client from "@/lib/api-client";
import { toast } from "sonner";
import type { components } from "@/generated/api";

type CreateCompleteReceiptRequest =
  components["schemas"]["CreateCompleteReceiptRequest"];

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
      ...queryKeys.receipts,
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

export function useReceipt(id: string | null, options: { enabled?: boolean } = {}) {
  return useQuery({
    ...localErrorPolicy.query,
    queryKey: [...queryKeys.receipts, id],
    enabled: !!id && (options.enabled ?? true),
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET("/api/receipts/{id}", {
        params: { path: { id: id! } },
        signal,
        ...localErrorPolicy.request,
      });
      if (error) throw error;
      return data;
    },
  });
}

export function useCreateReceipt() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async (body: {
      description?: string | null;
      location: string;
      date: string;
      taxAmount: number;
    }) => {
      const { data, error } = await client.POST("/api/receipts", { ...toastErrorPolicy.request, body });
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      invalidateDomainChange(queryClient, "receipt");
      toast.success("Receipt created");
    },
  });
}

export function useUpdateReceipt() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async (body: {
      id: string;
      description?: string | null;
      location: string;
      date: string;
      taxAmount: number;
    }) => {
      const { error } = await client.PUT("/api/receipts/{id}", {
        ...toastErrorPolicy.request,
        params: { path: { id: body.id } },
        body,
      });
      if (error) throw error;
    },
    onSuccess: () => {
      invalidateDomainChange(queryClient, "receipt");
      toast.success("Receipt updated");
    },
  });
}

export function useDeleteReceipts() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async (ids: string[]) => {
      const { error } = await client.DELETE("/api/receipts", { ...toastErrorPolicy.request, body: ids });
      if (error) throw error;
    },
    onMutate: async (ids) => {
      await queryClient.cancelQueries({ queryKey: [...queryKeys.receipts] });
      const previous = queryClient.getQueriesData<{ data: { id: string }[]; total: number }>({ queryKey: [...queryKeys.receipts, "list"] });
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
      invalidateDomainChange(queryClient, "receipt");
    },
    onSuccess: () => {
      toast.success("Receipt(s) deleted");
    },
  });
}

export function useDeletedReceipts(offset = 0, limit = 50, sortBy?: string | null, sortDirection?: string | null) {
  const query = useQuery({
    queryKey: [...queryKeys.receipts, "deleted", offset, limit, sortBy, sortDirection],
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
  return useSessionMutation({
    // The creation page owns its inline summary and error toast.
    ...localErrorPolicy.mutation,
    mutationFn: async (body: CreateCompleteReceiptRequest) => {
      const { data, error } = await client.POST("/api/receipts/complete", { ...localErrorPolicy.request, body });
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      invalidateDomainChange(queryClient, "receipt");
    },
  });
}

export function useLocationSuggestions(query: string) {
  return useQuery({
    ...localErrorPolicy.query,
    queryKey: [...queryKeys.receipts, "locations", query],
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET("/api/receipts/locations", {
        ...localErrorPolicy.request,
        signal,
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
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async (id: string) => {
      const { error } = await client.POST("/api/receipts/{id}/restore", {
        ...toastErrorPolicy.request,
        params: { path: { id } },
      });
      if (error) throw error;
    },
    onSuccess: () => {
      invalidateDomainChange(queryClient, "receipt");
      toast.success("Receipt restored");
    },
  });
}
