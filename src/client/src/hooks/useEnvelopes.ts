import { useMemo } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useStableQuery } from "@/hooks/useStableQuery";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import { localErrorPolicy, toastErrorPolicy } from "@/lib/request-error-policy";
import { invalidateDomainChange, queryKeys } from "@/lib/query-invalidation";
import client from "@/lib/api-client";
import type { components } from "@/generated/api";
import { toast } from "sonner";

export type Envelope = components["schemas"]["EnvelopeResponse"];
export type Occasion = components["schemas"]["OccasionResponse"];
export type ItemTypeEnvelopeDefault =
  components["schemas"]["ItemTypeEnvelopeDefaultResponse"];

/** All live envelopes, active and archived, ordered by name. */
export function useEnvelopes(enabled = true) {
  const query = useQuery({
    ...localErrorPolicy.query,
    queryKey: [...queryKeys.envelopes, "all"],
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET("/api/envelopes", {
        ...localErrorPolicy.request,
        signal,
      });
      if (error) throw error;
      return data;
    },
    enabled,
  });
  const base = useStableQuery(query);
  return useMemo(
    () => ({ ...base, envelopes: query.data?.data ?? [] }),
    [base, query.data],
  );
}

export function useCreateEnvelope() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async (name: string) => {
      const { data, error } = await client.POST("/api/envelopes", {
        ...toastErrorPolicy.request,
        body: { name, isActive: true },
      });
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      invalidateDomainChange(queryClient, "envelope", "created");
      toast.success("Envelope created");
    },
  });
}

export function useUpdateEnvelope() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async ({ id, ...body }: Envelope) => {
      const { error } = await client.PUT("/api/envelopes/{id}", {
        ...toastErrorPolicy.request,
        params: { path: { id } },
        body,
      });
      if (error) throw error;
    },
    onSuccess: () => {
      invalidateDomainChange(queryClient, "envelope");
      toast.success("Envelope updated");
    },
  });
}

export function useDeleteEnvelope() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async (id: string) => {
      const { error } = await client.DELETE("/api/envelopes/{id}", {
        ...toastErrorPolicy.request,
        params: { path: { id } },
      });
      if (error) throw error;
    },
    onSuccess: () => {
      invalidateDomainChange(queryClient, "envelope");
      toast.success("Envelope deleted");
    },
  });
}

/** All live occasions, active and archived, ordered by name. */
export function useOccasions(enabled = true) {
  const query = useQuery({
    ...localErrorPolicy.query,
    queryKey: [...queryKeys.occasions, "all"],
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET("/api/occasions", {
        ...localErrorPolicy.request,
        signal,
      });
      if (error) throw error;
      return data;
    },
    enabled,
  });
  const base = useStableQuery(query);
  return useMemo(
    () => ({ ...base, occasions: query.data?.data ?? [] }),
    [base, query.data],
  );
}

export function useCreateOccasion() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async (name: string) => {
      const { data, error } = await client.POST("/api/occasions", {
        ...toastErrorPolicy.request,
        body: { name, isActive: true },
      });
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      invalidateDomainChange(queryClient, "occasion", "created");
      toast.success("Occasion created");
    },
  });
}

export function useUpdateOccasion() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async ({ id, ...body }: Occasion) => {
      const { error } = await client.PUT("/api/occasions/{id}", {
        ...toastErrorPolicy.request,
        params: { path: { id } },
        body,
      });
      if (error) throw error;
    },
    onSuccess: () => {
      invalidateDomainChange(queryClient, "occasion");
      toast.success("Occasion updated");
    },
  });
}

export function useDeleteOccasion() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async (id: string) => {
      const { error } = await client.DELETE("/api/occasions/{id}", {
        ...toastErrorPolicy.request,
        params: { path: { id } },
      });
      if (error) throw error;
    },
    onSuccess: () => {
      invalidateDomainChange(queryClient, "occasion");
      toast.success("Occasion deleted");
    },
  });
}

export function useItemTypeEnvelopeDefaults(enabled = true) {
  const query = useQuery({
    ...localErrorPolicy.query,
    queryKey: [...queryKeys.itemTypeEnvelopeDefaults],
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET(
        "/api/item-type-envelope-defaults",
        { ...localErrorPolicy.request, signal },
      );
      if (error) throw error;
      return data;
    },
    enabled,
  });
  const base = useStableQuery(query);
  return useMemo(
    () => ({ ...base, defaults: query.data?.data ?? [] }),
    [base, query.data],
  );
}

export function useSetItemTypeEnvelopeDefault() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async ({
      categoryId,
      envelopeId,
    }: {
      categoryId: string;
      envelopeId: string;
    }) => {
      const { data, error } = await client.PUT(
        "/api/item-type-envelope-defaults/{categoryId}",
        {
          ...toastErrorPolicy.request,
          params: { path: { categoryId } },
          body: { envelopeId },
        },
      );
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      invalidateDomainChange(queryClient, "item-type-envelope-default");
      toast.success("Default envelope saved");
    },
  });
}

export function useDeleteItemTypeEnvelopeDefault() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async (categoryId: string) => {
      const { error } = await client.DELETE(
        "/api/item-type-envelope-defaults/{categoryId}",
        { ...toastErrorPolicy.request, params: { path: { categoryId } } },
      );
      if (error) throw error;
    },
    onSuccess: () => {
      invalidateDomainChange(queryClient, "item-type-envelope-default");
      toast.success("Default envelope removed");
    },
  });
}
