import { extractErrorMessage } from "@/lib/problem-details";

export function ReceiptNormalizationError({ error }: { error: unknown }) {
  if (!error) return null;
  const problem =
    typeof error === "object" ? (error as Record<string, unknown>) : undefined;
  const pendingId =
    typeof problem?.normalizedDescriptionId === "string" &&
    String(problem.normalizedDescriptionStatus).toLowerCase() ===
      "pendingreview"
      ? problem.normalizedDescriptionId
      : undefined;
  return (
    <div role="alert" className="space-y-2 text-sm text-destructive">
      <p>
        {extractErrorMessage(error) ??
          "The receipt could not be saved. Your draft is still here."}
      </p>
      {pendingId && (
        <p>
          <a
            className="underline"
            target="_blank"
            rel="noopener noreferrer"
            href={`/admin/normalized-descriptions?${new URLSearchParams({ tab: "review", reviewTarget: pendingId })}`}
          >
            Review the existing pending product in a new tab
          </a>
          . Return here and choose the reviewed product; your receipt draft
          stays open.
        </p>
      )}
      {problem?.status === 409 && (
        <p>
          Refresh product choices and confirm your selection again, or
          explicitly choose Review later. No receipt changes were saved by this
          attempt.
        </p>
      )}
    </div>
  );
}
