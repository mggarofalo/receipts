import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import client from "@/lib/api-client";
import { toApiError } from "@/lib/problem-details";
import { repairDomainChange } from "@/lib/query-invalidation";
import { getSessionVersion } from "@/lib/auth";
import type { components } from "@/generated/api";

export const normalizationAiKey = ["normalization-ai"] as const;
export type AiRun = components["schemas"]["NormalizationAiRunResponse"];
export function useAiConnection() {
  return useQuery({
    queryKey: [...normalizationAiKey, "connection"],
    queryFn: async ({ signal }) => {
      const { data, error, response } = await client.GET(
        "/api/normalization-ai/connection",
        { signal },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
    retry: false,
    refetchInterval: (query) => (query.state.data?.busy ? 2000 : false),
  });
}
export function useAiCandidates(
  q: string,
  offset: number,
  limit: number,
  enabled: boolean,
) {
  return useQuery({
    queryKey: [...normalizationAiKey, "candidates", q, offset, limit],
    enabled,
    queryFn: async ({ signal }) => {
      const { data, error, response } = await client.GET(
        "/api/normalization-ai/candidates",
        { signal, params: { query: { q, offset, limit } } },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
  });
}
export function useAiRuns(enabled: boolean) {
  return useQuery({
    queryKey: [...normalizationAiKey, "runs"],
    enabled,
    queryFn: async ({ signal }) => {
      const { data, error, response } = await client.GET(
        "/api/normalization-ai/runs",
        { signal },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
    refetchInterval: (query) =>
      query.state.data?.some((run) => ["queued", "running"].includes(run.state))
        ? 2000
        : false,
  });
}
export function useAiRun(id: string | null, enabled: boolean) {
  return useQuery({
    queryKey: [...normalizationAiKey, "run", id],
    enabled: enabled && !!id,
    queryFn: async ({ signal }) => {
      const { data, error, response } = await client.GET(
        "/api/normalization-ai/runs/{id}",
        { signal, params: { path: { id: id! } } },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
    refetchInterval: (query) =>
      ["queued", "running"].includes(query.state.data?.state ?? "")
        ? 1500
        : false,
  });
}
export function useAiActions(onRunCreated?: (run: AiRun) => void) {
  const cache = useQueryClient();
  const invalidate = () =>
    cache.invalidateQueries({ queryKey: normalizationAiKey });
  const start = useSessionMutation({
    mutationFn: async (
      body: components["schemas"]["StartNormalizationAiRunRequest"],
    ) => {
      const { data, error, response } = await client.POST(
        "/api/normalization-ai/runs",
        { body },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
    onSuccess: async (run) => {
      onRunCreated?.(run);
      await invalidate();
    },
  });
  const cancel = useSessionMutation({
    mutationFn: async (id: string) => {
      const { data, error, response } = await client.POST(
        "/api/normalization-ai/runs/{id}/cancel",
        { params: { path: { id } } },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
    onSuccess: invalidate,
  });
  const retry = useSessionMutation({
    mutationFn: async ({
      id,
      clientRequestId,
    }: {
      id: string;
      clientRequestId: string;
    }) => {
      const { data, error, response } = await client.POST(
        "/api/normalization-ai/runs/{id}/retry",
        { params: { path: { id } }, body: { clientRequestId } },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
    onSuccess: async (run) => {
      onRunCreated?.(run);
      await invalidate();
    },
  });
  const apply = useSessionMutation({
    mutationFn: async ({
      id,
      ...body
    }: components["schemas"]["ApplyNormalizationAiRunRequest"] & {
      id: string;
    }) => {
      const { data, error, response } = await client.POST(
        "/api/normalization-ai/runs/{id}/apply",
        { params: { path: { id } }, body },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
    onSuccess: async () => {
      const version = getSessionVersion();
      await Promise.all([
        invalidate(),
        repairDomainChange(
          cache,
          "normalized-description",
          () => getSessionVersion() === version,
        ),
      ]);
    },
  });
  return { start, cancel, retry, apply };
}
