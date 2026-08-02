import {
  EXPORT_PAGE_SIZE,
  MAX_EXPORT_ROWS,
  fetchAllReportPages,
} from "./fetch-all-report-pages";

function makeItems(count: number, offset = 0) {
  return Array.from({ length: count }, (_, i) => ({ id: offset + i }));
}

describe("fetchAllReportPages", () => {
  it("fetches a single page when everything fits", async () => {
    const fetchPage = vi.fn().mockResolvedValue({
      items: makeItems(30),
      totalCount: 30,
    });

    const result = await fetchAllReportPages(fetchPage);

    expect(result).toHaveLength(30);
    expect(fetchPage).toHaveBeenCalledTimes(1);
    expect(fetchPage).toHaveBeenCalledWith(1, EXPORT_PAGE_SIZE);
  });

  it("aggregates multiple pages in order", async () => {
    const fetchPage = vi
      .fn()
      .mockImplementation(async (page: number) => ({
        items:
          page === 1
            ? makeItems(EXPORT_PAGE_SIZE, 0)
            : makeItems(50, EXPORT_PAGE_SIZE),
        totalCount: EXPORT_PAGE_SIZE + 50,
      }));

    const result = await fetchAllReportPages(fetchPage);

    expect(result).toHaveLength(150);
    expect(result[0]).toEqual({ id: 0 });
    expect(result[149]).toEqual({ id: 149 });
    expect(fetchPage).toHaveBeenCalledTimes(2);
    expect(fetchPage).toHaveBeenNthCalledWith(1, 1, EXPORT_PAGE_SIZE);
    expect(fetchPage).toHaveBeenNthCalledWith(2, 2, EXPORT_PAGE_SIZE);
  });

  it("returns an empty array when the report has no rows", async () => {
    const fetchPage = vi.fn().mockResolvedValue({ items: [], totalCount: 0 });

    const result = await fetchAllReportPages(fetchPage);

    expect(result).toEqual([]);
    expect(fetchPage).toHaveBeenCalledTimes(1);
  });

  it("caps the export at MAX_EXPORT_ROWS", async () => {
    // Each mocked page returns far more than a real page would, so the cap
    // triggers without looping 100 times.
    const fetchPage = vi.fn().mockImplementation(async (page: number) => ({
      items: makeItems(6000, (page - 1) * 6000),
      totalCount: 50_000,
    }));

    const result = await fetchAllReportPages(fetchPage);

    expect(result).toHaveLength(MAX_EXPORT_ROWS);
    expect(fetchPage).toHaveBeenCalledTimes(2);
  });

  it("stops when a page comes back empty even if totalCount says otherwise", async () => {
    const fetchPage = vi.fn().mockImplementation(async (page: number) => ({
      items: page === 1 ? makeItems(EXPORT_PAGE_SIZE) : [],
      totalCount: 500,
    }));

    const result = await fetchAllReportPages(fetchPage);

    expect(result).toHaveLength(EXPORT_PAGE_SIZE);
    expect(fetchPage).toHaveBeenCalledTimes(2);
  });

  it("propagates fetch errors", async () => {
    const fetchPage = vi.fn().mockRejectedValue(new Error("boom"));

    await expect(fetchAllReportPages(fetchPage)).rejects.toThrow("boom");
  });
});
