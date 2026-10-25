import { invalidateDomainChange } from "@/lib/query-invalidation";
import { useQueryClient } from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import client from "@/lib/api-client";
import { toast } from "sonner";

export function usePurgeTrash() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    mutationFn: async () => {
      const { data, error } = await client.POST("/api/trash/purge");
      if (error) throw error;
      return data;
    },
    onSuccess: (result) => {
      invalidateDomainChange(queryClient, "trash-purge");
      if (result) {
        toast.info(`${result.retainedRecords} records kept in trash. ${result.reason}`);
      } else {
        toast.success("Trash emptied successfully");
      }
    },
  });
}
