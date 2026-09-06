import { MutationCache, QueryCache, QueryClient } from "@tanstack/react-query";
import { getSessionVersion } from "@/lib/auth";
import { getCacheErrorPresentation, isHttpServerError } from "@/lib/request-error-policy";
import { handleGlobalError } from "@/lib/global-error-handler";

/** A query cache belongs to the session that created it, including its errors. */
export function createAppQueryClient(): QueryClient {
  const sessionVersion = getSessionVersion();
  const onError = (error: unknown, meta?: Record<string, unknown>) => {
    const presentation = getCacheErrorPresentation(meta);
    if (getSessionVersion() !== sessionVersion || presentation === "local") return;
    // Global HTTP 5xx belongs to the transport bridge. Explicit toast mutations
    // suppress that bridge and let this cache present every failure once.
    if (presentation === "toast" || !isHttpServerError(error)) handleGlobalError(error);
  };
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 5 * 60 * 1000,
        retry: (failureCount, error) =>
          getSessionVersion() === sessionVersion &&
          !(error instanceof DOMException && error.name === "AbortError") &&
          // A global 5xx has already been presented by the transport. Retrying
          // automatically would turn the same operation into another bridge toast.
          !isHttpServerError(error) &&
          failureCount < 1,
      },
    },
    queryCache: new QueryCache({ onError: (error, query) => onError(error, query.meta) }),
    mutationCache: new MutationCache({
      onError: (error, _variables, _context, mutation) => onError(error, mutation.meta),
    }),
  });
}
