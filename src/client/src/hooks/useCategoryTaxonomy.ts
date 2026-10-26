import { useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { localErrorPolicy } from "@/lib/request-error-policy";

/** Complete revision, independent of classification assistance availability. */
export function useCategoryTaxonomy(enabled = true) {
  return useQuery({
    ...localErrorPolicy.query,
    queryKey: ["taxonomy", "current"],
    enabled,
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET("/api/taxonomy", {
        ...localErrorPolicy.request,
        signal,
      });
      if (error || !data) throw error ?? new Error("No taxonomy returned");
      return data;
    },
  });
}
