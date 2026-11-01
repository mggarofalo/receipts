import { keepPreviousData, useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";

export interface ItemDescriptionsParams {
  search: string;
  limit?: number;
}

export function useItemDescriptions(params: ItemDescriptionsParams) {
  const enabled = params.search.length >= 2;

  return useQuery({
    queryKey: [
      "reports",
      "item-descriptions",
      params.search,
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
  categoryId?: string;
  categoryView?: "asClassified" | "currentHierarchy";
  knowledgeCutoff?: string;
  capturedCurrentRevisionId?: string;
  /** Normalized description canonical name (RECEIPTS-841 drill-down target). */
  normalizedDescription?: string;
  startDate?: string;
  endDate?: string;
  granularity?: "exact" | "monthly" | "yearly";
}

export function useItemCostOverTime(params: ItemCostOverTimeParams) {
  const enabled = Boolean(
    params.description ||
    params.categoryId ||
    params.normalizedDescription,
  );

  return useQuery({
    queryKey: [
      "reports",
      "item-cost-over-time",
      params.description,
      params.categoryId,
      params.categoryView,
      params.knowledgeCutoff,
      params.capturedCurrentRevisionId,
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
              categoryId: params.categoryId,
              categoryView: params.categoryView,
              knowledgeCutoff: params.knowledgeCutoff,
              capturedCurrentRevisionId: params.capturedCurrentRevisionId,
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
