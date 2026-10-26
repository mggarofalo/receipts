import { useRef, useState } from "react";
import { Link } from "react-router";
import { useQuery } from "@tanstack/react-query";
import client from "@/lib/api-client";
import type { components } from "@/generated/api";
import { localErrorPolicy } from "@/lib/request-error-policy";
import { extractErrorMessage } from "@/lib/problem-details";
import { Button } from "@/components/ui/button";
import { assistanceFailureMessage } from "@/lib/assistance-failure";
import { useAuth } from "@/hooks/useAuth";

type Group = components["schemas"]["HistoricalGroupSummary"];
type Key = components["schemas"]["HistoricalGroupKey"];
type Intent = {
  operationId: string;
  taxonomyRevisionId: string;
  groups: Key[];
};
const intentKey = (actor: string) =>
  `receipts:historical-assistance-intent:${actor}`;
function readIntent(actor: string): Intent | undefined {
  try {
    const value = sessionStorage.getItem(intentKey(actor));
    if (!value) return undefined;
    const parsed: unknown = JSON.parse(value);
    if (typeof parsed !== "object" || parsed === null) return undefined;
    const candidate = parsed as Partial<Intent>;
    if (
      typeof candidate.operationId !== "string" ||
      typeof candidate.taxonomyRevisionId !== "string" ||
      !Array.isArray(candidate.groups) ||
      candidate.groups.length < 1 ||
      candidate.groups.length > 25 ||
      candidate.groups.some(
        (group) =>
          typeof group?.store !== "string" ||
          typeof group?.rawDescription !== "string" ||
          !(group.itemCode === null || typeof group.itemCode === "string"),
      )
    )
      return undefined;
    return candidate as Intent;
  } catch {
    return undefined;
  }
}

const errorMessage = (error: unknown) =>
  extractErrorMessage(error) ?? "Request failed. Try again.";

export function HistoricalAssistance({
  selected,
  revisionId,
}: {
  selected: Group[];
  revisionId?: string;
}) {
  const { user } = useAuth();
  const actor = user?.userId ?? "anonymous";
  const [intent, setIntent] = useState<Intent | undefined>(() =>
    readIntent(actor),
  );
  const [activeId, setActiveId] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const submitting = useRef(false);
  const status = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["category-assistance", "status"],
    queryFn: async ({ signal }) => {
      const result = await client.GET("/api/category-assistance/status", {
        ...localErrorPolicy.request,
        signal,
      });
      if (result.error || !result.data)
        throw result.error ?? new Error("Response unavailable");
      return result.data;
    },
  });
  const available =
    status.data?.capability === "ready" &&
    status.data.consentEnabled === true &&
    status.data.suggestionsAvailable === true;
  const recent = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["category-assistance", "historical-requests"],
    enabled: available,
    queryFn: async ({ signal }) => {
      const result = await client.GET(
        "/api/category-assistance/historical-requests",
        {
          ...localErrorPolicy.request,
          signal,
          params: { query: { offset: 0, limit: 25 } },
        },
      );
      if (result.error || !result.data)
        throw result.error ?? new Error("Response unavailable");
      return result.data;
    },
  });
  const active = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["category-assistance", "historical-request", activeId],
    enabled: available && !!activeId,
    queryFn: async ({ signal }) => {
      const result = await client.GET(
        "/api/category-assistance/historical-requests/{operationId}",
        {
          ...localErrorPolicy.request,
          signal,
          params: { path: { operationId: activeId! } },
        },
      );
      if (result.error || !result.data)
        throw result.error ?? new Error("Response unavailable");
      return result.data;
    },
  });
  if (!available) return null;

  const tooLarge = selected.some((row) => Number(row.purchaseCount) > 100);
  const selectedPurchases = selected.reduce(
    (total, row) => total + Number(row.purchaseCount),
    0,
  );
  const canRequest =
    !!revisionId &&
    selected.length > 0 &&
    selected.length <= 25 &&
    !tooLarge &&
    Number.isInteger(status.data?.dailyLimit) &&
    Number.isInteger(status.data?.pendingLimit) &&
    !busy;

  async function request() {
    if (submitting.current || (!intent && !canRequest)) return;
    const body = intent ?? {
      operationId: crypto.randomUUID(),
      taxonomyRevisionId: revisionId!,
      groups: selected.map((row) => row.group),
    };
    if (!intent) {
      try {
        sessionStorage.setItem(intentKey(actor), JSON.stringify(body));
      } catch {
        setError(
          "Request could not be saved for a safe retry. Check browser storage and try again.",
        );
        return;
      }
    }
    submitting.current = true;
    setIntent(body);
    setBusy(true);
    setError("");
    try {
      const result = await client.POST(
        "/api/category-assistance/historical-requests",
        {
          ...localErrorPolicy.request,
          body,
        },
      );
      if (result.error || !result.data)
        throw result.error ?? new Error("Request acknowledgement unavailable");
      const progress = result.data;
      setActiveId(progress.operationId);
      setIntent(undefined);
      sessionStorage.removeItem(intentKey(actor));
      await recent.refetch();
    } catch (cause) {
      setError(
        `${errorMessage(cause)} Check recent requests before retrying the same request.`,
      );
    } finally {
      submitting.current = false;
      setBusy(false);
    }
  }

  return (
    <section
      aria-label="Historical suggestions"
      className="space-y-3 rounded-lg border p-4"
    >
      <h2 className="text-lg font-semibold">Optional historical suggestions</h2>
      <p className="text-sm">
        Suggestions require separate review. Requesting them does not change
        saved classifications.
      </p>
      {Number.isInteger(status.data?.dailyLimit) &&
        Number.isInteger(status.data?.pendingLimit) && (
          <p className="text-sm">
            Current configured limits: {status.data?.dailyLimit} requests per
            day and {status.data?.pendingLimit} pending requests.
          </p>
        )}
      {(!Number.isInteger(status.data?.dailyLimit) ||
        !Number.isInteger(status.data?.pendingLimit)) && (
        <p className="text-sm">
          Configured limits unavailable.{" "}
          <Button variant="outline" onClick={() => void status.refetch()}>
            Refresh capability status
          </Button>
        </p>
      )}
      <p className="text-sm">
        This request includes {selected.length} selected visible groups and{" "}
        {selectedPurchases} purchases. Maximum: 25 groups, 100 purchases per
        group, 2,500 purchases total.
      </p>
      {tooLarge && (
        <p role="alert">
          A selected group has more than 100 purchases. Remove it to request
          suggestions.
        </p>
      )}
      {intent && (
        <p className="text-sm">
          Retry keeps the same {intent.groups.length} groups and operation ID:{" "}
          {intent.operationId}
        </p>
      )}
      <Button
        disabled={busy || (!intent && !canRequest)}
        onClick={() => void request()}
      >
        {intent ? "Retry request suggestions" : "Request suggestions"}
      </Button>
      {error && <p role="alert">{error}</p>}
      {activeId && (
        <div
          className="space-y-2 rounded-md border p-3"
          aria-label="Suggestion request progress"
        >
          <h3 className="font-medium">Request progress</h3>
          <p className="break-all text-xs">Operation ID: {activeId}</p>
          {active.isPending && <p role="status">Loading request progress…</p>}
          {active.isError && <p role="alert">{errorMessage(active.error)}</p>}
          {active.data && (
            <>
              <p>
                {active.data.state} · {active.data.groupCount} groups ·{" "}
                {active.data.itemCount} purchases
              </p>
              <ul className="space-y-2">
                {active.data.groups.map((entry) => (
                  <li
                    key={JSON.stringify(entry.group)}
                    className="break-words rounded border p-2 text-sm"
                  >
                    {entry.group.store} · {entry.group.rawDescription} ·{" "}
                    {entry.group.itemCode ?? "No code"}: {entry.itemCount}{" "}
                    purchases · {entry.states.join(", ") || "pending"}
                    {entry.failureReasons.some(Boolean) && (
                      <ul className="mt-1 list-disc pl-5">
                        {entry.failureReasons.map(
                          (reason, index) =>
                            reason && (
                              <li key={index}>
                                Purchase {index + 1}:{" "}
                                {assistanceFailureMessage(reason)}
                              </li>
                            ),
                        )}
                      </ul>
                    )}
                    {entry.attemptIds.map((attemptId) => (
                      <Link
                        key={attemptId}
                        className="ml-2 break-all underline"
                        to={`/classification?attempt=${encodeURIComponent(attemptId)}`}
                      >
                        Review suggestion {attemptId}
                      </Link>
                    ))}
                  </li>
                ))}
              </ul>
            </>
          )}
          <Button variant="outline" onClick={() => void active.refetch()}>
            Refresh suggestion progress
          </Button>
        </div>
      )}
      <div className="space-y-2">
        <h3 className="font-medium">Recent suggestion requests</h3>
        {recent.isPending && <p role="status">Loading suggestion requests…</p>}
        {recent.isError && <p role="alert">{errorMessage(recent.error)}</p>}
        <Button variant="outline" onClick={() => void recent.refetch()}>
          Refresh suggestion requests
        </Button>
        <ul className="space-y-1">
          {recent.data?.items.map((entry) => (
            <li key={entry.operationId}>
              <Button
                variant="ghost"
                className="h-auto max-w-full break-all text-left whitespace-normal"
                onClick={() => setActiveId(entry.operationId)}
              >
                {entry.state} · {entry.groupCount} groups · {entry.itemCount}{" "}
                purchases · {entry.operationId}
              </Button>
            </li>
          ))}
        </ul>
      </div>
    </section>
  );
}
