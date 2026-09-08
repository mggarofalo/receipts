import { useCallback, useMemo } from "react";
import { infiniteQueryOptions, useInfiniteQuery } from "@tanstack/react-query";
import { defaultFilter } from "cmdk";
import client from "@/lib/api-client";
import { localErrorPolicy } from "@/lib/request-error-policy";
import { queryKeys } from "@/lib/query-invalidation";
import { receiptToOption } from "@/lib/combobox-options";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import type { components } from "@/generated/api";

type ReceiptPage = components["schemas"]["ReceiptListResponse"];
export type PickerReceipt = components["schemas"]["ReceiptListItemResponse"];
const PAGE_SIZE = 50;

function pickerOptions(search: string, kind: "browse" | "search", enabled: boolean) {
  return infiniteQueryOptions({
    ...localErrorPolicy.query,
    queryKey: [...queryKeys.receipts, "picker", kind, search],
    enabled,
    initialPageParam: 0,
    queryFn: async ({ pageParam, signal }: { pageParam: number; signal: AbortSignal }): Promise<ReceiptPage> => {
      const { data, error } = await client.GET("/api/receipts", {
        ...localErrorPolicy.request,
        signal,
        params: { query: { offset: pageParam, limit: PAGE_SIZE, sortBy: "date", sortDirection: "desc", q: search || undefined } },
      });
      if (error) throw error;
      if (!data) throw new Error("Receipt search returned no page.");
      return data;
    },
    getNextPageParam: (page: ReceiptPage) => {
      const next = page.offset + page.data.length;
      return page.data.length > 0 && next < page.total ? next : undefined;
    },
  });
}

/** Bounded browsing plus remote location search, retaining local date/fuzzy matches. */
export function useReceiptPicker(open: boolean, rawSearch: string) {
  const search = rawSearch.trim();
  const debouncedSearch = useDebouncedValue(search);
  const isDebouncing = search !== debouncedSearch;
  const browse = useInfiniteQuery(pickerOptions("", "browse", open));
  const complete = browse.isSuccess && browse.data !== undefined && !browse.hasNextPage;
  const remoteEnabled = open && search.length > 0 && !isDebouncing && !complete;
  const remote = useInfiniteQuery(pickerOptions(debouncedSearch, "search", remoteEnabled));
  const searching = search.length > 0 && !complete;
  const current = searching ? remote : browse;

  const receipts = useMemo(() => {
    const byId = new Map<string, PickerReceipt>();
    for (const receipt of browse.data?.pages.flatMap((page) => page.data) ?? []) {
      const option = receiptToOption(receipt);
      if (!search || defaultFilter(`${option.label} ${option.sublabel}`, search) > 0) byId.set(receipt.id, receipt);
    }
    if (searching && !isDebouncing) {
      for (const receipt of remote.data?.pages.flatMap((page) => page.data) ?? []) byId.set(receipt.id, receipt);
    }
    return [...byId.values()];
  }, [browse.data, remote.data, search, searching, isDebouncing]);

  const { fetchNextPage, refetch } = current;
  const hasMore = !isDebouncing && current.hasNextPage && !complete;
  const isFetching = current.isFetching;
  // `remote` still points at the previous debounced key while the user is typing.
  // Never expose its page/error controls as though they belong to the new text.
  const nextPageFailed = !isDebouncing && current.isFetchNextPageError;
  const loadMore = useCallback(() => {
    if (open && !isDebouncing && hasMore && !isFetching && !nextPageFailed) void fetchNextPage();
  }, [open, isDebouncing, hasMore, isFetching, nextPageFailed, fetchNextPage]);
  const retry = useCallback(() => {
    if (!open || isDebouncing || isFetching) return;
    if (nextPageFailed) void fetchNextPage();
    else void refetch();
  }, [open, isDebouncing, isFetching, nextPageFailed, fetchNextPage, refetch]);
  const { refetch: refetchBrowse } = browse;
  const retryBrowse = useCallback(() => {
    if (open && !browse.isFetching) void refetchBrowse();
  }, [open, browse.isFetching, refetchBrowse]);

  const isError = !isDebouncing && current.isError;
  const isSuccess = !isDebouncing && current.isSuccess;
  const browseError = searching && browse.isError;
  const isLoading = open && (isDebouncing || current.isLoading || (searching && current.isPending));
  return useMemo(() => ({
    receipts, isLoading, isFetching, isError, isSuccess, nextPageFailed, hasMore, complete,
    isDebouncing, browseError, browseFetching: browse.isFetching,
    loadMore, retry, retryBrowse,
  }), [receipts, isLoading, isFetching, isError, isSuccess, nextPageFailed, hasMore, complete,
    isDebouncing, browseError, browse.isFetching, loadMore, retry, retryBrowse]);
}
