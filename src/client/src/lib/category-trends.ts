import type { components } from "@/generated/api";
import type { CategoryTrendsParams } from "@/hooks/useCategoryTrendsReport";
import type { CsvValue } from "@/lib/export-csv";

export type CategoryTrendsResult =
  components["schemas"]["CategoryTrendsResponse"];
type Series = components["schemas"]["CategoryTrendsSeries"];
const stateLabels: Record<string, string> = {
  unclassified: "Unclassified",
  missingAssignment: "Missing assignment",
  unresolvedLegacy: "Unresolved legacy category",
  unresolvedPath: "Unresolved path",
  beforeMigrationHistory: "Before recorded history",
  other: "Other",
};
export function trendIdentity(series: Series) {
  return JSON.stringify([
    series.currency,
    series.state,
    series.isOther,
    series.pathRevisionId,
    series.categoryId,
    series.path.map((node) => node.categoryId),
  ]);
}
export function trendLabel(series: Series) {
  return series.isOther
    ? "Other"
    : series.path.length
      ? series.path
          .map((node) => `${node.name}${node.archived ? " (archived)" : ""}`)
          .join(" / ")
      : (stateLabels[series.state] ?? series.state);
}
export function categoryTrendGroups(data: CategoryTrendsResult | undefined) {
  const series = data?.series ?? [];
  return [...new Set(series.map((item) => item.currency))].map((currency) => {
    const entries = series
      .map((item, index) => ({ item, index }))
      .filter((entry) => entry.item.currency === currency);
    const names = entries.map(({ item }) => trendLabel(item));
    return {
      currency,
      key: JSON.stringify(entries.map(({ item }) => trendIdentity(item))),
      categories: entries.map(({ item }, index) =>
        names.filter((name) => name === names[index]).length > 1
          ? `${names[index]} [${item.pathRevisionId ?? ""}/${item.categoryId ?? item.state}]`
          : names[index],
      ),
      buckets: (data?.buckets ?? []).map((bucket) => ({
        period: bucket.period,
        amounts: entries.map(({ index }) => Number(bucket.amounts[index] ?? 0)),
      })),
      total: data?.currencyTotals?.find((total) => total.currency === currency)
        ?.amount,
    };
  });
}

/** Materialize one returned result and its selection before asynchronous download starts. */
export function categoryTrendsExport(
  data: CategoryTrendsResult,
  params: CategoryTrendsParams,
  generatedAt = new Date().toISOString(),
) {
  const headers = [
    "Generated at",
    "View",
    "Rollup",
    "Knowledge cutoff",
    "Assignment selection",
    "Captured current revision",
    "Start date",
    "End date",
    "Granularity",
    "Top N per currency",
    "Category filter",
    "Envelope filter",
    "Occasion filter",
    "Period",
    "Currency",
    "Currency total",
    "Path revision",
    "Category ID",
    "Path node IDs",
    "Path",
    "Archived path nodes",
    "State",
    "Other",
    "Series purchase count",
    "Amount",
  ];
  const rows: CsvValue[][] = data.buckets.flatMap((bucket) =>
    (data.series ?? []).map((item, index) => [
      generatedAt,
      data.view,
      data.rollup,
      data.knowledgeCutoff,
      data.assignmentSelection,
      data.currentRevisionId,
      params.startDate,
      params.endDate,
      params.granularity,
      params.topN,
      params.categoryId,
      params.envelopeId,
      params.occasionId,
      bucket.period,
      item.currency,
      data.currencyTotals?.find((total) => total.currency === item.currency)
        ?.amount,
      item.pathRevisionId,
      item.categoryId,
      item.path.map((node) => node.categoryId).join(" / "),
      trendLabel(item),
      item.path
        .filter((node) => node.archived)
        .map((node) => node.categoryId)
        .join(" / "),
      item.state,
      item.isOther,
      item.purchaseCount,
      bucket.amounts[index],
    ]),
  );
  return { headers, rows };
}
