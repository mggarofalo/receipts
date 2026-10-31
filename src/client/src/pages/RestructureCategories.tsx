import { useRef, useState } from "react";
import { usePageTitle } from "@/hooks/usePageTitle";
import {
  useRestructureApplications,
  useRestructurePreview,
  useRestructureRun,
  type RestructureApplication,
  type RestructurePlan,
} from "@/hooks/useRestructurePlans";
import { extractErrorMessage } from "@/lib/problem-details";
import { PageHead } from "@/components/primitives";
import { RestructureReportView } from "@/components/categories/RestructureReportView";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
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
import { Spinner } from "@/components/ui/spinner";

const failure = (cause: unknown) =>
  extractErrorMessage(cause) ?? "Request failed. Try again.";

function progressText(application: RestructureApplication) {
  const p = application.progress;
  if (application.state === "undoing" || application.state === "undone") {
    return `${Number(p.undone)} of ${Number(p.applied)} purchases put back`;
  }
  return `${Number(p.applied)} of ${Number(p.total) - Number(p.conflicted)} purchases reassigned`;
}

function RestructureCategories() {
  usePageTitle("Restructure categories");
  const fileInput = useRef<HTMLInputElement>(null);
  const [plan, setPlan] = useState<RestructurePlan>();
  const [fileError, setFileError] = useState<string>();
  const [confirming, setConfirming] = useState(false);
  const [running, setRunning] = useState<RestructureApplication>();
  const preview = useRestructurePreview();
  const run = useRestructureRun();
  const applications = useRestructureApplications();

  async function choose(file: File | undefined) {
    setPlan(undefined);
    setFileError(undefined);
    preview.reset();
    if (!file) return;
    let parsed: RestructurePlan;
    try {
      parsed = JSON.parse(await file.text()) as RestructurePlan;
    } catch {
      setFileError("This file is not valid JSON.");
      return;
    }
    setPlan(parsed);
    preview.mutate(parsed);
  }

  function apply() {
    if (!plan) return;
    setConfirming(false);
    run.mutate(
      {
        kind: "apply",
        applicationId: crypto.randomUUID(),
        plan,
        onProgress: setRunning,
      },
      {
        onSuccess: () => {
          setPlan(undefined);
          preview.reset();
          if (fileInput.current) fileInput.current.value = "";
        },
      },
    );
  }

  function continueRun(kind: "resume" | "undo", id: string) {
    run.mutate({ kind, id, onProgress: setRunning });
  }

  const result = preview.data;
  const busy = run.isPending;

  return (
    <>
      <PageHead
        title="Restructure categories"
        sub="Apply a reviewed plan to the category tree and past purchases"
      />
      <div className="space-y-6">
        <Card>
          <CardHeader>
            <CardTitle>Plan</CardTitle>
            <CardDescription>
              Choose a restructure plan file. It is checked against this
              deployment and nothing changes until you apply it. Purchases keep
              their envelopes; only envelope merges in the plan move money
              between envelopes.
            </CardDescription>
          </CardHeader>
          <CardContent className="space-y-4">
            <div>
              <label htmlFor="restructure-plan" className="mb-1 block text-sm font-medium">
                Plan file
              </label>
              <input
                id="restructure-plan"
                ref={fileInput}
                type="file"
                accept="application/json,.json"
                disabled={busy}
                onChange={(event) => void choose(event.target.files?.[0])}
                className="block w-full text-sm"
              />
            </div>
            {fileError && (
              <Alert variant="destructive">
                <AlertDescription>{fileError}</AlertDescription>
              </Alert>
            )}
            {preview.isPending && (
              <p className="flex items-center gap-2 text-sm">
                <Spinner size="sm" /> Checking the plan…
              </p>
            )}
            {preview.isError && (
              <Alert variant="destructive">
                <AlertDescription>{failure(preview.error)}</AlertDescription>
              </Alert>
            )}
            {result && !result.valid && (
              <Alert variant="destructive">
                <AlertTitle>This plan cannot be applied</AlertTitle>
                <AlertDescription>
                  <ul className="list-disc pl-5">
                    {result.errors.map((error) => (
                      <li key={error}>{error}</li>
                    ))}
                  </ul>
                </AlertDescription>
              </Alert>
            )}
            {result?.valid && (
              <>
                {result.warnings.length > 0 && (
                  <Alert>
                    <AlertTitle>Check before applying</AlertTitle>
                    <AlertDescription>
                      <ul className="list-disc pl-5">
                        {result.warnings.map((warning) => (
                          <li key={warning}>{warning}</li>
                        ))}
                      </ul>
                    </AlertDescription>
                  </Alert>
                )}
                <RestructureReportView report={result.report} />
                <Button onClick={() => setConfirming(true)} disabled={busy}>
                  Apply plan
                </Button>
              </>
            )}
          </CardContent>
        </Card>

        {(busy || run.isError) && (
          <Alert variant={run.isError ? "destructive" : "default"} aria-live="polite">
            <AlertDescription className="flex items-center gap-2">
              {busy && <Spinner size="sm" />}
              {run.isError
                ? `${failure(run.error)} Completed work is kept; resume from the list below.`
                : running
                  ? progressText(running)
                  : "Applying category changes…"}
            </AlertDescription>
          </Alert>
        )}

        <Card>
          <CardHeader>
            <CardTitle>Applied plans</CardTitle>
            <CardDescription>
              Undo puts the categories, defaults, envelopes and every purchase
              back, except purchases someone has changed since.
            </CardDescription>
          </CardHeader>
          <CardContent>
            {applications.isPending && <Spinner size="sm" />}
            {applications.isError && (
              <Alert variant="destructive">
                <AlertDescription>{failure(applications.error)}</AlertDescription>
              </Alert>
            )}
            {applications.data?.length === 0 && (
              <p className="text-sm text-muted-foreground">No plans applied yet.</p>
            )}
            <ul className="divide-y">
              {applications.data?.map((application) => (
                <li key={application.id} className="flex flex-wrap items-center justify-between gap-3 py-3">
                  <div className="min-w-0">
                    <p className="font-medium">{application.reason}</p>
                    <p className="text-sm text-muted-foreground">
                      {new Date(application.appliedAt).toLocaleString()} · {application.state} ·{" "}
                      {progressText(application)} · {Number(application.progress.conflicted)} left as
                      they were
                    </p>
                  </div>
                  <div className="flex gap-2">
                    {(application.state === "preview" || application.state === "applying") && (
                      <Button variant="outline" size="sm" disabled={busy}
                        onClick={() => continueRun("resume", application.id)}>
                        Resume
                      </Button>
                    )}
                    {application.state !== "undone" && (
                      <Button variant="outline" size="sm" disabled={busy}
                        onClick={() => continueRun("undo", application.id)}>
                        {application.state === "undoing" ? "Continue undo" : "Undo"}
                      </Button>
                    )}
                  </div>
                </li>
              ))}
            </ul>
          </CardContent>
        </Card>
      </div>

      <AlertDialog open={confirming} onOpenChange={setConfirming}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Apply this plan?</AlertDialogTitle>
            <AlertDialogDescription>
              The category tree changes at once and{" "}
              {Number(result?.report.items.reassigned ?? 0)} purchases are
              reassigned in chunks. Keep this page open until it finishes; if
              it is interrupted, resume it from the list. You can undo it
              afterwards.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={apply}>Apply</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  );
}

export default RestructureCategories;
