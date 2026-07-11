import { useMemo } from "react";
import { useStableQuery } from "@/hooks/useStableQuery";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { toast } from "sonner";

export function useCategories(offset = 0, limit = 50, sortBy?: string | null, sortDirection?: string | null, isActive?: boolean | null, options: { enabled?: boolean } = {}) {
  const { enabled = true } = options;
  const query = useQuery({
    queryKey: ["categories", "list", offset, limit, sortBy, sortDirection, isActive],
    enabled,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/categories", {
        params: { query: { offset, limit, sortBy: sortBy ?? undefined, sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined, isActive: isActive ?? undefined } },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: Number(query.data?.total ?? 0) }), [base, query.data]);
}

/**
 * Fetches the complete category list by auto-paginating through every page.
 *
 * Picker components must show *all* categories, not a single page. The
 * paginated `useCategories` hook caps at one page (default 50), which silently
 * truncates dropdowns once the category count exceeds the page size. This hook
 * loops in 500-row chunks (the API's max `limit`) until `total` is reached.
 *
 * Returns the category array directly as `data` (not the paged envelope).
 */
export function useAllCategories(isActive?: boolean | null) {
  return useQuery({
    queryKey: ["categories", "all", isActive ?? undefined],
    queryFn: async ({ signal }) => {
      const pageSize = 500;
      const fetchPage = async (offset: number) => {
        const { data, error } = await client.GET("/api/categories", {
          params: {
            query: {
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

export function useCategory(id: string | null) {
  return useQuery({
    queryKey: ["categories", id],
    enabled: !!id,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/categories/{id}", {
        params: { path: { id: id! } },
      });
      if (error) throw error;
      return data;
    },
  });
}

export function useCreateCategory() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: {
      name: string;
      description?: string | null;
      isActive: boolean;
    }) => {
      const { data, error } = await client.POST("/api/categories", { body });
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["categories"] });
      toast.success("Category created");
    },
    onError: (err) => {
      // A string error carries a specific, already-formatted message; anything
      // else falls through to the global handler (which surfaces the server's
      // ProblemDetails detail, e.g. a duplicate-name message).
      if (typeof err === "string") toast.error(err);
    },
  });
}

export function useUpdateCategory() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: {
      id: string;
      name: string;
      description?: string | null;
      isActive: boolean;
    }) => {
      const { error } = await client.PUT("/api/categories/{id}", {
        params: { path: { id: body.id } },
        body,
      });
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["categories"] });
      toast.success("Category updated");
    },
  });
}

export interface DeleteCategoryConflict {
  message: string;
  receiptItemCount?: number;
}

export function useDeleteCategory() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      const { error, response } = await client.DELETE("/api/categories/{id}", {
        params: { path: { id } },
      });
      if (error) {
        if (response.status === 409) {
          const body = error as unknown as DeleteCategoryConflict;
          throw { conflict: true, ...body };
        }
        throw error;
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["categories"] });
      queryClient.invalidateQueries({ queryKey: ["categories", "deleted"] });
      toast.success("Category deleted");
    },
    onError: (error: unknown) => {
      const err = error as { conflict?: boolean; message?: string };
      if (err.conflict) {
        toast.error(err.message ?? "Cannot delete — dependencies reference this category");
      }
      // Non-conflict failures fall through to the global error handler.
    },
  });
}

export function useDeletedCategories(offset = 0, limit = 50, sortBy?: string | null, sortDirection?: string | null) {
  const query = useQuery({
    queryKey: ["categories", "deleted", offset, limit, sortBy, sortDirection],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/categories/deleted", {
        params: { query: { offset, limit, sortBy: sortBy ?? undefined, sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined } },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: Number(query.data?.total ?? 0) }), [base, query.data]);
}

export function useRestoreCategory() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      const { error } = await client.POST("/api/categories/{id}/restore", {
        params: { path: { id } },
      });
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["categories"] });
      queryClient.invalidateQueries({ queryKey: ["categories", "deleted"] });
      toast.success("Category restored");
    },
  });
}
