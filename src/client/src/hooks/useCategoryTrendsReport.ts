import { useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";
import type { operations } from "@/generated/api";

export type CategoryTrendsParams = NonNullable<
  operations["GetCategoryTrendsReport"]["parameters"]["query"]
>;

export function useCategoryTrendsReport(params: CategoryTrendsParams) {
  return useQuery({
    queryKey: ["reports", "category-trends", params],
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET("/api/reports/category-trends", {
        params: { query: params },
        signal,
      });
      if (error) throw error;
      return data;
    },
  });
}
