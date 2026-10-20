import { renderHook, waitFor } from "@testing-library/react";
import { createQueryWrapper } from "@/test/test-utils";
import { useNormalizedDescriptionById } from "./useNormalizedDescriptions";
import client from "@/lib/api-client";
vi.mock("@/lib/api-client", () => ({ default: { GET: vi.fn() } }));
beforeEach(() => vi.clearAllMocks());
it("does not fetch without an explicit target", () => {
  renderHook(() => useNormalizedDescriptionById(null), {
    wrapper: createQueryWrapper(),
  });
  expect(client.GET).not.toHaveBeenCalled();
});
it("fetches the exact target with cancellation independent of the paged list", async () => {
  const row = {
    id: "target-b",
    canonicalName: "Product B",
    status: "pendingReview",
  };
  vi.mocked(client.GET).mockResolvedValue({
    data: row,
    response: new Response(),
  } as never);
  const { result } = renderHook(
    () => useNormalizedDescriptionById("target-b"),
    { wrapper: createQueryWrapper() },
  );
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(result.current.data).toEqual(row);
  expect(client.GET).toHaveBeenCalledWith("/api/normalized-descriptions/{id}", {
    signal: expect.any(AbortSignal),
    params: { path: { id: "target-b" } },
  });
});
it.each([404, 403, 503])(
  "exposes bodiless %i as failure rather than an empty successful queue",
  async (status) => {
    vi.mocked(client.GET).mockResolvedValue({
      response: new Response(null, { status }),
    } as never);
    const { result } = renderHook(
      () => useNormalizedDescriptionById("missing"),
      { wrapper: createQueryWrapper() },
    );
    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toMatchObject({ status });
    expect(result.current.data).toBeUndefined();
  },
);
