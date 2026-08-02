import { useCallback, useMemo } from "react";
import { useSearchParams } from "react-router";

export type ReportParamPatch = Record<
  string,
  string | number | boolean | null | undefined
>;

/**
 * Shared hook that persists report filter state (date range, sort, page,
 * granularity, etc.) to the URL query string (RECEIPTS-840).
 *
 * Report components supply a pure `parse` function mapping raw
 * `URLSearchParams` to a typed, always-valid values object — missing or
 * malformed params must fall back to sane defaults inside `parse`, never
 * throw. Define `parse` as a module-level constant (not inline in the
 * component) so its reference is stable across renders.
 *
 * `update(patch)` merges a partial patch into the existing search params
 * (preserving unrelated keys, e.g. the `report` slug) and navigates with
 * `{ replace: true }` so filter tweaks don't spam browser history — only
 * the report picker's own navigation and full page loads create new
 * entries. Pass `null`/`undefined` for a key to remove it (reverting that
 * field to whatever `parse` treats as its default).
 */
export function useReportSearchParams<T>(
  parse: (params: URLSearchParams) => T,
): readonly [T, (patch: ReportParamPatch) => void] {
  const [searchParams, setSearchParams] = useSearchParams();

  const values = useMemo(() => parse(searchParams), [searchParams, parse]);

  const update = useCallback(
    (patch: ReportParamPatch) => {
      setSearchParams(
        (prev) => {
          const next = new URLSearchParams(prev);
          for (const [key, value] of Object.entries(patch)) {
            if (value === null || value === undefined || value === "") {
              next.delete(key);
            } else {
              next.set(key, String(value));
            }
          }
          return next;
        },
        { replace: true },
      );
    },
    [setSearchParams],
  );

  return useMemo(() => [values, update] as const, [values, update]);
}
