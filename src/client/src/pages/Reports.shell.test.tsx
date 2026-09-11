import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { downloadCsv } from "@/lib/export-csv";
import Reports from "./Reports";

const mockHealthSummary = vi.fn();

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

vi.mock("@/hooks/useReportsHealthSummary", () => ({
  useReportsHealthSummary: () => mockHealthSummary(),
}));

vi.mock("@/lib/export-csv", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/export-csv")>();
  return { ...actual, downloadCsv: vi.fn() };
});

const mockDownloadCsv = vi.mocked(downloadCsv);

function renderReports() {
  return render(
    <MemoryRouter initialEntries={["/reports"]}>
      <Reports />
    </MemoryRouter>,
  );
}

describe("Reports price-intelligence shell", () => {
  beforeAll(() => {
    if (!Element.prototype.hasPointerCapture) {
      Element.prototype.hasPointerCapture = () => false;
      Element.prototype.releasePointerCapture = () => {};
      Element.prototype.setPointerCapture = () => {};
    }
    if (!Element.prototype.scrollIntoView) {
      Element.prototype.scrollIntoView = () => {};
    }
  });

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date(2026, 8, 11, 12));
    localStorage.clear();
    mockDownloadCsv.mockClear();
    mockHealthSummary.mockReturnValue({ data: undefined });
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  function user() {
    return userEvent.setup();
  }

  it("renders the page heading, current month, and all three numbered views", () => {
    renderReports();

    expect(
      screen.getByRole("heading", { level: 1, name: "Reports" }),
    ).toBeInTheDocument();
    expect(
      screen.getByText("Personal price intelligence · September 2026"),
    ).toBeInTheDocument();

    const basket = screen.getByRole("tab", { name: "01 Basket" });
    expect(basket).toHaveAttribute("aria-selected", "true");
    expect(
      screen.getByRole("tab", { name: "02 Trips" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("tab", { name: "03 Comparisons" }),
    ).toBeInTheDocument();
  });

  it("restores the active view and persists subsequent view changes", async () => {
    localStorage.setItem("receipts.reportsView", "comparisons");
    const firstRender = renderReports();

    expect(
      screen.getByRole("tab", { name: "03 Comparisons" }),
    ).toHaveAttribute("aria-selected", "true");

    await user().click(screen.getByRole("tab", { name: "02 Trips" }));
    expect(localStorage.getItem("receipts.reportsView")).toBe("trips");

    firstRender.unmount();
    renderReports();
    expect(
      screen.getByRole("tab", { name: "02 Trips" }),
    ).toHaveAttribute("aria-selected", "true");
  });

  it("offers a year-by-year month grid and prevents future selection", async () => {
    const actor = user();
    renderReports();

    await actor.click(
      screen.getByRole("button", { name: "Choose report month" }),
    );

    expect(screen.getByText("2026")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Sep 2026" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "Oct 2026" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Nov 2026" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Dec 2026" })).toBeDisabled();

    await actor.click(screen.getByRole("button", { name: "Previous year" }));
    expect(screen.getByText("2025")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Dec 2025" })).toBeEnabled();

    await actor.click(screen.getByRole("button", { name: "Aug 2025" }));
    expect(
      screen.getByText("Personal price intelligence · August 2025"),
    ).toBeInTheDocument();

    await actor.click(screen.getByRole("tab", { name: "03 Comparisons" }));
    expect(
      screen.getByText("Personal price intelligence · August 2025"),
    ).toBeInTheDocument();
  });

  it("exposes all supported export formats", async () => {
    const actor = user();
    renderReports();

    await actor.click(screen.getByRole("button", { name: "Export" }));

    expect(
      screen.getByRole("menuitem", { name: "CSV — current view" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("menuitem", { name: "CSV — all views" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("menuitem", { name: "JSON — raw data" }),
    ).toBeInTheDocument();
  });

  it("exports each current view with its view-specific CSV shape", async () => {
    const actor = user();
    renderReports();

    await actor.click(screen.getByRole("button", { name: "Export" }));
    await actor.click(
      screen.getByRole("menuitem", { name: "CSV — current view" }),
    );
    expect(mockDownloadCsv).toHaveBeenLastCalledWith(
      "basket_2026-09.csv",
      "Item,Unit,2025-10,2025-11,2025-12,2026-01,2026-02,2026-03,2026-04,2026-05,2026-06,2026-07,2026-08,2026-09\r\n",
    );

    await actor.click(screen.getByRole("tab", { name: "02 Trips" }));
    await actor.click(screen.getByRole("button", { name: "Export" }));
    await actor.click(
      screen.getByRole("menuitem", { name: "CSV — current view" }),
    );
    expect(mockDownloadCsv).toHaveBeenLastCalledWith(
      "trips_2026-09.csv",
      "Date,Store,Items,Total,Average per item,Sigma\r\n",
    );

    await actor.click(
      screen.getByRole("tab", { name: "03 Comparisons" }),
    );
    await actor.click(screen.getByRole("button", { name: "Export" }));
    await actor.click(
      screen.getByRole("menuitem", { name: "CSV — current view" }),
    );
    expect(mockDownloadCsv).toHaveBeenLastCalledWith(
      "comparisons_2026-09.csv",
      "Metric,2026-09,2026-08,2025-09\r\n",
    );
  });

  it("exports a combined CSV containing all three view shapes", async () => {
    const actor = user();
    renderReports();

    await actor.click(screen.getByRole("button", { name: "Export" }));
    await actor.click(
      screen.getByRole("menuitem", { name: "CSV — all views" }),
    );

    expect(mockDownloadCsv).toHaveBeenCalledOnce();
    const [filename, csv] = mockDownloadCsv.mock.calls[0];
    expect(filename).toBe("reports_2026-09.csv");
    expect(csv).toContain("Basket\r\nItem,Unit,2025-10");
    expect(csv).toContain(
      "Trips\r\nDate,Store,Items,Total,Average per item,Sigma",
    );
    expect(csv).toContain(
      "Comparisons\r\nMetric,2026-09,2026-08,2025-09",
    );
  });

  it("downloads raw JSON for the selected month and active view", async () => {
    const actor = user();
    const createObjectUrl = vi
      .spyOn(URL, "createObjectURL")
      .mockReturnValue("blob:reports");
    const revokeObjectUrl = vi
      .spyOn(URL, "revokeObjectURL")
      .mockImplementation(() => {});
    const clickAnchor = vi
      .spyOn(HTMLAnchorElement.prototype, "click")
      .mockImplementation(() => {});
    renderReports();

    await actor.click(screen.getByRole("tab", { name: "02 Trips" }));
    await actor.click(screen.getByRole("button", { name: "Export" }));
    await actor.click(
      screen.getByRole("menuitem", { name: "JSON — raw data" }),
    );

    expect(createObjectUrl).toHaveBeenCalledWith(expect.any(Blob));
    const blob = createObjectUrl.mock.calls[0][0];
    if (!(blob instanceof Blob)) {
      throw new TypeError("Expected the JSON export to create a Blob URL");
    }
    const contents = await new Promise<string>((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result));
      reader.onerror = () => reject(reader.error);
      reader.readAsText(blob);
    });
    expect(JSON.parse(contents)).toMatchObject({
      month: "2026-09",
      activeView: "trips",
      views: {
        basket: {
          headers: [
            "Item",
            "Unit",
            "2025-10",
            "2025-11",
            "2025-12",
            "2026-01",
            "2026-02",
            "2026-03",
            "2026-04",
            "2026-05",
            "2026-06",
            "2026-07",
            "2026-08",
            "2026-09",
          ],
          rows: [],
        },
        trips: {
          headers: [
            "Date",
            "Store",
            "Items",
            "Total",
            "Average per item",
            "Sigma",
          ],
          rows: [],
        },
        comparisons: {
          headers: ["Metric", "2026-09", "2026-08", "2025-09"],
          rows: [],
        },
      },
    });
    expect(clickAnchor).toHaveBeenCalledOnce();
    expect(revokeObjectUrl).toHaveBeenCalledWith("blob:reports");
  });
});
