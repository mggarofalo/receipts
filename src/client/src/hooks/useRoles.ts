import {
  useQuery,
  useMutation,
  useQueryClient,
} from "@tanstack/react-query";
import client from "@/lib/api-client";
import { toast } from "sonner";

export function useUserRoles(userId: string | null) {
  return useQuery({
    queryKey: ["userRoles", userId],
    enabled: !!userId,
    queryFn: async () => {
      const { data, error } = await client.GET(
        "/api/users/{userId}/roles",
        { params: { path: { userId: userId! } } },
      );
      if (error) throw error;
      return data;
    },
  });
}

export function useAssignRole() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ userId, role }: { userId: string; role: string }) => {
      const { error } = await client.POST(
        "/api/users/{userId}/roles/{role}",
        { params: { path: { userId, role } } },
      );
      if (error) throw error;
    },
    onSuccess: (_data, { userId, role }) => {
      queryClient.invalidateQueries({ queryKey: ["userRoles", userId] });
      toast.success(`Role "${role}" assigned`);
    },
  });
}

export function useRemoveRole() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ userId, role }: { userId: string; role: string }) => {
      const { error } = await client.DELETE(
        "/api/users/{userId}/roles/{role}",
        { params: { path: { userId, role } } },
      );
      if (error) throw error;
    },
    onSuccess: (_data, { userId, role }) => {
      queryClient.invalidateQueries({ queryKey: ["userRoles", userId] });
      toast.success(`Role "${role}" removed`);
    },
  });
}
