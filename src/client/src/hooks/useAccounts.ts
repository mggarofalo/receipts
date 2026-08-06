import { useMemo } from "react";
import { useStableQuery } from "@/hooks/useStableQuery";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { toast } from "sonner";

export function useAccounts(offset = 0, limit = 50, sortBy?: string | null, sortDirection?: string | null, isActive?: boolean | null, options: { enabled?: boolean } = {}) {
  const { enabled = true } = options;
  const query = useQuery({
    queryKey: ["accounts", "list", offset, limit, sortBy, sortDirection, isActive],
    enabled,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/accounts", {
        params: { query: { offset, limit, sortBy: sortBy ?? undefined, sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined, isActive: isActive ?? undefined } },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: Number(query.data?.total ?? 0) }), [base, query.data]);
}

export function useAccount(id: string | null) {
  return useQuery({
    queryKey: ["accounts", id],
    enabled: !!id,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/accounts/{id}", {
        params: { path: { id: id! } },
      });
      if (error) throw error;
      return data;
    },
  });
}

export function useAccountCards(accountId: string | null) {
  // Keyed under "cards" so mutations in useCards (useUpdateCard, useDeleteCard,
  // useMergeCards) that invalidate ["cards"] also invalidate these per-account
  // card lists via React Query's prefix matching.
  const query = useQuery({
    queryKey: ["cards", "byAccount", accountId],
    enabled: !!accountId,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/accounts/{id}/cards", {
        params: { path: { id: accountId! } },
      });
      if (error) throw error;
      return data;
    },
  });
  return useMemo(
    () => ({
      data: query.data,
      isLoading: query.isLoading,
      isError: query.isError,
      error: query.error,
    }),
    [query.data, query.isLoading, query.isError, query.error],
  );
}

export function useCreateAccount() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: {
      name: string;
      isActive: boolean;
    }) => {
      const { data, error } = await client.POST("/api/accounts", { body });
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["accounts"] });
      toast.success("Account created");
    },
  });
}

export function useUpdateAccount() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: {
      id: string;
      name: string;
      isActive: boolean;
    }) => {
      const { error } = await client.PUT("/api/accounts/{id}", {
        params: { path: { id: body.id } },
        body,
      });
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["accounts"] });
      toast.success("Account updated");
    },
  });
}

export interface DeleteAccountConflict {
  /** ProblemDetails carries the prose in `detail`; the count rides as an extension member. */
  detail: string;
  cardCount: number;
}

export function useDeleteAccount() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      const { error, response } = await client.DELETE("/api/accounts/{id}", {
        params: { path: { id } },
      });
      if (error) {
        if (response.status === 409) {
          const body = error as unknown as DeleteAccountConflict;
          throw { conflict: true, ...body };
        }
        throw error;
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["accounts"] });
      toast.success("Account deleted");
    },
    onError: (error: unknown) => {
      const err = error as { conflict?: boolean; detail?: string; cardCount?: number };
      if (err.conflict) {
        toast.error(err.detail ?? "Cannot delete — cards reference this account");
      }
      // Non-conflict failures fall through to the global error handler.
    },
  });
}
