import { useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { queryKeys } from "@/lib/query-invalidation";
import type { operations } from "@/generated/api";

export type PurchaseSearchOptions = Omit<
  operations["SearchPurchases"]["parameters"]["query"],
  "q" | "offset" | "limit"
>;

/** Purchase projections share receipt/report invalidation, including curation. */
export function usePurchaseSearch(
  q: string,
  offset = 0,
  limit = 25,
  enabled = true,
  options: PurchaseSearchOptions = {},
) {
  const search = q.trim();
  return useQuery({
    queryKey: [
      ...queryKeys.reports,
      "purchase-search",
      search,
      offset,
      limit,
      options,
    ],
    enabled: enabled && search.length >= 2 && search.length <= 200,
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET("/api/search/purchases", {
        params: { query: { ...options, q: search, offset, limit } },
        signal,
      });
      if (error) throw error;
      return data;
    },
  });
}
