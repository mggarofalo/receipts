import { renderHook, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { toast } from "sonner";
import { server } from "@/test/msw/server";
import { createQueryWrapper } from "@/test/test-utils";
import { usePurgeTrash } from "./useTrash";
import { useDeleteReceipts } from "./useReceipts";
import { useDeleteAccount } from "./useAccounts";

/**
 * RECEIPTS-885 regression guard for the destructive mutations.
 *
 * ASP.NET answers an authorization failure with a bodiless 403, and a stale id
 * with a bodiless `NotFound()`. openapi-fetch reports both as a falsy `error`,
 * so `if (error) throw error` used to fall straight through: the mutation
 * resolved, `onSuccess` fired, and the user was told a destructive operation
 * had happened when the server had rejected it outright.
 *
 * These run against the real client (and therefore the real error-normalising
 * middleware) via MSW — a mocked `client` module would bypass the fix entirely.
 */

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

beforeEach(() => {
  vi.clearAllMocks();
});

describe("destructive mutations reject bodiless failures (RECEIPTS-885)", () => {
  it("usePurgeTrash rejects a bodiless 403 instead of reporting success", async () => {
    server.use(
      http.post(
        "*/api/trash/purge",
        () => new HttpResponse(null, { status: 403 }),
      ),
    );

    const { result } = renderHook(() => usePurgeTrash(), {
      wrapper: createQueryWrapper(),
    });

    result.current.mutate();

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toMatchObject({ status: 403 });
    expect(toast.success).not.toHaveBeenCalled();
  });

  it("useDeleteReceipts rejects a bodiless 403 instead of reporting success", async () => {
    server.use(
      http.delete(
        "*/api/receipts",
        () => new HttpResponse(null, { status: 403 }),
      ),
    );

    const { result } = renderHook(() => useDeleteReceipts(), {
      wrapper: createQueryWrapper(),
    });

    result.current.mutate(["11111111-1111-1111-1111-111111111111"]);

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toMatchObject({ status: 403 });
    expect(toast.success).not.toHaveBeenCalled();
  });

  it("useDeleteAccount rejects a bodiless 404 for a stale id", async () => {
    server.use(
      http.delete(
        "*/api/accounts/:id",
        () => new HttpResponse(null, { status: 404 }),
      ),
    );

    const { result } = renderHook(() => useDeleteAccount(), {
      wrapper: createQueryWrapper(),
    });

    result.current.mutate("11111111-1111-1111-1111-111111111111");

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toMatchObject({ status: 404 });
    expect(toast.success).not.toHaveBeenCalled();
  });

  it("useDeleteAccount still surfaces a 409 conflict body unchanged", async () => {
    // The normaliser must not flatten a non-ProblemDetails object body, or the
    // conflict dialog loses the fields it renders.
    server.use(
      http.delete("*/api/accounts/:id", () =>
        HttpResponse.json(
          {
            message: "Cannot delete — 3 cards reference this account",
            cardCount: 3,
          },
          { status: 409 },
        ),
      ),
    );

    const { result } = renderHook(() => useDeleteAccount(), {
      wrapper: createQueryWrapper(),
    });

    result.current.mutate("11111111-1111-1111-1111-111111111111");

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toMatchObject({
      conflict: true,
      message: "Cannot delete — 3 cards reference this account",
      cardCount: 3,
    });
    expect(toast.error).toHaveBeenCalledWith(
      "Cannot delete — 3 cards reference this account",
    );
  });
});
