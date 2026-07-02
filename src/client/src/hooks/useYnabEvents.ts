import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";

// Defined inline (matching the generated api.d.ts) — see useYnabStatus for why.
export type YnabSyncEventResponse = {
  id: string;
  occurredAt: string;
  eventType: string;
  receiptId?: string | null;
  transactionId?: string | null;
  httpStatus?: number | null;
  success: boolean;
  errorMessage?: string | null;
  requestId?: string | null;
};

type YnabSyncEventListResponse = {
  data: YnabSyncEventResponse[];
  total: number;
  offset: number;
  limit: number;
};

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
    queryFn: async () => {
      const { data, error } = await client.GET("/api/ynab/events" as never, {
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
      } as never);
      if (error) throw error;
      return data as unknown as YnabSyncEventListResponse;
    },
  });

  return useMemo(
    () => ({
      ...query,
      data: query.data?.data ?? [],
      total: Number(query.data?.total ?? 0),
    }),
    [query],
  );
}
