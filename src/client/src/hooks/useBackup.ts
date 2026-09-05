import { useCallback, useMemo } from "react";
import type { MutateOptions, UseMutateAsyncFunction, UseMutateFunction, UseMutationResult } from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import { assertSessionCurrent, getAccessToken, getSessionSignal, getSessionVersion } from "@/lib/auth";
import { showSuccess, showError } from "@/lib/toast";

const baseUrl = import.meta.env.VITE_API_URL ?? "";

interface ExportedBackup {
  blob: Blob;
  filename: string;
}

// The internal payload is needed by guarded onSuccess, but the existing public export action
// resolves void and does not expose the downloaded backup through mutation data/callbacks.
function exportCallbacks(callbacks?: MutateOptions<void, Error, void, unknown>): MutateOptions<ExportedBackup, Error, void, unknown> | undefined {
  return callbacks && {
    ...callbacks,
    onSuccess: (_backup, ...args) => callbacks.onSuccess?.(undefined, ...args),
    onSettled: (_backup, ...args) => callbacks.onSettled?.(undefined, ...args),
  };
}

export function useBackupExport(): UseMutationResult<void, Error, void, unknown> {
  const mutation = useSessionMutation<ExportedBackup, Error>({
    mutationFn: async () => {
      const token = getAccessToken();
      const sessionSignal = getSessionSignal();
      const res = await fetch(`${baseUrl}/api/backup/export`, {
        method: "POST",
        headers: {
          Authorization: `Bearer ${token}`,
        },
        signal: AbortSignal.any([sessionSignal, AbortSignal.timeout(300_000)]),
      });

      if (!res.ok) {
        if (res.status === 403)
          throw new Error("You do not have permission to export backups.");
        throw new Error(`Export failed (${res.status}).`);
      }

      const blob = await res.blob();
      const disposition = res.headers.get("Content-Disposition");
      let filename = `receipts-backup-${new Date().toISOString().slice(0, 10)}.sqlite`;
      if (disposition) {
        const match = disposition.match(/filename="?([^";\n]+)"?/);
        if (match) filename = match[1];
      }

      return { blob, filename };
    },
    onSuccess: ({ blob, filename }) => {
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = filename;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      URL.revokeObjectURL(url);
      showSuccess("Backup exported successfully.");
    },
    onError: (error: Error) => {
      showError(error.message);
    },
  });

  const execute = mutation.mutateAsync;
  const mutateAsync = useCallback<UseMutateAsyncFunction<void, Error, void, unknown>>(
    async (variables, callbacks) => {
      const version = getSessionVersion();
      try {
        await execute(variables, exportCallbacks(callbacks));
      } finally {
        assertSessionCurrent(version);
      }
    },
    [execute],
  );
  const mutate = useCallback<UseMutateFunction<void, Error, void, unknown>>(
    (variables, callbacks) => { void mutateAsync(variables, callbacks).catch(() => {}); },
    [mutateAsync],
  );
  return useMemo(() => ({ ...mutation, data: undefined, mutate, mutateAsync }), [mutation, mutate, mutateAsync]);
}
