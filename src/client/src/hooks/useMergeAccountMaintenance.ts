import { useCallback, useMemo, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { localErrorPolicy } from "@/lib/request-error-policy";
import client from "@/lib/api-client";
import { assertSessionCurrent, getSessionVersion } from "@/lib/auth";
import { invalidateDomainChange } from "@/lib/query-invalidation";

/**
 * Merge cleanup/retry commands retain their caller-owned error handling. Registering
 * them as global mutations would change the existing cleanup warning/retry flow.
 */
export function useMergeAccountMaintenance() {
  const queryClient = useQueryClient();
  const [sessionVersion] = useState(getSessionVersion);
  const execute = useCallback(async (request: () => Promise<void>) => {
    assertSessionCurrent(sessionVersion);
    try {
      await request();
    } catch (error) {
      // A late failure belongs to the old session too; never deliver it as a
      // current cleanup warning after logout or replacement login.
      assertSessionCurrent(sessionVersion);
      throw error;
    }
    assertSessionCurrent(sessionVersion);
    invalidateDomainChange(queryClient, "account");
  }, [queryClient, sessionVersion]);

  const discardAccount = useCallback((id: string) => execute(async () => {
    const { error } = await client.DELETE("/api/accounts/{id}", {
      params: { path: { id } },
      ...localErrorPolicy.request,
    });
    if (error) throw error;
  }), [execute]);

  const renameAccount = useCallback((id: string, name: string) => execute(async () => {
    const { error } = await client.PUT("/api/accounts/{id}", {
      params: { path: { id } },
      ...localErrorPolicy.request,
      body: { id, name, isActive: true },
    });
    if (error) throw error;
  }), [execute]);

  return useMemo(() => ({ discardAccount, renameAccount }), [discardAccount, renameAccount]);
}
