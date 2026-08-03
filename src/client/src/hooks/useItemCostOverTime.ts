import { keepPreviousData, useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";

export interface ItemDescriptionsParams {
  search: string;
  categoryOnly?: boolean;
  limit?: number;
}

export function useItemDescriptions(params: ItemDescriptionsParams) {
  const enabled = params.search.length >= 2;

  return useQuery({
    queryKey: [
      "reports",
      "item-descriptions",
      params.search,
      params.categoryOnly,
      params.limit,
    ],
    enabled,
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await client.GET(
        "/api/reports/item-descriptions",
        {
          params: {
            query: {
              search: params.search,
              categoryOnly: params.categoryOnly,
              limit: params.limit,
            },
          },
        },
      );
      if (error) throw error;
      return data;
    },
  });
}

export interface ItemCostOverTimeParams {
  description?: string;
  category?: string;
  /** Normalized description canonical name (RECEIPTS-841 drill-down target). */
  normalizedDescription?: string;
  startDate?: string;
  endDate?: string;
  granularity?: "exact" | "monthly" | "yearly";
}

export function useItemCostOverTime(params: ItemCostOverTimeParams) {
  const enabled = Boolean(
    params.description || params.category || params.normalizedDescription,
  );

  return useQuery({
    queryKey: [
      "reports",
      "item-cost-over-time",
      params.description,
      params.category,
      params.normalizedDescription,
      params.startDate,
      params.endDate,
      params.granularity,
    ],
    enabled,
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await client.GET(
        "/api/reports/item-cost-over-time",
        {
          params: {
            query: {
              description: params.description,
              category: params.category,
              normalizedDescription: params.normalizedDescription,
              startDate: params.startDate,
              endDate: params.endDate,
              granularity: params.granularity,
            },
          },
        },
      );
      if (error) throw error;
      return data;
    },
  });
}
