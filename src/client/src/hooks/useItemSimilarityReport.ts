import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { toast } from "sonner";

export interface ItemSimilarityParams {
  threshold?: number;
  sortBy?: "canonicalName" | "occurrences" | "maxSimilarity";
  sortDirection?: "asc" | "desc";
  page?: number;
  pageSize?: number;
}

export function useItemSimilarityReport(params: ItemSimilarityParams = {}) {
  return useQuery({
    queryKey: [
      "reports",
      "item-similarity",
      params.threshold,
      params.sortBy,
      params.sortDirection,
      params.page,
      params.pageSize,
    ],
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await client.GET(
        "/api/reports/item-similarity",
        {
          params: {
            query: {
              threshold: params.threshold,
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

export function useRenameItemSimilarityGroup() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({
      itemIds,
      newDescription,
    }: {
      itemIds: string[];
      newDescription: string;
    }) => {
      const { data, error } = await client.POST(
        "/api/reports/item-similarity/rename",
        {
          body: { itemIds, newDescription },
        },
      );
      if (error) throw error;
      return data;
    },
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({
        queryKey: ["reports", "item-similarity"],
      });
      queryClient.invalidateQueries({ queryKey: ["receipt-items"] });
      toast.success(
        `Renamed ${variables.itemIds.length} items to "${variables.newDescription}"`,
      );
    },
  });
}

export function useRefreshItemSimilarity() {
  return useMutation({
    mutationFn: async () => {
      const { data, error } = await client.POST(
        "/api/reports/item-similarity/refresh",
        {},
      );
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      toast.success(
        "Refresh requested — report will update in about a minute.",
      );
    },
  });
}
