import type { components } from "@/generated/api";
import type { CsvValue } from "@/lib/export-csv";

export type BudgetTrendsResult = components["schemas"]["BudgetTrendsResponse"];
type Series = components["schemas"]["BudgetTrendsSeries"];

export function budgetSeriesLabel(series: Series) {
  return series.isActive ? series.name : `${series.name} (archived)`;
}

/** One chart per currency: its series names and their amounts per period. */
export function budgetTrendGroups(data: BudgetTrendsResult | undefined) {
  const series = data?.series ?? [];
  return [...new Set(series.map((item) => item.currency))].map((currency) => {
    const entries = series
      .map((item, index) => ({ item, index }))
      .filter((entry) => entry.item.currency === currency);
    return {
      currency,
      key: `${currency}:${entries.map(({ item }) => item.id ?? (item.isOther ? "other" : "none")).join(",")}`,
      series: entries.map(({ item }) => item),
      categories: entries.map(({ item }) => budgetSeriesLabel(item)),
      buckets: (data?.buckets ?? []).map((bucket) => ({
        period: bucket.period,
        amounts: entries.map(({ index }) => Number(bucket.amounts[index] ?? 0)),
      })),
      total: entries.reduce((sum, { item }) => sum + item.total, 0),
    };
  });
}

/** Long-format rows: one per period and series, with the selection that produced them. */
export function budgetTrendsExport(
  data: BudgetTrendsResult,
  selection: {
    startDate?: string;
    endDate?: string;
    granularity: string;
    topN: number;
    envelopeId?: string;
    occasionId?: string;
  },
) {
  const headers = [
    "Group by",
    "Start date",
    "End date",
    "Granularity",
    "Top N per currency",
    "Envelope filter",
    "Occasion filter",
    "Period",
    "Currency",
    "Series ID",
    "Series",
    "Archived",
    "Other",
    "Series purchase count",
    "Amount",
  ];
  const rows: CsvValue[][] = data.buckets.flatMap((bucket) =>
    data.series.map((series, index) => [
      data.groupBy,
      selection.startDate ?? "",
      selection.endDate ?? "",
      selection.granularity,
      selection.topN,
      selection.envelopeId ?? "",
      selection.occasionId ?? "",
      bucket.period,
      series.currency,
      series.id ?? "",
      series.name,
      !series.isActive,
      series.isOther,
      series.purchaseCount,
      Number(bucket.amounts[index] ?? 0),
    ]),
  );
  return { headers, rows };
}
