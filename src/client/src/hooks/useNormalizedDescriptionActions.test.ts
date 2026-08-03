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
