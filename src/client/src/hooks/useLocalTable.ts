import { useCallback, useMemo, useState } from "react";
import { usePersistedTablePageSize } from "@/hooks/usePersistedTablePageSize";
import { isPageSize } from "@/lib/page-size";

interface LocalTableOptions<T> {
  tableId: string;
  /** Must be the complete collection, never one server page. */
  rows: readonly T[];
  filter?: (row: T) => boolean;
  compare?: (left: T, right: T) => number;
  /** A stable value identifying the current filters and sort. */
  resetKey: string;
  defaultPageSize?: number;
}

/** Complete cached data only: project once, then slice without mutating source rows. */
export function useLocalTable<T>({
  tableId,
  rows,
  filter,
  compare,
  resetKey,
  defaultPageSize = 25,
}: LocalTableOptions<T>) {
  const { pageSize, setPageSize: persistSize } = usePersistedTablePageSize(
    tableId,
    defaultPageSize,
  );
  const [pagination, setPagination] = useState({ key: resetKey, page: 1 });
  // Reset the stored page as well as the rendered page. Returning to a previous
  // filter must not resurrect its old offset. This adjusts only this component.
  if (pagination.key !== resetKey) {
    setPagination({ key: resetKey, page: 1 });
  }
  const projected = useMemo(() => {
    const result = filter ? rows.filter(filter) : [...rows];
    if (compare) result.sort(compare); // ES stable sort preserves source order for equal values.
    return result;
  }, [rows, filter, compare]);
  const total = projected.length;
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  const currentPage =
    pagination.key === resetKey ? Math.min(pagination.page, totalPages) : 1;
  const items = useMemo(
    () => projected.slice((currentPage - 1) * pageSize, currentPage * pageSize),
    [projected, currentPage, pageSize],
  );
  const setPage = useCallback(
    (page: number) => {
      if (!Number.isInteger(page)) return;
      setPagination({
        key: resetKey,
        page: Math.max(1, Math.min(page, totalPages)),
      });
    },
    [resetKey, totalPages],
  );
  const setPageSize = useCallback(
    (size: number) => {
      if (!isPageSize(size)) return;
      persistSize(size);
      setPagination({ key: resetKey, page: 1 });
    },
    [persistSize, resetKey],
  );
  return {
    items,
    total,
    totalPages,
    currentPage,
    pageSize,
    setPage,
    setPageSize,
  };
}
