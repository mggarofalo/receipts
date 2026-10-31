import { useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";
import type { operations } from "@/generated/api";

export type BudgetTrendsParams = NonNullable<
  operations["GetBudgetTrendsReport"]["parameters"]["query"]
>;

export function useBudgetTrendsReport(params: BudgetTrendsParams) {
  return useQuery({
    queryKey: ["reports", "budget-trends", params],
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET("/api/reports/budget-trends", {
        params: { query: params },
        signal,
      });
      if (error) throw error;
      return data;
    },
  });
}
