import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import type { components } from "@/generated/api";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import client from "@/lib/api-client";
import { getSessionVersion } from "@/lib/auth";
import { localErrorPolicy } from "@/lib/request-error-policy";
import { requestTimeout } from "@/lib/request-timeout";
import { invalidateAfterBackupImport } from "@/lib/query-invalidation";
import { showError, showSuccess } from "@/lib/toast";

const transferMiddleware = [...localErrorPolicy.request.middleware, requestTimeout(300_000)];

type BackupImportResult = components["schemas"]["BackupImportResponse"];

export function useBackupImport() {
  const queryClient = useQueryClient();
  const [sessionVersion] = useState(getSessionVersion);

  return useSessionMutation<BackupImportResult, Error, File>({
    ...localErrorPolicy.mutation,
    mutationFn: async (file) => {
      const formData = new FormData();
      formData.append("file", file);
      const { data, response } = await client.POST("/api/backup/import", {
        middleware: transferMiddleware,
        body: {},
        bodySerializer: () => formData,
      });
      if (!response.ok) {
        if (response.status === 400) throw new Error("Invalid or corrupt backup file.");
        if (response.status === 403) throw new Error("You do not have permission to import backups.");
        throw new Error(`Import failed (${response.status}).`);
      }
      if (!data) throw new Error("Import returned no result.");
      return data;
    },
    onSuccess: (data) => {
      // Read failures belong to their own presenters; they must not relabel a
      // committed restore as a failed import. Guard the continuation after cancellation.
      void invalidateAfterBackupImport(
        queryClient,
        () => getSessionVersion() === sessionVersion,
      ).catch(() => {});
      showSuccess(`Import complete: ${data.totalCreated} created, ${data.totalUpdated} updated.`);
    },
    onError: (error) => showError(error.message),
  });
}
