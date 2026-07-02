import { useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";

// Defined inline (matching the generated api.d.ts) — the YNAB paths exceed
// TypeScript's type-resolution depth when indexed via the generated `paths` union.
export type YnabStatusResponse = {
  isConfigured: boolean;
  lastValidatedAt?: string | null;
  lastPushSuccessAt?: string | null;
  lastPushFailureAt?: string | null;
  pushCountLast24h: number;
  pushCountLast7d: number;
  pushCountLast30d: number;
  pushSuccessLast30d: number;
  pushFailureLast30d: number;
};

export function useYnabStatus() {
  return useQuery({
    queryKey: ["ynab", "status"],
    refetchInterval: 30_000, // live-ish health snapshot; cheap (no live YNAB call server-side)
    queryFn: async () => {
      const { data, error } = await client.GET(
        "/api/ynab/status" as never,
        {} as never,
      );
      if (error) throw error;
      return data as unknown as YnabStatusResponse;
    },
  });
}
