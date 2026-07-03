import { useMemo } from "react";
import { useStableQuery } from "@/hooks/useStableQuery";
import { useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";

export function useMyAuthAuditLog(
  offset = 0,
  limit = 50,
  sortBy?: string | null,
  sortDirection?: string | null,
) {
  const query = useQuery({
    queryKey: ["auth-audit", "me", offset, limit, sortBy, sortDirection],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/auth/audit/me", {
        params: {
          query: {
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

export function useRecentAuthAuditLogs(
  offset = 0,
  limit = 50,
  sortBy?: string | null,
  sortDirection?: string | null,
) {
  const query = useQuery({
    queryKey: ["auth-audit", "recent", offset, limit, sortBy, sortDirection],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/auth/audit/recent", {
        params: {
          query: {
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

export function useFailedAuthAttempts(
  offset = 0,
  limit = 50,
  sortBy?: string | null,
  sortDirection?: string | null,
) {
  const query = useQuery({
    queryKey: ["auth-audit", "failed", offset, limit, sortBy, sortDirection],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/auth/audit/failed", {
        params: {
          query: {
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
