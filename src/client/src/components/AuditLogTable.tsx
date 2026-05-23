import type { AuditLog } from "@/lib/audit-utils";
import {
  parseChanges,
  actionBadgeVariant,
  formatAuditTimestamp,
  truncateId,
} from "@/lib/audit-utils";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Tooltip,
  TooltipContent,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import {
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
} from "@/components/ui/collapsible";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { SortableTableHead } from "@/components/SortableTableHead";
import { FieldDiff } from "@/components/FieldDiff";

interface AuditLogTableProps {
  logs: AuditLog[];
  isLoading: boolean;
  sortBy?: string | null;
  sortDirection?: "asc" | "desc";
  onToggleSort?: (column: string) => void;
  entityTypeLabels?: Record<string, string>;
  /** Maps a changed-by user id to a friendly label (e.g. email). */
  userLabels?: Record<string, string>;
}

function AuditRow({
  log,
  entityTypeLabels,
  userLabels,
}: {
  log: AuditLog;
  entityTypeLabels: Record<string, string>;
  userLabels: Record<string, string>;
}) {
  const changes = parseChanges(log.changesJson);
  const hasChanges = changes.length > 0;
  // A friendly name when we can resolve the user id; otherwise the raw id.
  const userLabel = log.changedByUserId
    ? userLabels[log.changedByUserId]
    : undefined;

  return (
    <Collapsible asChild>
      <>
        <TableRow>
          <TableCell className="text-xs">
            {formatAuditTimestamp(log.changedAt)}
          </TableCell>
          <TableCell>
            {entityTypeLabels[log.entityType] ?? log.entityType}
          </TableCell>
          <TableCell>
            <Tooltip>
              <TooltipTrigger asChild>
                <span className="font-mono text-xs cursor-default">
                  {truncateId(log.entityId)}
                </span>
              </TooltipTrigger>
              <TooltipContent>{log.entityId}</TooltipContent>
            </Tooltip>
          </TableCell>
          <TableCell>
            <Badge variant={actionBadgeVariant(log.action)}>{log.action}</Badge>
          </TableCell>
          <TableCell>
            {log.changedByUserId ? (
              <Tooltip>
                <TooltipTrigger asChild>
                  <span
                    className={
                      userLabel
                        ? "text-xs cursor-default"
                        : "font-mono text-xs cursor-default"
                    }
                  >
                    {userLabel ?? truncateId(log.changedByUserId)}
                  </span>
                </TooltipTrigger>
                <TooltipContent>{log.changedByUserId}</TooltipContent>
              </Tooltip>
            ) : log.changedByApiKeyId ? (
              <Tooltip>
                <TooltipTrigger asChild>
                  <span className="font-mono text-xs cursor-default">
                    API: {truncateId(log.changedByApiKeyId)}
                  </span>
                </TooltipTrigger>
                <TooltipContent>{log.changedByApiKeyId}</TooltipContent>
              </Tooltip>
            ) : (
              // No user and no API key → not an HTTP-request write. These are
              // background-pipeline / seed writes; label them so they read as
              // intentional system activity rather than a capture failure.
              <span className="text-muted-foreground italic">System</span>
            )}
          </TableCell>
          <TableCell className="text-center">
            {hasChanges ? (
              <CollapsibleTrigger className="text-primary hover:underline cursor-pointer">
                {changes.length}
              </CollapsibleTrigger>
            ) : (
              <span className="text-muted-foreground">—</span>
            )}
          </TableCell>
        </TableRow>
        {hasChanges && (
          <CollapsibleContent asChild>
            <tr>
              <td colSpan={6} className="p-0">
                <div className="bg-muted/30 px-6 py-3 border-b">
                  {changes.map((c) => (
                    <FieldDiff
                      key={c.field}
                      fieldName={c.field}
                      oldValue={c.oldValue}
                      newValue={c.newValue}
                    />
                  ))}
                </div>
              </td>
            </tr>
          </CollapsibleContent>
        )}
      </>
    </Collapsible>
  );
}

const EMPTY_LABELS: Record<string, string> = {};

export function AuditLogTable({
  logs,
  isLoading,
  sortBy = null,
  sortDirection = "desc",
  onToggleSort,
  entityTypeLabels = EMPTY_LABELS,
  userLabels = EMPTY_LABELS,
}: AuditLogTableProps) {
  if (isLoading) {
    return (
      <div className="space-y-2">
        {Array.from({ length: 5 }).map((_, i) => (
          <Skeleton key={i} className="h-12 w-full" />
        ))}
      </div>
    );
  }

  if (logs.length === 0) {
    return (
      <div className="py-12 text-center text-muted-foreground">
        No audit log entries found.
      </div>
    );
  }

  return (
    <div className="rounded-md border">
      <Table>
        <TableHeader>
          <TableRow>
            {onToggleSort ? (
              <>
                <SortableTableHead
                  column="changedAt"
                  label="Timestamp"
                  currentSortBy={sortBy}
                  currentSortDirection={sortDirection}
                  onToggleSort={onToggleSort}
                />
                <SortableTableHead
                  column="entityType"
                  label="Entity Type"
                  currentSortBy={sortBy}
                  currentSortDirection={sortDirection}
                  onToggleSort={onToggleSort}
                />
                <SortableTableHead
                  column="entityId"
                  label="Entity ID"
                  currentSortBy={sortBy}
                  currentSortDirection={sortDirection}
                  onToggleSort={onToggleSort}
                />
                <SortableTableHead
                  column="action"
                  label="Action"
                  currentSortBy={sortBy}
                  currentSortDirection={sortDirection}
                  onToggleSort={onToggleSort}
                />
              </>
            ) : (
              <>
                <TableHead>Timestamp</TableHead>
                <TableHead>Entity Type</TableHead>
                <TableHead>Entity ID</TableHead>
                <TableHead>Action</TableHead>
              </>
            )}
            <TableHead>Changed By</TableHead>
            <TableHead className="text-center">Changes</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {logs.map((log) => (
            <AuditRow
              key={log.id}
              log={log}
              entityTypeLabels={entityTypeLabels}
              userLabels={userLabels}
            />
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
