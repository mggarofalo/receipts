import { useState } from "react";
import { useNavigate } from "react-router";
import {
  useDuplicateDetectionReport,
  type MatchOn,
  type LocationTolerance,
  type TotalTolerance,
  type DuplicateDetectionParams,
} from "@/hooks/useDuplicateDetectionReport";
import { useDeleteReceipts } from "@/hooks/useReceipts";
import { formatCurrency } from "@/lib/format";
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

export default function DuplicateDetection() {
  const navigate = useNavigate();
  const [matchOn, setMatchOn] = useState<MatchOn>("dateAndLocation");
  const [locationTolerance, setLocationTolerance] =
    useState<LocationTolerance>("exact");
  const [totalTolerance, setTotalTolerance] = useState<TotalTolerance>(0);
  const [deleteTarget, setDeleteTarget] = useState<{
    id: string;
    location: string;
  } | null>(null);

  const params: DuplicateDetectionParams = {
    matchOn,
    locationTolerance,
    totalTolerance,
  };

  const { data, isLoading, isError } = useDuplicateDetectionReport(params);
  const deleteReceipts = useDeleteReceipts();

  const showLocationTolerance =
    matchOn === "dateAndLocation" || matchOn === "dateAndLocationAndTotal";
  const showTotalTolerance =
    matchOn === "dateAndTotal" || matchOn === "dateAndLocationAndTotal";

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

  if (isError) {
    return (
      <div className="rounded-lg border border-destructive p-6 text-center">
        <p className="text-destructive">
          Failed to load duplicate detection report.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end gap-4 rounded-lg border p-4">
        <div className="space-y-1">
          <Label htmlFor="match-on-select">Match On</Label>
          <Select
            value={matchOn}
            onValueChange={(v) => setMatchOn(v as MatchOn)}
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
              onValueChange={(v) =>
                setLocationTolerance(v as LocationTolerance)
              }
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
              onValueChange={(v) =>
                setTotalTolerance(Number(v) as TotalTolerance)
              }
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
      </div>

      {!data || data.groupCount === 0 ? (
        <div className="rounded-lg border p-6 text-center">
          <h2 className="card-title">No Duplicates Found</h2>
          <p className="mt-2 text-muted-foreground">
            No potential duplicate receipts were detected with the current
            settings.
          </p>
        </div>
      ) : (
        <>
          <div className="flex gap-6 rounded-lg border p-4">
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
          </div>

          <div className="space-y-6">
            {data.groups.map((group) => (
              <Card key={group.matchKey}>
                <CardHeader>
                  <CardTitle className="text-base">{group.matchKey}</CardTitle>
                  <CardDescription>
                    {group.receipts.length} receipts in this group
                  </CardDescription>
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
                        (o) => o.transactionTotal !== receipt.transactionTotal,
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
                                {receipt.date}
                              </p>
                              <p
                                className="text-sm font-medium"
                                style={
                                  totalDiffers
                                    ? { color: "var(--warn-ink)" }
                                    : undefined
                                }
                              >
                                {formatCurrency(Number(receipt.transactionTotal ?? 0))}
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
            ))}
          </div>
        </>
      )}

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
