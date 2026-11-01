import { differenceInDays, parseISO } from "date-fns";
import type { DateRange } from "@/hooks/useDashboard";

export type Granularity = "daily" | "monthly" | "quarterly" | "yearly";

export const GRANULARITIES = [
  "daily",
  "monthly",
  "quarterly",
  "yearly",
] as const;

/** URL value; "auto" derives the granularity from the date range. */
export const GRANULARITY_OR_AUTO = [...GRANULARITIES, "auto"] as const;

export const granularityOptions: { value: Granularity; label: string }[] = [
  { value: "daily", label: "Day" },
  { value: "monthly", label: "Month" },
  { value: "quarterly", label: "Quarter" },
  { value: "yearly", label: "Year" },
];

export const topNOptions = [3, 5, 7, 10, 15] as const;

export function getAutoGranularity(dateRange: DateRange): Granularity {
  if (!dateRange.startDate || !dateRange.endDate) {
    return "yearly";
  }
  const days = differenceInDays(
    parseISO(dateRange.endDate),
    parseISO(dateRange.startDate),
  );
  if (days <= 93) return "daily";
  if (days <= 730) return "monthly";
  return "yearly";
}
