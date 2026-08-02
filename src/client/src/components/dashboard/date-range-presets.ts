import {
  format,
  subMonths,
  startOfMonth,
  startOfQuarter,
  startOfYear,
} from "date-fns";
import type { DateRange } from "@/hooks/useDashboard";

export type PresetKey =
  | "1M"
  | "3M"
  | "12M"
  | "60M"
  | "MTD"
  | "QTD"
  | "YTD"
  | "all"
  | "year";

export interface Preset {
  label: string;
  getRange: (selectedYear?: number) => DateRange;
}

export const presets: Record<PresetKey, Preset> = {
  "1M": {
    label: "1M",
    getRange: () => ({
      startDate: format(subMonths(new Date(), 1), "yyyy-MM-dd"),
      endDate: format(new Date(), "yyyy-MM-dd"),
    }),
  },
  "3M": {
    label: "3M",
    getRange: () => ({
      startDate: format(subMonths(new Date(), 3), "yyyy-MM-dd"),
      endDate: format(new Date(), "yyyy-MM-dd"),
    }),
  },
  "12M": {
    label: "1Y",
    getRange: () => ({
      startDate: format(subMonths(new Date(), 12), "yyyy-MM-dd"),
      endDate: format(new Date(), "yyyy-MM-dd"),
    }),
  },
  "60M": {
    label: "5Y",
    getRange: () => ({
      startDate: format(subMonths(new Date(), 60), "yyyy-MM-dd"),
      endDate: format(new Date(), "yyyy-MM-dd"),
    }),
  },
  MTD: {
    label: "MTD",
    getRange: () => ({
      startDate: format(startOfMonth(new Date()), "yyyy-MM-dd"),
      endDate: format(new Date(), "yyyy-MM-dd"),
    }),
  },
  QTD: {
    label: "QTD",
    getRange: () => ({
      startDate: format(startOfQuarter(new Date()), "yyyy-MM-dd"),
      endDate: format(new Date(), "yyyy-MM-dd"),
    }),
  },
  YTD: {
    label: "YTD",
    getRange: () => ({
      startDate: format(startOfYear(new Date()), "yyyy-MM-dd"),
      endDate: format(new Date(), "yyyy-MM-dd"),
    }),
  },
  all: {
    label: "All",
    getRange: () => ({
      startDate: undefined,
      endDate: undefined,
    }),
  },
  year: {
    label: "Year",
    getRange: (selectedYear?: number) => {
      const y = selectedYear ?? new Date().getFullYear();
      return {
        startDate: `${y}-01-01`,
        endDate: `${y}-12-31`,
      };
    },
  },
};

export const presetGroups: { label: string; keys: PresetKey[] }[] = [
  { label: "Trailing", keys: ["1M", "3M", "12M", "60M"] },
  { label: "To Date", keys: ["MTD", "QTD", "YTD"] },
  { label: "", keys: ["all"] },
];

/** Preset keys whose range is computed relative to "now" (i.e. every key
 * except "year", which is pinned to a specific calendar year, and "all",
 * handled separately since it has no dates to compare). */
const RELATIVE_PRESET_KEYS = [
  "1M",
  "3M",
  "12M",
  "60M",
  "MTD",
  "QTD",
  "YTD",
] as const;

const YEAR_START_PATTERN = /^(\d{4})-01-01$/;

/**
 * Matches a date range against the known presets so the picker can
 * highlight whichever preset (if any) actually produced the current
 * `value` — including a `value` that arrived from outside the component
 * (URL search params, browser back/forward, a shared link). Meant to be
 * recomputed on every render rather than stored as state: storing it would
 * let it drift from `value` the moment something external changes the
 * range, which is exactly the bug this replaces (RECEIPTS-840 code review).
 *
 * Returns `preset: null` for a range that doesn't match any preset (a
 * genuinely custom range, or a "rolling" preset like "12M" whose bookmarked
 * dates have since fallen out of sync with today's rolling window).
 */
export function matchPreset(value: DateRange): {
  preset: PresetKey | null;
  year: number | null;
} {
  if (!value.startDate && !value.endDate) {
    return { preset: "all", year: null };
  }
  if (!value.startDate || !value.endDate) {
    return { preset: null, year: null };
  }

  const yearMatch = YEAR_START_PATTERN.exec(value.startDate);
  if (yearMatch && value.endDate === `${yearMatch[1]}-12-31`) {
    return { preset: "year", year: Number(yearMatch[1]) };
  }

  for (const key of RELATIVE_PRESET_KEYS) {
    const range = presets[key].getRange();
    if (range.startDate === value.startDate && range.endDate === value.endDate) {
      return { preset: key, year: null };
    }
  }

  return { preset: null, year: null };
}
