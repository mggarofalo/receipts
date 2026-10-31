import { useQuery, useQueryClient } from "@tanstack/react-query";
import type { components } from "@/generated/api";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import client from "@/lib/api-client";
import { invalidateDomainChange } from "@/lib/query-invalidation";
import { localErrorPolicy } from "@/lib/request-error-policy";
import { requestTimeout } from "@/lib/request-timeout";

export type RestructurePlan = components["schemas"]["RestructurePlan"];
export type RestructurePreview = components["schemas"]["RestructurePreview"];
export type RestructureApplication =
  components["schemas"]["RestructureApplicationSummary"];
export type RestructureReport = components["schemas"]["RestructureReport"];

export const restructureApplicationsKey = ["restructure-applications"] as const;

// A plan holds thousands of purchases, and the server checks all of them.
const slowRequest = {
  middleware: [...localErrorPolicy.request.middleware, requestTimeout(300_000)],
};
const CHUNK = 100;

export function useRestructureApplications() {
  return useQuery({
    ...localErrorPolicy.query,
    queryKey: restructureApplicationsKey,
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET("/api/restructure-plans", {
        ...localErrorPolicy.request,
        params: { query: { offset: 0, limit: 25 } },
        signal,
      });
      if (error || !data) throw error ?? new Error("Restructures unavailable");
      return data.items;
    },
  });
}

/** Validates a plan and reports what applying it would do. Changes nothing. */
export function useRestructurePreview() {
  return useSessionMutation<RestructurePreview, unknown, RestructurePlan>({
    ...localErrorPolicy.mutation,
    mutationFn: async (plan) => {
      const { data, error } = await client.POST(
        "/api/restructure-plans/preview",
        { ...slowRequest, body: plan },
      );
      if (error || !data) throw error ?? new Error("Preview unavailable");
      return data;
    },
  });
}

export type RestructureRun =
  | {
      kind: "apply";
      applicationId: string;
      plan: RestructurePlan;
      onProgress?: (application: RestructureApplication) => void;
    }
  | {
      kind: "resume" | "undo";
      id: string;
      onProgress?: (application: RestructureApplication) => void;
    };

/**
 * Applies, resumes or undoes a restructure, one chunk of purchases per request, until it is done.
 * An interrupted run is resumable: the server keeps every committed chunk.
 */
export function useRestructureRun() {
  const queryClient = useQueryClient();
  return useSessionMutation<RestructureApplication, unknown, RestructureRun>({
    ...localErrorPolicy.mutation,
    mutationFn: async (run) => {
      let current: RestructureApplication;
      if (run.kind === "apply") {
        const { data, error } = await client.POST("/api/restructure-plans", {
          ...slowRequest,
          body: { applicationId: run.applicationId, plan: run.plan },
        });
        if (error || !data) throw error ?? new Error("Restructure unavailable");
        current = data;
        run.onProgress?.(current);
      } else {
        const { data, error } = await client.GET("/api/restructure-plans/{id}", {
          ...localErrorPolicy.request,
          params: { path: { id: run.id } },
        });
        if (error || !data) throw error ?? new Error("Restructure unavailable");
        current = data;
      }
      const path =
        run.kind === "undo"
          ? ("/api/restructure-plans/{id}/undo" as const)
          : ("/api/restructure-plans/{id}/apply" as const);
      while (!finished(current, run.kind === "undo")) {
        const { data, error } = await client.POST(path, {
          ...slowRequest,
          params: { path: { id: current.id } },
          body: { limit: CHUNK },
        });
        if (error || !data) throw error ?? new Error("Restructure unavailable");
        current = data;
        run.onProgress?.(current);
      }
      return current;
    },
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: restructureApplicationsKey });
      invalidateDomainChange(queryClient, "taxonomy");
      invalidateDomainChange(queryClient, "receipt-item");
      invalidateDomainChange(queryClient, "envelope");
      invalidateDomainChange(queryClient, "occasion");
    },
  });
}

function finished(application: RestructureApplication, undo: boolean) {
  if (undo) return application.state === "undone";
  // A cancelled run stays cancelled; only undo continues from there.
  return application.state !== "preview" && application.state !== "applying";
}
