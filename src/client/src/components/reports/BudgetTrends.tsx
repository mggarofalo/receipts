import { useCallback, useMemo } from "react";
import { ChartCard, StackedAreaChart } from "@/components/charts";
import { DateRangeSelector } from "@/components/dashboard/DateRangeSelector";
import { BudgetFilters } from "@/components/reports/BudgetFilters";
import { parseBudgetFilterParams } from "@/lib/budget-filters";
import type { DateRange } from "@/hooks/useDashboard";
import {
  useBudgetTrendsReport,
  type BudgetTrendsParams,
} from "@/hooks/useBudgetTrendsReport";
import { useCsvExport } from "@/hooks/useCsvExport";
import { useReportSearchParams } from "@/hooks/useReportSearchParams";
import {
  budgetSeriesLabel,
  budgetTrendGroups,
  budgetTrendsExport,
} from "@/lib/budget-trends";
import { csvFilename } from "@/lib/export-csv";
import {
  parseDateRangeParam,
  parseEnumParam,
  parseNumberEnumParam,
  serializeDateRangeParam,
} from "@/lib/report-params";
import {
  GRANULARITY_OR_AUTO,
  getAutoGranularity,
  granularityOptions,
  topNOptions,
  type Granularity,
} from "@/lib/trend-granularity";
import { Button } from "@/components/ui/button";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";

const GROUP_BY = ["envelope", "occasion"] as const;
type GroupBy = (typeof GROUP_BY)[number];

function parseBudgetTrendsParams(searchParams: URLSearchParams) {
  const granularity = parseEnumParam(
    searchParams.get("granularity"),
    GRANULARITY_OR_AUTO,
    "auto",
  );
  const groupBy = parseEnumParam(
    searchParams.get("groupBy"),
    GROUP_BY,
    "envelope",
  );
  const filters = parseBudgetFilterParams(searchParams);
  return {
    dateRange: parseDateRangeParam(searchParams),
    granularityOverride: granularity === "auto" ? null : granularity,
    topN: parseNumberEnumParam(searchParams.get("topN"), topNOptions, 7),
    groupBy,
    // A filter on the grouped dimension has no control, so a link carrying one is ignored.
    envelopeId: groupBy === "envelope" ? undefined : filters.envelopeId,
    occasionId: groupBy === "occasion" ? undefined : filters.occasionId,
  };
}

function money(currency: string) {
  const format = new Intl.NumberFormat("en-US", {
    style: "currency",
    currency,
  });
  return (value: number) => format.format(value);
}

/** The budget view: spending by recorded envelope, or by occasion tag. */
export default function BudgetTrends() {
  const [urlParams, updateParams] = useReportSearchParams(
    parseBudgetTrendsParams,
  );
  const {
    dateRange,
    granularityOverride,
    topN,
    groupBy,
    envelopeId,
    occasionId,
  } = urlParams;
  const autoGranularity = useMemo(
    () => getAutoGranularity(dateRange),
    [dateRange],
  );
  const granularity: Granularity = granularityOverride ?? autoGranularity;

  const params: BudgetTrendsParams = {
    startDate: dateRange.startDate,
    endDate: dateRange.endDate,
    granularity,
    topN,
    groupBy,
    envelopeId,
    occasionId,
  };
  const { data, isLoading, isError, refetch } = useBudgetTrendsReport(params);
  const { exportCsv, isExporting } = useCsvExport();
  const groups = useMemo(() => budgetTrendGroups(data), [data]);

  const handleDateRangeChange = useCallback(
    (range: DateRange) => {
      updateParams({
        ...serializeDateRangeParam(range),
        granularity: undefined,
      });
    },
    [updateParams],
  );
  const noun = groupBy === "envelope" ? "envelope" : "occasion";

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <DateRangeSelector value={dateRange} onChange={handleDateRangeChange} />
        <label className="text-sm">
          Group by{" "}
          <select
            aria-label="Group by"
            className="rounded border bg-background p-1"
            value={groupBy}
            onChange={(event) => {
              const next = event.target.value as GroupBy;
              // A filter on the grouped dimension has no control; drop it.
              updateParams(
                next === "envelope"
                  ? { groupBy: next, envelopeId: null }
                  : { groupBy: next, occasionId: null },
              );
            }}
          >
            <option value="envelope">Envelope</option>
            <option value="occasion">Occasion</option>
          </select>
        </label>
        <BudgetFilters
          envelopeId={envelopeId}
          occasionId={occasionId}
          showEnvelope={groupBy === "occasion"}
          showOccasion={groupBy === "envelope"}
          onChange={updateParams}
        />
        <Button
          variant="outline"
          disabled={
            !data || isLoading || isError || isExporting || !data.series.length
          }
          onClick={() => {
            if (data)
              exportCsv({
                filename: csvFilename(`spending-by-${noun}`, dateRange),
                ...budgetTrendsExport(data, { ...params, granularity, topN }),
              });
          }}
        >
          Export CSV
        </Button>
      </div>

      <ChartCard
        title={
          groupBy === "envelope"
            ? "Spending by Envelope"
            : "Spending by Occasion"
        }
        subtitle={`Top ${noun}s per currency; remaining spending in Other`}
        loading={isLoading}
        empty={groups.length === 0 && !isLoading && !isError}
        emptyMessage="No spending for this selection"
        action={
          <div className="flex flex-wrap items-center gap-2">
            <div className="flex gap-1">
              {granularityOptions.map(({ value, label }) => (
                <Button
                  key={value}
                  variant={granularity === value ? "default" : "outline"}
                  size="sm"
                  onClick={() => updateParams({ granularity: value })}
                >
                  {label}
                </Button>
              ))}
            </div>
            <Select
              value={String(topN)}
              onValueChange={(value) => updateParams({ topN: Number(value) })}
            >
              <SelectTrigger
                size="sm"
                className="w-[100px]"
                aria-label={`Top N ${noun}s`}
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
            Spending by {noun} could not be loaded.{" "}
            <Button variant="outline" onClick={() => void refetch()}>
              Retry
            </Button>
          </div>
        ) : (
          groups.map((group) => {
            const format = money(group.currency);
            return (
              <section
                key={group.key}
                aria-label={`${group.currency} spending by ${noun}`}
                className="min-w-0 space-y-2"
              >
                <p className="text-sm font-medium">
                  {group.currency} - {format(group.total)} total
                </p>
                <StackedAreaChart
                  categories={group.categories}
                  buckets={group.buckets}
                  aria-label={`${group.currency} spending by ${noun}`}
                  formatValue={format}
                />
                <table className="w-full text-sm">
                  <thead>
                    <tr className="text-left text-muted-foreground">
                      <th className="font-normal">
                        {groupBy === "envelope" ? "Envelope" : "Occasion"}
                      </th>
                      <th className="text-right font-normal">Purchases</th>
                      <th className="text-right font-normal">Total</th>
                    </tr>
                  </thead>
                  <tbody>
                    {group.series.map((series) => (
                      <tr
                        key={series.id ?? (series.isOther ? "other" : "none")}
                      >
                        <td>{budgetSeriesLabel(series)}</td>
                        <td className="text-right">{series.purchaseCount}</td>
                        <td className="text-right money">
                          {format(series.total)}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </section>
            );
          })
        )}
      </ChartCard>
    </div>
  );
}
