import { useQueryClient } from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import { useYnabSettlementRepair } from "@/hooks/useYnab";
import { localErrorPolicy } from "@/lib/request-error-policy";
import client from "@/lib/api-client";
import type { components } from "@/generated/api";
import { queryKeys, type DomainChange } from "@/lib/query-invalidation";

export type YnabRemoteRevocation =
  components["schemas"]["YnabRemoteRevocation"];

const ynabConnectionChanges = [
  "ynab-connection",
] as const satisfies readonly DomainChange[];

/**
 * Asks the server for the YNAB address the browser must visit. Nothing is
 * stored yet, so no projection needs repair. The settings card owns the error
 * (a 409 problem document when OAuth is not configured on this deployment).
 */
export function useStartYnabAuthorization() {
  return useSessionMutation({
    ...localErrorPolicy.mutation,
    mutationFn: async () => {
      const { data, error } = await client.POST(
        "/api/ynab/oauth/authorization",
        localErrorPolicy.request,
      );
      if (error) throw error;
      if (!data) throw new Error("YNAB authorization response was empty");
      return data;
    },
  });
}

/**
 * Presents the code and state YNAB returned. The server treats an attempt as
 * single-use, so the caller must send a given pair once unless the previous
 * answer was a 503 or a network failure. The callback page owns every error.
 */
export function useCompleteYnabAuthorization() {
  const queryClient = useQueryClient();
  const repairOnSettled = useYnabSettlementRepair(ynabConnectionChanges);
  return useSessionMutation({
    ...localErrorPolicy.mutation,
    mutationFn: async (body: { code: string; state: string }) => {
      const { data, error } = await client.POST("/api/ynab/oauth/callback", {
        ...localErrorPolicy.request,
        body,
      });
      if (error) throw error;
      if (!data) throw new Error("YNAB connection response was empty");
      return data;
    },
    onSuccess: (status) => {
      // The response is the new connection status; show it without waiting
      // for the refetch that the settlement repair below starts.
      queryClient.setQueryData(queryKeys.ynabConnectionStatus, status);
    },
    // A response lost after the server stored the connection must still
    // refresh every credential-dependent read, so repair on settlement.
    onSettled: repairOnSettled,
  });
}

/** Removes the stored connection and reports whether YNAB accepted the revocation. */
export function useDisconnectYnab() {
  const repairOnSettled = useYnabSettlementRepair(ynabConnectionChanges);
  return useSessionMutation({
    ...localErrorPolicy.mutation,
    mutationFn: async () => {
      const { data, error } = await client.DELETE(
        "/api/ynab/oauth/connection",
        localErrorPolicy.request,
      );
      if (error) throw error;
      if (!data) throw new Error("YNAB disconnect response was empty");
      return data;
    },
    onSettled: repairOnSettled,
  });
}
