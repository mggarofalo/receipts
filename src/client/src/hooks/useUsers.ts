import { useMemo } from "react";
import { useStableQuery } from "@/hooks/useStableQuery";
import {
  useQuery,
  useQueryClient,
} from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import client, { attemptTokenRefresh } from "@/lib/api-client";
import { toast } from "sonner";

export function useUsers(
  offset = 0,
  limit = 50,
  sortBy?: string | null,
  sortDirection?: string | null,
  options: { enabled?: boolean } = {},
) {
  const { enabled = true } = options;
  const query = useQuery({
    queryKey: ["users", "list", offset, limit, sortBy, sortDirection],
    enabled,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/users", {
        params: { query: { offset, limit, sortBy: sortBy ?? undefined, sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined } },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: Number(query.data?.total ?? 0) }), [base, query.data]);
}

export function useUser(userId: string | null) {
  return useQuery({
    queryKey: ["users", userId],
    enabled: !!userId,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/users/{userId}", {
        params: { path: { userId: userId! } },
      });
      if (error) throw error;
      return data;
    },
  });
}

export function useCreateUser() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    mutationFn: async (body: {
      email: string;
      password: string;
      role: string;
      firstName?: string | null;
      lastName?: string | null;
    }) => {
      const { data, error } = await client.POST("/api/users", { body });
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["users"] });
      toast.success("User created");
    },
  });
}

export function useUpdateUser(currentUserId?: string) {
  const queryClient = useQueryClient();
  return useSessionMutation({
    mutationFn: async ({
      userId,
      body,
    }: {
      userId: string;
      body: {
        email: string;
        role: string;
        isDisabled: boolean;
        firstName?: string | null;
        lastName?: string | null;
      };
    }) => {
      const { error } = await client.PUT("/api/users/{userId}", {
        params: { path: { userId } },
        body,
      });
      if (error) throw error;
      return userId;
    },
    onSuccess: (_, variables) => {
      queryClient.invalidateQueries({ queryKey: ["users"] });
      toast.success("User updated");
      if (currentUserId && variables.userId === currentUserId) {
        attemptTokenRefresh();
      }
    },
  });
}

export function useDeleteUser() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    mutationFn: async (userId: string) => {
      const { error } = await client.DELETE("/api/users/{userId}", {
        params: { path: { userId } },
      });
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["users"] });
      toast.success("User deactivated");
    },
  });
}

export function useResetUserPassword() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    mutationFn: async ({
      userId,
      newPassword,
    }: {
      userId: string;
      newPassword: string;
    }) => {
      const { error } = await client.POST(
        "/api/users/{userId}/reset-password",
        {
          params: { path: { userId } },
          body: { newPassword },
        },
      );
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["users"] });
      toast.success("Password reset successfully");
    },
  });
}
