/**
 * RFC 9457 Problem Details error parsing.
 * Extracts field-level validation errors from a ProblemDetails response.
 */

export interface ProblemDetailsError {
  type?: string | null;
  title?: string | null;
  status?: number | null;
  detail?: string | null;
  instance?: string | null;
  errors?: Record<string, string[]>;
}

/**
 * Attempts to parse an error thrown by openapi-fetch into ProblemDetails.
 * Returns null if the error is not a ProblemDetails response.
 */
export function parseProblemDetails(
  error: unknown,
): ProblemDetailsError | null {
  if (error == null || typeof error !== "object") return null;

  const obj = error as Record<string, unknown>;

  // openapi-fetch throws the response body directly as the error
  if (typeof obj.status === "number" || typeof obj.title === "string") {
    return obj as ProblemDetailsError;
  }

  return null;
}

/**
 * Extracts a flat map of field name -> first error message from ProblemDetails.
 * Field names are normalized to camelCase (e.g., "Amount" -> "amount").
 */
export function extractFieldErrors(
  problem: ProblemDetailsError,
): Record<string, string> {
  const result: Record<string, string> = {};
  if (!problem.errors) return result;

  for (const [field, messages] of Object.entries(problem.errors)) {
    if (messages.length > 0) {
      // Normalize: "Amount" -> "amount", "Type" -> "type"
      const key = field.charAt(0).toLowerCase() + field.slice(1);
      result[key] = messages[0];
    }
  }

  return result;
}

// ASP.NET emits this generic title for any ValidationProblemDetails; the
// useful, actionable text lives in `errors` (field messages), so we prefer a
// field message over this boilerplate title.
const GENERIC_VALIDATION_TITLE = "One or more validation errors occurred.";

/**
 * Derives the single most actionable human-readable message from an error
 * thrown by openapi-fetch (which throws the ProblemDetails response body).
 *
 * Priority: `detail` (specific 4xx/409 messages like "Date cannot be in the
 * future" or "A card named X already exists") → first field-level validation
 * error → `title`. Returns undefined when the error isn't ProblemDetails-shaped
 * so callers can fall back to a status-based default. RECEIPTS-782.
 */
export function extractErrorMessage(error: unknown): string | undefined {
  const problem = parseProblemDetails(error);
  if (!problem) return undefined;

  const detail = problem.detail?.trim();
  if (detail) return detail;

  const firstFieldError = Object.values(extractFieldErrors(problem))[0];
  if (firstFieldError) return firstFieldError;

  const title = problem.title?.trim();
  if (title && title !== GENERIC_VALIDATION_TITLE) return title;

  return undefined;
}
