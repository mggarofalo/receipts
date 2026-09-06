import { queryKeys } from "@/lib/query-invalidation";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";

export interface DateRange {
  startDate?: string;
  endDate?: string;
}

export function useDashboardSummary(dateRange: DateRange) {
  return useQuery({
    queryKey: [...queryKeys.dashboard, "summary", dateRange.startDate, dateRange.endDate],
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/dashboard/summary", {
        params: {
          query: {
            startDate: dateRange.startDate,
            endDate: dateRange.endDate,
          },
        },
      });
      if (error) throw error;
      return data;
    },
  });
}

export function useDashboardSpendingOverTime(
  dateRange: DateRange,
  granularity?: "daily" | "monthly" | "quarterly" | "yearly",
) {
  return useQuery({
    queryKey: [
      ...queryKeys.dashboard,
      "spending-over-time",
      dateRange.startDate,
      dateRange.endDate,
      granularity,
    ],
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await client.GET(
        "/api/dashboard/spending-over-time",
        {
          params: {
            query: {
              startDate: dateRange.startDate,
              endDate: dateRange.endDate,
              granularity,
            },
          },
        },
      );
      if (error) throw error;
      return data;
    },
  });
}

export function useDashboardEarliestReceiptYear() {
  return useQuery({
    queryKey: [...queryKeys.dashboard, "earliest-receipt-year"],
    staleTime: Infinity,
    queryFn: async () => {
      const { data, error } = await client.GET(
        "/api/dashboard/earliest-receipt-year",
      );
      if (error) throw error;
      return data;
    },
  });
}

export function useDashboardSpendingByCategory(
  dateRange: DateRange,
  limit = 100,
) {
  return useQuery({
    queryKey: [
      ...queryKeys.dashboard,
      "spending-by-category",
      dateRange.startDate,
      dateRange.endDate,
      limit,
    ],
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await client.GET(
        "/api/dashboard/spending-by-category",
        {
          params: {
            query: {
              startDate: dateRange.startDate,
              endDate: dateRange.endDate,
              limit,
            },
          },
        },
      );
      if (error) throw error;
      return data;
    },
  });
}

export function useDashboardSpendingByStore(dateRange: DateRange) {
  return useQuery({
    queryKey: [
      ...queryKeys.dashboard,
      "spending-by-store",
      dateRange.startDate,
      dateRange.endDate,
    ],
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await client.GET(
        "/api/dashboard/spending-by-store",
        {
          params: {
            query: {
              startDate: dateRange.startDate,
              endDate: dateRange.endDate,
            },
          },
        },
      );
      if (error) throw error;
      return data;
    },
  });
}

export function useDashboardSpendingByAccount(dateRange: DateRange) {
  return useQuery({
    queryKey: [
      ...queryKeys.dashboard,
      "spending-by-account",
      dateRange.startDate,
      dateRange.endDate,
    ],
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await client.GET(
        "/api/dashboard/spending-by-account",
        {
          params: {
            query: {
              startDate: dateRange.startDate,
              endDate: dateRange.endDate,
            },
          },
        },
      );
      if (error) throw error;
      return data;
    },
  });
}
