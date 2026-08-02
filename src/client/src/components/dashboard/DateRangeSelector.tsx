import { useState, useCallback, useMemo } from "react";
import {
  format,
  subMonths,
  startOfMonth,
  startOfQuarter,
  startOfYear,
} from "date-fns";
import { Button } from "@/components/ui/button";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import type { DateRange } from "@/hooks/useDashboard";
import { useDashboardEarliestReceiptYear } from "@/hooks/useDashboard";

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

interface Preset {
  label: string;
  getRange: (selectedYear?: number) => DateRange;
}

const presets: Record<PresetKey, Preset> = {
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

const presetGroups: { label: string; keys: PresetKey[] }[] = [
  { label: "Trailing", keys: ["1M", "3M", "12M", "60M"] },
  { label: "To Date", keys: ["MTD", "QTD", "YTD"] },
  { label: "", keys: ["all"] },
];

interface DateRangeSelectorProps {
  value: DateRange;
  onChange: (range: DateRange) => void;
  /**
   * Preset highlighted on first render. Defaults to "1M" to match the
   * dashboard's historical default range. Callers that seed `value` with a
   * different default (e.g. reports using the shared 12-month default from
   * `lib/report-params`) should pass the matching preset key so the
   * highlighted button doesn't lie about which range is actually applied.
   */
  initialPreset?: PresetKey;
}

export function DateRangeSelector({
  value,
  onChange,
  initialPreset = "1M",
}: DateRangeSelectorProps) {
  const [activePreset, setActivePreset] = useState<PresetKey>(initialPreset);
  const [selectedYear, setSelectedYear] = useState<number>(
    new Date().getFullYear(),
  );
  const { data: earliestYearData } = useDashboardEarliestReceiptYear();

  const availableYears = useMemo(() => {
    const earliest = Number(earliestYearData?.year ?? new Date().getFullYear());
    const current = new Date().getFullYear();
    const years: number[] = [];
    for (let y = current; y >= earliest; y--) {
      years.push(y);
    }
    return years;
  }, [earliestYearData?.year]);

  const handlePreset = useCallback(
    (key: PresetKey) => {
      setActivePreset(key);
      if (key === "year") {
        onChange(presets.year.getRange(selectedYear));
      } else {
        onChange(presets[key].getRange());
      }
    },
    [onChange, selectedYear],
  );

  const handleYearChange = useCallback(
    (yearStr: string) => {
      const year = Number(yearStr);
      setSelectedYear(year);
      setActivePreset("year");
      onChange(presets.year.getRange(year));
    },
    [onChange],
  );

  const displayLabel = useMemo(() => {
    if (activePreset === "year") {
      return String(selectedYear);
    }
    if (activePreset in presets) {
      return presets[activePreset].label;
    }
    if (value.startDate && value.endDate) {
      return `${value.startDate} - ${value.endDate}`;
    }
    return "Select range";
  }, [activePreset, selectedYear, value.startDate, value.endDate]);

  const handleSelectChange = useCallback(
    (val: string) => {
      handlePreset(val as PresetKey);
    },
    [handlePreset],
  );

  return (
    <div className="flex items-center gap-2">
      {/* Dropdown for narrow screens */}
      <div className="sm:hidden">
        <Select value={activePreset} onValueChange={handleSelectChange}>
          <SelectTrigger size="sm">
            <SelectValue>{displayLabel}</SelectValue>
          </SelectTrigger>
          <SelectContent>
            {presetGroups.map((group) =>
              group.keys.map((key) => (
                <SelectItem key={key} value={key}>
                  {group.label ? `${group.label}: ${presets[key].label}` : presets[key].label}
                </SelectItem>
              )),
            )}
          </SelectContent>
        </Select>
      </div>

      {/* Button row for wider screens — grouped with separators */}
      <div className="hidden sm:flex items-center gap-1">
        {presetGroups.map((group, i) => (
          <div key={group.label || "misc"} className="flex items-center gap-1">
            {i > 0 && (
              <div className="mx-1 h-5 w-px bg-border" aria-hidden="true" />
            )}
            {group.keys.map((key) => (
              <Button
                key={key}
                variant={activePreset === key ? "default" : "outline"}
                size="sm"
                onClick={() => handlePreset(key)}
              >
                {presets[key].label}
              </Button>
            ))}
          </div>
        ))}
      </div>

      {/* Year dropdown */}
      <Select
        value={activePreset === "year" ? String(selectedYear) : ""}
        onValueChange={handleYearChange}
      >
        <SelectTrigger
          size="sm"
          className="w-[90px]"
          data-testid="year-dropdown"
        >
          <SelectValue placeholder="Year" />
        </SelectTrigger>
        <SelectContent>
          {availableYears.map((year) => (
            <SelectItem key={year} value={String(year)}>
              {year}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </div>
  );
}
