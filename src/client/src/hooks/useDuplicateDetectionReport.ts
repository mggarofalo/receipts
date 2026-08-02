import { keepPreviousData, useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";

export type MatchOn =
  | "dateAndLocation"
  | "dateAndTotal"
  | "dateAndLocationAndTotal";

export type LocationTolerance = "exact" | "normalized";

export type TotalTolerance = 0 | 0.01 | 0.05 | 0.1 | 0.5 | 1;

export interface DuplicateDetectionParams {
  matchOn?: MatchOn;
  locationTolerance?: LocationTolerance;
  totalTolerance?: TotalTolerance;
  /** Include groups already accepted as "not a duplicate". Defaults to false server-side. */
  includeAccepted?: boolean;
}

export function useDuplicateDetectionReport(
  params: DuplicateDetectionParams = {},
) {
  return useQuery({
    queryKey: [
      "reports",
      "duplicates",
      params.matchOn,
      params.locationTolerance,
      params.totalTolerance,
      params.includeAccepted,
    ],
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/reports/duplicates", {
        params: {
          query: {
            matchOn: params.matchOn,
            locationTolerance: params.locationTolerance,
            totalTolerance: params.totalTolerance,
            includeAccepted: params.includeAccepted,
          },
        },
      });
      if (error) throw error;
      return data;
    },
  });
}
