import { renderHook, waitFor } from "@testing-library/react";
import client from "@/lib/api-client";
import { createQueryWrapper } from "@/test/test-utils";
import { extractErrorMessage } from "@/lib/problem-details";
import { useReceiptNormalizationCandidates } from "./useReceiptNormalizationCandidates";

vi.mock("@/lib/api-client", () => ({ default: { GET: vi.fn() } }));
const api = vi.mocked(client);
const source = { location: "Store A", description: "BF FRANKS", itemCode: null as string | null };
beforeEach(() => vi.clearAllMocks());

it("requests the receipt-entry projection with exact source scope and cancellation", async () => {
  api.GET.mockResolvedValue({ data: [], response: new Response(null, { status: 200 }) } as never);
  const { result, rerender } = renderHook(({ key }) => useReceiptNormalizationCandidates(key), {
    initialProps: { key: source }, wrapper: createQueryWrapper(),
  });
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(api.GET).toHaveBeenLastCalledWith("/api/receipt-items/normalization-candidates", {
    middleware: [expect.objectContaining({ onRequest: expect.any(Function) })],
    signal: expect.any(AbortSignal), params: { query: { ...source, itemCode: undefined } },
  });
  rerender({ key: { ...source, location: "Store B", itemCode: "001" } });
  await waitFor(() => expect(api.GET).toHaveBeenLastCalledWith("/api/receipt-items/normalization-candidates", {
    middleware: [expect.objectContaining({ onRequest: expect.any(Function) })],
    signal: expect.any(AbortSignal), params: { query: { ...source, location: "Store B", itemCode: "001" } },
  }));
});

it.each([{ ...source, location: " " }, { ...source, description: " " }])("does not request incomplete source %j", (key) => {
  renderHook(() => useReceiptNormalizationCandidates(key), { wrapper: createQueryWrapper() });
  expect(api.GET).not.toHaveBeenCalled();
});

it("preserves a reasoned rejection for local recovery", async () => {
  api.GET.mockResolvedValue({ error: { status: 403, detail: "Sign in again." }, response: new Response(null, { status: 403 }) } as never);
  const { result } = renderHook(() => useReceiptNormalizationCandidates(source), { wrapper: createQueryWrapper() });
  await waitFor(() => expect(result.current.isError).toBe(true));
  expect(extractErrorMessage(result.current.error)).toBe("Sign in again.");
});

it("defers network retrieval until explicitly enabled", async () => {
  api.GET.mockResolvedValue({ data: [], response: new Response(null, { status: 200 }) } as never);
  const { result, rerender } = renderHook(
    ({ enabled }) => useReceiptNormalizationCandidates(source, { enabled }),
    { initialProps: { enabled: false }, wrapper: createQueryWrapper() },
  );
  expect(result.current.fetchStatus).toBe("idle");
  expect(api.GET).not.toHaveBeenCalled();
  rerender({ enabled: true });
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(api.GET).toHaveBeenCalledTimes(1);
});
