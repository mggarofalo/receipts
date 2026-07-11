import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { toast } from "sonner";

export function useSettings() {
  return useQuery({
    queryKey: ["normalized-descriptions", "settings"],
    queryFn: async () => {
      const { data, error } = await client.GET(
        "/api/normalized-descriptions/settings",
      );
      if (error) throw error;
      return data;
    },
  });
}

export function useUpdateSettingsMutation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({
      autoAcceptThreshold,
      pendingReviewThreshold,
    }: {
      autoAcceptThreshold: number;
      pendingReviewThreshold: number;
    }) => {
      const { data, error } = await client.PATCH(
        "/api/normalized-descriptions/settings",
        {
          body: { autoAcceptThreshold, pendingReviewThreshold },
        },
      );
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({
        queryKey: ["normalized-descriptions", "settings"],
      });
      toast.success("Settings saved");
    },
  });
}

export function useTestMatchMutation() {
  return useMutation({
    mutationFn: async ({
      description,
      topN = 5,
      autoAcceptThresholdOverride,
      pendingReviewThresholdOverride,
    }: {
      description: string;
      topN?: number;
      autoAcceptThresholdOverride?: number | null;
      pendingReviewThresholdOverride?: number | null;
    }) => {
      const { data, error } = await client.POST(
        "/api/normalized-descriptions/test",
        {
          body: {
            description,
            topN,
            autoAcceptThresholdOverride:
              autoAcceptThresholdOverride ?? undefined,
            pendingReviewThresholdOverride:
              pendingReviewThresholdOverride ?? undefined,
          },
        },
      );
      if (error) throw error;
      return data;
    },
  });
}

export function usePreviewImpactMutation() {
  return useMutation({
    mutationFn: async ({
      autoAcceptThreshold,
      pendingReviewThreshold,
    }: {
      autoAcceptThreshold: number;
      pendingReviewThreshold: number;
    }) => {
      const { data, error } = await client.POST(
        "/api/normalized-descriptions/settings/preview",
        {
          body: { autoAcceptThreshold, pendingReviewThreshold },
        },
      );
      if (error) throw error;
      return data;
    },
  });
}
