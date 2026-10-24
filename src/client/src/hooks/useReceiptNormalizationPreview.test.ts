import { act, renderHook, waitFor } from "@testing-library/react";
import { createQueryWrapper } from "@/test/test-utils";
import client from "@/lib/api-client";
import { useReceiptNormalizationPreview } from "./useReceiptNormalizationPreview";

vi.mock("@/lib/api-client", () => ({ default: { POST: vi.fn() } }));

const sourceA = {
  location: "Market A",
  description: "BF FRANKS",
  itemCode: "A1",
};
const sourceB = { ...sourceA, location: "Market B" };
const preview = (fingerprint: string) => ({
  sourceFingerprint: fingerprint,
  excluded: false,
  candidates: [],
  total: 0,
  offset: 0,
  limit: 20,
});
beforeEach(() => vi.clearAllMocks());

it("does not request normalization for an incomplete receipt source", () => {
  renderHook(
    () =>
      useReceiptNormalizationPreview({
        location: "",
        description: "",
        itemCode: null,
      }),
    { wrapper: createQueryWrapper() },
  );
  expect(client.POST).not.toHaveBeenCalled();
});

it("asks only for an advisory preview with scoped source, paging and cancellation", async () => {
  vi.mocked(client.POST).mockResolvedValue({
    data: preview("A"),
    response: new Response(),
  } as never);
  const { result } = renderHook(
    () => useReceiptNormalizationPreview(sourceA, "beef", 20),
    { wrapper: createQueryWrapper() },
  );
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(client.POST).toHaveBeenLastCalledWith(
    "/api/receipt-items/normalization-preview",
    expect.objectContaining({
      signal: expect.any(AbortSignal),
      body: expect.objectContaining({
        source: sourceA,
        query: "beef",
        offset: 20,
        limit: 10,
      }),
    }),
  );
});

it("does not expose an old store's preview after a late response arrives", async () => {
  let finishA!: (value: never) => void;
  vi.mocked(client.POST)
    .mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          finishA = resolve;
        }),
    )
    .mockResolvedValueOnce({
      data: preview("B-current"),
      response: new Response(),
    } as never);
  const { result, rerender } = renderHook(
    ({ source }) => useReceiptNormalizationPreview(source),
    {
      initialProps: { source: sourceA },
      wrapper: createQueryWrapper(),
    },
  );
  await waitFor(() => expect(client.POST).toHaveBeenCalledTimes(1));
  rerender({ source: sourceB });
  await waitFor(() =>
    expect(result.current.data?.sourceFingerprint).toBe("B-current"),
  );
  await act(async () => {
    finishA({ data: preview("A-obsolete"), response: new Response() } as never);
  });
  expect(result.current.data?.sourceFingerprint).toBe("B-current");
});

it("surfaces a failed lookup rather than presenting it as an empty successful catalog", async () => {
  vi.mocked(client.POST).mockResolvedValue({
    error: { detail: "Catalog lookup is temporarily unavailable" },
    response: new Response(null, { status: 503 }),
  } as never);
  const { result } = renderHook(() => useReceiptNormalizationPreview(sourceA), {
    wrapper: createQueryWrapper(),
  });
  await waitFor(() => expect(result.current.isError).toBe(true));
  expect(result.current.data).toBeUndefined();
  expect(result.current.error).toMatchObject({ status: 503 });
});
