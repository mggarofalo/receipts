import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import client from "@/lib/api-client";
import { toApiError } from "@/lib/problem-details";
import { repairDomainChange } from "@/lib/query-invalidation";
import { getSessionVersion } from "@/lib/auth";
import { normalizationAiKey, type AiRun } from "./useNormalizationAi";
import type { components } from "@/generated/api";
import { useEffect } from "react";

export type CatalogSource = components["schemas"]["NormalizationSourceKey"];
export type CatalogGroup =
  components["schemas"]["NormalizationSourceGroupResponse"];
export type CatalogCandidate =
  components["schemas"]["NormalizationCatalogCandidateResponse"];
export type CatalogOperation =
  components["schemas"]["NormalizationCatalogOperationResponse"];
export type CatalogMapping =
  components["schemas"]["NormalizationSourceMappingResponse"];
export const normalizationCatalogKey = ["normalization-catalog"] as const;

export function useCatalogCoverage() {
  return useQuery({
    queryKey: [...normalizationCatalogKey, "coverage"],
    queryFn: async ({ signal }) => {
      const { data, error, response } = await client.GET(
        "/api/normalization-catalog/coverage",
        { signal },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
  });
}

export function useCatalogSources(
  q: string,
  disposition: string,
  offset: number,
  limit: number,
) {
  return useQuery({
    queryKey: [
      ...normalizationCatalogKey,
      "sources",
      q,
      disposition,
      offset,
      limit,
    ],
    queryFn: async ({ signal }) => {
      const { data, error, response } = await client.GET(
        "/api/normalization-catalog/sources",
        { signal, params: { query: { q, disposition, offset, limit } } },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
  });
}

export function useCatalogCandidates(source: CatalogSource) {
  return useQuery({
    queryKey: [...normalizationCatalogKey, "candidates", source],
    queryFn: async ({ signal }) => {
      const { data, error, response } = await client.GET(
        "/api/normalization-catalog/candidates",
        {
          signal,
          params: {
            query: { ...source, itemCode: source.itemCode ?? undefined },
          },
        },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
  });
}

export function useCatalogMappings(source: CatalogSource) {
  return useQuery({
    queryKey: [...normalizationCatalogKey, "mappings", source],
    queryFn: async ({ signal }) => {
      const { data, error, response } = await client.GET(
        "/api/normalization-catalog/mappings",
        {
          signal,
          params: {
            query: { ...source, itemCode: source.itemCode ?? undefined },
          },
        },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
  });
}

export function useCatalogOperations() {
  const cache = useQueryClient();
  const result = useQuery<CatalogOperation[]>({
    queryKey: [...normalizationCatalogKey, "operations"],
    queryFn: async ({ signal }) => {
      const { data, error, response } = await client.GET(
        "/api/normalization-catalog/operations",
        { signal },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
    refetchInterval: (query) =>
      query.state.data?.some((operation) =>
        ["applying", "undoing"].includes(operation.state),
      )
        ? 1500
        : false,
  });
  const progress =
    result.data
      ?.map(({ id, applied, undone }) => `${id}:${applied}:${undone}`)
      .join("|") ?? "";
  useEffect(() => {
    if (!progress) return;
    const version = getSessionVersion();
    void repairDomainChange(
      cache,
      "normalized-description",
      () => getSessionVersion() === version,
    );
  }, [cache, progress]);
  return result;
}

export function useCatalogOperation(id: string | null) {
  const cache = useQueryClient();
  const result = useQuery<CatalogOperation>({
    queryKey: [...normalizationCatalogKey, "operation", id],
    enabled: !!id,
    refetchInterval: (query) =>
      ["applying", "undoing"].includes(query.state.data?.state ?? "")
        ? 1500
        : false,
    queryFn: async ({ signal }) => {
      const { data, error, response } = await client.GET(
        "/api/normalization-catalog/operations/{id}",
        { signal, params: { path: { id: id! } } },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
  });
  const applied = result.data?.applied ?? 0;
  const undone = result.data?.undone ?? 0;
  useEffect(() => {
    if (!applied && !undone) return;
    const version = getSessionVersion();
    // Background chunks commit after the initiating mutation has returned.
    // Repair read projections when polling observes new committed progress.
    void repairDomainChange(
      cache,
      "normalized-description",
      () => getSessionVersion() === version,
    );
  }, [cache, id, applied, undone]);
  return result;
}

export function useCatalogActions(
  onPreviewCreated?: (operation: CatalogOperation) => void,
) {
  const cache = useQueryClient();
  const refresh = async () => {
    const version = getSessionVersion();
    await Promise.all([
      cache.invalidateQueries({ queryKey: normalizationCatalogKey }),
      cache.invalidateQueries({ queryKey: normalizationAiKey }),
      repairDomainChange(
        cache,
        "normalized-description",
        () => getSessionVersion() === version,
      ),
    ]);
  };
  const preview = useSessionMutation({
    mutationFn: async (
      body: components["schemas"]["NormalizationCatalogPreviewRequest"],
    ) => {
      const { data, error, response } = await client.POST(
        "/api/normalization-catalog/operations",
        { body },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
    onSuccess: async (operation) => {
      cache.setQueryData(
        [...normalizationCatalogKey, "operation", operation.id],
        operation,
      );
      onPreviewCreated?.(operation);
      await cache.invalidateQueries({ queryKey: normalizationCatalogKey });
    },
  });
  const operate = useSessionMutation({
    mutationFn: async ({
      operation,
      action,
    }: {
      operation: CatalogOperation;
      action: "approve" | "resume" | "cancel" | "undo";
    }) => {
      const result =
        action === "resume"
          ? await client.POST(
              "/api/normalization-catalog/operations/{id}/resume",
              { params: { path: { id: operation.id } } },
            )
          : await client.POST(
              `/api/normalization-catalog/operations/{id}/${action}`,
              {
                params: { path: { id: operation.id } },
                body: { expectedRevision: operation.revision },
              },
            );
      if (!result.response.ok)
        throw toApiError(result.response.status, result.error);
      return result.data!;
    },
    onSuccess: async (operation) => {
      cache.setQueryData(
        [...normalizationCatalogKey, "operation", operation.id],
        operation,
      );
      await refresh();
    },
  });
  const revoke = useSessionMutation({
    mutationFn: async (mapping: CatalogMapping) => {
      const { data, error, response } = await client.POST(
        "/api/normalization-catalog/mappings/{id}/revoke",
        {
          params: { path: { id: mapping.id } },
          body: { expectedRevision: mapping.revision },
        },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
    onSuccess: refresh,
  });
  return { preview, operate, revoke };
}

export function useCatalogSuggestions(onStarted: (run: AiRun) => void) {
  const cache = useQueryClient();
  return useSessionMutation({
    mutationFn: async (
      body: components["schemas"]["StartNormalizationAiSourcesRequest"],
    ) => {
      const { data, error, response } = await client.POST(
        "/api/normalization-ai/source-runs",
        { body },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
    onSuccess: async (run) => {
      onStarted(run);
      await cache.invalidateQueries({ queryKey: normalizationAiKey });
    },
  });
}

export type CatalogLabelReview =
  components["schemas"]["NormalizationCatalogLabelReviewResponse"];

export function useCatalogLabelActions() {
  const cache = useQueryClient();
  const preview = useSessionMutation({
    mutationFn: async ({
      targetId,
      displayLabel,
    }: {
      targetId: string;
      displayLabel: string | null;
    }) => {
      const { data, error, response } = await client.POST(
        "/api/normalization-catalog/targets/{id}/preview-label",
        { params: { path: { id: targetId } }, body: { displayLabel } },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
  });
  const approve = useSessionMutation({
    mutationFn: async ({
      review,
      suggestion,
    }: {
      review: CatalogLabelReview;
      suggestion?: { runId: string; proposalId: string; runRevision: string };
    }) => {
      const { data, error, response } = await client.POST(
        "/api/normalization-catalog/targets/{id}/approve-label",
        {
          params: { path: { id: review.targetId } },
          body: {
            displayLabel: review.proposedDisplayLabel,
            expectedTargetRevision: review.targetRevision,
            expectedAffectedItemCount: review.affectedItemCount,
            suggestionRunId: suggestion?.runId,
            suggestionProposalId: suggestion?.proposalId,
            suggestionRunRevision: suggestion?.runRevision,
          },
        },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
    onSuccess: async () => {
      const version = getSessionVersion();
      await Promise.all([
        cache.invalidateQueries({ queryKey: normalizationCatalogKey }),
        cache.invalidateQueries({ queryKey: normalizationAiKey }),
        repairDomainChange(
          cache,
          "normalized-description",
          () => getSessionVersion() === version,
        ),
      ]);
    },
  });
  return { preview, approve };
}
