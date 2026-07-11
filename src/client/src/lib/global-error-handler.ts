import { toast } from "sonner";
import { showApiError, showNetworkError } from "@/lib/toast";
import { isTimeoutError, isNetworkError } from "@/lib/api-client";
import { extractErrorMessage } from "@/lib/problem-details";

/**
 * Single source of truth for surfacing query/mutation errors as toasts.
 *
 * Wired into React Query's `QueryCache` and `MutationCache` `onError` so every
 * failed request produces exactly one actionable toast. Individual mutation
 * hooks no longer toast generic "Failed to X" strings (RECEIPTS-782); they let
 * this handler surface the real server message instead.
 */
export function handleGlobalError(error: unknown) {
  if (isTimeoutError(error)) {
    toast.error("Request timed out. Please try again.");
    return;
  }

  if (
    error &&
    typeof error === "object" &&
    "status" in error &&
    typeof (error as Record<string, unknown>).status === "number"
  ) {
    // Surface the server's ProblemDetails message (400 "Date cannot be in the
    // future", 409 duplicate-name, field validation) instead of a bare status.
    showApiError(
      (error as Record<string, unknown>).status as number,
      extractErrorMessage(error),
    );
    return;
  }

  // Network failures throw a TypeError whose message varies by browser
  // ("Failed to fetch" in Chrome, "NetworkError when attempting to fetch
  // resource" in Firefox, "Load failed" in Safari). Match the error *type*
  // rather than an exact string so non-Chrome users still get a toast
  // (RECEIPTS-784).
  if (isNetworkError(error) || error instanceof TypeError) {
    showNetworkError();
    return;
  }
}
