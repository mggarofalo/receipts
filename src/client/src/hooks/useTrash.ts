import { invalidateDomainChange } from "@/lib/query-invalidation";
import { useQueryClient } from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import client from "@/lib/api-client";
import { toast } from "sonner";

export function usePurgeTrash() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    mutationFn: async () => {
      const { error } = await client.POST("/api/trash/purge");
      if (error) throw error;
    },
    onSuccess: () => {
      invalidateDomainChange(queryClient, "trash-purge");
      toast.success("Trash emptied successfully");
    },
  });
}
