import { renderHook, waitFor } from "@testing-library/react";
import { createQueryWrapper } from "@/test/test-utils";
import {
  usePendingNamePromotionPreview,
  usePendingNamePromotions,
  usePromotePendingNamesMutation,
  useUndoPendingNamePromotionMutation,
} from "./usePendingNamePromotion";

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

function respond(status: number, data?: unknown, error?: unknown) {
  return {
    data,
    error,
    response: { status, ok: status < 400 } as Response,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
  } as any;
}

const previewBody = {
  minimumPurchases: 2,
  nameCount: 1,
  purchaseCount: 7,
  fingerprint: "digest",
  names: [{ id: "n-1", canonicalName: "Oat milk", purchaseCount: 7 }],
};

beforeEach(() => {
  vi.clearAllMocks();
});

describe("usePendingNamePromotionPreview", () => {
  it("returns the eligible names", async () => {
    mockClient.GET.mockResolvedValue(respond(200, previewBody));

    const { result } = renderHook(() => usePendingNamePromotionPreview(), {
      wrapper: createQueryWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(previewBody);
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/normalized-descriptions/promotions/preview",
    );
  });

  it("treats a bodiless 403 as an error, not an empty preview", async () => {
    mockClient.GET.mockResolvedValue(respond(403));

    const { result } = renderHook(() => usePendingNamePromotionPreview(), {
      wrapper: createQueryWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toMatchObject({ status: 403 });
  });
});

describe("usePendingNamePromotions", () => {
  it("returns the promotion history", async () => {
    const history = [
      {
        id: "p-1",
        createdBy: "admin",
        createdAt: "2026-10-31T12:00:00Z",
        promotedCount: 3,
        purchaseCount: 12,
        restoredCount: null,
        keptCount: null,
      },
    ];
    mockClient.GET.mockResolvedValue(respond(200, history));

    const { result } = renderHook(() => usePendingNamePromotions(), {
      wrapper: createQueryWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(history);
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/normalized-descriptions/promotions",
    );
  });
});

describe("usePromotePendingNamesMutation", () => {
  it("posts the previewed fingerprint and reports what was promoted", async () => {
    mockClient.POST.mockResolvedValue(
      respond(200, { promotionId: "p-1", promotedCount: 3, purchaseCount: 12 }),
    );

    const { result } = renderHook(() => usePromotePendingNamesMutation(), {
      wrapper: createQueryWrapper(),
    });
    result.current.mutate({ expectedFingerprint: "digest" });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockClient.POST).toHaveBeenCalledWith(
      "/api/normalized-descriptions/promotions",
      { body: { expectedFingerprint: "digest" } },
    );
    expect(toast.success).toHaveBeenCalledWith(
      "Promoted 3 names covering 12 purchases",
    );
  });

  it("says so when nothing was eligible", async () => {
    mockClient.POST.mockResolvedValue(
      respond(200, { promotionId: null, promotedCount: 0, purchaseCount: 0 }),
    );

    const { result } = renderHook(() => usePromotePendingNamesMutation(), {
      wrapper: createQueryWrapper(),
    });
    result.current.mutate({ expectedFingerprint: "digest" });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(toast.success).toHaveBeenCalledWith("Nothing to promote");
  });

  it("surfaces a changed set as an error carrying the 409", async () => {
    mockClient.POST.mockResolvedValue(
      respond(409, undefined, {
        status: 409,
        detail: "The set of names eligible for promotion changed",
      }),
    );

    const { result } = renderHook(() => usePromotePendingNamesMutation(), {
      wrapper: createQueryWrapper(),
    });
    result.current.mutate({ expectedFingerprint: "stale" });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toMatchObject({ status: 409 });
    expect(toast.success).not.toHaveBeenCalled();
  });
});

describe("useUndoPendingNamePromotionMutation", () => {
  it("posts to the promotion's undo route and reports kept names", async () => {
    mockClient.POST.mockResolvedValue(
      respond(200, { restoredCount: 2, keptCount: 1 }),
    );

    const { result } = renderHook(() => useUndoPendingNamePromotionMutation(), {
      wrapper: createQueryWrapper(),
    });
    result.current.mutate({ id: "p-1" });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockClient.POST).toHaveBeenCalledWith(
      "/api/normalized-descriptions/promotions/{id}/undo",
      { params: { path: { id: "p-1" } } },
    );
    expect(toast.success).toHaveBeenCalledWith(
      "Returned 2 names to review; kept 1 changed name active",
    );
  });

  it("omits the kept clause when every name was returned", async () => {
    mockClient.POST.mockResolvedValue(
      respond(200, { restoredCount: 1, keptCount: 0 }),
    );

    const { result } = renderHook(() => useUndoPendingNamePromotionMutation(), {
      wrapper: createQueryWrapper(),
    });
    result.current.mutate({ id: "p-1" });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(toast.success).toHaveBeenCalledWith("Returned 1 name to review");
  });
});
