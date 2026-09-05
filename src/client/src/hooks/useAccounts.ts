import { useMemo } from "react";
import { useStableQuery } from "@/hooks/useStableQuery";
import { useQuery, useQueries, useQueryClient } from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import client from "@/lib/api-client";
import { toast } from "sonner";

export function useAccounts(offset = 0, limit = 50, sortBy?: string | null, sortDirection?: string | null, isActive?: boolean | null, options: { enabled?: boolean; q?: string } = {}) {
  const { enabled = true, q } = options;
  const search = q?.trim() || undefined;
  const query = useQuery({
    queryKey: ["accounts", "list", offset, limit, sortBy, sortDirection, isActive, search],
    enabled,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/accounts", {
        params: { query: { offset, limit, sortBy: sortBy ?? undefined, sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined, isActive: isActive ?? undefined, q: search } },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: Number(query.data?.total ?? 0) }), [base, query.data]);
}

/** Fetches the complete account list for pickers and entity lookups. */
export function useAllAccounts(isActive?: boolean | null) {
  return useQuery({
    queryKey: ["accounts", "all", isActive ?? undefined],
    queryFn: async ({ signal }) => {
      const pageSize = 500;
      const fetchPage = async (offset: number) => {
        const { data, error } = await client.GET("/api/accounts", {
          params: {
            query: {
              offset,
              limit: pageSize,
              sortBy: "name",
              sortDirection: "asc",
              isActive: isActive ?? undefined,
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

/**
 * The card lists of several accounts at once.
 *
 * The merge dialog has to know every card belonging to each source account in the
 * selection, not just the ones on the current page of /cards — the server refuses a
 * merge that would leave siblings behind on an account it is about to delete, and
 * before RECEIPTS-888 the user had no way to see which cards those were.
 *
 * Same query key as {@link useAccountCards}, so a single account's list is shared
 * between the two hooks and one `["cards"]` invalidation still clears both.
 */
export function useAccountsCards(accountIds: string[]) {
  const results = useQueries({
    queries: accountIds.map((accountId) => ({
      queryKey: ["cards", "byAccount", accountId],
      queryFn: async () => {
        const { data, error } = await client.GET("/api/accounts/{id}/cards", {
          params: { path: { id: accountId } },
        });
        if (error) throw error;
        return data;
      },
    })),
  });

  const isLoading = results.some((r) => r.isLoading);
  const isError = results.some((r) => r.isError);

  // useQueries hands back a fresh array (and fresh result objects) every render, so
  // memoising on `results` would rebuild the map each time and defeat every downstream
  // memo. Key on the resolved ids instead — the only part the dialog reacts to.
  const signature = accountIds
    .map((id, i) => `${id}:${(results[i]?.data ?? []).map((c) => c.id).join("|")}`)
    .join(";");

  const cardsByAccountId = useMemo(() => {
    const map = new Map<string, CardSummary[]>();
    accountIds.forEach((accountId, i) => {
      const cards = results[i]?.data;
      if (cards) {
        map.set(
          accountId,
          cards.map((c) => ({ id: c.id, name: c.name, cardCode: c.cardCode, accountId })),
        );
      }
    });
    return map;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [signature]);

  return useMemo(
    () => ({ cardsByAccountId, isLoading, isError }),
    [cardsByAccountId, isLoading, isError],
  );
}

/** The shape the merge dialog needs from a card: enough to list it and to place it. */
export interface CardSummary {
  id: string;
  name: string;
  cardCode: string;
  accountId: string;
}

export function useCreateAccount() {
  const queryClient = useQueryClient();
  return useSessionMutation({
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
  return useSessionMutation({
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
  return useSessionMutation({
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
