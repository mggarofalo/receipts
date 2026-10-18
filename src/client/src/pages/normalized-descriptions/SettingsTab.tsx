import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import {
  usePreviewImpactMutation,
  useSettings,
  useTestMatchMutation,
  useUpdateSettingsMutation,
} from "@/hooks/useNormalizedDescriptionSettings";
import { formatDecimal } from "@/lib/format";
import { useState } from "react";
export function SettingsTab() {
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
  // Blank overrides intentionally use live settings; invalid supplied values
  // must not silently turn into a different simulation.
  const testThresholdsValid =
    (autoAccept.trim() === "" || autoValid) &&
    (pendingReview.trim() === "" || pendingValid) &&
    (!(autoValid && pendingValid) || orderValid);

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
    if (!trimmed || !testThresholdsValid) return;
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
              <p className="text-xs text-muted-foreground">
                Submitted thresholds: auto-accept{" "}
                {previewImpact.variables?.autoAcceptThreshold}, pending-review{" "}
                {previewImpact.variables?.pendingReviewThreshold}. This result
                describes those inputs.
              </p>
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
              disabled={
                !testDescription.trim() ||
                !testThresholdsValid ||
                testMatch.isPending
              }
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
              <p className="text-xs text-muted-foreground">
                Submitted description: {testMatch.variables?.description}; top{" "}
                {testMatch.variables?.topN ?? 5}; auto-accept{" "}
                {testMatch.variables?.autoAcceptThresholdOverride ??
                  "live setting"}
                ; pending-review{" "}
                {testMatch.variables?.pendingReviewThresholdOverride ??
                  "live setting"}
                .
              </p>
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
