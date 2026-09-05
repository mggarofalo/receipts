import { act, cleanup, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { renderWithQueryClient } from "@/test/test-utils";
import { clearTokens, setTokens } from "@/lib/auth";
import { showError, showSuccess } from "@/lib/toast";
import BackupRestore from "./BackupRestore";

vi.mock("@/lib/toast", () => ({ showSuccess: vi.fn(), showError: vi.fn() }));

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<T>((done, fail) => {
    resolve = done;
    reject = fail;
  });
  return { promise, resolve, reject };
}

beforeEach(() => {
  vi.clearAllMocks();
  setTokens("Alice-access", "Alice-refresh");
});
afterEach(() => {
  cleanup();
  clearTokens();
  vi.unstubAllGlobals();
});

describe("BackupRestore session ownership", () => {
  it.each(["current", "obsolete-success", "obsolete-failure"] as const)(
    "handles a delayed raw import response for %s",
    async (outcome) => {
      const responseBody = deferred<{
        totalCreated: number;
        totalUpdated: number;
      }>();
      const bodyStarted = deferred<void>();
      let requestSignal: AbortSignal | null | undefined;
      const fetchMock = vi.fn(
        async (_input: RequestInfo | URL, init?: RequestInit) => {
          requestSignal = init?.signal;
          return {
            ok: true,
            status: 200,
            json: () => {
              bodyStarted.resolve();
              return responseBody.promise;
            },
          };
        },
      );
      vi.stubGlobal("fetch", fetchMock);
      const user = userEvent.setup();
      renderWithQueryClient(<BackupRestore />);
      const input = document.getElementById("backup-file") as HTMLInputElement;
      await user.upload(
        input,
        new File(["audit backup"], "audit.sqlite", {
          type: "application/octet-stream",
        }),
      );
      await user.click(screen.getByRole("button", { name: "Import Backup" }));
      await user.click(screen.getByRole("button", { name: "Confirm Import" }));
      await bodyStarted.promise;
      expect(fetchMock).toHaveBeenCalledOnce();
      expect(fetchMock.mock.calls[0][1]?.headers).toEqual({
        Authorization: "Bearer Alice-access",
      });
      if (outcome !== "current") {
        act(() => {
          clearTokens();
          setTokens("Bob-access", "Bob-refresh");
        });
        expect(requestSignal?.aborted).toBe(true);
      }
      await act(async () => {
        if (outcome === "obsolete-failure")
          responseBody.reject(new Error("Alice import failed"));
        else responseBody.resolve({ totalCreated: 2, totalUpdated: 3 });
      });
      if (outcome === "current") {
        await waitFor(() =>
          expect(showSuccess).toHaveBeenCalledWith(
            "Import complete: 2 created, 3 updated.",
          ),
        );
        expect(showError).not.toHaveBeenCalled();
        expect(input.value).toBe("");
      } else {
        // Keeping the old component mounted also proves callbacks cannot mutate its form state.
        await waitFor(() =>
          expect(
            screen.getByRole("button", { name: "Confirm Import" }),
          ).toBeEnabled(),
        );
        expect(showSuccess).not.toHaveBeenCalled();
        expect(showError).not.toHaveBeenCalled();
        expect(input.files?.[0]?.name).toBe("audit.sqlite");
      }
    },
  );
});
