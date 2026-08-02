import { useMemo } from "react";
import { useMutation } from "@tanstack/react-query";
import { downloadCsv, toCsv, type CsvValue } from "@/lib/export-csv";
import { showError } from "@/lib/toast";

export interface CsvExportRequest {
  /** Download filename, e.g. from {@link csvFilename}. */
  filename: string;
  /** Header row for the CSV. */
  headers: string[];
  /**
   * Data rows, or an async producer for them (used by paginated reports
   * that must fetch the full dataset before exporting).
   */
  rows: CsvValue[][] | (() => Promise<CsvValue[][]>);
}

/**
 * Shared CSV export action for report components. Resolves the rows,
 * builds the CSV, and triggers a browser download. Shows an error toast
 * if fetching or building fails.
 */
export function useCsvExport() {
  const mutation = useMutation({
    mutationFn: async ({ filename, headers, rows }: CsvExportRequest) => {
      const resolvedRows = typeof rows === "function" ? await rows() : rows;
      downloadCsv(filename, toCsv(headers, resolvedRows));
    },
    onError: () => {
      showError("Failed to export CSV.");
    },
  });

  return useMemo(
    () => ({
      exportCsv: mutation.mutate,
      isExporting: mutation.isPending,
    }),
    [mutation.mutate, mutation.isPending],
  );
}
