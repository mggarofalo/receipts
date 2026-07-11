import { useMemo } from "react";
import { useStableQuery } from "@/hooks/useStableQuery";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { toast } from "sonner";

export function useSubcategories(offset = 0, limit = 50, sortBy?: string | null, sortDirection?: string | null, isActive?: boolean | null, options: { enabled?: boolean } = {}) {
  const { enabled = true } = options;
  const query = useQuery({
    queryKey: ["subcategories", "list", offset, limit, sortBy, sortDirection, isActive],
    enabled,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/subcategories", {
        params: { query: { offset, limit, sortBy: sortBy ?? undefined, sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined, isActive: isActive ?? undefined } },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: Number(query.data?.total ?? 0) }), [base, query.data]);
}

export function useSubcategory(id: string | null) {
  return useQuery({
    queryKey: ["subcategories", id],
    enabled: !!id,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/subcategories/{id}", {
        params: { path: { id: id! } },
      });
      if (error) throw error;
      return data;
    },
  });
}

export function useSubcategoriesByCategoryId(categoryId: string | null, offset = 0, limit = 200, sortBy?: string | null, sortDirection?: string | null, isActive?: boolean | null) {
  const query = useQuery({
    queryKey: ["subcategories", "byCategory", categoryId, offset, limit, sortBy, sortDirection, isActive],
    enabled: !!categoryId,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/subcategories", {
        params: { query: { categoryId: categoryId!, offset, limit, sortBy: sortBy ?? undefined, sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined, isActive: isActive ?? undefined } },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: Number(query.data?.total ?? 0) }), [base, query.data]);
}

/**
 * Fetches every subcategory for a category by auto-paginating all pages.
 *
 * Picker components must show the complete subcategory list. The paginated
 * `useSubcategoriesByCategoryId` hook caps at one page (default 200), which
 * silently truncates dropdowns for categories with more subcategories. This
 * hook loops in 500-row chunks (the API's max `limit`) until `total` is
 * reached. Returns the subcategory array directly as `data`.
 */
export function useAllSubcategoriesByCategoryId(categoryId: string | null, isActive?: boolean | null) {
  return useQuery({
    queryKey: ["subcategories", "byCategory", "all", categoryId, isActive ?? undefined],
    enabled: !!categoryId,
    queryFn: async ({ signal }) => {
      const pageSize = 500;
      const fetchPage = async (offset: number) => {
        const { data, error } = await client.GET("/api/subcategories", {
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

export function useCreateSubcategory() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: {
      name: string;
      categoryId: string;
      description?: string | null;
      isActive: boolean;
    }) => {
      const { data, error } = await client.POST("/api/subcategories", {
        body,
      });
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["subcategories"] });
      toast.success("Subcategory created");
    },
    onError: (err) => {
      // A string error carries a specific, already-formatted message; anything
      // else falls through to the global handler (server ProblemDetails detail).
      if (typeof err === "string") toast.error(err);
    },
  });
}

export function useUpdateSubcategory() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: {
      id: string;
      name: string;
      categoryId: string;
      description?: string | null;
      isActive: boolean;
    }) => {
      const { error } = await client.PUT("/api/subcategories/{id}", {
        params: { path: { id: body.id } },
        body,
      });
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["subcategories"] });
      toast.success("Subcategory updated");
    },
  });
}

export interface AffectedReceipt {
  id: string;
  date: string;
  location: string;
}

export interface DeleteSubcategoryConflict {
  message: string;
  receiptItemCount: number;
  affectedReceipts: AffectedReceipt[];
}

export function useDeleteSubcategory() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      const { error, response } = await client.DELETE("/api/subcategories/{id}", {
        params: { path: { id } },
      });
      if (error) {
        if (response.status === 409) {
          const body = error as unknown as DeleteSubcategoryConflict;
          throw { conflict: true, ...body };
        }
        throw error;
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["subcategories"] });
      queryClient.invalidateQueries({ queryKey: ["subcategories", "deleted"] });
      toast.success("Subcategory deleted");
    },
    onError: (error: unknown) => {
      const err = error as { conflict?: boolean; message?: string; receiptItemCount?: number; affectedReceipts?: AffectedReceipt[] };
      if (err.conflict && err.affectedReceipts) {
        // Conflict with affected receipts — handled by the component via onError callback
        return;
      }
      if (err.conflict) {
        toast.error(err.message ?? "Cannot delete — receipt items reference this subcategory");
      }
      // Non-conflict failures fall through to the global error handler.
    },
  });
}

export function useDeletedSubcategories(offset = 0, limit = 50, sortBy?: string | null, sortDirection?: string | null) {
  const query = useQuery({
    queryKey: ["subcategories", "deleted", offset, limit, sortBy, sortDirection],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/subcategories/deleted", {
        params: { query: { offset, limit, sortBy: sortBy ?? undefined, sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined } },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: Number(query.data?.total ?? 0) }), [base, query.data]);
}

export function useRestoreSubcategory() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      const { error } = await client.POST("/api/subcategories/{id}/restore", {
        params: { path: { id } },
      });
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["subcategories"] });
      queryClient.invalidateQueries({ queryKey: ["subcategories", "deleted"] });
      toast.success("Subcategory restored");
    },
  });
}
