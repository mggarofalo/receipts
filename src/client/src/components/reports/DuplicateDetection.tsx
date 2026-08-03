import { useState } from "react";
import { useNavigate } from "react-router";
import {
  useDuplicateDetectionReport,
  type MatchOn,
  type LocationTolerance,
  type TotalTolerance,
  type DuplicateDetectionParams,
} from "@/hooks/useDuplicateDetectionReport";
import {
  useAcceptedDuplicates,
  useAcceptDuplicateGroup,
  useUnacceptDuplicateGroup,
} from "@/hooks/useDuplicateAcceptance";
import { useDeleteReceipts } from "@/hooks/useReceipts";
import { useCsvExport } from "@/hooks/useCsvExport";
import { useReportSearchParams } from "@/hooks/useReportSearchParams";
import { csvFilename } from "@/lib/export-csv";
import { formatCurrency, formatDate } from "@/lib/format";
import {
  parseBoolParam,
  parseEnumParam,
  parseNumberEnumParam,
} from "@/lib/report-params";
import { Skeleton } from "@/components/ui/skeleton";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardHeader,
  CardTitle,
  CardDescription,
  CardContent,
} from "@/components/ui/card";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";

interface DuplicateReceiptView {
  receiptId: string;
  location: string;
  date: string;
  transactionTotal: number;
}

/**
 * Stable identity for a group: its sorted receipt IDs. Used for React keys and for matching a
 * group against the in-flight mutation. Deliberately NOT `matchKey` — that is a display string
 * derived from the current tolerance settings, so two clusters whose seed totals differ by less
 * than half a cent render the same label and would collide as keys (RECEIPTS-834).
 */
function groupKey(receiptIds: readonly string[] | undefined): string | null {
  if (!receiptIds || receiptIds.length === 0) return null;
  return [...receiptIds].sort().join("|");
}

/**
 * True when `key` identifies the group whose mutation is in flight. Both arguments can be null —
 * `pendingKey` when nothing is pending, `key` when a group somehow arrived with no receipts — and
 * null must never match null, or an identity-less group would sit permanently disabled at idle.
 */
function isPendingGroup(pendingKey: string | null, key: string | null): boolean {
  return key !== null && pendingKey === key;
}

/**
 * Human-readable name for a group, used to disambiguate the accessible names of the per-group
 * buttons. Without it every group repeats the same "Undo" / "Not duplicates" label, which a screen
 * reader surfaces as a list of identical controls (WCAG 2.4.6, 4.1.2).
 */
function describeGroup(receipts: readonly DuplicateReceiptView[]): string {
  const first = receipts[0];
  return first
    ? `${formatDate(first.date)} at ${first.location}`
    : "this group";
}

const MATCH_ON_VALUES = [
  "dateAndLocation",
  "dateAndTotal",
  "dateAndLocationAndTotal",
] as const;
const LOCATION_TOLERANCE_VALUES = ["exact", "normalized"] as const;
const TOTAL_TOLERANCE_VALUES = [0, 0.01, 0.05, 0.1, 0.5, 1] as const;

interface DuplicateDetectionUrlParams {
  matchOn: MatchOn;
  locationTolerance: LocationTolerance;
  totalTolerance: TotalTolerance;
  includeAccepted: boolean;
}

function parseDuplicateDetectionParams(
  searchParams: URLSearchParams,
): DuplicateDetectionUrlParams {
  return {
    matchOn: parseEnumParam(
      searchParams.get("matchOn"),
      MATCH_ON_VALUES,
      "dateAndLocation",
    ),
    locationTolerance: parseEnumParam(
      searchParams.get("locationTolerance"),
      LOCATION_TOLERANCE_VALUES,
      "exact",
    ),
    totalTolerance: parseNumberEnumParam(
      searchParams.get("totalTolerance"),
      TOTAL_TOLERANCE_VALUES,
      0,
    ),
    includeAccepted: parseBoolParam(searchParams.get("includeAccepted"), false),
  };
}

export default function DuplicateDetection() {
  const navigate = useNavigate();
  const [urlParams, updateParams] = useReportSearchParams(
    parseDuplicateDetectionParams,
  );
  const { matchOn, locationTolerance, totalTolerance, includeAccepted } =
    urlParams;
  const [deleteTarget, setDeleteTarget] = useState<{
    id: string;
    location: string;
  } | null>(null);

  const params: DuplicateDetectionParams = {
    matchOn,
    locationTolerance,
    totalTolerance,
    includeAccepted: includeAccepted || undefined,
  };

  const { data, isLoading, isError } = useDuplicateDetectionReport(params);
  const accepted = useAcceptedDuplicates();
  const acceptGroup = useAcceptDuplicateGroup();
  const unacceptGroup = useUnacceptDuplicateGroup();
  const deleteReceipts = useDeleteReceipts();
  const { exportCsv, isExporting } = useCsvExport();

  function handleExport() {
    exportCsv({
      filename: csvFilename("duplicate-detection"),
      headers: ["Match Key", "Location", "Date", "Transaction Total", "Receipt ID"],
      rows: (data?.groups ?? []).flatMap((group) =>
        group.receipts.map((receipt) => [
          group.matchKey,
          receipt.location,
          receipt.date,
          receipt.transactionTotal,
          receipt.receiptId,
        ]),
      ),
    });
  }

  const showLocationTolerance =
    matchOn === "dateAndLocation" || matchOn === "dateAndLocationAndTotal";
  const showTotalTolerance =
    matchOn === "dateAndTotal" || matchOn === "dateAndLocationAndTotal";

  const acceptedGroups = accepted.data?.groups ?? [];

  // Scope the in-flight disable to the group actually being mutated. Both mutation hooks are
  // instantiated once for the whole component, so a bare `isPending` would grey out every group's
  // button at once. `variables` holds the receipt IDs the running mutation was called with.
  const pendingAcceptKey = acceptGroup.isPending
    ? groupKey(acceptGroup.variables)
    : null;
  const pendingUnacceptKey = unacceptGroup.isPending
    ? groupKey(unacceptGroup.variables)
    : null;

  function handleDelete() {
    if (!deleteTarget) return;
    deleteReceipts.mutate([deleteTarget.id], {
      onSuccess: () => setDeleteTarget(null),
    });
  }

  if (isLoading) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-16 w-full rounded-lg" />
        <Skeleton className="h-64 w-full rounded-lg" />
      </div>
    );
  }

  // A failed report is NOT an early return. The accepted-groups section is served by a separate
  // query, and it is the only place to undo an acceptance — bailing out here left a user whose
  // report happened to fail unable to see or reverse anything they had already accepted.
  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end gap-4 rounded-lg border p-4">
        <div className="space-y-1">
          <Label htmlFor="match-on-select">Match On</Label>
          <Select
            value={matchOn}
            onValueChange={(v) => updateParams({ matchOn: v })}
          >
            <SelectTrigger id="match-on-select" className="w-[200px]">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="dateAndLocation">Date & Location</SelectItem>
              <SelectItem value="dateAndTotal">Date & Total</SelectItem>
              <SelectItem value="dateAndLocationAndTotal">
                Date, Location & Total
              </SelectItem>
            </SelectContent>
          </Select>
        </div>

        {showLocationTolerance && (
          <div className="space-y-1">
            <Label htmlFor="location-tolerance-select">Location Matching</Label>
            <Select
              value={locationTolerance}
              onValueChange={(v) => updateParams({ locationTolerance: v })}
            >
              <SelectTrigger id="location-tolerance-select" className="w-[160px]">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="exact">Exact</SelectItem>
                <SelectItem value="normalized">Normalized</SelectItem>
              </SelectContent>
            </Select>
          </div>
        )}

        {showTotalTolerance && (
          <div className="space-y-1">
            <Label htmlFor="total-tolerance-select">Total Tolerance</Label>
            <Select
              value={String(totalTolerance)}
              onValueChange={(v) => updateParams({ totalTolerance: v })}
            >
              <SelectTrigger id="total-tolerance-select" className="w-[140px]">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="0">Exact ($0.00)</SelectItem>
                <SelectItem value="0.01">$0.01</SelectItem>
                <SelectItem value="0.05">$0.05</SelectItem>
                <SelectItem value="0.1">$0.10</SelectItem>
                <SelectItem value="0.5">$0.50</SelectItem>
                <SelectItem value="1">$1.00</SelectItem>
              </SelectContent>
            </Select>
          </div>
        )}

        <div className="flex items-center gap-2 pb-1">
          <Switch
            id="include-accepted-switch"
            checked={includeAccepted}
            onCheckedChange={(checked) =>
              updateParams({ includeAccepted: checked || null })
            }
          />
          <Label htmlFor="include-accepted-switch">Show accepted groups</Label>
        </div>
      </div>

      {isError ? (
        <div className="rounded-lg border border-destructive p-6 text-center">
          <p className="text-destructive">
            Failed to load duplicate detection report.
          </p>
        </div>
      ) : !data || data.groupCount === 0 ? (
        <div className="rounded-lg border p-6 text-center">
          <h2 className="card-title">No Duplicates Found</h2>
          <p className="mt-2 text-muted-foreground">
            No potential duplicate receipts were detected with the current
            settings.
          </p>
        </div>
      ) : (
        <>
          <div className="flex items-center gap-6 rounded-lg border p-4">
            <div>
              <p className="card-sub">Duplicate Groups</p>
              <p className="money-med">{data.groupCount}</p>
            </div>
            <div>
              <p className="card-sub">
                Total Duplicate Receipts
              </p>
              <p className="money-med">
                {data.totalDuplicateReceipts}
              </p>
            </div>
            <Button
              variant="outline"
              size="sm"
              className="ml-auto"
              disabled={isExporting}
              onClick={handleExport}
            >
              {isExporting ? "Exporting..." : "Export CSV"}
            </Button>
          </div>

          <div className="space-y-6">
            {data.groups.map((group) => {
              const receiptIds = group.receipts.map((r) => r.receiptId);
              const key = groupKey(receiptIds);
              return (
                <Card key={key}>
                  <CardHeader>
                    <div className="flex flex-wrap items-start justify-between gap-2">
                      <div className="min-w-0 space-y-1">
                        <CardTitle className="text-base">
                          {group.matchKey}
                        </CardTitle>
                        <CardDescription>
                          {group.receipts.length} receipts in this group
                        </CardDescription>
                      </div>
                      <div className="flex items-center gap-2">
                        {group.isAccepted && (
                          <Badge variant="secondary">Accepted</Badge>
                        )}
                        {group.isAccepted ? (
                          <Button
                            variant="outline"
                            size="sm"
                            aria-label={`Report ${describeGroup(group.receipts)} again`}
                            disabled={isPendingGroup(pendingUnacceptKey, key)}
                            onClick={() => unacceptGroup.mutate(receiptIds)}
                          >
                            Report again
                          </Button>
                        ) : (
                          <Button
                            variant="outline"
                            size="sm"
                            aria-label={`Mark ${describeGroup(group.receipts)} as not duplicates`}
                            disabled={isPendingGroup(pendingAcceptKey, key)}
                            onClick={() => acceptGroup.mutate(receiptIds)}
                          >
                            Not duplicates
                          </Button>
                        )}
                      </div>
                    </div>
                  </CardHeader>
                  <CardContent>
                    <div className="grid gap-3 sm:grid-cols-2">
                      {group.receipts.map((receipt, index) => {
                        const others = group.receipts.filter(
                          (_, i) => i !== index,
                        );
                        const locationDiffers = others.some(
                          (o) => o.location !== receipt.location,
                        );
                        const totalDiffers = others.some(
                          (o) =>
                            o.transactionTotal !== receipt.transactionTotal,
                        );

                        return (
                          <div
                            key={receipt.receiptId}
                            className="rounded-md border p-3 space-y-2"
                          >
                            <div className="flex items-start justify-between gap-2">
                              <div className="space-y-1 min-w-0">
                                <p
                                  className="text-sm font-medium truncate"
                                  style={
                                    locationDiffers
                                      ? { color: "var(--warn-ink)" }
                                      : undefined
                                  }
                                >
                                  {receipt.location}
                                </p>
                                <p className="text-sm text-muted-foreground">
                                  {formatDate(receipt.date)}
                                </p>
                                <p
                                  className="text-sm font-medium"
                                  style={
                                    totalDiffers
                                      ? { color: "var(--warn-ink)" }
                                      : undefined
                                  }
                                >
                                  {formatCurrency(
                                    Number(receipt.transactionTotal ?? 0),
                                  )}
                                </p>
                              </div>
                              <div className="flex flex-col gap-1">
                                {locationDiffers && (
                                  <Badge variant="outline" className="text-xs">
                                    Location differs
                                  </Badge>
                                )}
                                {totalDiffers && (
                                  <Badge variant="outline" className="text-xs">
                                    Total differs
                                  </Badge>
                                )}
                              </div>
                            </div>
                            <div className="flex gap-2">
                              <Button
                                variant="outline"
                                size="sm"
                                onClick={() =>
                                  navigate(`/receipts/${receipt.receiptId}`)
                                }
                              >
                                View
                              </Button>
                              <Button
                                variant="destructive"
                                size="sm"
                                onClick={() =>
                                  setDeleteTarget({
                                    id: receipt.receiptId,
                                    location: receipt.location,
                                  })
                                }
                              >
                                Delete
                              </Button>
                            </div>
                          </div>
                        );
                      })}
                    </div>
                  </CardContent>
                </Card>
              );
            })}
          </div>
        </>
      )}

      <AcceptedDuplicatesSection
        groups={acceptedGroups}
        isLoading={accepted.isLoading}
        isError={accepted.isError}
        pendingUndoKey={pendingUnacceptKey}
        onUndo={(receiptIds) => unacceptGroup.mutate(receiptIds)}
      />

      <AlertDialog
        open={deleteTarget !== null}
        onOpenChange={(open) => {
          if (!open) setDeleteTarget(null);
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete Receipt</AlertDialogTitle>
            <AlertDialogDescription>
              Are you sure you want to delete the receipt from &quot;
              {deleteTarget?.location}&quot;? This action cannot be undone.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={handleDelete}>Delete</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}

interface AcceptedDuplicatesSectionProps {
  groups: {
    receipts: DuplicateReceiptView[];
    /** Every member, including ones whose receipt was deleted and so are not in `receipts`. */
    memberReceiptIds: string[];
    acceptedAt: string;
  }[];
  isLoading: boolean;
  isError: boolean;
  /** Identity of the group whose undo is in flight, or null when none is. */
  pendingUndoKey: string | null;
  onUndo: (receiptIds: string[]) => void;
}

function AcceptedDuplicatesSection({
  groups,
  isLoading,
  isError,
  pendingUndoKey,
  onUndo,
}: AcceptedDuplicatesSectionProps) {
  return (
    <section className="space-y-3 rounded-lg border p-4">
      <div>
        <h2 className="card-title">Accepted Groups</h2>
        <p className="text-sm text-muted-foreground">
          Groups you marked as genuinely separate receipts. They are hidden from
          the report above until you undo them.
        </p>
      </div>

      {isLoading && <Skeleton className="h-16 w-full rounded-lg" />}

      {!isLoading && isError && (
        <p className="text-destructive">Failed to load accepted groups.</p>
      )}

      {!isLoading && !isError && groups.length === 0 && (
        <p className="text-muted-foreground">
          No groups have been accepted yet.
        </p>
      )}

      {!isLoading && !isError && groups.length > 0 && (
        <ul className="space-y-3">
          {groups.map((group) => {
            // Undo submits the COMPLETE member set, not the displayed subset. A group with a
            // soft-deleted member renders short, and un-accepting only the survivors would leave
            // the pairs touching that member stored with nothing able to reach them again.
            const receiptIds = group.memberReceiptIds;
            const key = groupKey(receiptIds);
            return (
              <li
                key={key}
                className="flex flex-wrap items-start justify-between gap-3 rounded-md border p-3"
              >
                <div className="min-w-0 space-y-1">
                  <p className="text-sm font-medium">
                    {group.receipts.length} receipts
                  </p>
                  <ul className="text-sm text-muted-foreground">
                    {group.receipts.map((receipt) => (
                      <li key={receipt.receiptId} className="truncate">
                        {formatDate(receipt.date)} — {receipt.location} —{" "}
                        {formatCurrency(Number(receipt.transactionTotal ?? 0))}
                      </li>
                    ))}
                  </ul>
                  <p className="text-xs text-muted-foreground">
                    Accepted {formatDate(group.acceptedAt)}
                  </p>
                </div>
                <Button
                  variant="outline"
                  size="sm"
                  aria-label={`Undo acceptance of ${describeGroup(group.receipts)}`}
                  disabled={isPendingGroup(pendingUndoKey, key)}
                  onClick={() => onUndo(receiptIds)}
                >
                  Undo
                </Button>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
