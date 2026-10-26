import { useQuery, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import type { components } from "@/generated/api";
import { localErrorPolicy } from "@/lib/request-error-policy";
import { useSessionMutation } from "@/hooks/useSessionMutation";

export type MappingSource = components["schemas"]["NormalizationSourceKey"];
export type MappingChange =
  components["schemas"]["CategoryMappingChangeRequest"];

export function useCategoryMapping(source: MappingSource) {
  const cache = useQueryClient();
  const queryKey = ["category-mapping", source];
  const inspection = useQuery({
    ...localErrorPolicy.query,
    queryKey,
    queryFn: async ({ signal }) => {
      const { data, error } = await client.POST(
        "/api/category-mappings/inspect",
        {
          ...localErrorPolicy.request,
          signal,
          body: { source },
        },
      );
      if (error || !data)
        throw error ?? new Error("No mapping inspection returned");
      return data;
    },
  });
  const revisionId = inspection.data?.mapping?.taxonomyRevisionId;
  const approvedTaxonomy = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["taxonomy", "revision", revisionId],
    enabled: !!revisionId,
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET("/api/taxonomy/revisions/{id}", {
        ...localErrorPolicy.request,
        signal,
        params: { path: { id: revisionId! } },
      });
      if (error || !data)
        throw error ?? new Error("No approval taxonomy returned");
      return data;
    },
  });
  const change = useSessionMutation({
    ...localErrorPolicy.mutation,
    mutationFn: async (body: MappingChange) => {
      const { data, error } = await client.POST(
        "/api/category-mappings/changes",
        {
          ...localErrorPolicy.request,
          body,
        },
      );
      if (error || !data)
        throw error ?? new Error("No mapping change returned");
      return data;
    },
    onSuccess: () => cache.invalidateQueries({ queryKey }),
  });
  return { inspection, approvedTaxonomy, change };
}
