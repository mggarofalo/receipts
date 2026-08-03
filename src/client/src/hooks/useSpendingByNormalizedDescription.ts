import { keepPreviousData, useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";

export interface SpendingByNormalizedDescriptionParams {
  from?: string;
  to?: string;
  sortBy?: "canonicalName" | "totalAmount" | "itemCount";
  sortDirection?: "asc" | "desc";
  page?: number;
  pageSize?: number;
}

export function useSpendingByNormalizedDescription(
  params: SpendingByNormalizedDescriptionParams = {},
) {
  return useQuery({
    queryKey: [
      "reports",
      "spending-by-normalized-description",
      params.from,
      params.to,
      params.sortBy,
      params.sortDirection,
      params.page,
      params.pageSize,
    ],
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await client.GET(
        "/api/reports/spending-by-normalized-description",
        {
          params: {
            query: {
              from: params.from,
              to: params.to,
              sortBy: params.sortBy,
              sortDirection: params.sortDirection,
              page: params.page,
              pageSize: params.pageSize,
            },
          },
        },
      );
      if (error) throw error;
      return data;
    },
  });
}
