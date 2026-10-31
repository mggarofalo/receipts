import { createElement, type ReactNode } from "react";
import { act, cleanup, renderHook, waitFor } from "@testing-library/react";
import { QueryClientProvider } from "@tanstack/react-query";
import { http, HttpResponse } from "msw";
import { clearTokens, setTokens } from "@/lib/auth";
import { createAppQueryClient } from "@/lib/query-client";
import { server } from "@/test/msw/server";
import {
  useRestructureApplications,
  useRestructurePreview,
  useRestructureRun,
  type RestructureApplication,
  type RestructurePlan,
  type RestructureReport,
} from "./useRestructurePlans";

vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://restructure.test"));

const report: RestructureReport = {
  taxonomy: { created: 1, renamed: 0, moved: 2, defined: 3, archived: 4, restored: 0, activeBefore: 9, activeAfter: 6 },
  items: { considered: 10, reassigned: 250, cleared: 1, unchanged: 5, conflicts: 1, missingFromDeployment: 0, byRule: { node: 250 } },
  transitions: [],
  defaults: { kept: 1, moved: 0, removed: 1, set: 0 },
  mappings: { reapproved: 0, repointed: 0, revoked: 0, leftStale: 0 },
  envelopeMerges: [],
  budget: { itemsChangingEnvelope: 0, itemsChangingYnabCategory: 0 },
  occasions: [],
};

const plan = { planId: "p" } as unknown as RestructurePlan;

function application(state: RestructureApplication["state"], applied: number, undone = 0): RestructureApplication {
  return {
    id: "app-1",
    planId: "p",
    reason: "Item types",
    appliedAt: "2026-10-30T00:00:00Z",
    undoneAt: null,
    state,
    report,
    progress: { operationId: "app-1", state, total: 251, applied, conflicted: 1, skipped: 0, pending: 250 - applied, undone, undoConflicted: 0, undoSkipped: 0 },
  };
}

let client: ReturnType<typeof createAppQueryClient>;
function Wrapper({ children }: { children: ReactNode }) {
  return createElement(QueryClientProvider, { client }, children);
}

beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  setTokens("access", "refresh");
  client = createAppQueryClient();
});
afterEach(() => {
  cleanup();
  client.clear();
  clearTokens();
  server.resetHandlers();
});

it("lists applications newest first as the server returns them", async () => {
  server.use(
    http.get("*/api/restructure-plans", () =>
      HttpResponse.json({ items: [application("completed", 250)], nextOffset: null }),
    ),
  );

  const { result } = renderHook(() => useRestructureApplications(), { wrapper: Wrapper });

  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(result.current.data?.map((row) => row.id)).toEqual(["app-1"]);
});

it("posts the plan for a dry run and returns its report", async () => {
  let body: unknown;
  server.use(
    http.post("*/api/restructure-plans/preview", async ({ request }) => {
      body = await request.json();
      return HttpResponse.json({ valid: true, errors: [], warnings: [], report });
    }),
  );

  const { result } = renderHook(() => useRestructurePreview(), { wrapper: Wrapper });
  const preview = await act(() => result.current.mutateAsync(plan));

  expect(body).toEqual(plan);
  expect(preview.report.items.reassigned).toBe(250);
});

it("applies chunk by chunk until nothing is pending, reporting progress", async () => {
  const applied: number[] = [];
  let calls = 0;
  server.use(
    http.post("*/api/restructure-plans", () => HttpResponse.json(application("preview", 0))),
    http.post("*/api/restructure-plans/:id/apply", async ({ request }) => {
      expect(await request.json()).toEqual({ limit: 100 });
      calls++;
      const done = Math.min(250, calls * 100);
      return HttpResponse.json(application(done === 250 ? "completed" : "applying", done));
    }),
  );

  const { result } = renderHook(() => useRestructureRun(), { wrapper: Wrapper });
  const final = await act(() =>
    result.current.mutateAsync({
      kind: "apply",
      applicationId: "app-1",
      plan,
      onProgress: (row) => applied.push(Number(row.progress.applied)),
    }),
  );

  expect(calls).toBe(3);
  expect(applied).toEqual([0, 100, 200, 250]);
  expect(final.state).toBe("completed");
});

it("keeps undoing until the server reports the restructure undone", async () => {
  let calls = 0;
  server.use(
    http.get("*/api/restructure-plans/:id", () => HttpResponse.json(application("completed", 250))),
    http.post("*/api/restructure-plans/:id/undo", () => {
      calls++;
      return HttpResponse.json(application(calls === 3 ? "undone" : "undoing", 250, Math.min(250, calls * 100)));
    }),
  );

  const { result } = renderHook(() => useRestructureRun(), { wrapper: Wrapper });
  const final = await act(() => result.current.mutateAsync({ kind: "undo", id: "app-1" }));

  expect(calls).toBe(3);
  expect(final.state).toBe("undone");
});

it("stops on a rejected chunk and surfaces the problem", async () => {
  server.use(
    http.get("*/api/restructure-plans/:id", () => HttpResponse.json(application("applying", 100))),
    http.post("*/api/restructure-plans/:id/apply", () =>
      HttpResponse.json({ status: 409, detail: "Categories changed." }, { status: 409, headers: { "Content-Type": "application/problem+json" } }),
    ),
  );

  const { result } = renderHook(() => useRestructureRun(), { wrapper: Wrapper });
  await act(async () => {
    await expect(result.current.mutateAsync({ kind: "resume", id: "app-1" })).rejects.toMatchObject({ detail: "Categories changed." });
  });
});

it("does not loop on a cancelled restructure", async () => {
  const apply = vi.fn();
  server.use(
    http.get("*/api/restructure-plans/:id", () => HttpResponse.json(application("cancelled", 100))),
    http.post("*/api/restructure-plans/:id/apply", () => {
      apply();
      return HttpResponse.json(application("cancelled", 100));
    }),
  );

  const { result } = renderHook(() => useRestructureRun(), { wrapper: Wrapper });
  await act(() => result.current.mutateAsync({ kind: "resume", id: "app-1" }));

  expect(apply).not.toHaveBeenCalled();
});
