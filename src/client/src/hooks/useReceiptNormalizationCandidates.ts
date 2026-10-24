import { useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { toApiError } from "@/lib/problem-details";
import { localErrorPolicy } from "@/lib/request-error-policy";
import type { components } from "@/generated/api";
import {
  normalizationSourceKey,
  type ReceiptNormalizationSource,
} from "@/lib/receipt-normalization";

export type ReceiptNormalizationSuggestion =
  components["schemas"]["ReceiptNormalizationSuggestionResponse"];

export function useReceiptNormalizationCandidates(source: ReceiptNormalizationSource) {
  return useQuery({
    ...localErrorPolicy.query,
    queryKey: ["normalization-catalog", "entry-candidates", normalizationSourceKey(source)],
    enabled: !!source.location.trim() && !!source.description.trim(),
    queryFn: async ({ signal }) => {
      const { data, error, response } = await client.GET(
        "/api/receipt-items/normalization-candidates",
        {
          ...localErrorPolicy.request,
          signal,
          params: { query: { ...source, itemCode: source.itemCode ?? undefined } },
        },
      );
      if (!response.ok) throw toApiError(response.status, error);
      return data!;
    },
  });
}
