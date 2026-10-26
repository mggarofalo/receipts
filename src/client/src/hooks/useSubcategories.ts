import { localErrorPolicy } from "@/lib/request-error-policy";
import { invalidateDomainChange, queryKeys } from "@/lib/query-invalidation";
import { useMemo } from "react";
import { useStableQuery } from "@/hooks/useStableQuery";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import client from "@/lib/api-client";
import { toast } from "sonner";

/**
 * Fetches every subcategory for a category by auto-paginating all pages.
 *
 * Legacy report pickers must show the complete subcategory list. This hook
 * loops in 500-row chunks (the API's max `limit`) until `total` is
 * reached. Returns the subcategory array directly as `data`.
 */
export function useAllSubcategoriesByCategoryId(
  categoryId: string | null,
  isActive?: boolean | null,
) {
  return useQuery({
    ...localErrorPolicy.query,
    queryKey: [
      ...queryKeys.subcategories,
      "byCategory",
      "all",
      categoryId,
      isActive ?? undefined,
    ],
    enabled: !!categoryId,
    queryFn: async ({ signal }) => {
      const pageSize = 500;
      const fetchPage = async (offset: number) => {
        const { data, error } = await client.GET("/api/subcategories", {
          ...localErrorPolicy.request,
          params: {
            query: {
              categoryId: categoryId!,
              offset,
              limit: pageSize,
              sortBy: "name",
              sortDirection: "asc",
              isActive: isActive ?? undefined,
            },
          },
          signal,
        });
        if (error) throw error;
        return data;
      };

      const first = await fetchPage(0);
      const all = [...(first?.data ?? [])];
      const total = Number(first?.total ?? all.length);
      for (let offset = pageSize; offset < total; offset += pageSize) {
        const page = await fetchPage(offset);
        all.push(...(page?.data ?? []));
      }
      return all;
    },
  });
}

export function useDeletedSubcategories() {
  const query = useQuery({
    queryKey: [...queryKeys.subcategories, "deleted", "all"],
    queryFn: async ({ signal }) => {
      const pageSize = 500;
      const fetchPage = async (offset: number) => {
        const { data, error } = await client.GET("/api/subcategories/deleted", {
          params: { query: { offset, limit: pageSize } },
          signal,
        });
        if (error) throw error;
        return data;
      };

      const first = await fetchPage(0);
      const all = [...(first?.data ?? [])];
      const total = Number(first?.total ?? all.length);
      for (let offset = pageSize; offset < total; offset += pageSize) {
        const page = await fetchPage(offset);
        all.push(...(page?.data ?? []));
      }
      return all;
    },
  });
  const base = useStableQuery(query);
  return useMemo(
    () => ({ ...base, data: query.data, total: query.data?.length ?? 0 }),
    [base, query.data],
  );
}

export function useRestoreSubcategory() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    mutationFn: async (id: string) => {
      const { error } = await client.POST("/api/subcategories/{id}/restore", {
        params: { path: { id } },
      });
      if (error) throw error;
    },
    onSuccess: () => {
      invalidateDomainChange(queryClient, "subcategory");
      toast.success("Subcategory restored");
    },
  });
}
