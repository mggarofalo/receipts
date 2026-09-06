import { useMemo } from "react";
import { locationHistory } from "@/lib/location-history";
import { useFieldHistory } from "./useFieldHistory";
import { useLocationSuggestions } from "./useReceipts";
import type { ComboboxOption } from "@/components/ui/combobox";

export function useLocationHistory() {
  const { entries: localEntries, options: _localOptions, add, clear } =
    useFieldHistory(locationHistory);

  const { data: apiLocations, isError, isFetching, refetch } = useLocationSuggestions("");

  const options: ComboboxOption[] = useMemo(() => {
    const seen = new Set<string>();
    const result: ComboboxOption[] = [];

    // Local MRU entries first (user's personal recent locations)
    for (const entry of localEntries) {
      const key = entry.toLowerCase();
      if (!seen.has(key)) {
        seen.add(key);
        result.push({ value: entry, label: entry });
      }
    }

    // API results (sorted by frequency) fill in the rest
    if (apiLocations) {
      for (const location of apiLocations) {
        const key = location.toLowerCase();
        if (!seen.has(key)) {
          seen.add(key);
          result.push({ value: location, label: location });
        }
      }
    }

    return result;
  }, [localEntries, apiLocations]);

  return useMemo(
    () => ({ locations: localEntries, options, add, clear, isError, isFetching, refetch }),
    [localEntries, options, add, clear, isError, isFetching, refetch],
  );
}
