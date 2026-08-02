import { format } from "date-fns";

/** A single CSV cell value. Null and undefined render as empty fields. */
export type CsvValue = string | number | boolean | null | undefined;

/** UTF-8 byte-order mark so Excel detects the encoding correctly. */
export const CSV_BOM = "\uFEFF";

/**
 * Leading characters that spreadsheet apps (Excel, Sheets) interpret as the
 * start of a formula. Untrusted text fields (locations, descriptions, etc.)
 * are neutralized to prevent CSV/formula injection (CWE-1236, OWASP).
 */
const FORMULA_TRIGGER_PATTERN = /^[=+\-@\t\r]/;

function escapeField(value: CsvValue): string {
  if (value === null || value === undefined) return "";
  let text = String(value);
  // Only string-typed values come from free-text user input; numbers (e.g.
  // negative totals) are never at risk and must render without a prefix.
  if (typeof value === "string" && FORMULA_TRIGGER_PATTERN.test(text)) {
    text = `'${text}`;
  }
  if (/[",\r\n]/.test(text)) {
    return `"${text.replaceAll('"', '""')}"`;
  }
  return text;
}

/**
 * Builds an RFC 4180 CSV string from a header row and data rows.
 * Fields containing commas, quotes, or line breaks are quoted; embedded
 * quotes are doubled. Rows are joined with CRLF and the output ends with
 * a trailing CRLF.
 */
export function toCsv(headers: string[], rows: CsvValue[][]): string {
  const lines = [headers, ...rows].map((row) =>
    row.map(escapeField).join(","),
  );
  return `${lines.join("\r\n")}\r\n`;
}

/**
 * Builds a CSV filename from a report slug and an optional date range,
 * e.g. `spending-by-location_2026-07-01_2026-08-01.csv`. When the report
 * has no date filter, the current date is used instead of a range.
 */
export function csvFilename(
  slug: string,
  range?: { startDate?: string; endDate?: string },
): string {
  if (range?.startDate && range?.endDate) {
    return `${slug}_${range.startDate}_${range.endDate}.csv`;
  }
  return `${slug}_${format(new Date(), "yyyy-MM-dd")}.csv`;
}

/**
 * Triggers a browser download of the given CSV content, prefixed with a
 * UTF-8 BOM so Excel opens it with the right encoding.
 */
export function downloadCsv(filename: string, csv: string): void {
  const blob = new Blob([CSV_BOM + csv], { type: "text/csv;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = filename;
  document.body.appendChild(anchor);
  anchor.click();
  document.body.removeChild(anchor);
  URL.revokeObjectURL(url);
}
