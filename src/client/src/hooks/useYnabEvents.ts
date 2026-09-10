import { useMemo } from "react";
import { useStableQuery } from "@/hooks/useStableQuery";
import { useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";
import type { components } from "@/generated/api";
import { localErrorPolicy } from "@/lib/request-error-policy";

export type YnabSyncEventResponse =
  components["schemas"]["YnabSyncEventResponse"];

export interface YnabEventFilters {
  offset?: number;
  limit?: number;
  sortBy?: string | null;
  sortDirection?: "asc" | "desc" | null;
  outcome?: "success" | "failure" | null;
  dateFrom?: string | null;
  dateTo?: string | null;
}

export function useYnabEvents(filters: YnabEventFilters = {}) {
  const {
    offset = 0,
    limit = 50,
    sortBy,
    sortDirection,
    outcome,
    dateFrom,
    dateTo,
  } = filters;

  const query = useQuery({
    ...localErrorPolicy.query,
    queryKey: [
      "ynab",
      "events",
      offset,
      limit,
      sortBy,
      sortDirection,
      outcome,
      dateFrom,
      dateTo,
    ],
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET("/api/ynab/events", {
        ...localErrorPolicy.request,
        signal,
        params: {
          query: {
            offset,
            limit,
            sortBy: sortBy ?? undefined,
            sortDirection: sortDirection ?? undefined,
            outcome: outcome ?? undefined,
            dateFrom: dateFrom ?? undefined,
            dateTo: dateTo ?? undefined,
          },
        },
      });
      if (error) throw error;
      return data;
    },
  });

  const base = useStableQuery(query);
  return useMemo(
    () => ({
      ...base,
      data: query.data?.data ?? [],
      total: Number(query.data?.total ?? 0),
    }),
    [base, query.data],
  );
}
