import { act, renderHook, waitFor } from "@testing-library/react";
import { createQueryWrapper } from "@/test/test-utils";
import { useCsvExport } from "./useCsvExport";
import { clearTokens, setTokens } from "@/lib/auth";

vi.mock("@/lib/export-csv", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/export-csv")>();
  return { ...actual, downloadCsv: vi.fn() };
});

vi.mock("@/lib/toast", () => ({
  showSuccess: vi.fn(),
  showError: vi.fn(),
}));

import { downloadCsv } from "@/lib/export-csv";
import { showError } from "@/lib/toast";

const mockDownloadCsv = vi.mocked(downloadCsv);
const mockShowError = vi.mocked(showError);

describe("useCsvExport", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    setTokens("Alice-access", "Alice-refresh");
  });

  afterEach(() => clearTokens());

  it("builds and downloads a csv from static rows", async () => {
    const { result } = renderHook(() => useCsvExport(), {
      wrapper: createQueryWrapper(),
    });

    result.current.exportCsv({
      filename: "report.csv",
      headers: ["Name", "Total"],
      rows: [
        ["Store A", 10.5],
        ["Store B", 20],
      ],
    });

    await waitFor(() => expect(mockDownloadCsv).toHaveBeenCalledTimes(1));
    expect(mockDownloadCsv).toHaveBeenCalledWith(
      "report.csv",
      "Name,Total\r\nStore A,10.5\r\nStore B,20\r\n",
    );
    expect(mockShowError).not.toHaveBeenCalled();
  });

  it("resolves an async row producer before downloading", async () => {
    const { result } = renderHook(() => useCsvExport(), {
      wrapper: createQueryWrapper(),
    });

    result.current.exportCsv({
      filename: "async.csv",
      headers: ["Value"],
      rows: async () => [["a"], ["b"]],
    });

    await waitFor(() => expect(mockDownloadCsv).toHaveBeenCalledTimes(1));
    expect(mockDownloadCsv).toHaveBeenCalledWith(
      "async.csv",
      "Value\r\na\r\nb\r\n",
    );
  });

  it("shows an error toast and skips download when the row producer fails", async () => {
    const { result } = renderHook(() => useCsvExport(), {
      wrapper: createQueryWrapper(),
    });

    result.current.exportCsv({
      filename: "fail.csv",
      headers: ["Value"],
      rows: async () => {
        throw new Error("fetch failed");
      },
    });

    await waitFor(() =>
      expect(mockShowError).toHaveBeenCalledWith("Failed to export CSV."),
    );
    expect(mockDownloadCsv).not.toHaveBeenCalled();
  });

  it("reports isExporting while the export is in flight", async () => {
    let resolveRows: (rows: string[][]) => void = () => {};
    const pending = new Promise<string[][]>((resolve) => {
      resolveRows = resolve;
    });

    const { result } = renderHook(() => useCsvExport(), {
      wrapper: createQueryWrapper(),
    });

    expect(result.current.isExporting).toBe(false);

    result.current.exportCsv({
      filename: "slow.csv",
      headers: ["Value"],
      rows: () => pending,
    });

    await waitFor(() => expect(result.current.isExporting).toBe(true));

    resolveRows([["done"]]);

    await waitFor(() => expect(result.current.isExporting).toBe(false));
    expect(mockDownloadCsv).toHaveBeenCalledWith(
      "slow.csv",
      "Value\r\ndone\r\n",
    );
  });

  it.each(["success", "failure"] as const)("does not download or toast a prior session's delayed %s", async (outcome) => {
    let resolveRows!: (rows: string[][]) => void;
    let rejectRows!: (error: Error) => void;
    let markStarted!: () => void;
    const started = new Promise<void>((resolve) => { markStarted = resolve; });
    const pending = new Promise<string[][]>((resolve, reject) => { resolveRows = resolve; rejectRows = reject; });
    const { result } = renderHook(() => useCsvExport(), { wrapper: createQueryWrapper() });
    await act(async () => {
      result.current.exportCsv({ filename: "Alice-private.csv", headers: ["Secret"], rows: () => { markStarted(); return pending; } });
      await started;
    });
    act(() => { clearTokens(); setTokens("Bob-access", "Bob-refresh"); });
    await act(async () => {
      if (outcome === "success") resolveRows([["Alice-only financial data"]]);
      else rejectRows(new Error("Alice export failed"));
    });
    await waitFor(() => expect(result.current.isExporting).toBe(false));
    expect(mockDownloadCsv).not.toHaveBeenCalled();
    expect(mockShowError).not.toHaveBeenCalled();
  });
});
