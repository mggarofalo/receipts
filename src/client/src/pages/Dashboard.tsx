import { useState, useCallback } from "react";
import { Link } from "react-router";
import { format, subMonths } from "date-fns";
import { usePageTitle } from "@/hooks/usePageTitle";
import { useReceipts } from "@/hooks/useReceipts";
import type { DateRange } from "@/hooks/useDashboard";
import { PageHead, Icon } from "@/components/primitives";
import { DateRangeSelector } from "@/components/dashboard/DateRangeSelector";
import { SummaryStats } from "@/components/dashboard/SummaryStats";
import { SpendingOverTimeWidget } from "@/components/dashboard/SpendingOverTimeWidget";
import { SpendingByCategoryWidget } from "@/components/dashboard/SpendingByCategoryWidget";
import { SpendingByAccountWidget } from "@/components/dashboard/SpendingByAccountWidget";
import { SpendingByStoreWidget } from "@/components/dashboard/SpendingByStoreWidget";

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

function formatShortDate(value: string | null | undefined): string {
  if (!value) return "—";
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return value;
  return d.toLocaleDateString("en-US", { month: "short", day: "numeric" });
}

function Dashboard() {
  usePageTitle("Dashboard");
  const [dateRange, setDateRange] = useState<DateRange>(getDefaultRange);

  const handleDateRangeChange = useCallback((range: DateRange) => {
    setDateRange(range);
  }, []);

  const recent = useReceipts(0, 4, "date", "desc");

  return (
    <>
      <PageHead
        title="Dashboard"
        sub={`${dateRange.startDate} → ${dateRange.endDate}`}
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
        style={{
          display: "grid",
          gridTemplateColumns: "minmax(0, 1fr) minmax(0, 1fr)",
          gap: 20,
          marginTop: 18,
        }}
      >
        <SpendingOverTimeWidget dateRange={dateRange} />
        <SpendingByCategoryWidget dateRange={dateRange} />
      </div>
      <div
        style={{
          display: "grid",
          gridTemplateColumns: "minmax(0, 1fr) minmax(0, 1fr)",
          gap: 20,
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
          {recent.total ? `Last ${recent.data?.length ?? 0} of ${recent.total}` : "—"}
        </div>
        <Link to="/receipts" className="card-action">
          See all <Icon.Arrow />
        </Link>
      </div>
      <div className="recent-strip">
        {recent.isLoading || !recent.data ? (
          <div className="recent" aria-hidden>
            <div className="r-store">Loading…</div>
          </div>
        ) : recent.data.length === 0 ? (
          <div className="recent" style={{ gridColumn: "1 / -1" }}>
            <div className="r-store">No receipts yet</div>
            <div className="r-meta">Add your first receipt to get started.</div>
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
