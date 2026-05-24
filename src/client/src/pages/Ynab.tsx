import { useState } from "react";
import { Link } from "react-router";
import { usePageTitle } from "@/hooks/usePageTitle";
import {
  useYnabStatus,
  useYnabSyncEvents,
  type YnabSyncEvent,
} from "@/hooks/useYnab";
import { PageHead, Icon, EmptyState } from "@/components/primitives";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { Alert, AlertDescription } from "@/components/ui/alert";

const PAGE_SIZE = 25;

type OutcomeFilter = "all" | "synced" | "failed";

function formatRelative(iso: string | null | undefined): string {
  if (!iso) return "never";
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "—";
  const diffMs = Date.now() - date.getTime();
  const diffMin = Math.floor(diffMs / 60_000);
  const diffHrs = Math.floor(diffMs / 3_600_000);
  const diffDays = Math.floor(diffMs / 86_400_000);
  if (diffMs < 60_000) return "just now";
  if (diffMin < 60) return `${diffMin}m ago`;
  if (diffHrs < 24) return `${diffHrs}h ago`;
  if (diffDays < 30) return `${diffDays}d ago`;
  return date.toLocaleDateString();
}

function formatAbsolute(iso: string | null | undefined): string {
  if (!iso) return "";
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "";
  return date.toLocaleString();
}

function StatusDot({ tone }: { tone: "pos" | "neg" | "warn" | "mute" }) {
  // Per design system: status colors live in CSS tokens. The dot is purely
  // decorative — the text label conveys the same meaning to screen readers.
  return <span className={`dot ${tone}`} aria-hidden="true" />;
}

function OutcomePill({ outcome }: { outcome: YnabSyncEvent["outcome"] }) {
  const label = outcome === "synced" ? "Synced" : outcome === "failed" ? "Failed" : "Pending";
  const tone = outcome === "synced" ? "pos" : outcome === "failed" ? "neg" : "warn";
  return (
    <span className={`chip ${tone}`}>
      <StatusDot tone={tone} />
      {label}
    </span>
  );
}

export default function Ynab() {
  usePageTitle("YNAB status");
  const { status, isLoading: statusLoading } = useYnabStatus();
  const [outcomeFilter, setOutcomeFilter] = useState<OutcomeFilter>("all");
  const [page, setPage] = useState(0);
  const offset = page * PAGE_SIZE;
  const { events, totalCount, isLoading: eventsLoading } = useYnabSyncEvents(
    offset,
    PAGE_SIZE,
    outcomeFilter === "all" ? undefined : outcomeFilter,
  );

  const totalPages = Math.max(1, Math.ceil(totalCount / PAGE_SIZE));
  const isConfigured = status?.isConfigured ?? false;
  const isConnected = status?.isConnected ?? false;

  return (
    <>
      <PageHead
        title="YNAB status"
        sub="Is anything stuck? Connection health, sync log."
      />

      {!statusLoading && !isConfigured && (
        <Alert>
          <AlertDescription>
            YNAB isn't configured yet.{" "}
            <Link to="/settings/ynab" className="underline">
              Connect a personal access token
            </Link>{" "}
            to start syncing.
          </AlertDescription>
        </Alert>
      )}

      {/* Health grid: four tiles, one row at desktop, stacks at mobile */}
      <section
        aria-labelledby="health-heading"
        style={{ marginTop: 16 }}
      >
        <h2
          id="health-heading"
          className="sr-only"
        >
          Health summary
        </h2>
        <div
          className="grid"
          style={{ display: "grid", gap: 12, gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))" }}
        >
          {/* Connection */}
          <Card>
            <CardHeader>
              <CardTitle className="text-sm font-medium">Connection</CardTitle>
            </CardHeader>
            <CardContent>
              {statusLoading ? (
                <Skeleton className="h-6 w-24" />
              ) : (
                <div className="flex items-center gap-2">
                  <StatusDot tone={!isConfigured ? "mute" : isConnected ? "pos" : "neg"} />
                  <span className="text-base font-semibold">
                    {!isConfigured ? "Not configured" : isConnected ? "Connected" : "Disconnected"}
                  </span>
                </div>
              )}
              {isConfigured && status?.selectedBudgetId && (
                <p className="text-xs text-muted-foreground" style={{ marginTop: 4 }}>
                  Budget · {status.selectedBudgetId}
                </p>
              )}
            </CardContent>
          </Card>

          {/* Last sync */}
          <Card>
            <CardHeader>
              <CardTitle className="text-sm font-medium">Last successful sync</CardTitle>
            </CardHeader>
            <CardContent>
              {statusLoading ? (
                <Skeleton className="h-6 w-24" />
              ) : (
                <>
                  <p className="text-base font-semibold">
                    {formatRelative(status?.lastSuccessUtc)}
                  </p>
                  {status?.lastSuccessUtc && (
                    <p className="text-xs text-muted-foreground" style={{ marginTop: 4 }}>
                      {formatAbsolute(status.lastSuccessUtc)}
                    </p>
                  )}
                </>
              )}
            </CardContent>
          </Card>

          {/* Last failure */}
          <Card>
            <CardHeader>
              <CardTitle className="text-sm font-medium">Last failure</CardTitle>
            </CardHeader>
            <CardContent>
              {statusLoading ? (
                <Skeleton className="h-6 w-24" />
              ) : status?.lastFailureUtc ? (
                <>
                  <p className="text-base font-semibold">
                    {formatRelative(status.lastFailureUtc)}
                  </p>
                  <p className="text-xs text-muted-foreground" style={{ marginTop: 4 }}>
                    {formatAbsolute(status.lastFailureUtc)}
                  </p>
                </>
              ) : (
                <p className="text-base font-semibold text-muted-foreground">none</p>
              )}
            </CardContent>
          </Card>

          {/* Rate limit */}
          <Card>
            <CardHeader>
              <CardTitle className="text-sm font-medium">Rate limit</CardTitle>
            </CardHeader>
            <CardContent>
              {statusLoading ? (
                <Skeleton className="h-6 w-24" />
              ) : (
                <>
                  <p className="text-base font-semibold">
                    {status?.rateLimit.remainingRequests ?? 0}{" "}
                    <span className="text-xs font-normal text-muted-foreground">
                      / {status?.rateLimit.maxRequests ?? 200} remaining
                    </span>
                  </p>
                  {status?.rateLimit.windowResetAt && (
                    <p className="text-xs text-muted-foreground" style={{ marginTop: 4 }}>
                      resets {formatRelative(status.rateLimit.windowResetAt)}
                    </p>
                  )}
                </>
              )}
            </CardContent>
          </Card>
        </div>
      </section>

      {/* Rolling counts: three windows × three buckets */}
      <section aria-labelledby="counts-heading" style={{ marginTop: 24 }}>
        <h2 id="counts-heading" className="sr-only">
          Sync counts by window
        </h2>
        <Card>
          <CardHeader>
            <CardTitle>Sync activity</CardTitle>
            <CardDescription>
              Push attempts by rolling window. Counts include both successful
              and failed attempts.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <table className="w-full text-sm" style={{ borderCollapse: "collapse" }}>
              <caption className="sr-only">YNAB sync attempt counts grouped by 24-hour, 7-day, and 30-day rolling windows.</caption>
              <thead>
                <tr style={{ borderBottom: "1px solid var(--line)", textAlign: "left" }}>
                  <th scope="col" style={{ padding: "8px 4px" }}>Window</th>
                  <th scope="col" style={{ padding: "8px 4px", textAlign: "right" }}>Total</th>
                  <th scope="col" style={{ padding: "8px 4px", textAlign: "right" }}>Success</th>
                  <th scope="col" style={{ padding: "8px 4px", textAlign: "right" }}>Failed</th>
                </tr>
              </thead>
              <tbody>
                {statusLoading ? (
                  <tr>
                    <td colSpan={4} style={{ padding: 12 }}>
                      <Skeleton className="h-4 w-full" />
                    </td>
                  </tr>
                ) : (
                  <>
                    <tr style={{ borderBottom: "1px solid var(--line)" }}>
                      <th scope="row" style={{ padding: "8px 4px", fontWeight: 500 }}>Last 24h</th>
                      <td style={{ padding: "8px 4px", textAlign: "right" }}>{status?.pushes24h ?? 0}</td>
                      <td style={{ padding: "8px 4px", textAlign: "right" }}>{status?.successes24h ?? 0}</td>
                      <td style={{ padding: "8px 4px", textAlign: "right" }}>{status?.failures24h ?? 0}</td>
                    </tr>
                    <tr style={{ borderBottom: "1px solid var(--line)" }}>
                      <th scope="row" style={{ padding: "8px 4px", fontWeight: 500 }}>Last 7 days</th>
                      <td style={{ padding: "8px 4px", textAlign: "right" }}>{status?.pushes7d ?? 0}</td>
                      <td style={{ padding: "8px 4px", textAlign: "right" }}>{status?.successes7d ?? 0}</td>
                      <td style={{ padding: "8px 4px", textAlign: "right" }}>{status?.failures7d ?? 0}</td>
                    </tr>
                    <tr>
                      <th scope="row" style={{ padding: "8px 4px", fontWeight: 500 }}>Last 30 days</th>
                      <td style={{ padding: "8px 4px", textAlign: "right" }}>{status?.pushes30d ?? 0}</td>
                      <td style={{ padding: "8px 4px", textAlign: "right" }}>{status?.successes30d ?? 0}</td>
                      <td style={{ padding: "8px 4px", textAlign: "right" }}>{status?.failures30d ?? 0}</td>
                    </tr>
                  </>
                )}
              </tbody>
            </table>
          </CardContent>
        </Card>
      </section>

      {/* Recent activity */}
      <section aria-labelledby="activity-heading" style={{ marginTop: 24 }}>
        <Card>
          <CardHeader>
            <CardTitle id="activity-heading">Recent activity</CardTitle>
            <CardDescription>
              One row per sync attempt. Most recent first.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <div className="flex items-center gap-2" style={{ marginBottom: 12 }}>
              {(["all", "synced", "failed"] as const).map((opt) => (
                <button
                  key={opt}
                  type="button"
                  className={`btn xs ${outcomeFilter === opt ? "primary" : ""}`}
                  aria-pressed={outcomeFilter === opt}
                  onClick={() => {
                    setOutcomeFilter(opt);
                    setPage(0);
                  }}
                >
                  {opt[0].toUpperCase() + opt.slice(1)}
                </button>
              ))}
            </div>

            {eventsLoading ? (
              <div role="status" aria-live="polite">
                <span className="sr-only">Loading sync events…</span>
                {[0, 1, 2].map((i) => (
                  <Skeleton key={i} className="h-6 w-full" style={{ marginBottom: 6 }} />
                ))}
              </div>
            ) : events.length === 0 ? (
              <EmptyState
                icon={Icon.Link}
                title="No sync events yet"
                body={
                  isConfigured
                    ? "Push a receipt to YNAB and it'll show up here."
                    : "Connect YNAB to start syncing."
                }
              />
            ) : (
              <>
                <table className="w-full text-sm" style={{ borderCollapse: "collapse" }}>
                  <caption className="sr-only">
                    YNAB sync attempts, most-recent first. {totalCount} total
                    {outcomeFilter !== "all" ? ` ${outcomeFilter}` : ""}.
                  </caption>
                  <thead>
                    <tr style={{ borderBottom: "1px solid var(--line)", textAlign: "left" }}>
                      <th scope="col" style={{ padding: "8px 4px" }}>When</th>
                      <th scope="col" style={{ padding: "8px 4px" }}>Outcome</th>
                      <th scope="col" style={{ padding: "8px 4px" }}>Receipt</th>
                      <th scope="col" style={{ padding: "8px 4px" }}>Error</th>
                    </tr>
                  </thead>
                  <tbody>
                    {events.map((ev) => (
                      <tr key={ev.id} style={{ borderBottom: "1px solid var(--line)" }}>
                        <td style={{ padding: "8px 4px" }} title={formatAbsolute(ev.occurredAt)}>
                          {formatRelative(ev.occurredAt)}
                        </td>
                        <td style={{ padding: "8px 4px" }}>
                          <OutcomePill outcome={ev.outcome} />
                        </td>
                        <td style={{ padding: "8px 4px" }}>
                          {ev.receiptId ? (
                            <Link to={`/receipts/${ev.receiptId}`} className="underline">
                              View
                            </Link>
                          ) : (
                            <span className="text-muted-foreground">—</span>
                          )}
                        </td>
                        <td style={{ padding: "8px 4px", maxWidth: 320, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }} title={ev.errorMessage ?? undefined}>
                          {ev.errorMessage ?? <span className="text-muted-foreground">—</span>}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>

                {totalPages > 1 && (
                  <nav
                    aria-label="Sync events pagination"
                    className="flex items-center justify-between"
                    style={{ marginTop: 12 }}
                  >
                    <span className="text-xs text-muted-foreground">
                      Page {page + 1} of {totalPages} · {totalCount.toLocaleString()} total
                    </span>
                    <div className="flex gap-2">
                      <Button
                        variant="ghost"
                        size="sm"
                        disabled={page === 0}
                        onClick={() => setPage((p) => Math.max(0, p - 1))}
                      >
                        Previous
                      </Button>
                      <Button
                        variant="ghost"
                        size="sm"
                        disabled={page + 1 >= totalPages}
                        onClick={() => setPage((p) => p + 1)}
                      >
                        Next
                      </Button>
                    </div>
                  </nav>
                )}
              </>
            )}
          </CardContent>
        </Card>
      </section>
    </>
  );
}
