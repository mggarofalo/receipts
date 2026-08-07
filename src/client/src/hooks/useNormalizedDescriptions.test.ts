import { renderHook, waitFor } from "@testing-library/react";
import { createQueryWrapper } from "@/test/test-utils";
import {
  useNormalizedDescriptions,
  useNormalizedDescription,
} from "./useNormalizedDescriptions";

vi.mock("@/lib/api-client", () => ({
  default: {
    GET: vi.fn(),
  },
}));

import client from "@/lib/api-client";
const mockClient = vi.mocked(client);

describe("useNormalizedDescriptions", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("fetches the default page with no filter", async () => {
    const mockData = {
      items: [
        {
          id: "n-1",
          canonicalName: "Apples",
          status: "active",
          createdAt: "2025-01-01T00:00:00Z",
        },
      ],
      totalCount: 1,
    };
    mockClient.GET.mockResolvedValue({
      data: mockData,
      error: undefined,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useNormalizedDescriptions(), {
      wrapper: createQueryWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(mockData);
    expect(result.current.items).toEqual(mockData.items);
    expect(result.current.total).toBe(1);
    // A window is always sent. The endpoint returns everything to a caller that omits it, which
    // is what RECEIPTS-879 exists to stop.
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/normalized-descriptions",
      {
        params: {
          query: { status: undefined, q: undefined, offset: 0, limit: 50 },
        },
      },
    );
  });

  it("fetches list with PendingReview filter", async () => {
    mockClient.GET.mockResolvedValue({
      data: { items: [], totalCount: 0 },
      error: undefined,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(
      () => useNormalizedDescriptions({ status: "PendingReview" }),
      { wrapper: createQueryWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/normalized-descriptions",
      {
        params: {
          query: {
            status: "PendingReview",
            q: undefined,
            offset: 0,
            limit: 50,
          },
        },
      },
    );
  });

  it("forwards the search term and paging window", async () => {
    mockClient.GET.mockResolvedValue({
      data: { items: [], totalCount: 412 },
      error: undefined,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(
      () =>
        useNormalizedDescriptions({
          status: "Active",
          q: "milk",
          offset: 100,
          limit: 25,
        }),
      { wrapper: createQueryWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/normalized-descriptions",
      {
        params: {
          query: { status: "Active", q: "milk", offset: 100, limit: 25 },
        },
      },
    );
    // The matching count, not the page length — a pager built on items.length would offer one
    // page of an empty result.
    expect(result.current.total).toBe(412);
  });

  it("treats a whitespace-only search as no search", async () => {
    mockClient.GET.mockResolvedValue({
      data: { items: [], totalCount: 0 },
      error: undefined,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(
      () => useNormalizedDescriptions({ q: "   " }),
      { wrapper: createQueryWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    // Otherwise a user who cleared the box back to a space gets an empty registry and no
    // explanation, and " milk" / "milk " are three separate cache entries.
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/normalized-descriptions",
      {
        params: {
          query: { status: undefined, q: undefined, offset: 0, limit: 50 },
        },
      },
    );
  });

  it("does not fetch when disabled, and reports an empty page rather than undefined", () => {
    const { result } = renderHook(
      () => useNormalizedDescriptions({ enabled: false }),
      { wrapper: createQueryWrapper() },
    );

    // The merge dialog mounts with its source closed; fetching candidates for a dialog nobody
    // opened is a wasted round trip on every review-queue render. Consumers still get a real
    // empty page, so a `.map` over `items` does not have to guard.
    expect(mockClient.GET).not.toHaveBeenCalled();
    expect(result.current.items).toEqual([]);
    expect(result.current.total).toBe(0);
  });

  it("propagates API errors", async () => {
    const apiError = { message: "Server error" };
    mockClient.GET.mockResolvedValue({
      data: undefined,
      error: apiError,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useNormalizedDescriptions(), {
      wrapper: createQueryWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toEqual(apiError);
  });
});

describe("useNormalizedDescription", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("does not fetch when id is null", () => {
    const { result } = renderHook(() => useNormalizedDescription(null), {
      wrapper: createQueryWrapper(),
    });
    expect(result.current.isPending).toBe(true);
    expect(mockClient.GET).not.toHaveBeenCalled();
  });

  it("fetches by id", async () => {
    const mockData = {
      id: "n-1",
      canonicalName: "Apples",
      status: "active",
      createdAt: "2025-01-01T00:00:00Z",
    };
    mockClient.GET.mockResolvedValue({
      data: mockData,
      error: undefined,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(() => useNormalizedDescription("n-1"), {
      wrapper: createQueryWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(mockData);
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/normalized-descriptions/{id}",
      { params: { path: { id: "n-1" } } },
    );
  });
});
