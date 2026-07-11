import { useMemo } from "react";
import { useStableQuery } from "@/hooks/useStableQuery";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { toast } from "sonner";

// Note: Cards are hard-delete entities (no soft-delete/restore).

export function useCards(offset = 0, limit = 50, sortBy?: string | null, sortDirection?: string | null, isActive?: boolean | null) {
  const query = useQuery({
    queryKey: ["cards", "list", offset, limit, sortBy, sortDirection, isActive],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/cards", {
        params: { query: { offset, limit, sortBy: sortBy ?? undefined, sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined, isActive: isActive ?? undefined } },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: Number(query.data?.total ?? 0) }), [base, query.data]);
}

export function useCard(id: string | null) {
  return useQuery({
    queryKey: ["cards", id],
    enabled: !!id,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/cards/{id}", {
        params: { path: { id: id! } },
      });
      if (error) throw error;
      return data;
    },
  });
}

export function useCreateCard() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: {
      cardCode: string;
      name: string;
      isActive: boolean;
      accountId: string;
    }) => {
      const { data, error } = await client.POST("/api/cards", { body });
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["cards"] });
      toast.success("Card created");
    },
  });
}

export function useUpdateCard() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: {
      id: string;
      cardCode: string;
      name: string;
      isActive: boolean;
      accountId: string;
    }) => {
      const { error } = await client.PUT("/api/cards/{id}", {
        params: { path: { id: body.id } },
        body,
      });
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["cards"] });
      toast.success("Card updated");
    },
  });
}

export interface DeleteCardConflict {
  message: string;
  transactionCount: number;
}

export function useDeleteCard() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      const { error, response } = await client.DELETE("/api/cards/{id}", {
        params: { path: { id } },
      });
      if (error) {
        if (response.status === 409) {
          const body = error as unknown as DeleteCardConflict;
          throw { conflict: true, ...body };
        }
        throw error;
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["cards"] });
      toast.success("Card deleted");
    },
    onError: (error: unknown) => {
      const err = error as { conflict?: boolean; message?: string; transactionCount?: number };
      if (err.conflict) {
        toast.error(err.message ?? "Cannot delete — transactions reference this card");
      }
      // Non-conflict failures fall through to the global error handler.
    },
  });
}

export interface YnabMappingConflict {
  accountId: string;
  accountName: string;
  ynabBudgetId: string;
  ynabAccountId: string;
  ynabAccountName: string;
}

export interface MergeCardsConflict {
  conflict: true;
  message: string;
  conflicts: YnabMappingConflict[];
}

export interface MergeCardsInput {
  targetAccountId: string;
  sourceCardIds: string[];
  ynabMappingWinnerAccountId?: string | null;
}

export function useMergeCards() {
  const queryClient = useQueryClient();
  return useMutation<void, MergeCardsConflict | unknown, MergeCardsInput>({
    mutationFn: async (input) => {
      const { error, response } = await client.POST("/api/cards/merge", {
        body: {
          targetAccountId: input.targetAccountId,
          sourceCardIds: input.sourceCardIds,
          ynabMappingWinnerAccountId: input.ynabMappingWinnerAccountId ?? null,
        },
      });
      if (error) {
        if (response.status === 409) {
          const body = error as unknown as { message: string; conflicts: YnabMappingConflict[] };
          const conflict: MergeCardsConflict = {
            conflict: true,
            message: body.message,
            conflicts: body.conflicts,
          };
          throw conflict;
        }
        throw error;
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["cards"] });
      queryClient.invalidateQueries({ queryKey: ["accounts"] });
      toast.success("Cards merged");
    },
    onError: (error: unknown) => {
      const err = error as Partial<MergeCardsConflict>;
      if (err.conflict) {
        // Caller handles conflict via onError or by inspecting mutation state.
        return;
      }
      // Non-conflict failures fall through to the global error handler.
    },
  });
}

export function isMergeCardsConflict(error: unknown): error is MergeCardsConflict {
  return (
    typeof error === "object" &&
    error !== null &&
    (error as { conflict?: boolean }).conflict === true &&
    Array.isArray((error as { conflicts?: unknown }).conflicts)
  );
}
