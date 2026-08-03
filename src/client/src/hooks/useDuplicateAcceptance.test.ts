import { createElement, type ReactNode } from "react";
import { renderHook, waitFor } from "@testing-library/react";
import { QueryClientProvider, type QueryClient } from "@tanstack/react-query";
import { createQueryClient, createQueryWrapper } from "@/test/test-utils";
import {
  ACCEPTED_DUPLICATES_QUERY_KEY,
  useAcceptDuplicateGroup,
  useAcceptedDuplicates,
  useUnacceptDuplicateGroup,
} from "./useDuplicateAcceptance";

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
const mockToast = vi.mocked(toast);

/** Wrapper bound to a caller-supplied QueryClient so invalidation can be spied on. */
function wrapperFor(queryClient: QueryClient) {
  return function QueryClientWrapper({ children }: { children: ReactNode }) {
    return createElement(QueryClientProvider, { client: queryClient }, children);
  };
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
function apiSuccess(data: unknown): any {
  return { data, error: undefined, response: {} as Response };
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
function apiError(error: unknown): any {
  return { data: undefined, error, response: {} as Response };
}

const acceptedGroupsResponse = {
  groupCount: 1,
  groups: [
    {
      acceptedAt: "2025-04-05T10:00:00Z",
      receipts: [
        {
          receiptId: "id-1",
          location: "Store A",
          date: "2025-03-01",
          transactionTotal: 25.5,
        },
        {
          receiptId: "id-2",
          location: "Store A",
          date: "2025-03-01",
          transactionTotal: 25.5,
        },
      ],
    },
  ],
};

describe("useAcceptedDuplicates", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("fetches accepted groups", async () => {
    mockClient.GET.mockResolvedValue(apiSuccess(acceptedGroupsResponse));

    const { result } = renderHook(() => useAcceptedDuplicates(), {
      wrapper: createQueryWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(acceptedGroupsResponse);
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/reports/duplicates/accepted",
    );
  });

  it("throws when API returns an error", async () => {
    const error = { message: "Server error" };
    mockClient.GET.mockResolvedValue(apiError(error));

    const { result } = renderHook(() => useAcceptedDuplicates(), {
      wrapper: createQueryWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toEqual(error);
  });
});

describe("useAcceptDuplicateGroup", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("posts the receipt ids and returns the response", async () => {
    mockClient.POST.mockResolvedValue(apiSuccess({ acceptedPairCount: 1 }));

    const { result } = renderHook(() => useAcceptDuplicateGroup(), {
      wrapper: createQueryWrapper(),
    });

    result.current.mutate(["id-1", "id-2"]);

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockClient.POST).toHaveBeenCalledWith(
      "/api/reports/duplicates/accepted",
      { body: { receiptIds: ["id-1", "id-2"] } },
    );
    expect(result.current.data).toEqual({ acceptedPairCount: 1 });
  });

  it("invalidates the duplicate report and accepted-groups caches on success", async () => {
    mockClient.POST.mockResolvedValue(apiSuccess({ acceptedPairCount: 1 }));
    const queryClient = createQueryClient();
    const invalidateSpy = vi.spyOn(queryClient, "invalidateQueries");

    const { result } = renderHook(() => useAcceptDuplicateGroup(), {
      wrapper: wrapperFor(queryClient),
    });

    result.current.mutate(["id-1", "id-2"]);

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ["reports", "duplicates"],
    });
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ACCEPTED_DUPLICATES_QUERY_KEY,
    });
  });

  it("fires a success toast", async () => {
    mockClient.POST.mockResolvedValue(apiSuccess({ acceptedPairCount: 1 }));

    const { result } = renderHook(() => useAcceptDuplicateGroup(), {
      wrapper: createQueryWrapper(),
    });

    result.current.mutate(["id-1", "id-2"]);

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockToast.success).toHaveBeenCalledWith(
      expect.stringContaining("Marked as not duplicates"),
    );
  });

  it("throws and skips the toast when the API returns an error", async () => {
    const error = { message: "At least two distinct receipts are required." };
    mockClient.POST.mockResolvedValue(apiError(error));

    const { result } = renderHook(() => useAcceptDuplicateGroup(), {
      wrapper: createQueryWrapper(),
    });

    result.current.mutate(["id-1"]);

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toEqual(error);
    expect(mockToast.success).not.toHaveBeenCalled();
  });
});

describe("useUnacceptDuplicateGroup", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("posts the receipt ids to the remove endpoint and returns the response", async () => {
    mockClient.POST.mockResolvedValue(apiSuccess({ removedPairCount: 1 }));

    const { result } = renderHook(() => useUnacceptDuplicateGroup(), {
      wrapper: createQueryWrapper(),
    });

    result.current.mutate(["id-1", "id-2"]);

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockClient.POST).toHaveBeenCalledWith(
      "/api/reports/duplicates/accepted/remove",
      { body: { receiptIds: ["id-1", "id-2"] } },
    );
    expect(result.current.data).toEqual({ removedPairCount: 1 });
  });

  it("invalidates the duplicate report and accepted-groups caches on success", async () => {
    mockClient.POST.mockResolvedValue(apiSuccess({ removedPairCount: 1 }));
    const queryClient = createQueryClient();
    const invalidateSpy = vi.spyOn(queryClient, "invalidateQueries");

    const { result } = renderHook(() => useUnacceptDuplicateGroup(), {
      wrapper: wrapperFor(queryClient),
    });

    result.current.mutate(["id-1", "id-2"]);

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ["reports", "duplicates"],
    });
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ACCEPTED_DUPLICATES_QUERY_KEY,
    });
  });

  it("fires a success toast", async () => {
    mockClient.POST.mockResolvedValue(apiSuccess({ removedPairCount: 1 }));

    const { result } = renderHook(() => useUnacceptDuplicateGroup(), {
      wrapper: createQueryWrapper(),
    });

    result.current.mutate(["id-1", "id-2"]);

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockToast.success).toHaveBeenCalledWith(
      expect.stringContaining("Acceptance undone"),
    );
  });

  it("throws and skips the toast when the API returns an error", async () => {
    const error = { message: "Server error" };
    mockClient.POST.mockResolvedValue(apiError(error));

    const { result } = renderHook(() => useUnacceptDuplicateGroup(), {
      wrapper: createQueryWrapper(),
    });

    result.current.mutate(["id-1", "id-2"]);

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toEqual(error);
    expect(mockToast.success).not.toHaveBeenCalled();
  });
});

describe("ACCEPTED_DUPLICATES_QUERY_KEY", () => {
  it("is not a prefix match of the duplicate report key", () => {
    // Both invalidations are required precisely because the accepted-groups
    // key does not sit under ["reports", "duplicates"].
    expect(ACCEPTED_DUPLICATES_QUERY_KEY).toEqual([
      "reports",
      "accepted-duplicates",
    ]);
  });
});
