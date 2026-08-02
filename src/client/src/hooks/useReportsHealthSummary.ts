import { useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";

/**
 * Headline counts for the data-quality reports, used by the reports hub to badge
 * the reports that currently need attention. Backed by COUNT-only queries, so it
 * is cheap enough to fetch on every visit to /reports.
 */
export function useReportsHealthSummary() {
  return useQuery({
    queryKey: ["reports", "health-summary"],
    // The counts move only when receipts/items change, and the hub is a
    // navigation surface rather than a live monitor — a short freshness window
    // keeps repeat visits from refetching on every mount.
    staleTime: 60_000,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/reports/health-summary");
      if (error) throw error;
      return data;
    },
  });
}
