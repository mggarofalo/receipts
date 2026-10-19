const LEGACY_PAGE_SIZE_KEY = "table-page-size";
export const PAGE_SIZE_OPTIONS = [10, 25, 50, 100];

export function isPageSize(size: number): boolean {
  return PAGE_SIZE_OPTIONS.includes(size);
}

export function getPersistedPageSize(tableId: string): number | null {
  try {
    const raw =
      localStorage.getItem(`table-page-size:${tableId}`) ??
      localStorage.getItem(LEGACY_PAGE_SIZE_KEY);
    if (raw === null) return null;
    const parsed = Number(raw);
    return isPageSize(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

export function persistPageSize(tableId: string, size: number): void {
  if (!isPageSize(size)) return;
  try {
    localStorage.setItem(`table-page-size:${tableId}`, String(size));
  } catch {
    // Ignore storage errors (full, disabled, etc.)
  }
}
