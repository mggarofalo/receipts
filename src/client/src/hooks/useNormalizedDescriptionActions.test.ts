import { renderHook, waitFor } from "@testing-library/react";
import { createQueryWrapper } from "@/test/test-utils";
import {
  useMergeMutation,
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

    result.current.mutate({ id: "src-1", receiptItemId: "item-1" });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockClient.POST).toHaveBeenCalledWith(
      "/api/normalized-descriptions/{id}/split",
      {
        params: { path: { id: "src-1" } },
        body: { receiptItemId: "item-1" },
      },
    );
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
    result.current.mutate({ id: "a", receiptItemId: "b" });
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
