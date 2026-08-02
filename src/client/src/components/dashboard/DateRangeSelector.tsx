import { useCallback, useMemo } from "react";
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
import {
  presets,
  presetGroups,
  matchPreset,
  type PresetKey,
} from "./date-range-presets";

interface DateRangeSelectorProps {
  value: DateRange;
  onChange: (range: DateRange) => void;
}

export function DateRangeSelector({ value, onChange }: DateRangeSelectorProps) {
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

  const matched = useMemo(() => matchPreset(value), [value]);
  const activePreset = matched.preset;
  const displayedYear = matched.year ?? new Date().getFullYear();

  const handlePreset = useCallback(
    (key: PresetKey) => {
      onChange(
        key === "year"
          ? presets.year.getRange(displayedYear)
          : presets[key].getRange(),
      );
    },
    [onChange, displayedYear],
  );

  const handleYearChange = useCallback(
    (yearStr: string) => {
      onChange(presets.year.getRange(Number(yearStr)));
    },
    [onChange],
  );

  const displayLabel = useMemo(() => {
    if (activePreset === "year") {
      return String(displayedYear);
    }
    if (activePreset) {
      return presets[activePreset].label;
    }
    if (value.startDate && value.endDate) {
      return `${value.startDate} - ${value.endDate}`;
    }
    return "Select range";
  }, [activePreset, displayedYear, value.startDate, value.endDate]);

  const handleSelectChange = useCallback(
    (val: string) => {
      handlePreset(val as PresetKey);
    },
    [handlePreset],
  );

  // Radix's Select.Value falls back to its `placeholder` (ignoring the
  // explicit `displayLabel` children below) whenever the owning Select's
  // `value` is the empty string — it treats that as "nothing selected".
  // For a custom, non-matching range there's no real PresetKey to hand it,
  // so a non-empty sentinel that matches no rendered SelectItem is used
  // purely to keep Radix in "something is selected, show my children" mode.
  const narrowSelectValue = activePreset ?? "custom";

  return (
    <div className="flex items-center gap-2">
      {/* Dropdown for narrow screens */}
      <div className="sm:hidden">
        <Select value={narrowSelectValue} onValueChange={handleSelectChange}>
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
        value={activePreset === "year" ? String(displayedYear) : ""}
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
