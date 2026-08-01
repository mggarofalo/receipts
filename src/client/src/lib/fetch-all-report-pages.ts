/** Page size used when fetching report data for export (API maximum). */
export const EXPORT_PAGE_SIZE = 100;

/** Hard cap on exported rows to keep exports bounded. */
export const MAX_EXPORT_ROWS = 10_000;

export interface ReportPage<TItem> {
  items: TItem[];
  totalCount: number;
}

/**
 * Fetches every page of a paginated report endpoint and returns the
 * concatenated items, capped at {@link MAX_EXPORT_ROWS}. Stops early if a
 * page comes back empty (defends against a stale totalCount).
 */
export async function fetchAllReportPages<TItem>(
  fetchPage: (page: number, pageSize: number) => Promise<ReportPage<TItem>>,
): Promise<TItem[]> {
  const all: TItem[] = [];
  let page = 1;
  for (;;) {
    const { items, totalCount } = await fetchPage(page, EXPORT_PAGE_SIZE);
    all.push(...items);
    const target = Math.min(totalCount, MAX_EXPORT_ROWS);
    if (items.length === 0 || all.length >= target) break;
    page += 1;
  }
  return all.length > MAX_EXPORT_ROWS ? all.slice(0, MAX_EXPORT_ROWS) : all;
}
