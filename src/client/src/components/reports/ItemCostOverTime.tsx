import { useState, useCallback, useMemo } from "react";
import { ChartCard, AreaTimeChart } from "@/components/charts";
import {
  useItemDescriptions,
  useItemCostOverTime,
} from "@/hooks/useItemCostOverTime";
import type { DateRange } from "@/hooks/useDashboard";
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
import { ChevronsUpDown } from "lucide-react";

type Granularity = "exact" | "monthly" | "yearly";
type WindowSize = "3" | "6" | "12";

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

function formatCurrency(value: number): string {
  return new Intl.NumberFormat("en-US", {
    style: "currency",
    currency: "USD",
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(value);
}

interface SelectedItem {
  description: string;
  category: string;
}

export default function ItemCostOverTime() {
  const [open, setOpen] = useState(false);
  const [searchInput, setSearchInput] = useState("");
  const [categoryOnly, setCategoryOnly] = useState(false);
  const [selectedItem, setSelectedItem] = useState<SelectedItem | null>(null);
  const [dateRange, setDateRange] = useState<DateRange>({
    startDate: undefined,
    endDate: undefined,
  });
  const [granularity, setGranularity] = useState<Granularity>("exact");
  const [showTrendline, setShowTrendline] = useState(false);
  const [windowSize, setWindowSize] = useState<WindowSize>("3");

  const debouncedSearch = useDebouncedValue(searchInput, 300);

  const { data: descriptionsData, isLoading: isSearching } =
    useItemDescriptions({
      search: debouncedSearch,
      categoryOnly,
    });

  const costParams = useMemo(
    () => ({
      description: categoryOnly ? undefined : selectedItem?.description,
      category: categoryOnly
        ? selectedItem?.category
        : undefined,
      startDate: dateRange.startDate,
      endDate: dateRange.endDate,
      granularity,
    }),
    [categoryOnly, selectedItem, dateRange, granularity],
  );

  const { data: costData, isLoading: isCostLoading } =
    useItemCostOverTime(costParams);

  const chartData = useMemo(
    () =>
      (costData?.buckets ?? []).map((b) => ({
        period: b.period,
        amount: Number(b.amount ?? 0),
      })),
    [costData?.buckets],
  );

  const trendlineData = useMemo(
    () =>
      showTrendline
        ? computeRollingAverage(chartData, Number(windowSize))
        : undefined,
    [showTrendline, chartData, windowSize],
  );

  const handleSelect = useCallback(
    (description: string, category: string) => {
      setSelectedItem({ description, category });
      setOpen(false);
      setSearchInput("");
    },
    [],
  );

  const handleCategoryToggle = useCallback(() => {
    setCategoryOnly((prev) => !prev);
    setSelectedItem(null);
    setSearchInput("");
  }, []);

  const handleGranularity = useCallback((g: Granularity) => {
    setGranularity(g);
  }, []);

  const handleToggleTrendline = useCallback(() => {
    setShowTrendline((prev) => !prev);
  }, []);

  const handleWindowSizeChange = useCallback((value: string) => {
    setWindowSize(value as WindowSize);
  }, []);

  const handleDateRangeChange = useCallback((range: DateRange) => {
    setDateRange(range);
  }, []);

  const displayLabel = selectedItem
    ? categoryOnly
      ? selectedItem.category
      : selectedItem.description
    : categoryOnly
      ? "Search categories..."
      : "Search items...";

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <div className="flex items-center gap-2">
          <Popover open={open} onOpenChange={setOpen}>
            <PopoverTrigger asChild>
              <Button
                variant="outline"
                role="combobox"
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
                  placeholder={
                    categoryOnly
                      ? "Type to search categories..."
                      : "Type to search items..."
                  }
                  value={searchInput}
                  onValueChange={setSearchInput}
                />
                <CommandList>
                  {debouncedSearch.length < 2 ? (
                    <CommandEmpty>Type at least 2 characters</CommandEmpty>
                  ) : isSearching ? (
                    <CommandEmpty>Searching...</CommandEmpty>
                  ) : !descriptionsData?.items?.length ? (
                    <CommandEmpty>No results found</CommandEmpty>
                  ) : (
                    <CommandGroup>
                      {descriptionsData.items.map((item) => (
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
                              {!categoryOnly && (
                                <span className="text-xs text-muted-foreground">
                                  {item.category}
                                </span>
                              )}
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

          <Button
            variant={categoryOnly ? "default" : "outline"}
            size="sm"
            onClick={handleCategoryToggle}
            aria-pressed={categoryOnly}
          >
            Category
          </Button>
        </div>

        <DateRangeSelector value={dateRange} onChange={handleDateRangeChange} />
      </div>

      {selectedItem && (
        <ChartCard
          title={
            categoryOnly
              ? `Category: ${selectedItem.category}`
              : selectedItem.description
          }
          subtitle={
            categoryOnly
              ? "Average unit price over time"
              : `${selectedItem.category} — Unit price over time`
          }
          loading={isCostLoading}
          empty={chartData.length === 0 && !isCostLoading}
          emptyMessage="No purchase history found for the selected item"
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
            trendlineData={trendlineData}
            formatValue={formatCurrency}
          />
        </ChartCard>
      )}

      {!selectedItem && (
        <div className="rounded-lg border p-6 text-center">
          <h2 className="card-title">Item Cost Over Time</h2>
          <p className="mt-2 text-muted-foreground">
            Search for an item above to see its price history
          </p>
        </div>
      )}
    </div>
  );
}
