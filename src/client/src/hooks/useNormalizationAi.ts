import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import client from "@/lib/api-client";
import { toApiError } from "@/lib/problem-details";
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
  return { cancel, retry };
}
