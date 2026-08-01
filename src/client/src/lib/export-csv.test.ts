import { CSV_BOM, csvFilename, downloadCsv, toCsv } from "./export-csv";

describe("toCsv", () => {
  it("joins headers and rows with CRLF and ends with a trailing CRLF", () => {
    const csv = toCsv(["a", "b"], [["1", "2"], ["3", "4"]]);
    expect(csv).toBe("a,b\r\n1,2\r\n3,4\r\n");
  });

  it("renders numbers and booleans as plain text", () => {
    const csv = toCsv(["n", "b"], [[1.5, true], [0, false]]);
    expect(csv).toBe("n,b\r\n1.5,true\r\n0,false\r\n");
  });

  it("renders null and undefined as empty fields", () => {
    const csv = toCsv(["a", "b", "c"], [[null, undefined, "x"]]);
    expect(csv).toBe("a,b,c\r\n,,x\r\n");
  });

  it("quotes fields containing commas", () => {
    const csv = toCsv(["name"], [["Smith, John"]]);
    expect(csv).toBe('name\r\n"Smith, John"\r\n');
  });

  it("quotes and doubles embedded quotes", () => {
    const csv = toCsv(["name"], [['He said "hi"']]);
    expect(csv).toBe('name\r\n"He said ""hi"""\r\n');
  });

  it("quotes fields containing newlines", () => {
    const csv = toCsv(["note"], [["line1\nline2"], ["a\r\nb"]]);
    expect(csv).toBe('note\r\n"line1\nline2"\r\n"a\r\nb"\r\n');
  });

  it("quotes header fields when needed", () => {
    const csv = toCsv(["Total, USD"], [["1"]]);
    expect(csv).toBe('"Total, USD"\r\n1\r\n');
  });

  it("handles an empty row set", () => {
    expect(toCsv(["a"], [])).toBe("a\r\n");
  });
});

describe("csvFilename", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date(2026, 6, 15, 12, 0, 0));
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("includes the date range when both bounds are present", () => {
    expect(
      csvFilename("spending-by-location", {
        startDate: "2026-07-01",
        endDate: "2026-08-01",
      }),
    ).toBe("spending-by-location_2026-07-01_2026-08-01.csv");
  });

  it("falls back to the current date when no range is given", () => {
    expect(csvFilename("out-of-balance")).toBe("out-of-balance_2026-07-15.csv");
  });

  it("falls back to the current date when the range is incomplete", () => {
    expect(csvFilename("out-of-balance", { startDate: "2026-07-01" })).toBe(
      "out-of-balance_2026-07-15.csv",
    );
  });
});

describe("downloadCsv", () => {
  const originalCreateObjectURL = URL.createObjectURL;
  const originalRevokeObjectURL = URL.revokeObjectURL;
  let capturedBlob: Blob | undefined;
  let clickSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    capturedBlob = undefined;
    URL.createObjectURL = vi.fn((blob: Blob) => {
      capturedBlob = blob;
      return "blob:mock-url";
    });
    URL.revokeObjectURL = vi.fn();
    clickSpy = vi
      .spyOn(HTMLAnchorElement.prototype, "click")
      .mockImplementation(() => {});
  });

  afterEach(() => {
    URL.createObjectURL = originalCreateObjectURL;
    URL.revokeObjectURL = originalRevokeObjectURL;
    clickSpy.mockRestore();
  });

  it("downloads a BOM-prefixed csv blob with the given filename", async () => {
    downloadCsv("report.csv", "a,b\r\n1,2\r\n");

    expect(clickSpy).toHaveBeenCalledTimes(1);
    expect(capturedBlob).toBeDefined();
    expect(capturedBlob?.type).toBe("text/csv;charset=utf-8");
    const bytes = new Uint8Array(await capturedBlob!.arrayBuffer());
    // UTF-8 BOM bytes precede the content so Excel detects the encoding.
    expect([bytes[0], bytes[1], bytes[2]]).toEqual([0xef, 0xbb, 0xbf]);
    const text = new TextDecoder("utf-8").decode(bytes);
    // TextDecoder strips the BOM, leaving the raw csv.
    expect(text).toBe("a,b\r\n1,2\r\n");
    expect(CSV_BOM).toBe("\uFEFF");
  });

  it("revokes the object url after clicking", () => {
    downloadCsv("report.csv", "a\r\n");
    expect(URL.revokeObjectURL).toHaveBeenCalledWith("blob:mock-url");
  });
});
