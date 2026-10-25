import { Link } from "react-router";
import type { AiRun } from "@/hooks/useNormalizationAi";

const beforeRequestCodes = [
  "runner_busy_timeout",
  "runner_setup_failed",
  "unavailable",
];
function isBeforeRequestFailure(run: AiRun) {
  return (
    run.state === "failed" && beforeRequestCodes.includes(run.errorCode ?? "")
  );
}
export function AiRunStatus({ run }: { run: AiRun }) {
  const reconnect = [
    "auth_required",
    "auth_failed",
    "not_connected",
    "connection_changed",
  ].includes(run.errorCode ?? "");
  const uncertain = ["unknown", "interrupted"].includes(run.state);
  const labels: Record<string, string> = {
    queued: "Queued",
    running: "Preparing suggestions",
    ready: "Ready",
    unknown: "Outcome uncertain",
    interrupted: "Outcome uncertain",
    failed: "Failed",
    applied: "Applied",
    cancelled: "Cancelled",
  };
  const label = reconnect
    ? "Reconnect"
    : isBeforeRequestFailure(run)
      ? "Failed before request"
      : (labels[run.state] ?? run.state);
  return (
    <>
      <p role="status">
        {label} / {run.model}
      </p>
      {run.state === "queued" && (
        <p>
          {run.errorCode === "runner_busy"
            ? "Waiting for the local runner. No model request has started; this run will retry automatically."
            : "Waiting to prepare suggestions."}
        </p>
      )}
      {run.state === "running" && (
        <p>Preparing suggestions. You can leave and reopen this run later.</p>
      )}
      {uncertain ? (
        <p role="alert">
          No validated result was saved. The previous request may have consumed
          allowance; retrying makes a new request and may consume allowance
          again.
        </p>
      ) : (
        run.errorDetail && (
          <p role="alert" className="text-destructive">
            {run.errorDetail}
          </p>
        )
      )}
      {isBeforeRequestFailure(run) && (
        <p>No model request was sent. Resolve the runner issue, then retry.</p>
      )}
      {reconnect && (
        <Link
          className="underline"
          to="/admin/normalized-descriptions?tab=chatgpt#chatgpt-connection-settings"
        >
          Open ChatGPT connection settings
        </Link>
      )}
      {run.usage && (
        <p className="text-sm text-muted-foreground">
          Reported usage: {run.usage.input} input tokens, {run.usage.output}{" "}
          output tokens.
        </p>
      )}
    </>
  );
}
