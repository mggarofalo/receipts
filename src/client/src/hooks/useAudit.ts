import { useMemo } from "react";
import { useStableQuery } from "@/hooks/useStableQuery";
import { useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";

export function useEntityAuditHistory(
  entityType: string | null,
  entityId: string | null,
  offset = 0,
  limit = 50,
  sortBy?: string | null,
  sortDirection?: string | null,
) {
  const query = useQuery({
    queryKey: ["audit", "entity", entityType, entityId, offset, limit, sortBy, sortDirection],
    enabled: !!entityType && !!entityId,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/audit", {
        params: {
          query: {
            entityType: entityType!,
            entityId: entityId!,
            offset,
            limit,
            sortBy: sortBy ?? undefined,
            sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined,
          },
        },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: Number(query.data?.total ?? 0) }), [base, query.data]);
}

export interface AuditLogFilters {
  offset?: number;
  limit?: number;
  sortBy?: string | null;
  sortDirection?: "asc" | "desc" | null;
  entityType?: string | null;
  action?: string | null;
  search?: string | null;
  dateFrom?: string | null;
  dateTo?: string | null;
}

export function useRecentAuditLogs(filters: AuditLogFilters = {}) {
  const {
    offset = 0,
    limit = 50,
    sortBy,
    sortDirection,
    entityType,
    action,
    search,
    dateFrom,
    dateTo,
  } = filters;

  const query = useQuery({
    queryKey: [
      "audit",
      "recent",
      offset,
      limit,
      sortBy,
      sortDirection,
      entityType,
      action,
      search,
      dateFrom,
      dateTo,
    ],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/audit/recent", {
        params: {
          query: {
            offset,
            limit,
            sortBy: sortBy ?? undefined,
            sortDirection: (sortDirection ?? undefined) as
              | "asc"
              | "desc"
              | undefined,
            entityType: entityType ?? undefined,
            action: action ?? undefined,
            search: search ?? undefined,
            dateFrom: dateFrom ?? undefined,
            dateTo: dateTo ?? undefined,
          },
        },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: Number(query.data?.total ?? 0) }), [base, query.data]);
}
