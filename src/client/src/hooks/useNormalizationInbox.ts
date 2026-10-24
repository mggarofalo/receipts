import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import client from "@/lib/api-client";
import { toApiError } from "@/lib/problem-details";
import type { components } from "@/generated/api";
import { localErrorPolicy } from "@/lib/request-error-policy";

// Receipt and catalog committed-change invalidation already owns this root.
export const normalizationInboxKey = [
  "normalization-catalog",
  "inbox",
] as const;
export type InboxPreferences =
  components["schemas"]["NormalizationAssistancePreferencesResponse"];
export function useNormalizationInbox(
  q: string,
  offset: number,
  limit: number,
) {
  return useQuery({
    ...localErrorPolicy.query,
    queryKey: [...normalizationInboxKey, "sources", q, offset, limit],
    queryFn: async ({ signal }) => {
      const { data, error, response } = await client.GET(
        "/api/normalization-inbox",
        {
          ...localErrorPolicy.request,
          signal,
          params: { query: { q, offset, limit } },
        },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
    refetchInterval: 30_000,
  });
}
export function useNormalizationInboxPreferences() {
  return useQuery({
    ...localErrorPolicy.query,
    queryKey: [...normalizationInboxKey, "preferences"],
    queryFn: async ({ signal }) => {
      const { data, error, response } = await client.GET(
        "/api/normalization-inbox/preferences",
        { ...localErrorPolicy.request, signal },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
  });
}
export function useNormalizationInboxActions() {
  const cache = useQueryClient();
  const invalidate = () =>
    cache.invalidateQueries({ queryKey: normalizationInboxKey });
  const savePreferences = useSessionMutation({
    ...localErrorPolicy.mutation,
    mutationFn: async (
      body: components["schemas"]["NormalizationAssistancePreferenceRequest"],
    ) => {
      const { data, error, response } = await client.PUT(
        "/api/normalization-inbox/preferences",
        { ...localErrorPolicy.request, body },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
    onSuccess: invalidate,
  });
  const retryAdmission = useSessionMutation({
    ...localErrorPolicy.mutation,
    mutationFn: async ({
      sourceHash,
      expectedRevision,
    }: {
      sourceHash: string;
      expectedRevision: string;
    }) => {
      const { data, error, response } = await client.POST(
        "/api/normalization-inbox/sources/{sourceHash}/retry-admission",
        {
          ...localErrorPolicy.request,
          params: { path: { sourceHash } },
          body: { expectedRevision },
        },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
    onSuccess: invalidate,
  });
  return { savePreferences, retryAdmission };
}
