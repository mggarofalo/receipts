import { useCallback, useMemo } from "react";
import { differenceInDays, parseISO } from "date-fns";
import { ChartCard, StackedAreaChart } from "@/components/charts";
import { DateRangeSelector } from "@/components/dashboard/DateRangeSelector";
import type { DateRange } from "@/hooks/useDashboard";
import { useCategoryTrendsReport } from "@/hooks/useCategoryTrendsReport";
import { useReportSearchParams } from "@/hooks/useReportSearchParams";
import {
  parseDateRangeParam,
  parseEnumParam,
  parseNumberEnumParam,
  serializeDateRangeParam,
} from "@/lib/report-params";
import { Button } from "@/components/ui/button";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";

type Granularity = "daily" | "monthly" | "quarterly" | "yearly";

const GRANULARITIES = ["daily", "monthly", "quarterly", "yearly"] as const;

const granularityOptions: { value: Granularity; label: string }[] = [
  { value: "daily", label: "Day" },
  { value: "monthly", label: "Month" },
  { value: "quarterly", label: "Quarter" },
  { value: "yearly", label: "Year" },
];

const topNOptions = [3, 5, 7, 10, 15] as const;

function getAutoGranularity(dateRange: DateRange): Granularity {
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

interface CategoryTrendsUrlParams {
  dateRange: DateRange;
  /** `null` means "auto" — derive from the date range (see {@link getAutoGranularity}). */
  granularityOverride: Granularity | null;
  topN: number;
}

const GRANULARITY_OR_AUTO = [...GRANULARITIES, "auto"] as const;

function parseCategoryTrendsParams(
  searchParams: URLSearchParams,
): CategoryTrendsUrlParams {
  const granularity = parseEnumParam(
    searchParams.get("granularity"),
    GRANULARITY_OR_AUTO,
    "auto",
  );

  return {
    dateRange: parseDateRangeParam(searchParams),
    granularityOverride: granularity === "auto" ? null : granularity,
    topN: parseNumberEnumParam(searchParams.get("topN"), topNOptions, 5),
  };
}

function formatCompactCurrency(value: number): string {
  return new Intl.NumberFormat("en-US", {
    style: "currency",
    currency: "USD",
    minimumFractionDigits: 0,
    maximumFractionDigits: 0,
  }).format(value);
}

export default function CategoryTrends() {
  const [urlParams, updateParams] = useReportSearchParams(
    parseCategoryTrendsParams,
  );
  const { dateRange, granularityOverride, topN } = urlParams;

  const autoGranularity = useMemo(
    () => getAutoGranularity(dateRange),
    [dateRange],
  );
  const granularity = granularityOverride ?? autoGranularity;

  const { data, isLoading } = useCategoryTrendsReport({
    startDate: dateRange.startDate,
    endDate: dateRange.endDate,
    granularity,
    topN,
  });

  const handleGranularity = useCallback(
    (g: Granularity) => {
      updateParams({ granularity: g });
    },
    [updateParams],
  );

  const handleTopNChange = useCallback(
    (value: string) => {
      updateParams({ topN: Number(value) });
    },
    [updateParams],
  );

  const handleDateRangeChange = useCallback(
    (range: DateRange) => {
      updateParams({
        ...serializeDateRangeParam(range),
        granularity: undefined,
      });
    },
    [updateParams],
  );

  const categories = useMemo(() => data?.categories ?? [], [data?.categories]);
  const buckets = useMemo(
    () =>
      (data?.buckets ?? []).map((b) => ({
        period: b.period,
        amounts: (b.amounts ?? []).map(Number),
      })),
    [data?.buckets],
  );

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <DateRangeSelector
          value={dateRange}
          onChange={handleDateRangeChange}
          initialPreset="12M"
        />
      </div>

      <ChartCard
        title="Category Trends"
        subtitle="Spending by category over time"
        loading={isLoading}
        empty={categories.length === 0 && !isLoading}
        emptyMessage="No category spending data for this date range"
        action={
          <div className="flex items-center gap-2">
            <div className="flex gap-1">
              {granularityOptions.map(({ value, label }) => (
                <Button
                  key={value}
                  variant={granularity === value ? "default" : "outline"}
                  size="sm"
                  onClick={() => handleGranularity(value)}
                >
                  {label}
                </Button>
              ))}
            </div>
            <Select value={String(topN)} onValueChange={handleTopNChange}>
              <SelectTrigger size="sm" className="w-[100px]" aria-label="Top N categories">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {topNOptions.map((n) => (
                  <SelectItem key={n} value={String(n)}>
                    Top {n}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
        }
      >
        <StackedAreaChart
          categories={categories}
          buckets={buckets}
          formatValue={formatCompactCurrency}
        />
      </ChartCard>
    </div>
  );
}
