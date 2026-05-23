import { useCallback, useEffect, useMemo, useState } from "react";
import type { FilterValues } from "@/components/FilterPanel";

const STORAGE_KEY_PREFIX = "receipts:filters:";

function storageKey(entityType: string): string {
  return `${STORAGE_KEY_PREFIX}${entityType}`;
}

function read(entityType: string): FilterValues {
  if (typeof window === "undefined") return {};
  try {
    const raw = window.localStorage.getItem(storageKey(entityType));
    if (!raw) return {};
    const parsed: unknown = JSON.parse(raw);
    // Light shape guard — must be a plain object.
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
      return {};
    }
    return parsed as FilterValues;
  } catch {
    return {};
  }
}

function write(entityType: string, values: FilterValues): void {
  if (typeof window === "undefined") return;
  try {
    // An empty filter set is the default — write the empty marker so reload
    // doesn't surface stale state from a previous session that has since
    // been cleared.
    const isEmpty = Object.keys(values).length === 0;
    if (isEmpty) {
      window.localStorage.removeItem(storageKey(entityType));
      return;
    }
    window.localStorage.setItem(storageKey(entityType), JSON.stringify(values));
  } catch {
    // localStorage may throw in privacy mode; failing closed is fine here.
  }
}

/**
 * Drop-in replacement for `useState<FilterValues>({})` that persists the
 * current filter set to localStorage on every change, keyed by entity type.
 *
 * Closes RECEIPTS-736: saved views persistence. Survives reloads in the
 * same browser; intentionally browser-scoped (not user-scoped) — multiple
 * users on the same device share state, which is acceptable for the
 * Receipts personal-device pattern.
 */
export function usePersistedFilters(
  entityType: string,
): [FilterValues, (next: FilterValues | ((prev: FilterValues) => FilterValues)) => void] {
  const [values, setValuesState] = useState<FilterValues>(() => read(entityType));

  // If the consumer changes entity types at runtime (unusual but possible
  // for shared list components), re-hydrate from storage. Behaves like a
  // re-init when the dependency changes.
  useEffect(() => {
    setValuesState(read(entityType));
  }, [entityType]);

  const setValues = useCallback(
    (next: FilterValues | ((prev: FilterValues) => FilterValues)) => {
      setValuesState((prev) => {
        const resolved =
          typeof next === "function"
            ? (next as (p: FilterValues) => FilterValues)(prev)
            : next;
        write(entityType, resolved);
        return resolved;
      });
    },
    [entityType],
  );

  // react-hook-stability requires stable tuple identity when the underlying
  // state hasn't changed.
  return useMemo(
    () => [values, setValues] as const,
    [values, setValues],
  );
}
