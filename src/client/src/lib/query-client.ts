import { MutationCache, QueryCache, QueryClient } from "@tanstack/react-query";
import { getSessionVersion } from "@/lib/auth";
import { handleGlobalError } from "@/lib/global-error-handler";

/** A query cache belongs to the session that created it, including its errors. */
export function createAppQueryClient(): QueryClient {
  const sessionVersion = getSessionVersion();
  const onError = (error: unknown) => {
    if (getSessionVersion() === sessionVersion) handleGlobalError(error);
  };
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 5 * 60 * 1000,
        retry: (failureCount, error) =>
          getSessionVersion() === sessionVersion &&
          !(error instanceof DOMException && error.name === "AbortError") &&
          failureCount < 1,
      },
    },
    queryCache: new QueryCache({ onError }),
    mutationCache: new MutationCache({ onError }),
  });
}
