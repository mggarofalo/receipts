import { lazy, Suspense, useCallback, useMemo } from "react";
import { useSearchParams } from "react-router";
import { usePageTitle } from "@/hooks/usePageTitle";
import { usePermission } from "@/hooks/usePermission";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { PageHead } from "@/components/primitives";

interface ReportConfig {
  slug: string;
  name: string;
  component: React.LazyExoticComponent<React.ComponentType>;
  adminOnly?: boolean;
}

const REPORTS: ReportConfig[] = [
  {
    slug: "out-of-balance",
    name: "Out of Balance",
    component: lazy(() => import("@/components/reports/OutOfBalance")),
  },
  {
    slug: "item-similarity",
    name: "Item Similarity",
    component: lazy(() => import("@/components/reports/ItemSimilarity")),
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
  {
    slug: "normalized-descriptions",
    name: "Normalized Descriptions",
    component: lazy(() => import("@/components/reports/NormalizedDescriptions")),
    adminOnly: true,
  },
];

const DEFAULT_REPORT = REPORTS[0].slug;

function ReportFallback() {
  return <Skeleton className="h-32 w-full rounded-lg" />;
}

function Reports() {
  const [searchParams, setSearchParams] = useSearchParams();
  const { isAdmin } = usePermission();

  const availableReports = useMemo(
    () => REPORTS.filter((r) => !r.adminOnly || isAdmin()),
    [isAdmin],
  );
  const validSlugs = useMemo(
    () => new Set(availableReports.map((r) => r.slug)),
    [availableReports],
  );

  const rawReport = searchParams.get("report");
  const activeSlug =
    rawReport && validSlugs.has(rawReport) ? rawReport : DEFAULT_REPORT;

  const activeReport = useMemo(
    () =>
      availableReports.find((r) => r.slug === activeSlug) ?? availableReports[0],
    [activeSlug, availableReports],
  );

  usePageTitle(`Reports - ${activeReport.name}`);

  const handleReportChange = useCallback(
    (slug: string) => {
      setSearchParams({ report: slug }, { replace: true });
    },
    [setSearchParams],
  );

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
              {availableReports.map((report) => (
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
