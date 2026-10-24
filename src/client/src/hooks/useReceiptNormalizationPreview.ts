import { useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { localErrorPolicy } from "@/lib/request-error-policy";
import { toApiError } from "@/lib/problem-details";
import {
  normalizationSourceKey,
  type ReceiptNormalizationSource,
} from "@/lib/receipt-normalization";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";

export function useReceiptNormalizationPreview(
  source: ReceiptNormalizationSource,
  query = "",
  offset = 0,
) {
  const key = normalizationSourceKey(source);
  const settledKey = useDebouncedValue(key);
  const settledQuery = useDebouncedValue(query);
  const isDebouncing = key !== settledKey || query !== settledQuery;
  const result = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["normalization-catalog", "entry-preview", key, query, offset],
    enabled:
      !!source.location.trim() && !!source.description.trim() && !isDebouncing,
    queryFn: async ({ signal }) => {
      const { data, error, response } = await client.POST(
        "/api/receipt-items/normalization-preview",
        {
          ...localErrorPolicy.request,
          signal,
          body: { source, query: query || null, offset, limit: 10 },
        },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
  });
  return { ...result, isDebouncing };
}
