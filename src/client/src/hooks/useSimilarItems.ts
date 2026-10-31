import { useMemo } from "react";
import { useStableQuery } from "@/hooks/useStableQuery";
import { localErrorPolicy } from "@/lib/request-error-policy";
import { useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { useDebouncedValue } from "./useDebouncedValue";

export function useSimilarItems(
  query: string,
  options?: { limit?: number; threshold?: number; enabled?: boolean },
) {
  const debouncedQuery = useDebouncedValue(query, 300);
  const enabled =
    (options?.enabled ?? true) && debouncedQuery.length >= 2;

  const queryResult = useQuery({
    ...localErrorPolicy.query,
    queryKey: [
      "similarItems",
      debouncedQuery,
      options?.limit,
      options?.threshold,
    ],
    enabled,
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET(
        "/api/item-templates/similar",
        {
          ...localErrorPolicy.request,
          params: {
            query: {
              q: debouncedQuery,
              limit: options?.limit ?? 5,
              threshold: options?.threshold ?? 0.3,
            },
          },
          signal,
        },
      );
      if (error) throw error;
      return data;
    },
    staleTime: 30_000,
  });
  const base = useStableQuery(queryResult);
  const isDebouncing = debouncedQuery !== query;
  return useMemo(() => ({ ...base, isDebouncing }), [base, isDebouncing]);
}
