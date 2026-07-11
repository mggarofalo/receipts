import { useMutation, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { toast } from "sonner";

export function usePurgeTrash() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async () => {
      const { error } = await client.POST("/api/trash/purge");
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["accounts", "deleted"] });
      queryClient.invalidateQueries({ queryKey: ["receipts", "deleted"] });
      queryClient.invalidateQueries({
        queryKey: ["receipt-items", "deleted"],
      });
      queryClient.invalidateQueries({
        queryKey: ["transactions", "deleted"],
      });
      toast.success("Trash emptied successfully");
    },
  });
}
