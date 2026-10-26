import { lazy, Suspense, useCallback, useMemo } from "react";
import { Link, Navigate, useSearchParams } from "react-router";
import { parseCategoryViewParam } from "@/lib/report-params";
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
import { PageHead } from "@/components/primitives";

// Old links to the retired "Normalized Descriptions" report (moved to
// /admin/normalized-descriptions in RECEIPTS-837) still point at this slug —
// redirect them instead of silently falling back to the hub.
const NORMALIZED_DESCRIPTIONS_REDIRECT = "/admin/normalized-descriptions";
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
        description: "Totals, visits, and average per visit.",
        component: lazy(
          () => import("@/components/reports/SpendingByLocation"),
        ),
      },
      {
        slug: "spending-by-normalized-description",
        name: "Spending by Normalized Description",
        description: "Product variants grouped under a canonical name.",
        component: lazy(
          () => import("@/components/reports/SpendingByNormalizedDescription"),
        ),
      },
      {
        slug: "category-trends",
        name: "Category Trends",
        description: "Smaller categories are grouped into Other.",
        component: lazy(() => import("@/components/reports/CategoryTrends")),
      },
      {
        slug: "item-cost-over-time",
        name: "Item Cost Over Time",
        description: "Unit prices for one item or category.",
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
          "Items, tax, and adjustments differ from the recorded total.",
        component: lazy(() => import("@/components/reports/OutOfBalance")),
        metric: "outOfBalanceCount",
        formatCount: (n) =>
          pluralize(n, "out-of-balance receipt", "out-of-balance receipts"),
      },
      {
        slug: "duplicate-detection",
        name: "Duplicate Detection",
        description: "Potential duplicates by date, location, or total.",
        component: lazy(
          () => import("@/components/reports/DuplicateDetection"),
        ),
        metric: "duplicateGroupCount",
        formatCount: (n) => pluralize(n, "duplicate group", "duplicate groups"),
      },
      {
        slug: "uncategorized-items",
        name: "Uncategorized Items",
        description: "Items filed under Uncategorized.",
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

function OperationalReportLibrary({
  counts,
  sharedParams,
}: {
  counts: Counts;
  sharedParams: string;
}) {
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
                  to={`/reports?${sharedParams}&report=${report.slug}`}
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

function Reports() {
  const [searchParams, setSearchParams] = useSearchParams();

  const categoryView = parseCategoryViewParam(searchParams);
  const sharedParams = useMemo(() => {
    const params = new URLSearchParams({ categoryView });
    for (const key of ["startDate", "endDate"]) {
      const value = searchParams.get(key);
      if (value) params.set(key, value);
    }
    return params.toString();
  }, [searchParams, categoryView]);
  const rawReport = searchParams.get("report");

  const activeReport = useMemo(
    () => REPORTS.find((r) => r.slug === rawReport) ?? null,
    [rawReport],
  );

  usePageTitle(activeReport ? `Reports - ${activeReport.name}` : "Reports");

  const { data: counts } = useReportsHealthSummary();

  const handleReportChange = useCallback(
    (slug: string) => {
      // Preserve shared dates and category view while resetting report-specific filters.
      setSearchParams(`${sharedParams}&report=${slug}`, { replace: true });
    },
    [setSearchParams, sharedParams],
  );

  const handleBackToHub = useCallback(() => {
    setSearchParams(sharedParams, { replace: true });
  }, [setSearchParams, sharedParams]);

  if (rawReport === "normalized-descriptions") {
    return <Navigate to={NORMALIZED_DESCRIPTIONS_REDIRECT} replace />;
  }

  if (!activeReport) {
    return (
      <>
        <PageHead title="Reports" />
        <Card>
          <CardHeader>
            <CardTitle>Purchase history</CardTitle>
            <Button asChild className="w-fit">
              <Link to={`/search?categoryView=${categoryView}`}>
                Search purchase history
              </Link>
            </Button>
          </CardHeader>
        </Card>
        <OperationalReportLibrary counts={counts} sharedParams={sharedParams} />
      </>
    );
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
            <Button asChild variant="outline">
              <Link to={`/search?categoryView=${categoryView}`}>
                Search purchase history
              </Link>
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
export { REPORTS, REPORT_GROUPS };
