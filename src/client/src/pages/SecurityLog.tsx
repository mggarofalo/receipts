import { useCallback } from "react";
import {
  useMyAuthAuditLog,
  useRecentAuthAuditLogs,
  useFailedAuthAttempts,
} from "@/hooks/useAuthAudit";
import { usePageTitle } from "@/hooks/usePageTitle";
import { usePermission } from "@/hooks/usePermission";
import { useServerPagination } from "@/hooks/useServerPagination";
import { useServerSort } from "@/hooks/useServerSort";
import type { AuthAuditLog } from "@/lib/audit-utils";
import { useEnumMetadata } from "@/hooks/useEnumMetadata";
import { AuthAuditTable } from "@/components/AuthAuditTable";
import { Pagination } from "@/components/Pagination";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { PageHead } from "@/components/primitives";

function SecurityLog() {
  usePageTitle("Security Log");
  const { isAdmin } = usePermission();
  const { authEventLabels } = useEnumMetadata();
  const { sortBy, sortDirection, toggleSort } = useServerSort({ defaultSortBy: "timestamp", defaultSortDirection: "desc" });

  const myPagination = useServerPagination({ sortBy, sortDirection });
  const recentPagination = useServerPagination({ sortBy, sortDirection });
  const failedPagination = useServerPagination({ sortBy, sortDirection });

  const { resetPage: resetMyPage } = myPagination;
  const { resetPage: resetRecentPage } = recentPagination;
  const { resetPage: resetFailedPage } = failedPagination;

  const myLogs = useMyAuthAuditLog(myPagination.offset, myPagination.limit, sortBy, sortDirection);
  const recentLogs = useRecentAuthAuditLogs(recentPagination.offset, recentPagination.limit, sortBy, sortDirection);
  const failedLogs = useFailedAuthAttempts(failedPagination.offset, failedPagination.limit, sortBy, sortDirection);

  const handleSort = useCallback((column: string) => {
    toggleSort(column);
    resetMyPage();
    resetRecentPage();
    resetFailedPage();
  }, [toggleSort, resetMyPage, resetRecentPage, resetFailedPage]);

  const myTotal = myLogs.total;
  const recentTotal = recentLogs.total;
  const failedTotal = failedLogs.total;

  return (
    <>
      <PageHead title="Security log" sub="Sign-in activity and security events" />
      <div className="space-y-4">

      <Tabs defaultValue="my-activity">
        <TabsList>
          <TabsTrigger value="my-activity">My Activity</TabsTrigger>
          {isAdmin() && (
            <TabsTrigger value="all-events">All Events</TabsTrigger>
          )}
          {isAdmin() && (
            <TabsTrigger value="failed-logins">Failed Logins</TabsTrigger>
          )}
        </TabsList>

        <TabsContent value="my-activity" className="space-y-4">
          <AuthAuditTable
            logs={(myLogs.data ?? []) as AuthAuditLog[]}
            isLoading={myLogs.isLoading}
            sortBy={sortBy}
            sortDirection={sortDirection}
            onToggleSort={handleSort}
            authEventLabels={authEventLabels}
          />
          <Pagination
            currentPage={myPagination.currentPage}
            totalItems={myTotal}
            pageSize={myPagination.pageSize}
            totalPages={myPagination.totalPages(myTotal)}
            onPageChange={(page) => myPagination.setPage(page, myTotal)}
            onPageSizeChange={myPagination.setPageSize}
          />
        </TabsContent>

        {isAdmin() && (
          <TabsContent value="all-events" className="space-y-4">
            <AuthAuditTable
              logs={(recentLogs.data ?? []) as AuthAuditLog[]}
              isLoading={recentLogs.isLoading}
              showUsername
              sortBy={sortBy}
              sortDirection={sortDirection}
              onToggleSort={handleSort}
              authEventLabels={authEventLabels}
            />
            <Pagination
              currentPage={recentPagination.currentPage}
              totalItems={recentTotal}
              pageSize={recentPagination.pageSize}
              totalPages={recentPagination.totalPages(recentTotal)}
              onPageChange={(page) => recentPagination.setPage(page, recentTotal)}
              onPageSizeChange={recentPagination.setPageSize}
            />
          </TabsContent>
        )}

        {isAdmin() && (
          <TabsContent value="failed-logins" className="space-y-4">
            <AuthAuditTable
              logs={(failedLogs.data ?? []) as AuthAuditLog[]}
              isLoading={failedLogs.isLoading}
              showUsername
              sortBy={sortBy}
              sortDirection={sortDirection}
              onToggleSort={handleSort}
              authEventLabels={authEventLabels}
            />
            <Pagination
              currentPage={failedPagination.currentPage}
              totalItems={failedTotal}
              pageSize={failedPagination.pageSize}
              totalPages={failedPagination.totalPages(failedTotal)}
              onPageChange={(page) => failedPagination.setPage(page, failedTotal)}
              onPageSizeChange={failedPagination.setPageSize}
            />
          </TabsContent>
        )}
      </Tabs>
    </div>
    </>
  );
}

export default SecurityLog;
