import { renderHook, waitFor } from "@testing-library/react";
import { createQueryWrapper } from "@/test/test-utils";
import {
  useRequeuePendingPreview,
  useRequeuePendingMutation,
} from "./useNormalizedDescriptionMaintenance";

vi.mock("@/lib/api-client", () => ({
  default: {
    GET: vi.fn(),
    POST: vi.fn(),
  },
}));

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

import client from "@/lib/api-client";
import { toast } from "sonner";
const mockClient = vi.mocked(client);

const previewBody = {
  pendingDescriptionCount: 4,
  linkedItemCount: 120,
  staleMatchScoreCount: 118,
  estimatedResolverCycles: 3,
  estimatedCatchUpSeconds: 90,
};

describe("useRequeuePendingPreview", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("returns the preview counts", async () => {
    mockClient.GET.mockResolvedValue({
      data: previewBody,
      error: undefined,
      response: { status: 200, ok: true } as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useRequeuePendingPreview(), {
      wrapper: createQueryWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(previewBody);
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/normalized-descriptions/requeue-pending/preview",
    );
  });

  it("treats a bodiless 403 as an error, not an empty preview", async () => {
    // The endpoint is admin-gated and ASP.NET sends no body on a 403, which openapi-fetch
    // surfaces as `error: undefined`. Branching on `error` alone would render "nothing to
    // requeue" to an operator who simply is not allowed to see the number.
    mockClient.GET.mockResolvedValue({
      data: undefined,
      error: undefined,
      response: { status: 403, ok: false } as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useRequeuePendingPreview(), {
      wrapper: createQueryWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toMatchObject({ status: 403 });
  });
});

describe("useRequeuePendingMutation", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("posts the previewed count and reports what was destroyed", async () => {
    mockClient.POST.mockResolvedValue({
      data: {
        deletedDescriptionCount: 4,
        unlinkedItemCount: 120,
        clearedMatchScoreCount: 118,
      },
      error: undefined,
      response: { status: 200, ok: true } as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useRequeuePendingMutation(), {
      wrapper: createQueryWrapper(),
    });

    result.current.mutate({ expectedPendingCount: 4 });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockClient.POST).toHaveBeenCalledWith(
      "/api/normalized-descriptions/requeue-pending",
      { body: { expectedPendingCount: 4 } },
    );
    expect(toast.success).toHaveBeenCalledWith(
      "Requeued 4 pending descriptions — 120 items awaiting re-resolution",
    );
  });

  it("singularises the success message for a single row", async () => {
    mockClient.POST.mockResolvedValue({
      data: {
        deletedDescriptionCount: 1,
        unlinkedItemCount: 1,
        clearedMatchScoreCount: 1,
      },
      error: undefined,
      response: { status: 200, ok: true } as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useRequeuePendingMutation(), {
      wrapper: createQueryWrapper(),
    });

    result.current.mutate({ expectedPendingCount: 1 });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(toast.success).toHaveBeenCalledWith(
      "Requeued 1 pending description — 1 item awaiting re-resolution",
    );
  });

  it("reports a no-op re-run rather than claiming work was done", async () => {
    mockClient.POST.mockResolvedValue({
      data: {
        deletedDescriptionCount: 0,
        unlinkedItemCount: 0,
        clearedMatchScoreCount: 0,
      },
      error: undefined,
      response: { status: 200, ok: true } as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useRequeuePendingMutation(), {
      wrapper: createQueryWrapper(),
    });

    result.current.mutate({ expectedPendingCount: 0 });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(toast.success).toHaveBeenCalledWith(
      "Nothing to requeue — no pending descriptions",
    );
  });

  it("surfaces a 409 stale-preview guard as an error carrying the status", async () => {
    mockClient.POST.mockResolvedValue({
      data: undefined,
      error: "The pending-review count changed since it was previewed.",
      response: { status: 409, ok: false } as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useRequeuePendingMutation(), {
      wrapper: createQueryWrapper(),
    });

    result.current.mutate({ expectedPendingCount: 4 });

    await waitFor(() => expect(result.current.isError).toBe(true));
    // The status must survive onto the thrown value: the global MutationCache handler keys off
    // it to surface the server's message, and this hook keys off it to refetch the preview.
    expect(result.current.error).toMatchObject({
      status: 409,
      detail: "The pending-review count changed since it was previewed.",
    });
    // No toast from the hook itself — the global handler owns error toasts (RECEIPTS-782), and
    // a second one here would double up.
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("treats a bodiless 403 as a failure rather than a silent success", async () => {
    mockClient.POST.mockResolvedValue({
      data: undefined,
      error: undefined,
      response: { status: 403, ok: false } as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useRequeuePendingMutation(), {
      wrapper: createQueryWrapper(),
    });

    result.current.mutate({ expectedPendingCount: 4 });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(toast.success).not.toHaveBeenCalled();
  });
});
