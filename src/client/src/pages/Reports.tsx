import { lazy, Suspense, useCallback, useMemo } from "react";
import { Navigate, useSearchParams } from "react-router";
import { usePageTitle } from "@/hooks/usePageTitle";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { PageHead } from "@/components/primitives";

// Old links to the retired "Normalized Descriptions" report (moved to
// /admin/normalized-descriptions in RECEIPTS-837) still point at this slug —
// redirect them instead of silently falling back to the default report.
const NORMALIZED_DESCRIPTIONS_REDIRECT = "/admin/normalized-descriptions";

interface ReportConfig {
  slug: string;
  name: string;
  component: React.LazyExoticComponent<React.ComponentType>;
}

const REPORTS: ReportConfig[] = [
  {
    slug: "out-of-balance",
    name: "Out of Balance",
    component: lazy(() => import("@/components/reports/OutOfBalance")),
  },
  {
    slug: "item-cost-over-time",
    name: "Item Cost Over Time",
    component: lazy(() => import("@/components/reports/ItemCostOverTime")),
  },
  {
    slug: "spending-by-location",
    name: "Spending by Location",
    component: lazy(() => import("@/components/reports/SpendingByLocation")),
  },
  {
    slug: "spending-by-normalized-description",
    name: "Spending by Normalized Description",
    component: lazy(
      () => import("@/components/reports/SpendingByNormalizedDescription"),
    ),
  },
  {
    slug: "category-trends",
    name: "Category Trends",
    component: lazy(() => import("@/components/reports/CategoryTrends")),
  },
  {
    slug: "duplicate-detection",
    name: "Duplicate Detection",
    component: lazy(() => import("@/components/reports/DuplicateDetection")),
  },
  {
    slug: "uncategorized-items",
    name: "Uncategorized Items",
    component: lazy(() => import("@/components/reports/UncategorizedItems")),
  },
];

const DEFAULT_REPORT = REPORTS[0].slug;

function ReportFallback() {
  return <Skeleton className="h-32 w-full rounded-lg" />;
}

function Reports() {
  const [searchParams, setSearchParams] = useSearchParams();

  const validSlugs = useMemo(() => new Set(REPORTS.map((r) => r.slug)), []);

  const rawReport = searchParams.get("report");
  const activeSlug =
    rawReport && validSlugs.has(rawReport) ? rawReport : DEFAULT_REPORT;

  const activeReport = useMemo(
    () => REPORTS.find((r) => r.slug === activeSlug) ?? REPORTS[0],
    [activeSlug],
  );

  usePageTitle(`Reports - ${activeReport.name}`);

  const handleReportChange = useCallback(
    (slug: string) => {
      setSearchParams({ report: slug }, { replace: true });
    },
    [setSearchParams],
  );

  if (rawReport === "normalized-descriptions") {
    return <Navigate to={NORMALIZED_DESCRIPTIONS_REDIRECT} replace />;
  }

  return (
    <>
      <PageHead
        title="Reports"
        sub={activeReport.name}
        actions={
          <Select value={activeReport.slug} onValueChange={handleReportChange}>
            <SelectTrigger className="w-[260px]" aria-label="Select report">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {REPORTS.map((report) => (
                <SelectItem key={report.slug} value={report.slug}>
                  {report.name}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        }
      />
      <Suspense fallback={<ReportFallback />}>
        <activeReport.component />
      </Suspense>
    </>
  );
}

export default Reports;
export { REPORTS, DEFAULT_REPORT };
