import { useMemo } from "react";
import type { UseQueryResult } from "@tanstack/react-query";

/**
 * Projects a TanStack Query result into a referentially-stable object.
 *
 * Spreading `...query` into a memo makes eslint-plugin-react-hooks (v7) infer
 * the whole, per-render-fresh `query` object as the dependency, so the memo
 * recomputes every render and provides no referential stability. Listing the
 * specific fields lets the returned identity stay stable across renders while
 * still satisfying `react-hooks/preserve-manual-memoization`.
 *
 * List hooks wrap this and add their own derived fields (e.g. `data`/`total`),
 * depending on `[base, query.data]` so the derived object is stable too.
 */
export function useStableQuery<TData, TError>(
  query: UseQueryResult<TData, TError>,
) {
  return useMemo(
    () => ({
      data: query.data,
      error: query.error,
      status: query.status,
      fetchStatus: query.fetchStatus,
      isLoading: query.isLoading,
      isPending: query.isPending,
      isError: query.isError,
      isSuccess: query.isSuccess,
      isFetching: query.isFetching,
      isRefetching: query.isRefetching,
      refetch: query.refetch,
    }),
    [
      query.data,
      query.error,
      query.status,
      query.fetchStatus,
      query.isLoading,
      query.isPending,
      query.isError,
      query.isSuccess,
      query.isFetching,
      query.isRefetching,
      query.refetch,
    ],
  );
}
