import { useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";
import type { components } from "@/generated/api";
import { localErrorPolicy } from "@/lib/request-error-policy";

export type YnabStatusResponse = components["schemas"]["YnabStatusResponse"];

export function useYnabStatus() {
  return useQuery({
    ...localErrorPolicy.query,
    queryKey: ["ynab", "status"],
    refetchInterval: 30_000, // live-ish health snapshot; cheap (no live YNAB call server-side)
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET("/api/ynab/status", {
        ...localErrorPolicy.request,
        signal,
      });
      if (error) throw error;
      return data;
    },
  });
}
