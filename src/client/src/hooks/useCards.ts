import { useMemo } from "react";
import { useStableQuery } from "@/hooks/useStableQuery";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { toApiError } from "@/lib/problem-details";
import { toast } from "sonner";
import type { components } from "@/generated/api";

// Note: Cards are hard-delete entities (no soft-delete/restore).

export function useCards(offset = 0, limit = 50, sortBy?: string | null, sortDirection?: string | null, isActive?: boolean | null, options: { enabled?: boolean } = {}) {
  const { enabled = true } = options;
  const query = useQuery({
    queryKey: ["cards", "list", offset, limit, sortBy, sortDirection, isActive],
    enabled,
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

/** Fetches the complete card list for pickers and entity lookups. */
export function useAllCards(isActive?: boolean | null) {
  return useQuery({
    queryKey: ["cards", "all", isActive ?? undefined],
    queryFn: async ({ signal }) => {
      const pageSize = 500;
      const fetchPage = async (offset: number) => {
        const { data, error } = await client.GET("/api/cards", {
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
  /** ProblemDetails carries the prose in `detail`; the count rides as an extension member. */
  detail: string;
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
      const err = error as { conflict?: boolean; detail?: string; transactionCount?: number };
      if (err.conflict) {
        toast.error(err.detail ?? "Cannot delete — transactions reference this card");
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

/** What the merge actually changed. All zero means it changed nothing. */
export interface MergeCardsImpact {
  accountsRemoved: number;
  cardsMoved: number;
  transactionsRepointed: number;
}

const NO_IMPACT: MergeCardsImpact = {
  accountsRemoved: 0,
  cardsMoved: 0,
  transactionsRepointed: 0,
};

export function isNoOpMerge(impact: MergeCardsImpact): boolean {
  return (
    impact.accountsRemoved === 0 &&
    impact.cardsMoved === 0 &&
    impact.transactionsRepointed === 0
  );
}

/**
 * Renders the impact as the toast's supporting line.
 *
 * Only non-zero clauses appear: a merge that moved cards between accounts but had
 * no transactions to carry should not claim "0 transactions repointed", which reads
 * like something went wrong.
 */
export function describeMergeImpact(impact: MergeCardsImpact): string {
  const plural = (n: number, noun: string) => `${n} ${noun}${n === 1 ? "" : "s"}`;
  const parts = [`${plural(impact.cardsMoved, "card")} moved`];
  if (impact.transactionsRepointed > 0) {
    parts.push(`${plural(impact.transactionsRepointed, "transaction")} repointed`);
  }
  if (impact.accountsRemoved > 0) {
    parts.push(`${plural(impact.accountsRemoved, "empty account")} removed`);
  }
  return `${parts.join(", ")}.`;
}

export type MergeCardsPreview = components["schemas"]["MergeCardsPreviewResponse"];

/**
 * A merge to preview. `targetAccountId` may be null, meaning "an account that does not
 * exist yet" — the only way to validate a "New account" merge without first creating
 * the account it might not need (RECEIPTS-902).
 */
export interface MergeCardsPreviewInput {
  targetAccountId: string | null;
  sourceCardIds: string[];
  ynabMappingWinnerAccountId?: string | null;
}

/**
 * What the merge would do, fetched before the user commits to it.
 *
 * Merging is irreversible and has no undo, and the dialog's only warning used to be a
 * line of prose (RECEIPTS-889). Disabled until a target is chosen, because the impact
 * is meaningless without one.
 *
 * Failures resolve to `null` rather than throwing: a preview that cannot be fetched
 * must not toast an error over a dialog the user has not submitted yet. The dialog
 * holds submit while this is unresolved, and the merge itself still validates.
 *
 * `null` and not `undefined` — React Query rejects an undefined query result outright,
 * which would turn a quiet failure into the error state this is avoiding.
 */
export function useMergeCardsPreview(input: MergeCardsPreviewInput | null) {
  return useQuery({
    queryKey: [
      "cards",
      "mergePreview",
      input?.targetAccountId ?? null,
      [...(input?.sourceCardIds ?? [])].sort().join(","),
      input?.ynabMappingWinnerAccountId ?? null,
    ],
    // No target-account requirement: a null target previews a merge into an account
    // that does not exist yet, which is how "New account" mode validates a selection
    // before creating anything (RECEIPTS-902).
    enabled: !!input && input.sourceCardIds.length > 0,
    // The preview describes live data the user is about to destroy; a cached one from
    // before they changed the selection would describe a merge they are not running.
    staleTime: 0,
    retry: false,
    queryFn: async (): Promise<MergeCardsPreview | null> => {
      const { data, response } = await client.POST("/api/cards/merge/preview", {
        body: {
          targetAccountId: input!.targetAccountId ?? null,
          sourceCardIds: input!.sourceCardIds,
          ynabMappingWinnerAccountId: input!.ynabMappingWinnerAccountId ?? null,
        },
      });
      if (!response.ok) return null;
      return data ?? null;
    },
  });
}

export function useMergeCards() {
  const queryClient = useQueryClient();
  return useMutation<MergeCardsImpact, MergeCardsConflict | unknown, MergeCardsInput>({
    mutationFn: async (input) => {
      const { data, error, response } = await client.POST("/api/cards/merge", {
        body: {
          targetAccountId: input.targetAccountId,
          sourceCardIds: input.sourceCardIds,
          ynabMappingWinnerAccountId: input.ynabMappingWinnerAccountId ?? null,
        },
      });

      // Branch on the status, NOT on `error` being truthy. Merge rejects with
      // bodiless responses (403 from RequireAdmin, 404 for a stale card or
      // target account), and openapi-fetch reports those as `error: undefined`
      // — so `if (error)` would fall through and report a destructive
      // operation that never happened as a success.
      // Falling back to zeroes on a bodiless 200 is deliberate: the generic
      // "Cards merged" that used to be shown unconditionally is exactly the claim
      // this endpoint can no longer make without evidence. No body, no boast.
      if (response.ok) return data ?? NO_IMPACT;

      // `error &&` matters now that the guard above is a status check rather
      // than a truthiness check: without it a bodiless 409 would reach the
      // dereference below and throw a TypeError, which the global handler
      // would misreport as a network failure.
      if (response.status === 409 && error) {
        const body = error as unknown as { message: string; conflicts: YnabMappingConflict[] };
        const conflict: MergeCardsConflict = {
          conflict: true,
          message: body.message,
          conflicts: body.conflicts,
        };
        throw conflict;
      }

      // Everything else goes to the global handler as a ProblemDetails-shaped
      // object. Since RECEIPTS-886 the merge endpoint already answers that way
      // for its most useful rejections ("all of its cards must be included in
      // the merge, or none"); toApiError still stamps the real HTTP status over
      // the body, which is what the bodiless 403/404 case depends on.
      throw toApiError(response.status, error);
    },
    onSuccess: (impact) => {
      // A merge that changed nothing has nothing to invalidate, and saying "Cards
      // merged" for it is the bug this replaced (RECEIPTS-893). It is not an error
      // either — the cards are where the user asked them to be — so it is reported
      // as information, not success.
      if (isNoOpMerge(impact)) {
        toast.info("Nothing to merge", {
          description: "Every selected card already belonged to that account.",
        });
        return;
      }

      queryClient.invalidateQueries({ queryKey: ["cards"] });
      queryClient.invalidateQueries({ queryKey: ["accounts"] });
      toast.success("Cards merged", { description: describeMergeImpact(impact) });
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
