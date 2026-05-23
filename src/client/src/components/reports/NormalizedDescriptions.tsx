import { useMemo, useState } from "react";
import { useNormalizedDescriptions } from "@/hooks/useNormalizedDescriptions";
import {
  useMergeMutation,
  useSplitMutation,
  useUpdateStatusMutation,
} from "@/hooks/useNormalizedDescriptionActions";
import {
  useSettings,
  useUpdateSettingsMutation,
  useTestMatchMutation,
  usePreviewImpactMutation,
} from "@/hooks/useNormalizedDescriptionSettings";
import { useReceiptItems } from "@/hooks/useReceiptItems";
import { usePermission } from "@/hooks/usePermission";
import { formatDecimal } from "@/lib/format";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsList, TabsTrigger, TabsContent } from "@/components/ui/tabs";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import type { components } from "@/generated/api";

type NormalizedDescription =
  components["schemas"]["NormalizedDescriptionResponse"];

type ReceiptItem = {
  id: string;
  description: string;
  normalizedDescriptionId?: string | null;
  normalizedDescriptionName?: string | null;
};

type TabKey = "review" | "registry" | "settings";

export default function NormalizedDescriptions() {
  const { isAdmin } = usePermission();
  const [tab, setTab] = useState<TabKey>("review");

  return (
    <Tabs
      value={tab}
      onValueChange={(v) => setTab(v as TabKey)}
      className="space-y-4"
    >
      <TabsList>
        <TabsTrigger value="review">Review Queue</TabsTrigger>
        <TabsTrigger value="registry">Registry</TabsTrigger>
        {isAdmin() && <TabsTrigger value="settings">Settings</TabsTrigger>}
      </TabsList>
      <TabsContent value="review">
        <ReviewQueueTab />
      </TabsContent>
      <TabsContent value="registry">
        <RegistryTab />
      </TabsContent>
      {isAdmin() && (
        <TabsContent value="settings">
          <SettingsTab />
        </TabsContent>
      )}
    </Tabs>
  );
}

function ReviewQueueTab() {
  const { data: pendingData, isLoading, isError } = useNormalizedDescriptions(
    "PendingReview",
  );
  const { data: activeData } = useNormalizedDescriptions("Active");
  const updateStatus = useUpdateStatusMutation();
  const [mergeTarget, setMergeTarget] = useState<NormalizedDescription | null>(
    null,
  );
  const [splitTarget, setSplitTarget] = useState<NormalizedDescription | null>(
    null,
  );

  const pending = useMemo(() => {
    const items = pendingData?.items ?? [];
    return [...items].sort(
      (a, b) =>
        new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime(),
    );
  }, [pendingData?.items]);

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
        <p className="text-destructive">Failed to load review queue.</p>
      </div>
    );
  }

  if (pending.length === 0) {
    return (
      <div className="rounded-lg border p-6 text-center">
        <h2 className="card-title">Review Queue Empty</h2>
        <p className="mt-2 text-muted-foreground">
          No descriptions are awaiting review right now.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div className="flex gap-6 rounded-lg border p-4">
        <div>
          <p className="card-sub">Pending Review</p>
          <p className="money-med">
            {pendingData?.totalCount ?? pending.length}
          </p>
        </div>
      </div>

      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Canonical Name</TableHead>
            <TableHead>Status</TableHead>
            <TableHead>Created</TableHead>
            <TableHead className="text-right">Actions</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {pending.map((row) => (
            <TableRow key={row.id}>
              <TableCell className="font-medium">{row.canonicalName}</TableCell>
              <TableCell>
                <Badge variant="secondary">Pending Review</Badge>
              </TableCell>
              <TableCell>
                <span className="text-sm text-muted-foreground">
                  {new Date(row.createdAt).toLocaleDateString()}
                </span>
              </TableCell>
              <TableCell className="text-right space-x-2">
                <Button
                  variant="outline"
                  size="sm"
                  disabled={updateStatus.isPending}
                  onClick={() =>
                    updateStatus.mutate({ id: row.id, status: "active" })
                  }
                >
                  Approve
                </Button>
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => setMergeTarget(row)}
                >
                  Merge
                </Button>
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => setSplitTarget(row)}
                >
                  Split
                </Button>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>

      <MergeDialog
        source={mergeTarget}
        candidates={activeData?.items ?? []}
        onClose={() => setMergeTarget(null)}
      />
      <SplitDialog
        source={splitTarget}
        onClose={() => setSplitTarget(null)}
      />
    </div>
  );
}

interface MergeDialogProps {
  source: NormalizedDescription | null;
  candidates: NormalizedDescription[];
  onClose: () => void;
}

function MergeDialog({ source, candidates, onClose }: MergeDialogProps) {
  const merge = useMergeMutation();
  const [targetId, setTargetId] = useState<string | undefined>();
  const [search, setSearch] = useState("");

  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    const list = candidates.filter((c) => c.id !== source?.id);
    if (!q) return list.slice(0, 50);
    return list
      .filter((c) => c.canonicalName.toLowerCase().includes(q))
      .slice(0, 50);
  }, [candidates, search, source?.id]);

  function handleClose() {
    setTargetId(undefined);
    setSearch("");
    onClose();
  }

  function handleConfirm() {
    if (!source || !targetId) return;
    merge.mutate(
      { id: targetId, discardId: source.id },
      { onSuccess: () => handleClose() },
    );
  }

  return (
    <Dialog
      open={source !== null}
      onOpenChange={(open) => {
        if (!open) handleClose();
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Merge Into Active Entry</DialogTitle>
          <DialogDescription>
            Pick the canonical row to keep. All receipt items currently linked
            to &quot;{source?.canonicalName}&quot; will be re-pointed at the
            chosen row, and this pending-review entry will be deleted.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3">
          <div>
            <Label htmlFor="merge-search">Search active entries</Label>
            <Input
              id="merge-search"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Start typing a canonical name…"
              className="mt-1"
            />
          </div>
          <div className="max-h-64 overflow-y-auto rounded border">
            {filtered.length === 0 ? (
              <p className="p-4 text-sm text-muted-foreground">
                No matching active entries.
              </p>
            ) : (
              <ul className="divide-y">
                {filtered.map((c) => (
                  <li key={c.id}>
                    <label className="flex cursor-pointer items-center gap-2 p-2 text-sm hover:bg-muted/50">
                      <input
                        type="radio"
                        name="merge-target"
                        value={c.id}
                        checked={targetId === c.id}
                        onChange={() => setTargetId(c.id)}
                      />
                      <span className="font-medium">{c.canonicalName}</span>
                    </label>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={handleClose}>
            Cancel
          </Button>
          <Button
            onClick={handleConfirm}
            disabled={!targetId || merge.isPending}
          >
            {merge.isPending ? "Merging…" : "Merge"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

interface SplitDialogProps {
  source: NormalizedDescription | null;
  onClose: () => void;
}

function SplitDialog({ source, onClose }: SplitDialogProps) {
  const split = useSplitMutation();
  const [selectedItemId, setSelectedItemId] = useState<string | undefined>();
  // Pull a broad page of receipt items and filter client-side by normalized
  // description id. The list endpoint doesn't expose a dedicated filter today,
  // so this keeps the dialog functional without requiring a new API.
  const { data: items, isLoading } = useReceiptItems(0, 200);

  const linked = useMemo(() => {
    if (!source || !items) return [] as ReceiptItem[];
    return (items as ReceiptItem[]).filter(
      (i) => i.normalizedDescriptionId === source.id,
    );
  }, [items, source]);

  function handleClose() {
    setSelectedItemId(undefined);
    onClose();
  }

  function handleConfirm() {
    if (!source || !selectedItemId) return;
    split.mutate(
      { id: source.id, receiptItemId: selectedItemId },
      { onSuccess: () => handleClose() },
    );
  }

  return (
    <Dialog
      open={source !== null}
      onOpenChange={(open) => {
        if (!open) handleClose();
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Split Out a Receipt Item</DialogTitle>
          <DialogDescription>
            Pick a receipt item currently linked to &quot;
            {source?.canonicalName}&quot;. It will be detached into a brand-new
            normalized description that keeps the item&apos;s raw description.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3">
          {isLoading ? (
            <Skeleton className="h-24 w-full rounded" />
          ) : linked.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              No linked receipt items found in the most recent 200 items. Split
              from the receipt detail page if the item is older.
            </p>
          ) : (
            <ul className="max-h-64 overflow-y-auto divide-y rounded border">
              {linked.map((item) => (
                <li key={item.id}>
                  <label className="flex cursor-pointer items-center gap-2 p-2 text-sm hover:bg-muted/50">
                    <input
                      type="radio"
                      name="split-target"
                      value={item.id}
                      checked={selectedItemId === item.id}
                      onChange={() => setSelectedItemId(item.id)}
                    />
                    <span>{item.description}</span>
                  </label>
                </li>
              ))}
            </ul>
          )}
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={handleClose}>
            Cancel
          </Button>
          <Button
            onClick={handleConfirm}
            disabled={!selectedItemId || split.isPending}
          >
            {split.isPending ? "Splitting…" : "Split"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function RegistryTab() {
  const { data, isLoading, isError } = useNormalizedDescriptions("Active");
  const [search, setSearch] = useState("");

  const filtered = useMemo(() => {
    const items = data?.items ?? [];
    const q = search.trim().toLowerCase();
    if (!q) return items;
    return items.filter((i) => i.canonicalName.toLowerCase().includes(q));
  }, [data?.items, search]);

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
        <p className="text-destructive">Failed to load registry.</p>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div className="flex items-end gap-4">
        <div className="flex-1 max-w-md">
          <Label htmlFor="registry-search">Search</Label>
          <Input
            id="registry-search"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Filter by canonical name…"
            className="mt-1"
          />
        </div>
        <div className="text-right">
          <p className="card-sub">Active Entries</p>
          <p className="money-med">{data?.totalCount ?? 0}</p>
        </div>
      </div>
      {filtered.length === 0 ? (
        <div className="rounded-lg border p-6 text-center">
          <p className="text-muted-foreground">
            {search
              ? "No active entries match your search."
              : "No active entries yet."}
          </p>
        </div>
      ) : (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Canonical Name</TableHead>
              <TableHead>Created</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {filtered.map((row) => (
              <TableRow key={row.id}>
                <TableCell className="font-medium">
                  {row.canonicalName}
                </TableCell>
                <TableCell>
                  <span className="text-sm text-muted-foreground">
                    {new Date(row.createdAt).toLocaleDateString()}
                  </span>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </div>
  );
}

function SettingsTab() {
  const settings = useSettings();

  if (settings.isLoading) {
    return <Skeleton className="h-48 w-full rounded-lg" />;
  }

  if (settings.isError || !settings.data) {
    return (
      <div className="rounded-lg border border-destructive p-6 text-center">
        <p className="text-destructive">Failed to load settings.</p>
      </div>
    );
  }

  return (
    <SettingsForm
      initialAutoAccept={settings.data.autoAcceptThreshold}
      initialPendingReview={settings.data.pendingReviewThreshold}
    />
  );
}

interface SettingsFormProps {
  initialAutoAccept: number;
  initialPendingReview: number;
}

function SettingsForm({
  initialAutoAccept,
  initialPendingReview,
}: SettingsFormProps) {
  const updateSettings = useUpdateSettingsMutation();
  const previewImpact = usePreviewImpactMutation();
  const testMatch = useTestMatchMutation();

  const [autoAccept, setAutoAccept] = useState(() => String(initialAutoAccept));
  const [pendingReview, setPendingReview] = useState(() =>
    String(initialPendingReview),
  );
  const [testDescription, setTestDescription] = useState("");
  const [topN, setTopN] = useState(5);

  const autoVal = parseFloat(autoAccept);
  const pendingVal = parseFloat(pendingReview);
  const autoValid = Number.isFinite(autoVal) && autoVal >= 0 && autoVal <= 1;
  const pendingValid =
    Number.isFinite(pendingVal) && pendingVal >= 0 && pendingVal <= 1;
  const orderValid = autoValid && pendingValid && pendingVal < autoVal;
  const canSubmit = autoValid && pendingValid && orderValid;

  function validationMessage() {
    if (!autoValid || !pendingValid) {
      return "Thresholds must be numbers between 0 and 1.";
    }
    if (!orderValid) {
      return "Pending-review threshold must be strictly less than the auto-accept threshold.";
    }
    return null;
  }

  function handleSave() {
    if (!canSubmit) return;
    updateSettings.mutate({
      autoAcceptThreshold: autoVal,
      pendingReviewThreshold: pendingVal,
    });
  }

  function handlePreview() {
    if (!canSubmit) return;
    previewImpact.mutate({
      autoAcceptThreshold: autoVal,
      pendingReviewThreshold: pendingVal,
    });
  }

  function handleTest() {
    const trimmed = testDescription.trim();
    if (!trimmed) return;
    testMatch.mutate({
      description: trimmed,
      topN,
      autoAcceptThresholdOverride: autoValid ? autoVal : undefined,
      pendingReviewThresholdOverride: pendingValid ? pendingVal : undefined,
    });
  }

  const validationError = validationMessage();

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader>
          <CardTitle>Thresholds</CardTitle>
          <CardDescription>
            Auto-accept is the similarity score at which the resolver re-uses an
            existing canonical entry. Pending-review is the floor for flagging a
            new description for admin review. Both are between 0 and 1, and
            pending-review must be strictly less than auto-accept.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <div>
              <Label htmlFor="auto-accept-input">Auto-Accept Threshold</Label>
              <Input
                id="auto-accept-input"
                type="number"
                min={0}
                max={1}
                step={0.01}
                value={autoAccept}
                onChange={(e) => setAutoAccept(e.target.value)}
                className="mt-1"
                aria-invalid={!autoValid}
              />
            </div>
            <div>
              <Label htmlFor="pending-review-input">
                Pending-Review Threshold
              </Label>
              <Input
                id="pending-review-input"
                type="number"
                min={0}
                max={1}
                step={0.01}
                value={pendingReview}
                onChange={(e) => setPendingReview(e.target.value)}
                className="mt-1"
                aria-invalid={!pendingValid || !orderValid}
              />
            </div>
          </div>
          {validationError && (
            <p
              role="alert"
              className="text-sm text-destructive"
              data-testid="threshold-validation-error"
            >
              {validationError}
            </p>
          )}
          <div className="flex gap-2">
            <Button
              onClick={handleSave}
              disabled={!canSubmit || updateSettings.isPending}
            >
              {updateSettings.isPending ? "Saving…" : "Save"}
            </Button>
            <Button
              variant="outline"
              onClick={handlePreview}
              disabled={!canSubmit || previewImpact.isPending}
            >
              {previewImpact.isPending ? "Computing…" : "Preview impact"}
            </Button>
          </div>
          {previewImpact.data && (
            <div
              className="rounded border p-3 text-sm"
              data-testid="preview-impact-panel"
            >
              <h3 className="card-title">Projected impact</h3>
              <div className="mt-2 grid grid-cols-3 gap-4">
                <div>
                  <p className="text-xs text-muted-foreground">Auto-accepted</p>
                  <p>
                    <span>{previewImpact.data.current.autoAccepted}</span>
                    {" \u2192 "}
                    <strong>{previewImpact.data.proposed.autoAccepted}</strong>
                  </p>
                </div>
                <div>
                  <p className="text-xs text-muted-foreground">
                    Pending review
                  </p>
                  <p>
                    <span>{previewImpact.data.current.pendingReview}</span>
                    {" \u2192 "}
                    <strong>{previewImpact.data.proposed.pendingReview}</strong>
                  </p>
                </div>
                <div>
                  <p className="text-xs text-muted-foreground">Unresolved</p>
                  <p>
                    <span>{previewImpact.data.current.unresolved}</span>
                    {" \u2192 "}
                    <strong>{previewImpact.data.proposed.unresolved}</strong>
                  </p>
                </div>
              </div>
              <p className="mt-2 text-xs text-muted-foreground">
                Auto-to-pending: {previewImpact.data.deltas.autoToPending} |
                Pending-to-auto: {previewImpact.data.deltas.pendingToAuto} |
                Unresolved-to-auto: {previewImpact.data.deltas.unresolvedToAuto}{" "}
                | Unresolved-to-pending:{" "}
                {previewImpact.data.deltas.unresolvedToPending}
              </p>
            </div>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Test a Description</CardTitle>
          <CardDescription>
            Run any description through the classifier with the currently-edited
            thresholds (or the live values when the inputs are blank) to see
            which canonical rows would match.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="flex flex-wrap items-end gap-3">
            <div className="flex-1 min-w-[220px]">
              <Label htmlFor="test-description-input">Description</Label>
              <Input
                id="test-description-input"
                value={testDescription}
                onChange={(e) => setTestDescription(e.target.value)}
                placeholder="e.g. banana"
                className="mt-1"
              />
            </div>
            <div className="w-28">
              <Label htmlFor="test-topn-input">Top N</Label>
              <Select
                value={String(topN)}
                onValueChange={(v) => setTopN(Number(v))}
              >
                <SelectTrigger
                  id="test-topn-input"
                  className="mt-1 w-full"
                  aria-label="Top N candidates"
                >
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {[3, 5, 10, 20].map((n) => (
                    <SelectItem key={n} value={String(n)}>
                      {n}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <Button
              onClick={handleTest}
              disabled={!testDescription.trim() || testMatch.isPending}
            >
              {testMatch.isPending ? "Testing…" : "Test"}
            </Button>
          </div>
          {testMatch.data && (
            <div
              className="rounded border p-3 text-sm"
              data-testid="test-match-panel"
            >
              <div className="flex items-center gap-2">
                <span className="font-semibold">Simulated outcome:</span>
                <Badge variant="secondary">
                  {testMatch.data.simulatedOutcome}
                </Badge>
              </div>
              {testMatch.data.candidates.length === 0 ? (
                <p className="mt-2 text-muted-foreground">
                  No candidates returned.
                </p>
              ) : (
                <Table className="mt-2">
                  <TableHeader>
                    <TableRow>
                      <TableHead>Canonical Name</TableHead>
                      <TableHead>Status</TableHead>
                      <TableHead className="text-right">Similarity</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {testMatch.data.candidates.map((c) => (
                      <TableRow key={c.normalizedDescriptionId}>
                        <TableCell className="font-medium">
                          {c.canonicalName}
                        </TableCell>
                        <TableCell>
                          <Badge
                            variant={
                              c.status === "Active" ? "default" : "secondary"
                            }
                          >
                            {c.status}
                          </Badge>
                        </TableCell>
                        <TableCell className="text-right money">
                          {formatDecimal(c.cosineSimilarity, 4)}
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
