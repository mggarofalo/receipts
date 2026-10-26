import { useCallback } from "react";
import { Link } from "react-router";
import { format, subMonths } from "date-fns";
import { usePageTitle } from "@/hooks/usePageTitle";
import { useReceipts } from "@/hooks/useReceipts";
import { formatShortDate } from "@/lib/format";
import type { DateRange } from "@/hooks/useDashboard";
import { PageHead, Icon } from "@/components/primitives";
import { Skeleton } from "@/components/ui/skeleton";
import { DateRangeSelector } from "@/components/dashboard/DateRangeSelector";
import { SummaryStats } from "@/components/dashboard/SummaryStats";
import { SpendingOverTimeWidget } from "@/components/dashboard/SpendingOverTimeWidget";
import { SpendingByCategoryWidget } from "@/components/dashboard/SpendingByCategoryWidget";
import { SpendingByAccountWidget } from "@/components/dashboard/SpendingByAccountWidget";
import { SpendingByStoreWidget } from "@/components/dashboard/SpendingByStoreWidget";
import { useReportSearchParams } from "@/hooks/useReportSearchParams";
import {
  parseCategoryViewParam,
  parseDateRangeParam,
  serializeDateRangeParam,
} from "@/lib/report-params";

function parseDashboardParams(params: URLSearchParams) {
  return {
    dateRange:
      params.has("startDate") || params.has("endDate")
        ? parseDateRangeParam(params)
        : getDefaultRange(),
    categoryView: parseCategoryViewParam(params),
  };
}

function getDefaultRange(): DateRange {
  const now = new Date();
  return {
    startDate: format(subMonths(now, 1), "yyyy-MM-dd"),
    endDate: format(now, "yyyy-MM-dd"),
  };
}

function formatMoney(value: number | string | null | undefined): string {
  const n = Number(value ?? 0);
  return new Intl.NumberFormat("en-US", {
    style: "currency",
    currency: "USD",
  }).format(n);
}

function Dashboard() {
  usePageTitle("Dashboard");
  const [{ dateRange, categoryView }, updateParams] =
    useReportSearchParams(parseDashboardParams);

  const handleDateRangeChange = useCallback(
    (range: DateRange) => {
      updateParams(serializeDateRangeParam(range));
    },
    [updateParams],
  );

  const reportParams = new URLSearchParams({ categoryView });
  for (const [key, value] of Object.entries(
    serializeDateRangeParam(dateRange),
  )) {
    if (value) reportParams.set(key, value);
  }

  const recent = useReceipts(0, 4, "date", "desc");

  return (
    <>
      <PageHead
        title="Dashboard"
        sub={
          dateRange.startDate && dateRange.endDate
            ? `${dateRange.startDate} → ${dateRange.endDate}`
            : dateRange.startDate
              ? `From ${dateRange.startDate}`
              : dateRange.endDate
                ? `Through ${dateRange.endDate}`
                : "All time"
        }
        actions={
          <>
            <DateRangeSelector
              value={dateRange}
              onChange={handleDateRangeChange}
            />
            <Link to="/receipts/new" className="btn primary">
              <Icon.Plus /> New receipt
            </Link>
          </>
        }
      />

      <SummaryStats dateRange={dateRange} />

      <div
        className="grid grid-cols-1 gap-5 lg:grid-cols-2"
        style={{
          marginTop: 18,
        }}
      >
        <SpendingOverTimeWidget dateRange={dateRange} />
        <div className="min-w-0">
          <div className="flex flex-wrap items-center justify-between gap-2 mb-2">
            <label className="text-sm">
              Category view{" "}
              <select
                aria-label="Category view"
                className="rounded border bg-background p-1"
                value={categoryView}
                onChange={(event) =>
                  updateParams({
                    categoryView: event.target.value,
                    page: null,
                    offset: null,
                    capturedCurrentRevisionId: null,
                  })
                }
              >
                <option value="asClassified">As recorded</option>
                <option value="currentHierarchy">
                  Current category hierarchy
                </option>
              </select>
            </label>
            <Link className="card-action" to={`/reports?${reportParams}`}>
              Reports <Icon.Arrow />
            </Link>
          </div>
          <SpendingByCategoryWidget
            dateRange={dateRange}
            options={{ categoryView }}
          />
        </div>
      </div>
      <div
        className="grid grid-cols-1 gap-5 lg:grid-cols-2"
        style={{
          marginTop: 14,
        }}
      >
        <SpendingByAccountWidget dateRange={dateRange} />
        <SpendingByStoreWidget dateRange={dateRange} />
      </div>

      <div className="section-head">
        <h3>Recent</h3>
        <div className="line" />
        <div className="aux">
          {recent.total
            ? `Last ${recent.data?.length ?? 0} of ${recent.total}`
            : "—"}
        </div>
        <Link to="/receipts" className="card-action">
          See all <Icon.Arrow />
        </Link>
      </div>
      <div
        className="recent-strip"
        role={recent.isLoading || !recent.data ? "status" : undefined}
        aria-busy={recent.isLoading || !recent.data || undefined}
        aria-live={recent.isLoading || !recent.data ? "polite" : undefined}
      >
        {recent.isLoading || !recent.data ? (
          <>
            <span className="sr-only">Loading recent receipts…</span>
            {/* Mono placeholder bars matching the .recent card structure
                — keeps the dashboard layout stable while data fetches. */}
            {Array.from({ length: 4 }).map((_, i) => (
              <div key={i} className="recent" aria-hidden="true">
                <Skeleton className="h-4 w-24 mb-2" />
                <Skeleton className="h-3 w-16 mb-2" />
                <Skeleton className="h-4 w-14" />
              </div>
            ))}
          </>
        ) : recent.data.length === 0 ? (
          <div className="recent" style={{ gridColumn: "1 / -1" }}>
            <div className="r-store">No receipts yet</div>
            <Link
              to="/receipts/new"
              className="btn xs ghost"
              style={{ marginTop: 6 }}
            >
              <Icon.Plus /> New receipt
            </Link>
          </div>
        ) : (
          recent.data.slice(0, 4).map((r) => (
            <Link
              key={r.id}
              to={`/receipts/${r.id}`}
              className="recent"
              aria-label={`Open receipt for ${r.location}`}
            >
              <div className="r-store">{r.location}</div>
              <div className="r-meta">{formatShortDate(r.date)}</div>
              <div className="r-amt">{formatMoney(r.taxAmount)} tax</div>
            </Link>
          ))
        )}
      </div>
    </>
  );
}

export default Dashboard;
