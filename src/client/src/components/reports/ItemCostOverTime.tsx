import { useState, useCallback, useMemo } from "react";
import { ChartCard, AreaTimeChart } from "@/components/charts";
import { CategoryPathPicker } from "@/components/categories/CategoryPathPicker";
import { useCategoryTaxonomy } from "@/hooks/useCategoryTaxonomy";
import { categoryPaths } from "@/lib/category-paths";
import {
  useItemDescriptions,
  useItemCostOverTime,
} from "@/hooks/useItemCostOverTime";
import type { DateRange } from "@/hooks/useDashboard";
import { useReportSearchParams } from "@/hooks/useReportSearchParams";
import { DateRangeSelector } from "@/components/dashboard/DateRangeSelector";
import { Button } from "@/components/ui/button";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import {
  Command,
  CommandInput,
  CommandList,
  CommandEmpty,
  CommandGroup,
  CommandItem,
} from "@/components/ui/command";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { computeRollingAverage } from "@/lib/rolling-average";
import {
  parseBoolParam,
  parseCategoryReportParams,
  parseDateRangeParam,
  parseEnumParam,
  parseNormalizedDescriptionParam,
  parseSelectedItemParam,
  serializeDateRangeParam,
  serializeSelectedItemParam,
  type SelectedItemUrlValue,
} from "@/lib/report-params";
import { ChevronsUpDown } from "lucide-react";

type Granularity = "exact" | "monthly" | "yearly";
type WindowSize = "3" | "6" | "12";

const GRANULARITIES = ["exact", "monthly", "yearly"] as const;
const WINDOW_SIZES = ["3", "6", "12"] as const;

const granularityOptions: { value: Granularity; label: string }[] = [
  { value: "exact", label: "Each purchase" },
  { value: "monthly", label: "Monthly" },
  { value: "yearly", label: "Yearly" },
];

const windowSizeOptions: { value: WindowSize; label: string }[] = [
  { value: "3", label: "3-period" },
  { value: "6", label: "6-period" },
  { value: "12", label: "12-period" },
];

function formatCurrency(value: number, currency: string): string {
  try {
    return new Intl.NumberFormat("en-US", {
      style: "currency",
      currency,
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
    }).format(value);
  } catch {
    return `${value.toFixed(2)} ${currency}`;
  }
}

function localDateTime(iso?: string): string {
  if (!iso) return "";
  const date = new Date(iso);
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

type SelectedItem = SelectedItemUrlValue;

interface ItemCostOverTimeUrlParams {
  selectedItem: SelectedItem | null;
  /**
   * Canonical name drilled in from Spending by Normalized Description
   * (RECEIPTS-841). Picking an item or category path clears it.
   */
  normalizedDescription: string | null;
  categoryId?: string;
  categoryView: "asClassified" | "currentHierarchy";
  knowledgeCutoff?: string;
  capturedCurrentRevisionId?: string;
  /** Existing category-only links remain readable until migrated. */
  legacyCategory: string | null;
  dateRange: DateRange;
  granularity: Granularity;
  showTrendline: boolean;
  windowSize: WindowSize;
}

function parseItemCostOverTimeParams(
  searchParams: URLSearchParams,
): ItemCostOverTimeUrlParams {
  const { selectedItem, categoryOnly } = parseSelectedItemParam(searchParams);
  const categoryParams = parseCategoryReportParams(searchParams);
  return {
    selectedItem,
    normalizedDescription: parseNormalizedDescriptionParam(searchParams),
    categoryId: categoryParams.categoryId,
    categoryView: categoryParams.categoryView,
    knowledgeCutoff: categoryParams.knowledgeCutoff,
    capturedCurrentRevisionId: categoryParams.capturedCurrentRevisionId,
    legacyCategory: categoryOnly ? (selectedItem?.category ?? null) : null,
    dateRange: parseDateRangeParam(searchParams),
    granularity: parseEnumParam(
      searchParams.get("granularity"),
      GRANULARITIES,
      "exact",
    ),
    showTrendline: parseBoolParam(searchParams.get("trendline"), false),
    windowSize: parseEnumParam(
      searchParams.get("windowSize"),
      WINDOW_SIZES,
      "3",
    ),
  };
}

export default function ItemCostOverTime() {
  const [open, setOpen] = useState(false);
  const [searchInput, setSearchInput] = useState("");
  const [urlParams, updateParams] = useReportSearchParams(
    parseItemCostOverTimeParams,
  );
  const {
    selectedItem,
    normalizedDescription,
    categoryId,
    categoryView,
    knowledgeCutoff,
    capturedCurrentRevisionId,
    legacyCategory,
    dateRange,
    granularity,
    showTrendline,
    windowSize,
  } = urlParams;
  const taxonomy = useCategoryTaxonomy();
  const selectedPath = useMemo(
    () =>
      categoryPaths(taxonomy.data?.nodes ?? []).find(
        (row) => row.node.id === categoryId,
      ),
    [taxonomy.data?.nodes, categoryId],
  );
  const selectedCategory =
    categoryId && taxonomy.data && selectedPath
      ? {
          categoryId,
          taxonomyRevisionId: taxonomy.data.id,
          path: selectedPath.path,
        }
      : undefined;

  const debouncedSearch = useDebouncedValue(searchInput, 300);

  const { data: descriptionsData, isLoading: isSearching } =
    useItemDescriptions({
      search: debouncedSearch,
    });
  const itemChoices = useMemo(
    () =>
      (descriptionsData?.items ?? []).filter(
        (item, index, items) =>
          items.findIndex(
            (candidate) => candidate.description === item.description,
          ) === index,
      ),
    [descriptionsData?.items],
  );

  // A normalized-description drill-down replaces the item/category selection
  // entirely: it spans every raw description linked to that canonical name, so
  // sending `description` alongside it would narrow the series back down.
  const costParams = useMemo(
    () => ({
      ...(normalizedDescription
        ? { normalizedDescription }
        : categoryId
          ? { categoryId }
          : legacyCategory
            ? { category: legacyCategory }
            : { description: selectedItem?.description }),
      categoryView,
      knowledgeCutoff,
      capturedCurrentRevisionId,
      startDate: dateRange.startDate,
      endDate: dateRange.endDate,
      granularity,
    }),
    [
      normalizedDescription,
      categoryId,
      categoryView,
      knowledgeCutoff,
      capturedCurrentRevisionId,
      legacyCategory,
      selectedItem,
      dateRange,
      granularity,
    ],
  );

  const { data: costData, isLoading: isCostLoading } =
    useItemCostOverTime(costParams);

  const currencySeries = useMemo(() => {
    const groups = new Map<string, { period: string; amount: number }[]>();
    for (const bucket of costData?.buckets ?? []) {
      const currency = bucket.currency;
      const series = groups.get(currency) ?? [];
      series.push({
        period: bucket.period,
        amount: Number(bucket.amount ?? 0),
      });
      groups.set(currency, series);
    }
    return [...groups.entries()].sort(([a], [b]) => a.localeCompare(b));
  }, [costData?.buckets]);

  const handleSelect = useCallback(
    (description: string, category: string) => {
      updateParams({
        ...serializeSelectedItemParam({ description, category }, false),
        // A fresh pick supersedes any normalized-description drill-down.
        normalized: undefined,
        categoryId: undefined,
      });
      setOpen(false);
      setSearchInput("");
    },
    [updateParams],
  );

  const handleGranularity = useCallback(
    (g: Granularity) => {
      updateParams({ granularity: g });
    },
    [updateParams],
  );

  const handleToggleTrendline = useCallback(() => {
    updateParams({ trendline: showTrendline ? undefined : "true" });
  }, [showTrendline, updateParams]);

  const handleWindowSizeChange = useCallback(
    (value: string) => {
      updateParams({ windowSize: value });
    },
    [updateParams],
  );

  const handleDateRangeChange = useCallback(
    (range: DateRange) => {
      updateParams(serializeDateRangeParam(range));
    },
    [updateParams],
  );

  const displayLabel = normalizedDescription
    ? normalizedDescription
    : legacyCategory
      ? "Search items..."
      : selectedItem
        ? selectedItem.description
        : "Search items...";

  const hasSelection = Boolean(
    normalizedDescription || categoryId || legacyCategory || selectedItem,
  );

  const chartTitle = normalizedDescription
    ? `Normalized: ${normalizedDescription}`
    : categoryId
      ? `Category: ${selectedPath?.label ?? categoryId}`
      : legacyCategory
        ? `Category: ${legacyCategory}`
        : (selectedItem?.description ?? "");

  const chartSubtitle = normalizedDescription
    ? "Unit price over time across every item with this normalized description"
    : categoryId || legacyCategory
      ? "Average unit price over time"
      : "Unit price over time";
  const displaySeries: Array<[string, { period: string; amount: number }[]]> =
    currencySeries.length ? currencySeries : [["", []]];

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <div className="flex items-center gap-2">
          <Popover open={open} onOpenChange={setOpen}>
            <PopoverTrigger asChild>
              <Button
                variant="outline"
                role="combobox"
                aria-label="Search items"
                aria-expanded={open}
                className="w-[300px] justify-between"
              >
                <span className="truncate">{displayLabel}</span>
                <ChevronsUpDown className="ml-2 h-4 w-4 shrink-0 opacity-50" />
              </Button>
            </PopoverTrigger>
            <PopoverContent className="w-[300px] p-0">
              <Command shouldFilter={false}>
                <CommandInput
                  placeholder="Type to search items..."
                  value={searchInput}
                  onValueChange={setSearchInput}
                />
                <CommandList>
                  {debouncedSearch.length < 2 ? (
                    <CommandEmpty>Type at least 2 characters</CommandEmpty>
                  ) : isSearching ? (
                    <CommandEmpty>Searching...</CommandEmpty>
                  ) : !itemChoices.length ? (
                    <CommandEmpty>No results found</CommandEmpty>
                  ) : (
                    <CommandGroup>
                      {itemChoices.map((item) => (
                        <CommandItem
                          key={`${item.description}-${item.category}`}
                          value={`${item.description}-${item.category}`}
                          onSelect={() =>
                            handleSelect(item.description, item.category)
                          }
                        >
                          <div className="flex w-full items-center justify-between">
                            <div className="flex flex-col">
                              <span className="text-sm">
                                {item.description}
                              </span>
                            </div>
                            <span className="text-xs text-muted-foreground">
                              {item.occurrences}x
                            </span>
                          </div>
                        </CommandItem>
                      ))}
                    </CommandGroup>
                  )}
                </CommandList>
              </Command>
            </PopoverContent>
          </Popover>
        </div>

        <div className="min-w-[220px] max-w-[320px] flex-1">
          <CategoryPathPicker
            allowUnclassified={false}
            aria-label="Category path"
            revision={taxonomy.data}
            loading={taxonomy.isLoading}
            error={taxonomy.isError}
            value={selectedCategory}
            onValueChange={(choice) =>
              updateParams({
                categoryId: choice.categoryId,
                item: undefined,
                category: undefined,
                categoryOnly: undefined,
                normalized: undefined,
              })
            }
          />
        </div>
        {(categoryId || legacyCategory) && (
          <Button
            variant="ghost"
            size="sm"
            onClick={() =>
              updateParams({
                categoryId: undefined,
                item: undefined,
                category: undefined,
                categoryOnly: undefined,
              })
            }
          >
            Clear category
          </Button>
        )}

        <DateRangeSelector value={dateRange} onChange={handleDateRangeChange} />
      </div>

      <div className="flex flex-wrap items-center gap-3 text-sm">
        <div className="flex items-center gap-2">
          Category view
          <Select
            value={categoryView}
            onValueChange={(value) =>
              updateParams({
                categoryView: value,
                capturedCurrentRevisionId: undefined,
              })
            }
          >
            <SelectTrigger aria-label="Category view" className="w-[190px]">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="asClassified">As classified</SelectItem>
              <SelectItem value="currentHierarchy">
                Current hierarchy
              </SelectItem>
            </SelectContent>
          </Select>
        </div>
        <label className="flex items-center gap-2">
          Known by
          <input
            aria-label="Known by"
            type="datetime-local"
            className="h-9 rounded-md border bg-background px-2"
            value={localDateTime(knowledgeCutoff)}
            onChange={(event) =>
              updateParams({
                knowledgeCutoff: event.target.value
                  ? new Date(event.target.value).toISOString()
                  : undefined,
              })
            }
          />
        </label>
      </div>

      {hasSelection &&
        displaySeries.map(([currency, chartData]) => (
          <ChartCard
            key={currency}
            title={chartTitle}
            subtitle={`${chartSubtitle}${currency ? ` · ${currency}` : ""}`}
            loading={isCostLoading}
            empty={chartData.length === 0 && !isCostLoading}
            emptyMessage="No purchase history found for the selected item"
            action={
              <div className="flex flex-wrap items-center gap-2">
                <div className="flex flex-wrap gap-1">
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
                <div className="flex items-center gap-1.5">
                  <Button
                    variant={showTrendline ? "default" : "outline"}
                    size="sm"
                    onClick={handleToggleTrendline}
                    aria-pressed={showTrendline}
                  >
                    Trendline
                  </Button>
                  {showTrendline && (
                    <Select
                      value={windowSize}
                      onValueChange={handleWindowSizeChange}
                    >
                      <SelectTrigger
                        size="sm"
                        aria-label="Rolling average window size"
                      >
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        {windowSizeOptions.map(({ value, label }) => (
                          <SelectItem key={value} value={value}>
                            {label}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  )}
                </div>
              </div>
            }
          >
            <AreaTimeChart
              data={chartData}
              trendlineData={
                showTrendline
                  ? computeRollingAverage(chartData, Number(windowSize))
                  : undefined
              }
              formatValue={(amount) => formatCurrency(amount, currency)}
              aria-label={`${chartTitle} · ${currency}`}
            />
          </ChartCard>
        ))}

      {!hasSelection && (
        <div className="rounded-lg border p-6 text-center">
          <h2 className="card-title">Item Cost Over Time</h2>
          <p className="mt-2 text-muted-foreground">
            Choose an item or category above to see its price history
          </p>
        </div>
      )}
    </div>
  );
}
