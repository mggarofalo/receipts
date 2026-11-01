import { useCallback, useMemo } from "react";
import { ChartCard, StackedAreaChart } from "@/components/charts";
import { DateRangeSelector } from "@/components/dashboard/DateRangeSelector";
import { BudgetFilters } from "@/components/reports/BudgetFilters";
import { parseBudgetFilterParams } from "@/lib/budget-filters";
import type { DateRange } from "@/hooks/useDashboard";
import { useCategoryTrendsReport } from "@/hooks/useCategoryTrendsReport";
import { useReportSearchParams } from "@/hooks/useReportSearchParams";
import {
  parseDateRangeParam,
  parseCategoryReportParams,
  parseEnumParam,
  parseNumberEnumParam,
  serializeDateRangeParam,
} from "@/lib/report-params";
import {
  categoryTrendGroups,
  categoryTrendsExport,
} from "@/lib/category-trends";
import { useCsvExport } from "@/hooks/useCsvExport";
import {
  GRANULARITY_OR_AUTO,
  getAutoGranularity,
  granularityOptions,
  topNOptions,
  type Granularity,
} from "@/lib/trend-granularity";
import { csvFilename } from "@/lib/export-csv";
import type { CategoryTrendsParams } from "@/hooks/useCategoryTrendsReport";
import { Button } from "@/components/ui/button";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";

interface CategoryTrendsUrlParams {
  dateRange: DateRange;
  /** `null` means "auto" — derive from the date range (see {@link getAutoGranularity}). */
  granularityOverride: Granularity | null;
  topN: number;
  selection: ReturnType<typeof parseCategoryReportParams>;
  budget: ReturnType<typeof parseBudgetFilterParams>;
}

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
    selection: parseCategoryReportParams(searchParams),
    budget: parseBudgetFilterParams(searchParams),
    granularityOverride: granularity === "auto" ? null : granularity,
    topN: parseNumberEnumParam(searchParams.get("topN"), topNOptions, 5),
  };
}

export default function CategoryTrends() {
  const [urlParams, updateParams] = useReportSearchParams(
    parseCategoryTrendsParams,
  );
  const { dateRange, granularityOverride, topN, selection, budget } = urlParams;

  const autoGranularity = useMemo(
    () => getAutoGranularity(dateRange),
    [dateRange],
  );
  const granularity = granularityOverride ?? autoGranularity;

  const params: CategoryTrendsParams = {
    ...selection,
    ...budget,
    startDate: dateRange.startDate,
    endDate: dateRange.endDate,
    granularity,
    topN,
  };
  const { data, isLoading, isError, refetch } = useCategoryTrendsReport(params);
  const { exportCsv, isExporting } = useCsvExport();

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

  const groups = useMemo(() => categoryTrendGroups(data), [data]);
  const partial = data?.series?.some(
    (item) => !["classified", "unclassified", "other"].includes(item.state),
  );

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <DateRangeSelector value={dateRange} onChange={handleDateRangeChange} />
        <label className="text-sm">
          Category view{" "}
          <select
            aria-label="Category view"
            className="rounded border bg-background p-1"
            value={selection.categoryView}
            onChange={(event) =>
              updateParams({
                categoryView: event.target.value,
                page: null,
                offset: null,
                capturedCurrentRevisionId: null,
              })
            }
          >
            <option value="asClassified">As classified</option>
            <option value="currentHierarchy">Current hierarchy</option>
          </select>
        </label>
        <label className="text-sm">
          Rollup{" "}
          <select
            aria-label="Category rollup"
            className="rounded border bg-background p-1"
            value={selection.rollup}
            onChange={(event) =>
              updateParams({
                rollup: event.target.value,
                page: null,
                offset: null,
              })
            }
          >
            <option value="assignedCategory">Assigned category</option>
            <option value="root">Root category</option>
          </select>
        </label>
        <BudgetFilters
          envelopeId={budget.envelopeId}
          occasionId={budget.occasionId}
          onChange={updateParams}
        />
        <Button
          variant="outline"
          disabled={
            !data || isLoading || isError || isExporting || !data.series?.length
          }
          onClick={() => {
            if (data)
              exportCsv({
                filename: csvFilename("category-trends", dateRange),
                ...categoryTrendsExport(data, params),
              });
          }}
        >
          Export CSV
        </Button>
      </div>

      <ChartCard
        title="Category Trends"
        subtitle="Top categories per currency; remaining spending in Other"
        loading={isLoading}
        empty={groups.length === 0 && !isLoading && !isError}
        emptyMessage="No category spending data for this date range"
        action={
          <div className="flex flex-wrap items-center gap-2">
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
              <SelectTrigger
                size="sm"
                className="w-[100px]"
                aria-label="Top N categories"
              >
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
        {isError ? (
          <div role="alert">
            Category trends could not be loaded.{" "}
            <Button variant="outline" onClick={() => void refetch()}>
              Retry
            </Button>
          </div>
        ) : (
          <>
            {partial && (
              <p role="status">
                Partial category history - unresolved amounts included
              </p>
            )}
            {selection.knowledgeCutoff && (
              <p className="text-sm">
                Classification knowledge at {selection.knowledgeCutoff}.
                Financial facts remain current.
              </p>
            )}
            {selection.categoryId && (
              <p className="text-sm break-all">
                Category filter: {selection.categoryId}{" "}
                <Button
                  variant="ghost"
                  onClick={() => updateParams({ categoryId: null })}
                >
                  Clear filter
                </Button>
              </p>
            )}
            {data?.currentRevisionId && (
              <p className="text-sm break-all">
                Captured taxonomy revision: {data.currentRevisionId}
              </p>
            )}
            {groups.map((group) => (
              <section
                key={group.key}
                aria-label={`${group.currency} category trends`}
                className="min-w-0"
              >
                <p className="text-sm font-medium">
                  {group.currency}
                  {group.total !== undefined
                    ? ` - ${new Intl.NumberFormat("en-US", { style: "currency", currency: group.currency }).format(group.total)} total`
                    : ""}
                </p>
                <StackedAreaChart
                  categories={group.categories}
                  buckets={group.buckets}
                  aria-label={`${group.currency} category trends`}
                  formatValue={(value) =>
                    new Intl.NumberFormat("en-US", {
                      style: "currency",
                      currency: group.currency,
                    }).format(value)
                  }
                />
              </section>
            ))}
          </>
        )}
      </ChartCard>
    </div>
  );
}
