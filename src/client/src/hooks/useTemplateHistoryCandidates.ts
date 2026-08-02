import { useMemo } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useStableQuery } from "@/hooks/useStableQuery";
import client from "@/lib/api-client";

/**
 * Recurring receipt-item descriptions that do not have an item template yet.
 *
 * The query key is nested under `["itemTemplates", …]` on purpose: every item-template
 * mutation already invalidates that prefix, so creating, deleting, or restoring a
 * template automatically refetches the candidate list and the affected row disappears
 * (or reappears) without any extra wiring.
 */
export function useTemplateHistoryCandidates(
  offset = 0,
  limit = 10,
  minCount = 2,
  options: { enabled?: boolean } = {},
) {
  const { enabled = true } = options;
  const query = useQuery({
    queryKey: ["itemTemplates", "historyCandidates", offset, limit, minCount],
    enabled,
    // Widening the page changes the query key. Without this the section would
    // unmount mid-interaction (isLoading flips true), dropping keyboard focus
    // from the "Show more" button the user just pressed.
    placeholderData: keepPreviousData,
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET(
        "/api/item-templates/history-candidates",
        {
          params: { query: { offset, limit, minCount } },
          signal,
        },
      );
      if (error) throw error;
      return data;
    },
  });

  const base = useStableQuery(query);
  return useMemo(
    () => ({
      ...base,
      data: query.data?.data,
      total: Number(query.data?.total ?? 0),
    }),
    [base, query.data],
  );
}
