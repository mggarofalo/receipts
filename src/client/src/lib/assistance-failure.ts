/** The API sends sanitized codes. Keep provider details out of the UI. */
export function assistanceFailureMessage(
  code: string | null | undefined,
): string {
  switch (code) {
    case "InvalidInput":
      return "Category setup could not be used for this request.";
    case "PayloadTooLarge":
      return "The category hierarchy is too large for this request.";
    case "Disabled":
      return "Category assistance is disabled.";
    case "MissingCredential":
      return "Category assistance is not configured.";
    case "Busy":
      return "Category assistance is busy. Try again later.";
    case "Authentication":
      return "Category assistance authentication failed.";
    case "RateLimited":
      return "Category assistance reached a request limit. Try again later.";
    case "Timeout":
      return "Category assistance timed out. Try again.";
    case "Unavailable":
      return "Category assistance is unavailable. Try again later.";
    case "InvalidResponse":
      return "Category assistance returned an unusable response.";
    default:
      return "Category assistance could not complete this request.";
  }
}
