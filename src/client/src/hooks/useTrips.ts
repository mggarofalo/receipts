import { useQuery } from "@tanstack/react-query";
import { localErrorPolicy } from "@/lib/request-error-policy";
import client from "@/lib/api-client";

export function useTripByReceiptId(receiptId: string | null) {
  return useQuery({
    ...localErrorPolicy.query,
    queryKey: ["trips", receiptId],
    enabled: !!receiptId,
    queryFn: async () => {
      const { data, error } = await client.GET(
        "/api/trips",
        { ...localErrorPolicy.request, params: { query: { receiptId: receiptId! } } },
      );
      if (error) throw error;
      return data;
    },
  });
}
