import { renderHook, waitFor } from "@testing-library/react";
import { QueryClient } from "@tanstack/react-query";
import { createQueryWrapper } from "@/test/test-utils";
import {
  useMergeMutation,
  useRenameMutation,
  useSplitMutation,
  useUpdateStatusMutation,
} from "./useNormalizedDescriptionActions";

vi.mock("@/lib/api-client", () => ({
  default: {
    POST: vi.fn(),
    PATCH: vi.fn(),
  },
}));

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

import client from "@/lib/api-client";
import { toast } from "sonner";
const mockClient = vi.mocked(client);

describe("useMergeMutation", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("merges with path and body", async () => {
    mockClient.POST.mockResolvedValue({
      data: { itemsRelinkedCount: 4 },
      error: undefined,
      response: { status: 200, ok: true } as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useMergeMutation(), {
      wrapper: createQueryWrapper(),
    });

    result.current.mutate({ id: "keep-1", discardId: "drop-1" });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockClient.POST).toHaveBeenCalledWith(
      "/api/normalized-descriptions/{id}/merge",
      {
        params: { path: { id: "keep-1" } },
        body: { discardId: "drop-1" },
      },
    );
  });

  it("reports the re-linked count when items moved", async () => {
    mockClient.POST.mockResolvedValue({
      data: { itemsRelinkedCount: 4 },
      error: undefined,
      response: { status: 200, ok: true } as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useMergeMutation(), {
      wrapper: createQueryWrapper(),
    });
    result.current.mutate({ id: "keep-1", discardId: "drop-1" });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(toast.success).toHaveBeenCalledWith("Merged — 4 items re-linked");
  });

  // RECEIPTS-891: zero used to mean three things — identical ids, a missing row, or a
  // genuine merge with nothing to move — and "Merge completed" was vague enough to
  // cover all three. The first two are rejections now, so this can name what happened.
  it("says a zero count merged nothing rather than staying vague", async () => {
    mockClient.POST.mockResolvedValue({
      data: { itemsRelinkedCount: 0 },
      error: undefined,
      response: { status: 200, ok: true } as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useMergeMutation(), {
      wrapper: createQueryWrapper(),
    });
    result.current.mutate({ id: "keep-1", discardId: "drop-1" });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(toast.success).toHaveBeenCalledWith("Merged — no items needed re-linking");
  });

  it("does not report a merge against a stale id as a success", async () => {
    // The 404 the server now sends. Previously this arrived as 200 { count: 0 }.
    mockClient.POST.mockResolvedValue({
      data: undefined,
      error: {
        type: "https://tools.ietf.org/html/rfc9110#section-15.5.5",
        title: "Not Found",
        status: 404,
        detail: "Normalized description 0f3c… not found.",
      },
      response: { status: 404, ok: false } as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useMergeMutation(), {
      wrapper: createQueryWrapper(),
    });
    result.current.mutate({ id: "keep-1", discardId: "stale-1" });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(toast.success).not.toHaveBeenCalled();
  });

  it("propagates errors", async () => {
    mockClient.POST.mockResolvedValue({
      data: undefined,
      error: { message: "nope" },
      response: { status: 500, ok: false } as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);
    const { result } = renderHook(() => useMergeMutation(), {
      wrapper: createQueryWrapper(),
    });
    result.current.mutate({ id: "a", discardId: "b" });
    await waitFor(() => expect(result.current.isError).toBe(true));
  });
});

describe("useSplitMutation", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("splits with path and body", async () => {
    mockClient.POST.mockResolvedValue({
      data: {
        id: "new-id",
        canonicalName: "Banana",
        status: "active",
        createdAt: "2025-01-01T00:00:00Z",
      },
      error: undefined,
      response: { status: 200, ok: true } as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useSplitMutation(), {
      wrapper: createQueryWrapper(),
    });

    result.current.mutate({
      id: "src-1",
      receiptItemIds: ["item-1", "item-2"],
      canonicalName: "Banana",
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockClient.POST).toHaveBeenCalledWith(
      "/api/normalized-descriptions/{id}/split",
      {
        params: { path: { id: "src-1" } },
        body: { receiptItemIds: ["item-1", "item-2"], canonicalName: "Banana" },
      },
    );
    // The toast counts what actually moved — "an item was split" would understate a bulk
    // correction and leave the reviewer unsure whether the rest went through (RECEIPTS-877).
    expect(toast.success).toHaveBeenCalledWith('2 items split into "Banana"');
  });

  it("propagates errors", async () => {
    mockClient.POST.mockResolvedValue({
      data: undefined,
      error: { message: "nope" },
      response: { status: 500, ok: false } as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);
    const { result } = renderHook(() => useSplitMutation(), {
      wrapper: createQueryWrapper(),
    });
    result.current.mutate({
      id: "a",
      receiptItemIds: ["b"],
      canonicalName: "Banana",
    });
    await waitFor(() => expect(result.current.isError).toBe(true));
  });
});

describe("useRenameMutation", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("patches the label and says what changed", async () => {
    mockClient.PATCH.mockResolvedValue({
      data: { id: "n-1", displayLabel: "Milk", displayName: "Milk" },
      error: undefined,
      response: { status: 200, ok: true } as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useRenameMutation(), {
      wrapper: createQueryWrapper(),
    });
    result.current.mutate({ id: "n-1", displayLabel: "Milk" });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockClient.PATCH).toHaveBeenCalledWith(
      "/api/normalized-descriptions/{id}/rename",
      { params: { path: { id: "n-1" } }, body: { displayLabel: "Milk" } },
    );
    expect(toast.success).toHaveBeenCalledWith('Renamed to "Milk"');
  });

  it("reports a cleared label differently from a set one", async () => {
    mockClient.PATCH.mockResolvedValue({
      data: { id: "n-1", displayLabel: null, displayName: "MILK 2% GAL" },
      error: undefined,
      response: { status: 200, ok: true } as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useRenameMutation(), {
      wrapper: createQueryWrapper(),
    });
    result.current.mutate({ id: "n-1", displayLabel: null });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(toast.success).toHaveBeenCalledWith(
      "Name cleared — showing the matched text again",
    );
  });

  // The report groups by display name, so a rename relabels a bucket. Without this the report
  // serves its cache and the rename looks like it did nothing (RECEIPTS-876).
  it("invalidates the reports cache", async () => {
    const invalidateSpy = vi.spyOn(QueryClient.prototype, "invalidateQueries");
    mockClient.PATCH.mockResolvedValue({
      data: { id: "n-1", displayLabel: "Milk", displayName: "Milk" },
      error: undefined,
      response: { status: 200, ok: true } as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useRenameMutation(), {
      wrapper: createQueryWrapper(),
    });
    result.current.mutate({ id: "n-1", displayLabel: "Milk" });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ["reports"] });
    invalidateSpy.mockRestore();
  });

  it("propagates a 409 name collision", async () => {
    mockClient.PATCH.mockResolvedValue({
      data: undefined,
      error: { detail: "Another normalized description already displays that name." },
      response: { status: 409, ok: false } as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useRenameMutation(), {
      wrapper: createQueryWrapper(),
    });
    result.current.mutate({ id: "n-1", displayLabel: "Milk" });

    await waitFor(() => expect(result.current.isError).toBe(true));
  });
});

describe("useUpdateStatusMutation", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("patches with status", async () => {
    mockClient.PATCH.mockResolvedValue({
      data: undefined,
      error: undefined,
      response: { status: 204, ok: true } as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useUpdateStatusMutation(), {
      wrapper: createQueryWrapper(),
    });

    result.current.mutate({ id: "n-1", status: "active" });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockClient.PATCH).toHaveBeenCalledWith(
      "/api/normalized-descriptions/{id}/status",
      {
        params: { path: { id: "n-1" } },
        body: { status: "active" },
      },
    );
  });

  // RECEIPTS-875: approving is only observable because the spending report re-fetches and drops
  // the "Unreviewed" badge. Without this invalidation the report serves its cached copy and
  // approval once again changes nothing the user can see.
  it("invalidates the reports cache so the spending report drops the badge", async () => {
    const invalidateSpy = vi.spyOn(QueryClient.prototype, "invalidateQueries");
    mockClient.PATCH.mockResolvedValue({
      data: undefined,
      error: undefined,
      response: { status: 204, ok: true } as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useUpdateStatusMutation(), {
      wrapper: createQueryWrapper(),
    });
    result.current.mutate({ id: "n-1", status: "active" });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ["reports"] });
    invalidateSpy.mockRestore();
  });

  // RECEIPTS-874: Approve is the one action with no confirmation dialog, so this toast is the
  // only place its effect is ever stated. It used to read "Approved as active", which restated
  // the status the row had just been given and said nothing about what changed.
  it.each([
    ["active", /nothing was moved/i],
    ["active", /no longer reported as unreviewed/i],
    ["rejected", /items unlinked/i],
    ["rejected", /will not be suggested again/i],
    ["pendingReview", /items stay linked/i],
    ["pendingReview", /reported as unreviewed/i],
  ] as const)(
    "toast for %s names what changed (%s)",
    async (status, expected) => {
      mockClient.PATCH.mockResolvedValue({
        data: undefined,
        error: undefined,
        response: { status: 204, ok: true } as Response,
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      const { result } = renderHook(() => useUpdateStatusMutation(), {
        wrapper: createQueryWrapper(),
      });
      result.current.mutate({ id: "n-1", status });

      await waitFor(() => expect(result.current.isSuccess).toBe(true));
      expect(toast.success).toHaveBeenCalledWith(expect.stringMatching(expected));
    },
  );

  it("propagates errors", async () => {
    mockClient.PATCH.mockResolvedValue({
      data: undefined,
      error: { message: "nope" },
      response: { status: 500, ok: false } as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);
    const { result } = renderHook(() => useUpdateStatusMutation(), {
      wrapper: createQueryWrapper(),
    });
    result.current.mutate({ id: "a", status: "pendingReview" });
    await waitFor(() => expect(result.current.isError).toBe(true));
  });
});
