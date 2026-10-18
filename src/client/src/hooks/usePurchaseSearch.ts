import { useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { queryKeys } from "@/lib/query-invalidation";

/** Purchase projections share receipt/report invalidation, including curation. */
export function usePurchaseSearch(
  q: string,
  offset = 0,
  limit = 25,
  enabled = true,
) {
  const search = q.trim();
  return useQuery({
    queryKey: [...queryKeys.reports, "purchase-search", search, offset, limit],
    enabled: enabled && search.length >= 2 && search.length <= 200,
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET("/api/search/purchases", {
        params: { query: { q: search, offset, limit } },
        signal,
      });
      if (error) throw error;
      return data;
    },
  });
}
