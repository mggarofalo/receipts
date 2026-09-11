import { lazy, Suspense, useCallback, useMemo, useState } from "react";
import { format, subMonths, subYears } from "date-fns";
import { Link, Navigate, useSearchParams } from "react-router";
import { usePageTitle } from "@/hooks/usePageTitle";
import { useReportsHealthSummary } from "@/hooks/useReportsHealthSummary";
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { Icon, PageHead } from "@/components/primitives";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { downloadCsv, toCsv, type CsvValue } from "@/lib/export-csv";
import { cn } from "@/lib/utils";

// Old links to the retired "Normalized Descriptions" report (moved to
// /admin/normalized-descriptions in RECEIPTS-837) still point at this slug —
// redirect them instead of silently falling back to the hub.
const NORMALIZED_DESCRIPTIONS_REDIRECT = "/admin/normalized-descriptions";
const REPORTS_VIEW_STORAGE_KEY = "receipts.reportsView";

type IntelligenceView = "basket" | "trips" | "comparisons";

interface IntelligenceViewConfig {
  id: IntelligenceView;
  number: string;
  label: string;
  sublabel: string;
  title: string;
  description: string;
  exportHeaders: (month: Date) => string[];
  exportRows: CsvValue[][];
}

const INTELLIGENCE_VIEWS: IntelligenceViewConfig[] = [
  {
    id: "basket",
    number: "01",
    label: "Basket",
    sublabel: "Your CPI · staples · unit-price",
    title: "Your basket starts with the items you track",
    description:
      "Add basket items to compare like-for-like prices across the months you shop.",
    exportHeaders: (month) => [
      "Item",
      "Unit",
      ...Array.from({ length: 12 }, (_, index) =>
        format(subMonths(month, 11 - index), "yyyy-MM"),
      ),
    ],
    exportRows: [],
  },
  {
    id: "trips",
    number: "02",
    label: "Trips",
    sublabel: "Per-trip · outliers · $/item",
    title: "Trips need a little history",
    description:
      "Once enough receipts land in this month, this view will call out unusual trips and show your cost per item.",
    exportHeaders: () => [
      "Date",
      "Store",
      "Items",
      "Total",
      "Average per item",
      "Sigma",
    ],
    exportRows: [],
  },
  {
    id: "comparisons",
    number: "03",
    label: "Comparisons",
    sublabel: "MoM · YoY · narrative",
    title: "Comparisons appear as your history grows",
    description:
      "This month will be compared with the prior month and the same month last year when those periods have receipts.",
    exportHeaders: (month) => [
      "Metric",
      format(month, "yyyy-MM"),
      format(subMonths(month, 1), "yyyy-MM"),
      format(subYears(month, 1), "yyyy-MM"),
    ],
    exportRows: [],
  },
];

const MONTH_NAMES = Array.from({ length: 12 }, (_, month) =>
  format(new Date(2024, month, 1), "MMM"),
);

function isIntelligenceView(value: string | null): value is IntelligenceView {
  return INTELLIGENCE_VIEWS.some((view) => view.id === value);
}

function initialIntelligenceView(): IntelligenceView {
  try {
    const saved = window.localStorage.getItem(REPORTS_VIEW_STORAGE_KEY);
    return isIntelligenceView(saved) ? saved : "basket";
  } catch {
    return "basket";
  }
}

function downloadJson(filename: string, value: unknown): void {
  const blob = new Blob([JSON.stringify(value, null, 2) + "\n"], {
    type: "application/json;charset=utf-8",
  });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = filename;
  document.body.appendChild(anchor);
  anchor.click();
  document.body.removeChild(anchor);
  URL.revokeObjectURL(url);
}

/** Key on the health-summary payload that supplies a report's live count. */
type HealthMetric =
  | "outOfBalanceCount"
  | "duplicateGroupCount"
  | "uncategorizedItemCount";

interface ReportConfig {
  slug: string;
  name: string;
  /** One line, shown in the picker and on the hub card. */
  description: string;
  component: React.LazyExoticComponent<React.ComponentType>;
  /** Data-quality reports only: which health-summary count badges this report. */
  metric?: HealthMetric;
  /** Data-quality reports only: renders the count as a sentence. */
  formatCount?: (count: number) => string;
}

interface ReportGroup {
  id: string;
  label: string;
  reports: ReportConfig[];
}

function pluralize(count: number, singular: string, plural: string): string {
  return `${count} ${count === 1 ? singular : plural}`;
}

const REPORT_GROUPS: ReportGroup[] = [
  {
    id: "spending",
    label: "Spending",
    reports: [
      {
        slug: "spending-by-location",
        name: "Spending by Location",
        description:
          "Total spend, visit count, and average per visit for every store.",
        component: lazy(
          () => import("@/components/reports/SpendingByLocation"),
        ),
      },
      {
        slug: "spending-by-normalized-description",
        name: "Spending by Normalized Description",
        description:
          "Spend rolled up by canonical item name, so variants of one product total together.",
        component: lazy(
          () => import("@/components/reports/SpendingByNormalizedDescription"),
        ),
      },
      {
        slug: "category-trends",
        name: "Category Trends",
        description:
          "Category spending over time, with everything past the top few collapsed into Other.",
        component: lazy(() => import("@/components/reports/CategoryTrends")),
      },
      {
        slug: "item-cost-over-time",
        name: "Item Cost Over Time",
        description:
          "Unit-price history for a single item or category, to catch creeping costs.",
        component: lazy(() => import("@/components/reports/ItemCostOverTime")),
      },
    ],
  },
  {
    id: "data-quality",
    label: "Data Quality",
    reports: [
      {
        slug: "out-of-balance",
        name: "Out of Balance",
        description:
          "Receipts whose items, tax, and adjustments do not add up to the recorded total.",
        component: lazy(() => import("@/components/reports/OutOfBalance")),
        metric: "outOfBalanceCount",
        formatCount: (n) =>
          pluralize(n, "out-of-balance receipt", "out-of-balance receipts"),
      },
      {
        slug: "duplicate-detection",
        name: "Duplicate Detection",
        description:
          "Receipts that look like double entries because they share a date, location, or total.",
        component: lazy(
          () => import("@/components/reports/DuplicateDetection"),
        ),
        metric: "duplicateGroupCount",
        formatCount: (n) => pluralize(n, "duplicate group", "duplicate groups"),
      },
      {
        slug: "uncategorized-items",
        name: "Uncategorized Items",
        description:
          "Receipt items still filed under Uncategorized and waiting to be sorted.",
        component: lazy(
          () => import("@/components/reports/UncategorizedItems"),
        ),
        metric: "uncategorizedItemCount",
        formatCount: (n) =>
          pluralize(n, "uncategorized item", "uncategorized items"),
      },
    ],
  },
];

const REPORTS: ReportConfig[] = REPORT_GROUPS.flatMap((group) => group.reports);

function ReportFallback() {
  return <Skeleton className="h-32 w-full rounded-lg" />;
}

type Counts = Record<HealthMetric, number> | undefined;

/**
 * Full-sentence count badge for a data-quality report. Renders nothing while the
 * summary is in flight or unavailable — a missing badge is a better failure mode
 * than a wrong or zeroed one.
 */
function HealthBadge({
  report,
  counts,
  className,
}: {
  report: ReportConfig;
  counts: Counts;
  className?: string;
}) {
  if (!report.metric || !report.formatCount || !counts) return null;

  const count = counts[report.metric];

  return (
    <Badge
      variant={count > 0 ? "destructive" : "secondary"}
      className={className}
    >
      {count > 0 ? report.formatCount(count) : "All clear"}
    </Badge>
  );
}

/**
 * Compact count badge for the picker. The number alone would read as a bare
 * digit to a screen reader, so the full sentence is attached off-screen.
 */
function PickerBadge({
  report,
  counts,
}: {
  report: ReportConfig;
  counts: Counts;
}) {
  if (!report.metric || !report.formatCount || !counts) return null;

  const count = counts[report.metric];
  if (count === 0) return null;

  return (
    <Badge variant="destructive" className="ml-2">
      <span aria-hidden="true">{count}</span>
      <span className="sr-only">{report.formatCount(count)}</span>
    </Badge>
  );
}

function ReportMonthPicker({
  month,
  onChange,
}: {
  month: Date;
  onChange: (month: Date) => void;
}) {
  const today = new Date();
  const [open, setOpen] = useState(false);
  const [displayYear, setDisplayYear] = useState(month.getFullYear());

  const currentYear = today.getFullYear();
  const currentMonth = today.getMonth();

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button
          variant="outline"
          aria-label="Choose report month"
          className="font-mono text-xs"
        >
          <Icon.Calendar aria-hidden="true" />
          {format(month, "MMM yyyy")}
          <Icon.ChevronD aria-hidden="true" />
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="w-72 p-3">
        <div className="mb-3 flex items-center justify-between">
          <Button
            type="button"
            variant="ghost"
            size="icon"
            aria-label="Previous year"
            onClick={() => setDisplayYear((year) => year - 1)}
          >
            <Icon.ChevronR className="rotate-180" aria-hidden="true" />
          </Button>
          <strong className="font-serif text-lg font-normal">
            {displayYear}
          </strong>
          <Button
            type="button"
            variant="ghost"
            size="icon"
            aria-label="Next year"
            disabled={displayYear >= currentYear}
            onClick={() => setDisplayYear((year) => year + 1)}
          >
            <Icon.ChevronR aria-hidden="true" />
          </Button>
        </div>
        <div className="grid grid-cols-3 gap-1" role="grid">
          {MONTH_NAMES.map((name, monthIndex) => {
            const isFuture =
              displayYear > currentYear ||
              (displayYear === currentYear && monthIndex > currentMonth);
            const isSelected =
              displayYear === month.getFullYear() &&
              monthIndex === month.getMonth();

            return (
              <Button
                key={name}
                type="button"
                variant={isSelected ? "default" : "ghost"}
                size="sm"
                disabled={isFuture}
                aria-label={`${name} ${displayYear}`}
                aria-pressed={isSelected}
                onClick={() => {
                  onChange(new Date(displayYear, monthIndex, 1));
                  setOpen(false);
                }}
              >
                {name}
              </Button>
            );
          })}
        </div>
      </PopoverContent>
    </Popover>
  );
}

function IntelligenceEmptyState({ view }: { view: IntelligenceViewConfig }) {
  return (
    <section
      id={`reports-panel-${view.id}`}
      role="tabpanel"
      aria-labelledby={`reports-tab-${view.id}`}
      className="border-border bg-card flex min-h-72 flex-col items-center justify-center border px-6 py-14 text-center"
    >
      <span className="bg-muted text-muted-foreground mb-5 flex size-12 items-center justify-center rounded-full">
        <Icon.Chart width={22} height={22} aria-hidden="true" />
      </span>
      <h2 className="font-serif text-2xl font-normal">{view.title}</h2>
      <p className="text-muted-foreground mt-3 max-w-xl text-sm leading-6">
        {view.description}
      </p>
      <p className="text-muted-foreground mt-6 font-mono text-[11px] tracking-wide uppercase">
        Keep adding receipts — this report fills in automatically.
      </p>
    </section>
  );
}

function OperationalReportLibrary({ counts }: { counts: Counts }) {
  return (
    <section
      className="border-border mt-10 border-t pt-8"
      aria-labelledby="operational-reports-title"
    >
      <div className="mb-5 max-w-2xl">
        <h2
          id="operational-reports-title"
          className="font-serif text-2xl font-normal"
        >
          Operational reports
        </h2>
        <p className="text-muted-foreground mt-2 text-sm leading-6">
          Review spending detail and resolve data-quality work while price
          intelligence builds its history.
        </p>
      </div>
      <div className="flex flex-col gap-8">
        {REPORT_GROUPS.map((group) => (
          <section key={group.id} aria-labelledby={`report-group-${group.id}`}>
            <h3
              id={`report-group-${group.id}`}
              className="text-muted-foreground mb-3 text-xs font-semibold tracking-wide uppercase"
            >
              {group.label}
            </h3>
            <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
              {group.reports.map((report) => (
                <Link
                  key={report.slug}
                  to={`/reports?report=${report.slug}`}
                  className="rounded-xl"
                >
                  <Card className="h-full gap-3">
                    <CardHeader>
                      <CardTitle>{report.name}</CardTitle>
                      <CardDescription>{report.description}</CardDescription>
                      <HealthBadge
                        report={report}
                        counts={counts}
                        className="mt-1"
                      />
                    </CardHeader>
                  </Card>
                </Link>
              ))}
            </div>
          </section>
        ))}
      </div>
    </section>
  );
}

function IntelligenceShell({ counts }: { counts: Counts }) {
  const [activeViewId, setActiveViewId] = useState<IntelligenceView>(
    initialIntelligenceView,
  );
  const [month, setMonth] = useState(() => {
    const today = new Date();
    return new Date(today.getFullYear(), today.getMonth(), 1);
  });

  const activeView = INTELLIGENCE_VIEWS.find(
    (view) => view.id === activeViewId,
  )!;
  const monthKey = format(month, "yyyy-MM");

  const selectView = useCallback((view: IntelligenceView) => {
    setActiveViewId(view);
    try {
      window.localStorage.setItem(REPORTS_VIEW_STORAGE_KEY, view);
    } catch {
      // Storage can be unavailable in hardened/private browser contexts; the
      // active tab still works for the lifetime of this page.
    }
  }, []);

  const handleTabKeyDown = useCallback(
    (event: React.KeyboardEvent<HTMLButtonElement>) => {
      const currentIndex = INTELLIGENCE_VIEWS.findIndex(
        (view) => view.id === activeViewId,
      );
      let nextIndex: number | null = null;

      if (event.key === "ArrowRight") {
        nextIndex = (currentIndex + 1) % INTELLIGENCE_VIEWS.length;
      } else if (event.key === "ArrowLeft") {
        nextIndex =
          (currentIndex - 1 + INTELLIGENCE_VIEWS.length) %
          INTELLIGENCE_VIEWS.length;
      } else if (event.key === "Home") {
        nextIndex = 0;
      } else if (event.key === "End") {
        nextIndex = INTELLIGENCE_VIEWS.length - 1;
      }

      if (nextIndex === null) return;

      event.preventDefault();
      const nextView = INTELLIGENCE_VIEWS[nextIndex];
      selectView(nextView.id);
      document.getElementById(`reports-tab-${nextView.id}`)?.focus();
    },
    [activeViewId, selectView],
  );

  const exportCurrentCsv = useCallback(() => {
    downloadCsv(
      `${activeView.id}_${monthKey}.csv`,
      toCsv(activeView.exportHeaders(month), activeView.exportRows),
    );
  }, [activeView, month, monthKey]);

  const exportAllCsv = useCallback(() => {
    const csv = INTELLIGENCE_VIEWS.map((view) =>
      [
        toCsv([view.label], []),
        toCsv(view.exportHeaders(month), view.exportRows),
      ].join(""),
    ).join("\r\n");
    downloadCsv(`reports_${monthKey}.csv`, csv);
  }, [month, monthKey]);

  const exportRawJson = useCallback(() => {
    downloadJson(`reports_${monthKey}.json`, {
      month: monthKey,
      activeView: activeView.id,
      views: Object.fromEntries(
        INTELLIGENCE_VIEWS.map((view) => [
          view.id,
          { headers: view.exportHeaders(month), rows: view.exportRows },
        ]),
      ),
    });
  }, [activeView.id, month, monthKey]);

  return (
    <>
      <PageHead
        title="Reports"
        sub={`Personal price intelligence · ${format(month, "MMMM yyyy")}`}
        actions={
          <>
            <ReportMonthPicker month={month} onChange={setMonth} />
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button variant="outline">
                  <Icon.Upload className="rotate-180" aria-hidden="true" />
                  Export
                  <Icon.ChevronD aria-hidden="true" />
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end" className="w-52">
                <DropdownMenuItem onSelect={exportCurrentCsv}>
                  CSV — current view
                </DropdownMenuItem>
                <DropdownMenuItem onSelect={exportAllCsv}>
                  CSV — all views
                </DropdownMenuItem>
                <DropdownMenuItem onSelect={exportRawJson}>
                  JSON — raw data
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          </>
        }
      />

      <div className="border-border mb-4 flex flex-col gap-3 border-b pb-4 lg:flex-row lg:items-end lg:justify-between">
        <div
          role="tablist"
          aria-label="Price intelligence views"
          className="bg-muted grid grid-cols-3 gap-1 rounded-md p-1"
        >
          {INTELLIGENCE_VIEWS.map((view) => {
            const selected = view.id === activeView.id;
            return (
              <button
                key={view.id}
                type="button"
                role="tab"
                id={`reports-tab-${view.id}`}
                aria-label={`${view.number} ${view.label}`}
                aria-selected={selected}
                aria-controls={`reports-panel-${view.id}`}
                tabIndex={selected ? 0 : -1}
                onClick={() => selectView(view.id)}
                onKeyDown={handleTabKeyDown}
                className={cn(
                  "flex min-h-11 items-center justify-center gap-2 rounded-sm px-3 py-2 text-sm transition-colors",
                  selected
                    ? "bg-primary text-primary-foreground"
                    : "text-muted-foreground hover:bg-background hover:text-foreground",
                )}
              >
                <span className="font-mono text-[10px] opacity-70">
                  {view.number}
                </span>
                <span className="font-serif text-base">{view.label}</span>
              </button>
            );
          })}
        </div>
        <p className="text-muted-foreground font-mono text-[10px] tracking-widest uppercase">
          {activeView.sublabel}
        </p>
      </div>

      <IntelligenceEmptyState view={activeView} />
      <OperationalReportLibrary counts={counts} />
    </>
  );
}

function Reports() {
  const [searchParams, setSearchParams] = useSearchParams();

  const rawReport = searchParams.get("report");

  const activeReport = useMemo(
    () => REPORTS.find((r) => r.slug === rawReport) ?? null,
    [rawReport],
  );

  usePageTitle(activeReport ? `Reports - ${activeReport.name}` : "Reports");

  const { data: counts } = useReportsHealthSummary();

  const handleReportChange = useCallback(
    (slug: string) => {
      // Replace rather than merge: any filter params belong to the report being
      // navigated away from and must not leak into the next one.
      setSearchParams({ report: slug }, { replace: true });
    },
    [setSearchParams],
  );

  const handleBackToHub = useCallback(() => {
    setSearchParams({}, { replace: true });
  }, [setSearchParams]);

  if (rawReport === "normalized-descriptions") {
    return <Navigate to={NORMALIZED_DESCRIPTIONS_REDIRECT} replace />;
  }

  if (!activeReport) {
    return <IntelligenceShell counts={counts} />;
  }

  return (
    <>
      <PageHead
        title="Reports"
        sub={activeReport.name}
        actions={
          <>
            <Button variant="outline" onClick={handleBackToHub}>
              All reports
            </Button>
            <Select
              value={activeReport.slug}
              onValueChange={handleReportChange}
            >
              <SelectTrigger className="w-[260px]" aria-label="Select report">
                <SelectValue placeholder="Select a report" />
              </SelectTrigger>
              <SelectContent>
                {REPORT_GROUPS.map((group) => (
                  <SelectGroup key={group.id}>
                    <SelectLabel>{group.label}</SelectLabel>
                    {group.reports.map((report) => (
                      <SelectItem key={report.slug} value={report.slug}>
                        {report.name}
                        <PickerBadge report={report} counts={counts} />
                      </SelectItem>
                    ))}
                  </SelectGroup>
                ))}
              </SelectContent>
            </Select>
          </>
        }
      />

      <Suspense fallback={<ReportFallback />}>
        <activeReport.component />
      </Suspense>
    </>
  );
}

export default Reports;
export { INTELLIGENCE_VIEWS, REPORTS, REPORT_GROUPS, REPORTS_VIEW_STORAGE_KEY };
