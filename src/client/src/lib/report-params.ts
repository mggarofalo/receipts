import { format, subMonths } from "date-fns";
import { parseDateValue } from "@/lib/format";
import type { DateRange } from "@/hooks/useDashboard";

export type CategoryReportView = "asClassified" | "currentHierarchy";

export function parseCategoryViewParam(
  params: URLSearchParams,
): CategoryReportView {
  return params.get("categoryView") === "currentHierarchy"
    ? "currentHierarchy"
    : "asClassified";
}

/** Category selection shared by report URLs. Invalid optional values are ignored. */
export function parseCategoryReportParams(params: URLSearchParams) {
  const categoryView = parseCategoryViewParam(params);
  const uuid = (value: string | null) =>
    value &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(
      value,
    )
      ? value
      : undefined;
  const cutoff = params.get("knowledgeCutoff");
  return {
    categoryView,
    rollup:
      params.get("rollup") === "root"
        ? ("root" as const)
        : ("assignedCategory" as const),
    categoryId: uuid(params.get("categoryId")),
    knowledgeCutoff:
      cutoff &&
      /^\d{4}-\d{2}-\d{2}T.*Z$/.test(cutoff) &&
      Number.isFinite(Date.parse(cutoff))
        ? new Date(cutoff).toISOString()
        : undefined,
    capturedCurrentRevisionId:
      categoryView === "currentHierarchy"
        ? uuid(params.get("capturedCurrentRevisionId"))
        : undefined,
  };
}

/**
 * The standard default date range for every date-filtered report: the
 * trailing 12 months through today. Centralized here so reports can't drift
 * out of sync with each other (RECEIPTS-840) — previously each report
 * hard-coded its own default (most used 1 month; Item Cost Over Time used
 * "all time").
 */
export function getDefaultRange(): DateRange {
  const now = new Date();
  return {
    startDate: format(subMonths(now, 12), "yyyy-MM-dd"),
    endDate: format(now, "yyyy-MM-dd"),
  };
}

const ISO_DATE_PATTERN = /^\d{4}-\d{2}-\d{2}$/;

function isValidIsoDate(value: string | null): value is string {
  return (
    value !== null &&
    ISO_DATE_PATTERN.test(value) &&
    parseDateValue(value) !== null
  );
}

/**
 * Sentinel written to the `startDate` param to mean "no date filter" (the
 * DateRangeSelector's "All" preset) — distinct from the param being absent
 * entirely, which means "use the default range". Without this sentinel,
 * selecting "All" and reloading the page (or sharing the link) would
 * silently revert to the default instead of staying unrestricted.
 */
const ALL_TIME_SENTINEL = "all";

export interface DateRangeParamKeys {
  startDate: string;
  endDate: string;
}

const DEFAULT_RANGE_KEYS: DateRangeParamKeys = {
  startDate: "startDate",
  endDate: "endDate",
};

/**
 * Reads a date range from URL search params, falling back to
 * {@link getDefaultRange} when the params are absent or malformed — garbage
 * strings, a reversed range, or only one side present. Recognizes the "all"
 * sentinel (see {@link ALL_TIME_SENTINEL}) as an explicit open-ended range.
 */
export function parseDateRangeParam(
  searchParams: URLSearchParams,
  keys: DateRangeParamKeys = DEFAULT_RANGE_KEYS,
): DateRange {
  const rawStart = searchParams.get(keys.startDate);
  const rawEnd = searchParams.get(keys.endDate);

  if (rawStart === ALL_TIME_SENTINEL) {
    return { startDate: undefined, endDate: undefined };
  }

  if (
    isValidIsoDate(rawStart) &&
    isValidIsoDate(rawEnd) &&
    rawStart <= rawEnd
  ) {
    return { startDate: rawStart, endDate: rawEnd };
  }

  return getDefaultRange();
}

/**
 * Serializes a date range for a {@link useReportSearchParams} `update()`
 * call. An open-ended range (both sides undefined) writes the "all"
 * sentinel so it round-trips through a reload instead of collapsing back to
 * the default; a partial range (only one side set) is treated as malformed
 * and reverts to the default rather than writing an inconsistent pair.
 */
export function serializeDateRangeParam(
  range: DateRange,
  keys: DateRangeParamKeys = DEFAULT_RANGE_KEYS,
): Record<string, string | undefined> {
  if (!range.startDate && !range.endDate) {
    return { [keys.startDate]: ALL_TIME_SENTINEL, [keys.endDate]: undefined };
  }
  if (range.startDate && range.endDate) {
    return { [keys.startDate]: range.startDate, [keys.endDate]: range.endDate };
  }
  const fallback = getDefaultRange();
  return {
    [keys.startDate]: fallback.startDate,
    [keys.endDate]: fallback.endDate,
  };
}

/** Parses a param against an allow-list, falling back when missing/unknown. */
export function parseEnumParam<T extends string>(
  value: string | null,
  allowed: readonly T[],
  fallback: T,
): T {
  return value !== null && (allowed as readonly string[]).includes(value)
    ? (value as T)
    : fallback;
}

/** Parses a numeric param against an allow-list, falling back when missing/unknown. */
export function parseNumberEnumParam<T extends number>(
  value: string | null,
  allowed: readonly T[],
  fallback: T,
): T {
  if (value === null || value.trim() === "") return fallback;
  const parsed = Number(value);
  return allowed.includes(parsed as T) ? (parsed as T) : fallback;
}

/**
 * Parses a positive integer param (e.g. a page number), falling back when
 * missing, non-numeric, non-integer, zero/negative, or absurdly large. The
 * upper bound exists purely to guard against pathological URLs — it is not
 * a substitute for clamping to the real page count once data has loaded.
 */
export function parsePositiveIntParam(
  value: string | null,
  fallback: number,
  max = 100_000,
): number {
  if (value === null || value.trim() === "") return fallback;
  const parsed = Number(value);
  if (!Number.isInteger(parsed) || parsed < 1 || parsed > max) return fallback;
  return parsed;
}

/** Parses a boolean param encoded as the literal string "true"/"false". */
export function parseBoolParam(
  value: string | null,
  fallback: boolean,
): boolean {
  if (value === "true") return true;
  if (value === "false") return false;
  return fallback;
}

/**
 * Parses the Item Cost Over Time item selector (`item`). Older links in the
 * retired category-text mode (`categoryOnly=true`) name a legacy category, not
 * an item, so they open with nothing selected.
 */
export function parseSelectedItemParam(
  searchParams: URLSearchParams,
): string | null {
  if (parseBoolParam(searchParams.get("categoryOnly"), false)) return null;
  return searchParams.get("item") || null;
}

/** Serializes the Item Cost Over Time item selector for an `update()` call. */
export function serializeSelectedItemParam(
  description: string | null,
): Record<string, string | undefined> {
  // Older links carried a legacy category beside the item; drop it on write.
  return {
    item: description ?? undefined,
    category: undefined,
    categoryOnly: undefined,
  };
}

/**
 * Parses the Item Cost Over Time normalized-description selector (`normalized`),
 * the drill-down target from Spending by Normalized Description (RECEIPTS-841).
 * Unlike `item`/`category` this is a single canonical name that stands alone,
 * because a normalized description spans many raw descriptions and categories.
 * Blank/whitespace-only values are treated as absent.
 */
export function parseNormalizedDescriptionParam(
  searchParams: URLSearchParams,
): string | null {
  const value = searchParams.get("normalized");
  if (value === null) return null;
  const trimmed = value.trim();
  return trimmed === "" ? null : trimmed;
}

/**
 * Bucket label the Spending by Normalized Description report uses for receipt
 * items that have no normalized description. It is a synthetic grouping, not a
 * real canonical name, so it has no drill-down target.
 */
export const NOT_NORMALIZED_LABEL = "(Not Normalized)";

/**
 * Bucket label the Spending by Location report uses for receipts with an empty
 * location. Like {@link NOT_NORMALIZED_LABEL} it is synthetic — there is no
 * literal location string to filter the receipts list by — so it is excluded
 * from drill-down linking.
 */
export const NO_LOCATION_LABEL = "(No Location)";

/**
 * Builds the receipts-list URL for a Spending by Location row, or `null` when
 * the row is the synthetic "(No Location)" bucket, which has no filterable
 * location value behind it.
 */
export function buildLocationDrillDownHref(location: string): string | null {
  if (!location || location === NO_LOCATION_LABEL) return null;
  return `/receipts?location=${encodeURIComponent(location)}`;
}

/**
 * Builds the Item Cost Over Time URL for a Spending by Normalized Description
 * row, carrying the current date range over so the destination report opens on
 * the same window the user was looking at. Returns `null` for the synthetic
 * "(Not Normalized)" bucket, which is not a real normalized description.
 */
export function buildItemCostDrillDownHref(
  canonicalName: string,
  range: DateRange,
): string | null {
  if (!canonicalName || canonicalName === NOT_NORMALIZED_LABEL) return null;

  const params = new URLSearchParams({
    report: "item-cost-over-time",
    normalized: canonicalName,
  });
  for (const [key, value] of Object.entries(serializeDateRangeParam(range))) {
    if (value !== undefined) params.set(key, value);
  }
  return `/reports?${params.toString()}`;
}
