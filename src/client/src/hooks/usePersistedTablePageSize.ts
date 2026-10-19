import { useCallback, useState } from "react";
import {
  getPersistedPageSize,
  isPageSize,
  persistPageSize,
} from "@/lib/page-size";

/** The table ID is a stable identity, not a search phrase or a page number. */
export function usePersistedTablePageSize(
  tableId: string,
  defaultPageSize = 25,
) {
  const [pageSize, setSize] = useState(
    () => getPersistedPageSize(tableId) ?? defaultPageSize,
  );
  const setPageSize = useCallback(
    (size: number) => {
      if (!isPageSize(size)) return;
      persistPageSize(tableId, size);
      setSize(size);
    },
    [tableId],
  );
  return { pageSize, setPageSize };
}
