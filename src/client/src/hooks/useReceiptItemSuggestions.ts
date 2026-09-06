import { useMemo } from "react";
import { useStableQuery } from "@/hooks/useStableQuery";
import { localErrorPolicy } from "@/lib/request-error-policy";
import { useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { useDebouncedValue } from "./useDebouncedValue";

export function useReceiptItemSuggestions(
  itemCode: string,
  location?: string | null,
  options?: { limit?: number; enabled?: boolean },
) {
  const debouncedItemCode = useDebouncedValue(itemCode, 300);
  const enabled =
    (options?.enabled ?? true) && debouncedItemCode.length >= 1;

  const queryResult = useQuery({
    ...localErrorPolicy.query,
    queryKey: [
      "receiptItemSuggestions",
      debouncedItemCode,
      location,
      options?.limit,
    ],
    enabled,
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET(
        "/api/receipt-items/suggestions",
        {
          ...localErrorPolicy.request,
          params: {
            query: {
              itemCode: debouncedItemCode,
              location: location ?? undefined,
              limit: options?.limit ?? 10,
            },
          },
          signal,
        },
      );
      if (error) throw error;
      return data;
    },
    staleTime: 30_000,
  });
  const base = useStableQuery(queryResult);
  const isDebouncing = debouncedItemCode !== itemCode;
  return useMemo(() => ({ ...base, isDebouncing }), [base, isDebouncing]);
}
