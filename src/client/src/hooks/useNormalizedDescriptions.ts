import { useMemo } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";

export type NormalizedDescriptionStatusFilter =
  | "Active"
  | "PendingReview"
  | "Rejected";

/** Default page size. Matches the server's own default for a caller that sends no window. */
export const NORMALIZED_DESCRIPTION_PAGE_SIZE = 50;

/**
 * Server-side ceiling on `limit`, mirroring `NormalizedDescriptionsController.MaxPageSize`.
 * Asking for more is a 400, so anything building a page size from user input clamps to this.
 */
export const NORMALIZED_DESCRIPTION_MAX_PAGE_SIZE = 200;

export interface NormalizedDescriptionListOptions {
  /**
   * One status, or several to match any of them (RECEIPTS-878). Omit for no filter.
   *
   * The merge dialog passes two: every legitimate target is Active or PendingReview, and never
   * Rejected — merging items into a tombstone would resurrect text the reviewer retired.
   */
  status?: NormalizedDescriptionStatusFilter | NormalizedDescriptionStatusFilter[];
  /** Matches the display name and the matched text, so a renamed row is findable by either. */
  q?: string;
  offset?: number;
  limit?: number;
  enabled?: boolean;
}

/**
 * One page of the normalized-description registry (RECEIPTS-879).
 *
 * The endpoint used to return every row and this hook returned all of them, leaving both the
 * registry and the merge dialog to filter thousands of rows in the browser. Filtering and paging
 * now happen server-side, so callers must pass their own window and search term — a caller that
 * keeps filtering `data` client-side is only filtering the page it happens to hold.
 */
export function useNormalizedDescriptions({
  status,
  q,
  offset = 0,
  limit = NORMALIZED_DESCRIPTION_PAGE_SIZE,
  enabled = true,
}: NormalizedDescriptionListOptions = {}) {
  // Normalised here rather than at each call site so that "milk", "milk " and " milk" are one
  // cache entry instead of three, and so a box the user has cleared back to spaces is not a search.
  const trimmedQ = q?.trim() || undefined;

  // One value or many, sorted so ["Active","PendingReview"] and ["PendingReview","Active"] hit the
  // same cache entry. openapi-fetch serialises the array as a repeated query param.
  const statuses = useMemo(() => {
    if (status === undefined) return undefined;
    const list = Array.isArray(status) ? status : [status];
    return list.length > 0 ? [...list].sort() : undefined;
  }, [status]);
  const statusKey = statuses?.join(",");

  const query = useQuery({
    queryKey: [
      "normalized-descriptions",
      "list",
      statusKey,
      trimmedQ,
      offset,
      limit,
    ],
    enabled,
    // Keeps the previous page on screen while the next one loads, so paging and typing in the
    // search box do not blank the table on every keystroke.
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/normalized-descriptions", {
        params: { query: { status: statuses, q: trimmedQ, offset, limit } },
      });
      if (error) throw error;
      return data;
    },
  });

  return useMemo(
    () => ({
      ...query,
      items: query.data?.items ?? [],
      // The count of rows matching the filter, not the page length. Pagers depend on the
      // difference; the schema used to define them as the same thing.
      total: query.data?.totalCount ?? 0,
    }),
    [query],
  );
}

export function useNormalizedDescription(id: string | null) {
  return useQuery({
    queryKey: ["normalized-descriptions", id],
    enabled: !!id,
    queryFn: async () => {
      const { data, error } = await client.GET(
        "/api/normalized-descriptions/{id}",
        { params: { path: { id: id! } } },
      );
      if (error) throw error;
      return data;
    },
  });
}
