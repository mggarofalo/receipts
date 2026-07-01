import { useCallback, useMemo, useState } from "react";
import { Link } from "react-router";
import { usePageTitle } from "@/hooks/usePageTitle";
import { useYnabConnectionStatus, useYnabRateLimitStatus } from "@/hooks/useYnab";
import { useYnabStatus } from "@/hooks/useYnabStatus";
import { useYnabEvents } from "@/hooks/useYnabEvents";
import { useServerPagination } from "@/hooks/useServerPagination";
import { useServerSort } from "@/hooks/useServerSort";
import { YnabEventsTable } from "@/components/YnabEventsTable";
import { Pagination } from "@/components/Pagination";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Spinner } from "@/components/ui/spinner";
import { Combobox } from "@/components/ui/combobox";
import { EmptyState, Icon, PageHead } from "@/components/primitives";

function formatRelativeTime(dateStr: string): string {
  const diffMs = Date.now() - new Date(dateStr).getTime();
  const diffMin = Math.floor(diffMs / 60000);
  const diffHrs = Math.floor(diffMs / 3600000);
  const diffDays = Math.floor(diffMs / 86400000);
  if (diffMin < 1) return "just now";
  if (diffMin < 60) return `${diffMin}m ago`;
  if (diffHrs < 24) return `${diffHrs}h ago`;
  return `${diffDays}d ago`;
}

const OUTCOME_OPTIONS = [
  { value: "all", label: "All outcomes" },
  { value: "success", label: "Success" },
  { value: "failure", label: "Failure" },
];

export default function Ynab() {
  usePageTitle("YNAB Status");

  const {
    isConfigured,
    isConnected,
    lastSuccessfulSyncUtc,
    isLoading: connectionLoading,
  } = useYnabConnectionStatus();
  const { rateLimitStatus } = useYnabRateLimitStatus(isConfigured);
  const { data: status } = useYnabStatus();

  const [outcome, setOutcome] = useState<"all" | "success" | "failure">("all");

  const { sortBy, sortDirection, toggleSort } = useServerSort({
    defaultSortBy: "occurredAt",
    defaultSortDirection: "desc",
  });
  const pagination = useServerPagination({ sortBy, sortDirection });

  const handleOutcomeChange = useCallback(
    (value: string) => {
      setOutcome(value as "all" | "success" | "failure");
      pagination.resetPage();
    },
    [pagination],
  );

  const { data: events, total, isLoading } = useYnabEvents({
    offset: pagination.offset,
    limit: pagination.limit,
    sortBy,
    sortDirection,
    outcome: outcome !== "all" ? outcome : null,
  });

  const successRate = useMemo(() => {
    if (!status || status.pushCountLast30d === 0) return null;
    return Math.round((status.pushSuccessLast30d / status.pushCountLast30d) * 100);
  }, [status]);

  if (!connectionLoading && !isConfigured) {
    return (
      <>
        <PageHead title="YNAB status" sub="Integration health and recent sync activity" />
        <EmptyState
          icon={Icon.Link}
          title="YNAB is not configured"
          body={
            <>
              Set the <code>YNAB_PAT</code> environment variable to enable the
              integration, then choose a budget in{" "}
              <Link to="/settings/ynab" className="text-primary hover:underline">
                YNAB settings
              </Link>
              .
            </>
          }
        />
      </>
    );
  }

  return (
    <>
      <PageHead title="YNAB status" sub="Integration health and recent sync activity" />
      <div className="space-y-6">
        <div className="grid gap-4 md:grid-cols-3">
          {/* Connection */}
          <Card>
            <CardHeader>
              <CardTitle>Connection</CardTitle>
              <CardDescription>Token status and last sync.</CardDescription>
            </CardHeader>
            <CardContent>
              {connectionLoading ? (
                <div className="flex items-center gap-2">
                  <Spinner className="h-4 w-4" />
                  <span className="text-sm text-muted-foreground">
                    Checking connection...
                  </span>
                </div>
              ) : (
                <div className="space-y-2">
                  {isConnected ? (
                    <Badge className="bg-green-100 text-green-800 hover:bg-green-100 border-green-300">
                      Connected
                    </Badge>
                  ) : (
                    <Badge variant="destructive">Disconnected</Badge>
                  )}
                  <p className="text-sm text-muted-foreground">
                    {lastSuccessfulSyncUtc
                      ? `Last sync ${formatRelativeTime(lastSuccessfulSyncUtc)}`
                      : "No syncs yet"}
                  </p>
                  {status?.lastValidatedAt && (
                    <p className="text-xs text-muted-foreground">
                      Validated {formatRelativeTime(status.lastValidatedAt)}
                    </p>
                  )}
                </div>
              )}
            </CardContent>
          </Card>

          {/* Sync stats */}
          <Card>
            <CardHeader>
              <CardTitle>Push activity</CardTitle>
              <CardDescription>Transactions pushed to YNAB.</CardDescription>
            </CardHeader>
            <CardContent>
              <div className="space-y-2 text-sm">
                <div className="flex items-center justify-between">
                  <span className="text-muted-foreground">Last 24h / 7d / 30d</span>
                  <span className="font-medium">
                    {status?.pushCountLast24h ?? 0} / {status?.pushCountLast7d ?? 0} /{" "}
                    {status?.pushCountLast30d ?? 0}
                  </span>
                </div>
                <div className="flex items-center justify-between">
                  <span className="text-muted-foreground">Success rate (30d)</span>
                  <span className="font-medium">
                    {successRate === null ? "—" : `${successRate}%`}
                  </span>
                </div>
                <div className="flex items-center justify-between text-xs text-muted-foreground">
                  <span>
                    {status?.lastPushSuccessAt
                      ? `Last success ${formatRelativeTime(status.lastPushSuccessAt)}`
                      : "No successful pushes"}
                  </span>
                </div>
                {status?.lastPushFailureAt && (
                  <div className="text-xs text-destructive">
                    Last failure {formatRelativeTime(status.lastPushFailureAt)}
                  </div>
                )}
              </div>
            </CardContent>
          </Card>

          {/* Rate limit */}
          <Card>
            <CardHeader>
              <CardTitle>API rate limit</CardTitle>
              <CardDescription>YNAB allows 200 requests per hour.</CardDescription>
            </CardHeader>
            <CardContent>
              {rateLimitStatus ? (
                <div className="space-y-3">
                  <div className="flex items-center justify-between text-sm">
                    <span className="text-muted-foreground">
                      {rateLimitStatus.requestsUsed} / {rateLimitStatus.maxRequests} used
                    </span>
                    <span className="font-medium">
                      {rateLimitStatus.remainingRequests} left
                    </span>
                  </div>
                  <div className="h-2 rounded-full bg-muted overflow-hidden">
                    <div
                      role="progressbar"
                      aria-label="YNAB API rate limit usage"
                      aria-valuenow={rateLimitStatus.requestsUsed}
                      aria-valuemin={0}
                      aria-valuemax={rateLimitStatus.maxRequests}
                      className={`h-full rounded-full transition-all ${
                        rateLimitStatus.remainingRequests <= 20
                          ? "bg-destructive"
                          : rateLimitStatus.remainingRequests <= 50
                            ? "bg-amber-500"
                            : "bg-primary"
                      }`}
                      style={{
                        width: `${(rateLimitStatus.requestsUsed / rateLimitStatus.maxRequests) * 100}%`,
                      }}
                    />
                  </div>
                  {rateLimitStatus.remainingRequests <= 20 && (
                    <Alert variant="destructive">
                      <AlertDescription>
                        API quota is running low. Pushes may be throttled until the
                        window resets.
                      </AlertDescription>
                    </Alert>
                  )}
                </div>
              ) : (
                <span className="text-sm text-muted-foreground">
                  No rate-limit data yet.
                </span>
              )}
            </CardContent>
          </Card>
        </div>

        <div className="space-y-4">
          <div className="flex items-center gap-3 flex-wrap">
            <h2 className="text-sm font-medium">Recent activity</h2>
            <Combobox
              options={OUTCOME_OPTIONS}
              value={outcome}
              onValueChange={handleOutcomeChange}
              placeholder="Outcome"
              searchPlaceholder="Filter..."
              className="w-[160px]"
              aria-label="Filter by outcome"
            />
          </div>

          <YnabEventsTable
            events={events}
            isLoading={isLoading}
            sortBy={sortBy}
            sortDirection={sortDirection}
            onToggleSort={toggleSort}
          />

          <Pagination
            currentPage={pagination.currentPage}
            totalItems={total}
            pageSize={pagination.pageSize}
            totalPages={pagination.totalPages(total)}
            onPageChange={(page) => pagination.setPage(page, total)}
            onPageSizeChange={pagination.setPageSize}
          />
        </div>
      </div>
    </>
  );
}
