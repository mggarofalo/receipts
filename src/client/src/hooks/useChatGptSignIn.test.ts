import { act, renderHook } from "@testing-library/react";
import { createQueryWrapper } from "@/test/test-utils";
import client from "@/lib/api-client";
import { useChatGptSignIn } from "./useChatGptSignIn";
import { useQueryClient } from "@tanstack/react-query";
import { clearTokens } from "@/lib/auth";

vi.mock("@/lib/api-client", () => ({
  default: { GET: vi.fn(), POST: vi.fn() },
}));
const api = vi.mocked(client);
const id = "10000000-0000-0000-0000-000000000001";
const waiting = () => ({
  id,
  state: "waiting",
  userCode: "TEST-ONLY",
  verificationUrl: "https://auth.openai.com/codex/device",
  localExpiresAt: new Date(Date.now() + 10000).toISOString(),
});
const ok = (data: unknown) => ({
  data,
  response: new Response(null, { status: 200 }),
  error: undefined,
});
const mount = () =>
  renderHook(() => useChatGptSignIn(), { wrapper: createQueryWrapper() });
beforeEach(() => {
  vi.clearAllMocks();
  vi.useFakeTimers();
});
it("never stores ceremony in query or mutation caches and refreshes connection/preferences after disconnect", async () => {
  api.POST.mockResolvedValueOnce(ok(waiting()) as never).mockResolvedValueOnce(
    ok(undefined) as never,
  );
  const { result } = renderHook(
    () => ({ ceremony: useChatGptSignIn(), cache: useQueryClient() }),
    { wrapper: createQueryWrapper() },
  );
  const invalidate = vi.spyOn(result.current.cache, "invalidateQueries");
  await act(async () => result.current.ceremony.start(true));
  expect(result.current.cache.getQueryCache().getAll()).toEqual([]);
  expect(result.current.cache.getMutationCache().getAll()).toEqual([]);
  await act(async () => result.current.ceremony.disconnect());
  expect(result.current.ceremony.snapshot?.state).toBe("disconnected");
  expect(invalidate).toHaveBeenCalledWith({ queryKey: ["normalization-ai"] });
  expect(invalidate).toHaveBeenCalledWith({
    queryKey: ["normalization-catalog", "inbox", "preferences"],
  });
});

it("clears ceremony and aborts polling immediately when the app session changes", async () => {
  api.POST.mockResolvedValue(ok(waiting()) as never);
  const { result } = mount();
  await act(async () => result.current.start(true));
  act(() => clearTokens());
  expect(result.current.snapshot).toBeNull();
  await act(async () => vi.advanceTimersByTimeAsync(20000));
  expect(api.GET).not.toHaveBeenCalled();
});
afterEach(() => vi.useRealTimers());

it("requires fresh explicit consent and sends no-store requests", async () => {
  api.POST.mockResolvedValue(ok(waiting()) as never);
  const { result } = mount();
  await act(async () => result.current.start(false));
  expect(api.POST).not.toHaveBeenCalled();
  await act(async () => result.current.start(true));
  expect(api.POST).toHaveBeenCalledWith(
    "/api/normalization-ai/connection/sign-in",
    expect.objectContaining({
      body: { ownerConsent: true },
      cache: "no-store",
      signal: expect.any(AbortSignal),
    }),
  );
  expect(result.current.snapshot?.userCode).toBe("TEST-ONLY");
  expect(localStorage.getItem("TEST-ONLY")).toBeNull();
});

it.each([
  "success",
  "cancelled",
  "expired",
  "rejected",
  "restart",
  "reconnect",
])("clears all ceremony values and stops polling on %s", async (state) => {
  api.POST.mockResolvedValue(ok(waiting()) as never);
  api.GET.mockResolvedValue(ok({ ...waiting(), state }) as never);
  const { result } = mount();
  await act(async () => result.current.start(true));
  await act(async () => vi.advanceTimersByTimeAsync(1500));
  expect(result.current.snapshot).toEqual({ id, state });
  expect(result.current.active).toBe(false);
  await act(async () => vi.advanceTimersByTimeAsync(20000));
  expect(api.GET).toHaveBeenCalledTimes(1);
});

it("follows same-host busy ids but does not poll external contention", async () => {
  api.POST.mockResolvedValueOnce(ok({ id, state: "busy" }) as never);
  api.GET.mockResolvedValue(ok(waiting()) as never);
  const { result } = mount();
  await act(async () => result.current.start(true));
  expect(result.current.active).toBe(true);
  await act(async () => vi.advanceTimersByTimeAsync(1500));
  expect(result.current.snapshot?.userCode).toBe("TEST-ONLY");
  api.POST.mockResolvedValueOnce(
    ok({ id: "00000000-0000-0000-0000-000000000000", state: "busy" }) as never,
  );
  await act(async () => result.current.start(true));
  expect(result.current.active).toBe(false);
  expect(result.current.snapshot?.userCode).toBeUndefined();
  await act(async () => vi.advanceTimersByTimeAsync(1500));
  expect(api.GET).toHaveBeenCalledTimes(1);
});

it("clears expired display values while keeping server polling authoritative", async () => {
  api.POST.mockResolvedValue(
    ok({
      ...waiting(),
      localExpiresAt: new Date(Date.now() + 1000).toISOString(),
    }) as never,
  );
  const { result } = mount();
  await act(async () => result.current.start(true));
  await act(async () => vi.advanceTimersByTimeAsync(1000));
  expect(result.current.snapshot).toEqual({ id, state: "waiting" });
  expect(result.current.active).toBe(true);
});

it("never renders an unsafe verification URL", async () => {
  api.POST.mockResolvedValue(
    ok({
      ...waiting(),
      verificationUrl: "https://evil.example/device",
    }) as never,
  );
  const { result } = mount();
  await act(async () => result.current.start(true));
  expect(result.current.snapshot).toEqual({ id, state: "waiting" });
});

it("cancel supersedes an in-flight poll, clears the code immediately, and never disconnects", async () => {
  api.POST.mockResolvedValueOnce(ok(waiting()) as never).mockResolvedValueOnce(
    ok({ id, state: "cancelled" }) as never,
  );
  let resolve!: (value: unknown) => void;
  api.GET.mockReturnValue(
    new Promise((done) => {
      resolve = done;
    }) as never,
  );
  const { result } = mount();
  await act(async () => result.current.start(true));
  await act(async () => vi.advanceTimersByTimeAsync(1500));
  await act(async () => result.current.cancel());
  await act(async () => resolve(ok(waiting())));
  expect(result.current.snapshot).toEqual({ id, state: "cancelled" });
  expect(
    (api.POST.mock.calls as unknown as Array<[string]>).map(([path]) => path),
  ).toEqual([
    "/api/normalization-ai/connection/sign-in",
    "/api/normalization-ai/connection/sign-in/{id}/cancel",
  ]);
});

it.each([401, 403, 409, 503])(
  "clears ceremony on HTTP %s without retaining server diagnostics",
  async (status) => {
    api.POST.mockResolvedValue(ok(waiting()) as never);
    api.GET.mockResolvedValue({
      response: new Response(null, { status }),
      error: { detail: "sensitive diagnostic" },
    } as never);
    const { result } = mount();
    await act(async () => result.current.start(true));
    await act(async () => vi.advanceTimersByTimeAsync(1500));
    expect(result.current.snapshot).toBeNull();
    expect(result.current.error).toBeTruthy();
    expect(result.current.error).not.toContain("sensitive diagnostic");
  },
);

it("aborts pending replies and timers on unmount", async () => {
  api.POST.mockResolvedValue(ok(waiting()) as never);
  const { result, unmount } = mount();
  await act(async () => result.current.start(true));
  const signal = (
    api.POST.mock.calls as unknown as Array<[string, { signal?: AbortSignal }]>
  )[0][1]?.signal;
  unmount();
  expect(signal?.aborted).toBe(true);
  await act(async () => vi.advanceTimersByTimeAsync(20000));
  expect(api.GET).not.toHaveBeenCalled();
});

it("deduplicates concurrent start clicks and exposes loading until response", async () => {
  let resolve!: (value: unknown) => void;
  api.POST.mockReturnValue(
    new Promise((done) => {
      resolve = done;
    }) as never,
  );
  const { result } = mount();
  act(() => {
    void result.current.start(true);
    void result.current.start(true);
  });
  expect(result.current.pending).toBe(true);
  expect(api.POST).toHaveBeenCalledTimes(1);
  await act(async () => resolve(ok(waiting())));
  expect(result.current.pending).toBe(false);
});
