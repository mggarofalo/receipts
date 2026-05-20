import { useState, useCallback, useMemo } from "react";
import { format } from "date-fns";
import { usePageTitle } from "@/hooks/usePageTitle";
import { useRecentAuditLogs } from "@/hooks/useAudit";
import { useServerPagination } from "@/hooks/useServerPagination";
import { useServerSort } from "@/hooks/useServerSort";
import type { AuditLog as AuditLogEntry } from "@/lib/audit-utils";
import { useEnumMetadata } from "@/hooks/useEnumMetadata";
import { AuditLogTable } from "@/components/AuditLogTable";
import { Pagination } from "@/components/Pagination";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { Icon, PageHead } from "@/components/primitives";
import { Calendar } from "@/components/ui/calendar";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import { Combobox } from "@/components/ui/combobox";
import { cn } from "@/lib/utils";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";

function csvField(value: string): string {
  if (value.includes(",") || value.includes('"') || value.includes("\n") || value.includes("\r")) {
    return `"${value.replace(/"/g, '""')}"`;
  }
  return value;
}

function exportToCsv(logs: AuditLogEntry[]) {
  const headers = [
    "Timestamp",
    "Entity Type",
    "Entity ID",
    "Action",
    "Changed By (User)",
    "Changed By (API Key)",
    "IP Address",
    "Changes JSON",
  ];
  const rows = logs.map((log) =>
    [
      log.changedAt,
      log.entityType,
      log.entityId,
      log.action,
      log.changedByUserId ?? "",
      log.changedByApiKeyId ?? "",
      log.ipAddress ?? "",
      log.changesJson,
    ]
      .map(csvField)
      .join(","),
  );
  const csv = [headers.join(","), ...rows].join("\n");
  const blob = new Blob([csv], { type: "text/csv" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = `audit-log-${new Date().toISOString().slice(0, 10)}.csv`;
  a.click();
  URL.revokeObjectURL(url);
}

function DateRangePicker({
  from,
  to,
  onFromChange,
  onToChange,
}: {
  from: Date | undefined;
  to: Date | undefined;
  onFromChange: (d: Date | undefined) => void;
  onToChange: (d: Date | undefined) => void;
}) {
  return (
    <div className="flex items-center gap-1">
      <Popover>
        <PopoverTrigger asChild>
          <Button
            variant="outline"
            size="sm"
            className={cn(
              "w-[120px] justify-start text-left font-normal",
              !from && "text-muted-foreground",
            )}
          >
            {from ? format(from, "MMM d, yyyy") : "From"}
          </Button>
        </PopoverTrigger>
        <PopoverContent className="w-auto p-0" align="start">
          <Calendar
            mode="single"
            selected={from}
            onSelect={onFromChange}
            disabled={(d) => (to ? d > to : false)}
          />
        </PopoverContent>
      </Popover>
      <span className="text-muted-foreground text-xs">—</span>
      <Popover>
        <PopoverTrigger asChild>
          <Button
            variant="outline"
            size="sm"
            className={cn(
              "w-[120px] justify-start text-left font-normal",
              !to && "text-muted-foreground",
            )}
          >
            {to ? format(to, "MMM d, yyyy") : "To"}
          </Button>
        </PopoverTrigger>
        <PopoverContent className="w-auto p-0" align="start">
          <Calendar
            mode="single"
            selected={to}
            onSelect={onToChange}
            disabled={(d) => (from ? d < from : false)}
          />
        </PopoverContent>
      </Popover>
      {(from || to) && (
        <Button
          variant="ghost"
          size="sm"
          onClick={() => {
            onFromChange(undefined);
            onToChange(undefined);
          }}
        >
          Clear
        </Button>
      )}
    </div>
  );
}

function AuditLog() {
  usePageTitle("Audit Log");
  const { entityTypes, entityTypeLabels, auditActions } = useEnumMetadata();
  const [search, setSearch] = useState("");
  const [entityTypeFilter, setEntityTypeFilter] = useState("all");
  const [actionFilter, setActionFilter] = useState("all");
  const [dateFrom, setDateFrom] = useState<Date | undefined>();
  const [dateTo, setDateTo] = useState<Date | undefined>();

  const debouncedSearch = useDebouncedValue(search, 300);

  const { sortBy, sortDirection, toggleSort } = useServerSort({
    defaultSortBy: "changedAt",
    defaultSortDirection: "desc",
  });
  const pagination = useServerPagination({ sortBy, sortDirection });

  // Reset pagination when filters change
  const handleEntityTypeChange = useCallback(
    (value: string) => {
      setEntityTypeFilter(value);
      pagination.resetPage();
    },
    [pagination],
  );

  const handleActionChange = useCallback(
    (value: string) => {
      setActionFilter(value);
      pagination.resetPage();
    },
    [pagination],
  );

  const handleDateFromChange = useCallback(
    (d: Date | undefined) => {
      setDateFrom(d);
      pagination.resetPage();
    },
    [pagination],
  );

  const handleDateToChange = useCallback(
    (d: Date | undefined) => {
      setDateTo(d);
      pagination.resetPage();
    },
    [pagination],
  );

  const entityTypeOptions = useMemo(
    () => [
      { value: "all", label: "All Types" },
      ...entityTypes.map((t) => ({ value: t.value, label: entityTypeLabels[t.value] ?? t.value })),
    ],
    [entityTypes, entityTypeLabels],
  );

  const actionOptions = useMemo(
    () => [
      { value: "all", label: "All Actions" },
      ...auditActions.map((a) => ({
        value: a.value,
        label: a.label,
      })),
    ],
    [auditActions],
  );

  const { data, total, isLoading } = useRecentAuditLogs({
    offset: pagination.offset,
    limit: pagination.limit,
    sortBy,
    sortDirection,
    entityType: entityTypeFilter !== "all" ? entityTypeFilter : null,
    action: actionFilter !== "all" ? actionFilter : null,
    search: debouncedSearch || null,
    dateFrom: dateFrom ? dateFrom.toISOString() : null,
    dateTo: dateTo ? dateTo.toISOString() : null,
  });

  const logs = (data ?? []) as AuditLogEntry[];

  return (
    <>
      <PageHead
        title="Audit log"
        sub={`${logs.length} ${logs.length === 1 ? "entry" : "entries"}`}
        actions={
          <button
            type="button"
            className="btn"
            onClick={() => exportToCsv(logs)}
            disabled={logs.length === 0}
          >
            <Icon.Upload /> Export CSV
          </button>
        }
      />
      <div className="space-y-4">

      <div className="flex items-center gap-3 flex-wrap">
        <Input
          placeholder="Search by entity ID..."
          aria-label="Search audit log by entity ID"
          value={search}
          onChange={(e) => {
            setSearch(e.target.value);
            pagination.resetPage();
          }}
          className="max-w-xs"
        />
        <Combobox
          options={entityTypeOptions}
          value={entityTypeFilter}
          onValueChange={handleEntityTypeChange}
          placeholder="Entity Type"
          searchPlaceholder="Search types..."
          className="w-[160px]"
          aria-label="Filter by entity type"
        />
        <Combobox
          options={actionOptions}
          value={actionFilter}
          onValueChange={handleActionChange}
          placeholder="Action"
          searchPlaceholder="Search actions..."
          className="w-[140px]"
          aria-label="Filter by action"
        />
        <DateRangePicker
          from={dateFrom}
          to={dateTo}
          onFromChange={handleDateFromChange}
          onToChange={handleDateToChange}
        />
      </div>

      <AuditLogTable
        logs={logs}
        isLoading={isLoading}
        sortBy={sortBy}
        sortDirection={sortDirection}
        onToggleSort={toggleSort}
        entityTypeLabels={entityTypeLabels}
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
    </>
  );
}

export default AuditLog;
